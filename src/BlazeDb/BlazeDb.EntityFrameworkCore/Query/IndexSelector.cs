using System.Linq.Expressions;
using BlazeDb.EntityFrameworkCore.Metadata;

namespace BlazeDb.EntityFrameworkCore.Query;

/// <summary>
/// Decides which index, if any, a query should read from.
///
/// The rules are deliberately shallow - there are no statistics to consult and every row is
/// already in memory, so the win is asymptotic rather than marginal: an equality lookup or a range
/// scan instead of touching every row. Preference runs primary-key equality (or membership) first,
/// then compound equality (most selective index), then single-property equality or membership, then a range, and
/// finally an ordering that an ordered index can serve for free. Anything not turned into a source
/// stays a residual filter.
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
        string? rangeMember = null;

        if (!TryPrimaryKey(plan, binding, comparisons, used) &&
            !TryCompoundEquality(plan, binding, comparisons, used) &&
            !TrySingleEquality(plan, binding, comparisons, used))
        {
            rangeMember = TryRange(plan, binding, comparisons, used);
        }

        var orderingSatisfied = TrySatisfyOrdering(plan, binding, orderings, rangeMember);

        return new IndexSelection(Conjuncts.Rebuild(conjuncts, used, parameter, predicates), orderingSatisfied);
    }

    private static bool TryPrimaryKey(
        QueryPlan plan, IBlazeDbTableBinding binding, List<(int Index, Comparison? Comparison)> comparisons,
        HashSet<int> used)
    {
        if (binding.KeyMember is null)
        {
            return false;
        }
        // Equality first: one probe beats a list of them, and a list is not tried when both appear.
        foreach (var membership in (bool[])[false, true])
        {
            foreach (var (i, comparison) in comparisons)
            {
                if (comparison!.Member != binding.KeyMember || comparison.IsMembership != membership)
                {
                    continue;
                }
                if (membership)
                {
                    plan.UseKeys(comparison.InValues!);
                }
                else if (comparison.Operator == ExpressionType.Equal && comparison.Value is not null)
                {
                    plan.UseKey(comparison.Value);
                }
                else
                {
                    continue;
                }
                used.Add(i);
                return true;
            }
        }
        return false;
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

            // The engine indexes the tuple of the members, so the lookup key is that same tuple; each
            // value already has its member's type, which is the type the tuple declares for it.
            var key = Activator.CreateInstance(index.KeyType, matched.Select(m => m.Comparison.Value).ToArray());
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

    /// <summary>
    /// A single-property equality, or a membership list, against a hash index - or an ordered one,
    /// which answers equality as a range of one key, still far better than a scan.
    /// </summary>
    private static bool TrySingleEquality(
        QueryPlan plan, IBlazeDbTableBinding binding, List<(int Index, Comparison? Comparison)> comparisons,
        HashSet<int> used)
    {
        foreach (var membership in (bool[])[false, true])
        {
            foreach (var (i, comparison) in comparisons)
            {
                if (comparison!.IsMembership != membership ||
                    (!membership && (comparison.Operator != ExpressionType.Equal || comparison.Value is null)))
                {
                    continue;
                }
                var index = Single(binding, comparison.Member, ordered: false)
                            ?? Single(binding, comparison.Member, ordered: true);
                if (index is null)
                {
                    continue;
                }
                if (membership)
                {
                    plan.UseEqualityIn(index, comparison.InValues!);
                }
                else
                {
                    plan.UseEquality(index, comparison.Value!);
                }
                used.Add(i);
                return true;
            }
        }
        return false;
    }

    /// <summary>Returns the member the chosen range covers, or null when no range was chosen.</summary>
    private static string? TryRange(
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
            var usedHere = new List<int>();
            foreach (var (i, comparison) in group)
            {
                if (comparison!.Value is not { } bound)
                {
                    continue;
                }
                var lower = comparison.Operator is ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual;
                if (lower && !hasFrom)
                {
                    (hasFrom, from) = (true, bound);
                }
                else if (!lower && !hasTo)
                {
                    (hasTo, to) = (true, bound);
                }
                else
                {
                    continue;
                }

                // The engine's ranges are inclusive at both ends, so a strict comparison keeps its
                // conjunct as a residual filter that trims the boundary rows.
                if (comparison.Operator is ExpressionType.GreaterThanOrEqual or ExpressionType.LessThanOrEqual)
                {
                    usedHere.Add(i);
                }
            }

            if (hasFrom || hasTo)
            {
                plan.UseRange(index, hasFrom, from, hasTo, to, descending: false);
                used.UnionWith(usedHere);
                return group.Key;
            }
        }
        return null;
    }

    private static bool TrySatisfyOrdering(
        QueryPlan plan, IBlazeDbTableBinding binding, List<MethodCallExpression> orderings, string? rangeMember)
    {
        // Only a single OrderBy can be answered by an index: a ThenBy would need the index to be
        // compound over exactly the same members, which the ordering syntax cannot express here.
        if (orderings.Count != 1)
        {
            return false;
        }

        var call = orderings[0];
        var selector = Comparison.StripQuotes(call.Arguments[1]) as LambdaExpression;
        if (selector?.Body is not MemberExpression member || member.Expression != selector.Parameters[0])
        {
            return false;
        }

        var descending = call.Method.Name is nameof(Queryable.OrderByDescending);

        if (plan.HasIndexSource)
        {
            // A range over the ordering's own member already walks the index in order; all that is
            // left is to walk it in the requested direction instead of sorting afterwards.
            if (rangeMember == member.Member.Name)
            {
                plan.SetRangeDirection(descending);
                return true;
            }
            return false;
        }

        var index = Single(binding, member.Member.Name, ordered: true);
        if (index is null)
        {
            return false;
        }

        plan.UseRange(index, hasFrom: false, null, hasTo: false, null, descending);
        return true;
    }

    private static BlazeDbIndex? Single(IBlazeDbTableBinding binding, string member, bool ordered) =>
        binding.Indexes.FirstOrDefault(i =>
            i.Ordered == ordered && i.Members.Count == 1 && i.Members[0] == member);
}

