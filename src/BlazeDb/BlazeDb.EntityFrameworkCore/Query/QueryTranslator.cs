using System.Linq.Expressions;
using System.Reflection;
using BlazeDb.EntityFrameworkCore.Metadata;

namespace BlazeDb.EntityFrameworkCore.Query;

/// <summary>
/// Splits a LINQ query into the part the engine can answer and the part it cannot.
///
/// The engine understands a source (a scan or an index lookup), residual filters, an ordering and
/// paging - so the translator consumes the longest prefix of the operator chain that fits that
/// shape, choosing an index where a predicate or an ordering allows one. Whatever is left, from a
/// projection to a group-by, is handed back untouched and runs as LINQ to Objects over the rows
/// the plan produced. Rows are live objects in memory, so that fallback costs nothing but the
/// enumeration itself, and no query is ever rejected for being untranslatable.
/// </summary>
internal static class QueryTranslator
{
    public static TranslatedQuery Translate(Expression expression, IBlazeDbTableBinding binding, Type entityClrType)
    {
        var chain = Decompose(expression);
        var plan = binding.CreatePlan();

        var predicates = new List<LambdaExpression>();
        var orderings = new List<MethodCallExpression>();
        var consumed = 0;
        var stage = Stage.Shape;

        foreach (var call in chain)
        {
            var name = call.Method.Name;
            var next = StageOf(name);
            if (next is null || next < stage)
            {
                break;
            }

            switch (name)
            {
                case nameof(Queryable.Where):
                    if (call.Arguments.Count != 2 || Lambda(call, 1).Parameters.Count != 1)
                    {
                        goto done;
                    }
                    predicates.Add(Lambda(call, 1));
                    break;

                case nameof(Queryable.OrderBy):
                case nameof(Queryable.OrderByDescending):
                case nameof(Queryable.ThenBy):
                case nameof(Queryable.ThenByDescending):
                    if (call.Arguments.Count != 2)
                    {
                        goto done;
                    }
                    orderings.Add(call);
                    break;

                case nameof(Queryable.Skip):
                    // A Skip after a Take changes which rows the Take applies to; the plan applies
                    // Skip first, so it cannot absorb one. Consecutive Skips add up.
                    if (plan.Take >= 0 || !TryConstantInt(call.Arguments[1], out var skip))
                    {
                        goto done;
                    }
                    plan.Skip = (int)Math.Min((long)plan.Skip + Math.Max(0, skip), int.MaxValue);
                    break;

                case nameof(Queryable.Take):
                    if (!TryConstantInt(call.Arguments[1], out var take))
                    {
                        goto done;
                    }
                    // LINQ treats a negative count as zero; the plan uses -1 for "no limit", and
                    // a second Take can only shrink the first.
                    take = Math.Max(0, take);
                    plan.Take = plan.Take < 0 ? take : Math.Min(plan.Take, take);
                    break;

                default:
                    goto done;
            }

            stage = next.Value;
            consumed++;
        }

    done:
        // Index selection happens once the whole prefix is known, so an ordering can influence it:
        // an OrderBy over an indexed property is answered by walking that index instead of sorting.
        var selection = IndexSelector.Choose(plan, binding, predicates, orderings);
        foreach (var predicate in selection.Residual)
        {
            plan.AddPredicate(predicate);
        }
        if (!selection.OrderingSatisfied)
        {
            foreach (var ordering in orderings)
            {
                plan.AddOrdering(ordering);
            }
        }

        return new TranslatedQuery(plan, RebuildRemainder(chain, consumed, entityClrType));
    }

    /// <summary>
    /// Operator bands, in the order the engine applies them. A chain is only consumed while it does
    /// not go back a band: filtering and sorting commute with each other (a plan filters, then sorts,
    /// whichever way they were written) so they share one band, but paging pins the order of what
    /// follows - <c>Take(5).Where(...)</c> does not mean <c>Where(...).Take(5)</c> - so a filter or
    /// sort after paging is left for the fallback.
    /// </summary>
    private enum Stage
    {
        Shape = 0,
        Page = 1,
    }

    private static Stage? StageOf(string methodName) => methodName switch
    {
        nameof(Queryable.Where) => Stage.Shape,
        nameof(Queryable.OrderBy) or nameof(Queryable.OrderByDescending) => Stage.Shape,
        // ThenBy composes onto the ordering the plan already has.
        nameof(Queryable.ThenBy) or nameof(Queryable.ThenByDescending) => Stage.Shape,
        nameof(Queryable.Skip) or nameof(Queryable.Take) => Stage.Page,
        _ => null,
    };

    /// <summary>Flattens the call chain into root-to-outermost order.</summary>
    private static List<MethodCallExpression> Decompose(Expression expression)
    {
        var chain = new List<MethodCallExpression>();
        var current = expression;
        while (current is MethodCallExpression call && call.Arguments.Count > 0 && IsQueryOperator(call))
        {
            chain.Add(call);
            current = call.Arguments[0];
        }
        chain.Reverse();
        return chain;
    }

    private static bool IsQueryOperator(MethodCallExpression call) =>
        call.Method.DeclaringType == typeof(Queryable) || call.Method.DeclaringType == typeof(Enumerable);

