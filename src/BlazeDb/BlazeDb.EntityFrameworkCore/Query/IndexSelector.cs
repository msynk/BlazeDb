using System.Linq.Expressions;
using BlazeDb.EntityFrameworkCore.Metadata;

namespace BlazeDb.EntityFrameworkCore.Query;

/// <summary>
/// Decides which index, if any, a query should read from.
///
/// The rules are deliberately shallow - there are no statistics to consult and every row is
/// already in memory, so the win is asymptotic rather than marginal: an equality lookup or a range
/// scan instead of touching every row. Preference runs compound equality first (most selective),
/// then single-property equality, then a range, and finally an ordering that an ordered index can
/// serve for free. Anything not turned into a source stays a residual filter.
/// </summary>
internal static class IndexSelector
{
    public static IndexSelection Choose(
        QueryPlan plan,
        IBlazeDbTableBinding binding,
        List<LambdaExpression> predicates,
        List<MethodCallExpression> orderings)
    {
        if (predicates.Count == 0 && orderings.Count == 0)
        {
            return new IndexSelection([], OrderingSatisfied: false);
        }

        var conjuncts = Conjuncts.Flatten(predicates, out var parameter);
        var comparisons = conjuncts
            .Select((c, i) => (Index: i, Comparison: Comparison.TryRead(c, parameter)))
            .Where(x => x.Comparison is not null)
            .ToList();

        var used = new HashSet<int>();

        if (!TryCompoundEquality(plan, binding, comparisons, used) &&
            !TrySingleEquality(plan, binding, comparisons, used))
        {
            TryRange(plan, binding, comparisons, used);
        }

        var orderingSatisfied = TrySatisfyOrdering(plan, binding, orderings);

        return new IndexSelection(Conjuncts.Rebuild(conjuncts, used, parameter, predicates), orderingSatisfied);
    }

    private static bool TryCompoundEquality(
        QueryPlan plan, IBlazeDbTableBinding binding, List<(int Index, Comparison? Comparison)> comparisons,
        HashSet<int> used)
    {
        foreach (var index in binding.Indexes.Where(i => i.Members.Count > 1).OrderByDescending(i => i.Members.Count))
        {
            var matched = new List<(int Index, Comparison Comparison)>();
            foreach (var member in index.Members)
            {
                var match = comparisons.FirstOrDefault(c =>
                    c.Comparison!.Operator == ExpressionType.Equal &&
                    c.Comparison.Member == member &&
                    c.Comparison.Value is not null &&
                    !matched.Any(m => m.Index == c.Index));
                if (match.Comparison is null)
                {
                    matched.Clear();
                    break;
                }
                matched.Add((match.Index, match.Comparison));
            }
            if (matched.Count != index.Members.Count)
            {
                continue;
            }

            // The engine indexes the tuple of the members, so the lookup key is that same tuple.
            var tupleType = index.KeyType;
            var key = Activator.CreateInstance(tupleType, matched.Select(m => m.Comparison.Value).ToArray());
            if (key is null)
            {
                continue;
            }

            plan.UseEquality(index, key);
            foreach (var (i, _) in matched)
            {
                used.Add(i);
            }
            return true;
        }
        return false;
    }

    private static bool TrySingleEquality(
        QueryPlan plan, IBlazeDbTableBinding binding, List<(int Index, Comparison? Comparison)> comparisons,
        HashSet<int> used)
    {
        foreach (var (i, comparison) in comparisons)
        {
            if (comparison!.Operator != ExpressionType.Equal || comparison.Value is null)
            {
                continue;
            }
            // A hash index answers equality in one step; an ordered index needs a range of one key,
            // which is still far better than a scan, so it is the second choice.
            var index = Single(binding, comparison.Member, ordered: false)
                        ?? Single(binding, comparison.Member, ordered: true);
            if (index is null)
            {
                continue;
            }

            plan.UseEquality(index, comparison.Value);
            used.Add(i);
            return true;
        }
        return false;
    }

    private static void TryRange(
        QueryPlan plan, IBlazeDbTableBinding binding, List<(int Index, Comparison? Comparison)> comparisons,
        HashSet<int> used)
    {
        foreach (var group in comparisons.Where(c => c.Comparison!.IsRange).GroupBy(c => c.Comparison!.Member))
        {
            var index = Single(binding, group.Key, ordered: true);
            if (index is null)
            {
                continue;
            }

            object? from = null, to = null;
            bool hasFrom = false, hasTo = false;
            foreach (var (i, comparison) in group)
            {
                if (comparison!.Value is null)
                {
                    continue;
                }
                var lower = comparison.Operator is ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual;
                if (lower && !hasFrom)
                {
                    (hasFrom, from) = (true, comparison.Value);
                }
                else if (!lower && !hasTo)
                {
                    (hasTo, to) = (true, comparison.Value);
                }
                else
                {
                    continue;
                }

                // The engine's ranges are inclusive at both ends, so a strict comparison keeps its
                // conjunct as a residual filter that trims the boundary rows.
                if (comparison.Operator is ExpressionType.GreaterThanOrEqual or ExpressionType.LessThanOrEqual)
                {
                    used.Add(i);
                }
            }

            if (hasFrom || hasTo)
            {
                plan.UseRange(index, hasFrom, from, hasTo, to, descending: false);
                return;
            }
        }
    }