internal sealed record IndexSelection(IReadOnlyList<LambdaExpression> Residual, bool OrderingSatisfied);
/// <summary>
/// One <c>row.Member op value</c> test (or <c>values.Contains(row.Member)</c>), with the value already
/// evaluated and converted to the member's own type. C# widens the operands of a comparison before it
/// is compared - <c>row.Kind == Kind.A</c> is really <c>(int)row.Kind == 0</c> - so the boxed value in
/// the tree is often not of the property's type, and the engine's boxed index API unboxes exactly the
/// index key type. Converting here, once, means no selector rule can hand an index a value of the wrong
/// type; a value that cannot be converted losslessly yields no comparison, and the test stays a filter.
/// </summary>
internal sealed class Comparison
{
    private Comparison(string member, ExpressionType op, object? value, IReadOnlyList<object>? inValues = null)
    {
        Member = member;
        Operator = op;
        Value = value;
        InValues = inValues;
    }

    public string Member { get; }

    /// <summary>The operator, or <see cref="ExpressionType.Call"/> for a membership test.</summary>
    public ExpressionType Operator { get; }

    /// <summary>The value, of the member's type; null only for a comparison against null.</summary>
    public object? Value { get; }

    /// <summary>For <c>values.Contains(row.Member)</c>: the distinct non-null values, of the member's type.</summary>
    public IReadOnlyList<object>? InValues { get; }

    public bool IsMembership => InValues is not null;

    public bool IsRange => Operator is ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual
        or ExpressionType.LessThan or ExpressionType.LessThanOrEqual;