    /// <summary>
    /// Rebuilds the operators the plan did not consume, rooted at a placeholder the executor swaps
    /// for the rows the plan produced.
    /// </summary>
    private static Expression? RebuildRemainder(
        List<MethodCallExpression> chain, int consumed, Type entityClrType)
    {
        if (consumed == chain.Count)
        {
            return null;
        }

        Expression current = RowsPlaceholder.For(entityClrType);
        for (var i = consumed; i < chain.Count; i++)
        {
            var call = chain[i];
            var arguments = call.Arguments.ToArray();
            arguments[0] = current;
            current = Expression.Call(call.Object, call.Method, arguments);
        }
        return current;
    }

    private static LambdaExpression Lambda(MethodCallExpression call, int index)
    {
        var argument = call.Arguments[index];
        while (argument is UnaryExpression { NodeType: ExpressionType.Quote } quote)
        {
            argument = quote.Operand;
        }
        return (LambdaExpression)argument;
    }

    private static bool TryConstantInt(Expression expression, out int value)
    {
        if (ExpressionEvaluator.TryEvaluate(expression, out var boxed) && boxed is int i)
        {
            value = i;
            return true;
        }
        value = 0;
        return false;
    }
}

/// <summary>A plan, plus the LINQ the plan could not absorb.</summary>
internal sealed record TranslatedQuery(QueryPlan Plan, Expression? Remainder);

/// <summary>
/// Stands in for the plan's result inside a rebuilt remainder expression, so the executor can
/// substitute the actual rows once the plan has run.
/// </summary>
internal sealed class RowsPlaceholder : Expression
{
    private RowsPlaceholder(Type type) => Type = type;

    public static RowsPlaceholder For(Type entityClrType) =>
        new(typeof(IQueryable<>).MakeGenericType(entityClrType));

    public override Type Type { get; }

    public override ExpressionType NodeType => ExpressionType.Extension;

    public override bool CanReduce => false;

    protected override Expression VisitChildren(ExpressionVisitor visitor) => this;
}

/// <summary>Replaces the placeholder with a concrete queryable over the plan's rows.</summary>
internal sealed class RowsPlaceholderReplacer : ExpressionVisitor
{
    private readonly Expression _rows;

    public RowsPlaceholderReplacer(Expression rows) => _rows = rows;

    public override Expression? Visit(Expression? node) =>
        node is RowsPlaceholder ? _rows : base.Visit(node);
}

/// <summary>
/// Evaluates the closed-over parts of a query - a captured variable, a constant, a field of a
/// closure. LINQ trees carry these as member accesses over a display class, and reading them
/// directly avoids compiling an expression just to learn a value. Anything more elaborate that
/// still does not depend on the row (<c>DateTime.UtcNow.AddDays(-7)</c>, <c>ids.Count</c>) is
/// compiled and run once, so it can still drive an index lookup instead of forcing a scan.
/// </summary>
internal static class ExpressionEvaluator
{
    public static bool TryEvaluate(Expression expression, out object? value)
    {
        switch (expression)
        {
            case ConstantExpression constant:
                value = constant.Value;
                return true;

            case MemberExpression member:
            {
                if (member.Expression is null)
                {
                    return TryRead(member.Member, null, out value);
                }
                if (TryEvaluate(member.Expression, out var target) && target is not null)
                {
                    return TryRead(member.Member, target, out value);
                }
                value = null;
                return false;
            }

            case UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert
                when convert.Type.IsAssignableFrom(convert.Operand.Type)
                     || Nullable.GetUnderlyingType(convert.Type) == convert.Operand.Type:
                return TryEvaluate(convert.Operand, out value);

            case ParameterExpression:
                value = null;
                return false;

            default:
                return TryCompile(expression, out value);
        }
    }

    private static bool TryCompile(Expression expression, out object? value)
    {
        value = null;
        if (!RowIndependence.Check(expression))
        {
            return false;
        }
        try
        {
            // Interpreted: this runs once per translation, so a JIT compile would cost more than it saves.
            value = Expression.Lambda<Func<object?>>(Expression.Convert(expression, typeof(object))).Compile(preferInterpretation: true).Invoke();
            return true;
        }
        catch
        {
            // Whatever it was, the predicate still runs as a residual filter and gives the right
            // answer; only the chance of an index lookup is lost.
            return false;
        }
    }

    /// <summary>
    /// A subtree can be evaluated ahead of the rows only if it neither reads a lambda parameter nor
    /// contains a query root or another provider-specific node that has no value on its own.
    /// </summary>
    private sealed class RowIndependence : ExpressionVisitor
    {
        private bool _independent = true;

        public static bool Check(Expression expression)
        {
            var visitor = new RowIndependence();
            visitor.Visit(expression);
            return visitor._independent;
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            _independent = false;
            return node;
        }

        protected override Expression VisitExtension(Expression node)
        {
            _independent = false;
            return node;
        }

        protected override Expression VisitLambda<T>(Expression<T> node)
        {
            // A nested lambda's own parameters are fine; only free parameters matter. Treating any
            // lambda as dependent is the conservative choice and costs nothing but an index.
            _independent = false;
            return node;
        }
    }

    private static bool TryRead(MemberInfo member, object? target, out object? value)
    {
        switch (member)
        {
            case FieldInfo field:
                value = field.GetValue(target);
                return true;
            case PropertyInfo { CanRead: true } property when property.GetIndexParameters().Length == 0:
                value = property.GetValue(target);
                return true;
            default:
                value = null;
                return false;
        }
    }
}