    private static bool TrySatisfyOrdering(
        QueryPlan plan, IBlazeDbTableBinding binding, List<MethodCallExpression> orderings)
    {
        // Only a single OrderBy can be answered by an index: a ThenBy would need the index to be
        // compound over exactly the same members, which the ordering syntax cannot express here.
        if (orderings.Count != 1 || plan.HasIndexSource)
        {
            return false;
        }

        var call = orderings[0];
        var selector = Comparison.StripQuotes(call.Arguments[1]) as LambdaExpression;
        if (selector?.Body is not MemberExpression member || member.Expression != selector.Parameters[0])
        {
            return false;
        }

        var index = Single(binding, member.Member.Name, ordered: true);
        if (index is null)
        {
            return false;
        }

        var descending = call.Method.Name is nameof(Queryable.OrderByDescending);
        plan.UseRange(index, hasFrom: false, null, hasTo: false, null, descending);
        return true;
    }

    private static BlazeDbIndex? Single(IBlazeDbTableBinding binding, string member, bool ordered) =>
        binding.Indexes.FirstOrDefault(i =>
            i.Ordered == ordered && i.Members.Count == 1 && i.Members[0] == member);
}

internal sealed record IndexSelection(IReadOnlyList<LambdaExpression> Residual, bool OrderingSatisfied);

/// <summary>One <c>row.Member op value</c> test, with the value already evaluated.</summary>
internal sealed class Comparison
{
    private Comparison(string member, ExpressionType op, object? value)
    {
        Member = member;
        Operator = op;
        Value = value;
    }

    public string Member { get; }

    public ExpressionType Operator { get; }

    public object? Value { get; }

    public bool IsRange => Operator is ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual
        or ExpressionType.LessThan or ExpressionType.LessThanOrEqual;

    public static Comparison? TryRead(Expression conjunct, ParameterExpression? parameter)
    {
        if (parameter is null || conjunct is not BinaryExpression binary)
        {
            return null;
        }
        var op = binary.NodeType;
        if (op is not (ExpressionType.Equal or ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual
            or ExpressionType.LessThan or ExpressionType.LessThanOrEqual))
        {
            return null;
        }

        if (TryMember(binary.Left, parameter, out var member) &&
            ExpressionEvaluator.TryEvaluate(binary.Right, out var value))
        {
            return new Comparison(member, op, value);
        }
        if (TryMember(binary.Right, parameter, out member) &&
            ExpressionEvaluator.TryEvaluate(binary.Left, out value))
        {
            return new Comparison(member, Mirror(op), value);
        }
        return null;
    }

    private static bool TryMember(Expression expression, ParameterExpression parameter, out string member)
    {
        var current = StripQuotes(expression);
        while (current is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
        {
            current = convert.Operand;
        }
        if (current is MemberExpression access && access.Expression == parameter)
        {
            member = access.Member.Name;
            return true;
        }
        member = string.Empty;
        return false;
    }

    /// <summary>Flips a comparison written the other way round, as in <c>18 &lt;= row.Age</c>.</summary>
    private static ExpressionType Mirror(ExpressionType op) => op switch
    {
        ExpressionType.GreaterThan => ExpressionType.LessThan,
        ExpressionType.GreaterThanOrEqual => ExpressionType.LessThanOrEqual,
        ExpressionType.LessThan => ExpressionType.GreaterThan,
        ExpressionType.LessThanOrEqual => ExpressionType.GreaterThanOrEqual,
        _ => op,
    };

    public static Expression StripQuotes(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Quote } quote)
        {
            expression = quote.Operand;
        }
        return expression;
    }
}

/// <summary>
/// Splits the collected <c>Where</c> bodies into individual AND-ed tests and puts the survivors
/// back together once index selection has claimed the ones it can serve.
/// </summary>
internal static class Conjuncts
{
    public static List<Expression> Flatten(List<LambdaExpression> predicates, out ParameterExpression? parameter)
    {
        parameter = predicates.Count > 0 ? predicates[0].Parameters[0] : null;
        var result = new List<Expression>();
        foreach (var predicate in predicates)
        {
            // Separate Where calls each declare their own parameter; rewriting them to a single one
            // lets the surviving tests be recombined into one lambda.
            var body = ReferenceEquals(predicate.Parameters[0], parameter)
                ? predicate.Body
                : new ParameterRewriter(predicate.Parameters[0], parameter!).Visit(predicate.Body)!;
            Split(body, result);
        }
        return result;
    }

    public static IReadOnlyList<LambdaExpression> Rebuild(
        List<Expression> conjuncts, HashSet<int> used, ParameterExpression? parameter, List<LambdaExpression> original)
    {
        if (used.Count == 0)
        {
            return original;
        }
        if (used.Count == conjuncts.Count || parameter is null)
        {
            return [];
        }

        Expression? body = null;
        for (var i = 0; i < conjuncts.Count; i++)
        {
            if (used.Contains(i))
            {
                continue;
            }
            body = body is null ? conjuncts[i] : Expression.AndAlso(body, conjuncts[i]);
        }
        return body is null ? [] : [Expression.Lambda(body, parameter)];
    }

    private static void Split(Expression expression, List<Expression> into)
    {
        if (expression is BinaryExpression { NodeType: ExpressionType.AndAlso } and)
        {
            Split(and.Left, into);
            Split(and.Right, into);
            return;
        }
        into.Add(expression);
    }

    private sealed class ParameterRewriter : ExpressionVisitor
    {
        private readonly ParameterExpression _from;
        private readonly ParameterExpression _to;

        public ParameterRewriter(ParameterExpression from, ParameterExpression to)
        {
            _from = from;
            _to = to;
        }

        protected override Expression VisitParameter(ParameterExpression node) => node == _from ? _to : node;
    }
}