    public static Comparison? TryRead(Expression conjunct, ParameterExpression? parameter)
    {
        if (parameter is null)
        {
            return null;
        }
        if (conjunct is MethodCallExpression call)
        {
            return TryReadMembership(call, parameter);
        }
        if (conjunct is not BinaryExpression binary)
        {
            return null;
        }
        var op = binary.NodeType;
        if (op is not (ExpressionType.Equal or ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual
            or ExpressionType.LessThan or ExpressionType.LessThanOrEqual))
        {
            return null;
        }

        if (TryMember(binary.Left, parameter, out var member, out var memberType) &&
            ExpressionEvaluator.TryEvaluate(binary.Right, out var value))
        {
            return Create(member, op, value, memberType);
        }
        if (TryMember(binary.Right, parameter, out member, out memberType) &&
            ExpressionEvaluator.TryEvaluate(binary.Left, out value))
        {
            return Create(member, Mirror(op), value, memberType);
        }
        return null;
    }

    private static Comparison? Create(string member, ExpressionType op, object? value, Type memberType)
    {
        if (value is null)
        {
            return new Comparison(member, op, null);
        }
        return KeyCoercion.TryCoerce(value, memberType, out var coerced) ? new Comparison(member, op, coerced) : null;
    }

    /// <summary>
    /// Reads <c>values.Contains(row.Member)</c> - the static <c>Enumerable.Contains</c> or an instance
    /// <c>Contains</c> on a list, set or array - when the values do not depend on the row. A string
    /// receiver is left alone (<c>text.Contains(row.Letter)</c> is a substring test), and so is a set
    /// with its own comparer, which may match values an index keyed on the default comparer would not.
    /// A null in the list would match rows whose value is null, which no index holds, so that too
    /// stays a filter.
    /// </summary>
    private static Comparison? TryReadMembership(MethodCallExpression call, ParameterExpression parameter)
    {
        if (call.Method.Name != nameof(Enumerable.Contains))
        {
            return null;
        }
        Expression source, item;
        if (call.Object is null && call.Arguments.Count == 2)
        {
            (source, item) = (call.Arguments[0], call.Arguments[1]);
        }
        else if (call.Object is not null && call.Arguments.Count == 1)
        {
            (source, item) = (call.Object, call.Arguments[0]);
        }
        else
        {
            return null;
        }

        if (!TryMember(item, parameter, out var member, out var memberType) ||
            !ExpressionEvaluator.TryEvaluate(source, out var evaluated) ||
            evaluated is not System.Collections.IEnumerable values || evaluated is string ||
            UsesCustomComparer(evaluated))
        {
            return null;
        }

        var keys = new List<object>();
        var seen = new HashSet<object>();
        foreach (var value in values)
        {
            if (!KeyCoercion.TryCoerce(value, memberType, out var key))
            {
                return null;
            }
            if (seen.Add(key))
            {
                keys.Add(key);
            }
        }
        return new Comparison(member, ExpressionType.Call, null, keys);
    }

    /// <summary>
    /// True when the collection carries a comparer other than the default one for its element type -
    /// a HashSet with an ignore-case comparer, a SortedSet with a custom ordering - so its Contains
    /// answers a different question than an index lookup on the raw value would.
    /// </summary>
    private static bool UsesCustomComparer(object collection)
    {
        var comparerProperty = collection.GetType().GetProperty("Comparer");
        if (comparerProperty?.GetValue(collection) is not { } comparer)
        {
            return false;
        }
        var elementType = collection.GetType().GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            ?.GetGenericArguments()[0];
        if (elementType is null)
        {
            return true;
        }
        var defaultEquality = typeof(EqualityComparer<>).MakeGenericType(elementType).GetProperty("Default")!.GetValue(null);
        var defaultOrder = typeof(Comparer<>).MakeGenericType(elementType).GetProperty("Default")!.GetValue(null);
        return !Equals(comparer, defaultEquality) && !Equals(comparer, defaultOrder);
    }

    private static bool TryMember(Expression expression, ParameterExpression parameter, out string member, out Type memberType)
    {
        var current = StripQuotes(expression);
        while (current is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
        {
            current = convert.Operand;
        }
        if (current is MemberExpression access && access.Expression == parameter)
        {
            member = access.Member.Name;
            memberType = access.Type;
            return true;
        }
        member = string.Empty;
        memberType = typeof(object);
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
