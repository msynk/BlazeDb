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
internal static class BlazeDbIndexSelector
{
    public static BlazeDbIndexSelection Choose(
        BlazeDbQueryPlan plan,
        IBlazeDbTableBinding binding,
        List<LambdaExpression> predicates,
        List<MethodCallExpression> orderings,
        bool useSecondaryIndexes = true)
    {
        if (predicates.Count == 0 && orderings.Count == 0)
        {
            return new BlazeDbIndexSelection([], OrderingSatisfied: false);
        }

        var conjuncts = BlazeDbConjuncts.Flatten(predicates, out var parameter);
        var comparisons = conjuncts
            .Select((c, i) => (Index: i, Comparison: BlazeDbComparison.TryRead(c, parameter)))
            .Where(x => x.Comparison is not null)
            .ToList();

        var used = new HashSet<int>();
        string? rangeMember = null;

        if (!TryPrimaryKey(plan, binding, comparisons, used) && useSecondaryIndexes &&
            !TryCompoundEquality(plan, binding, comparisons, used) &&
            !TrySingleEquality(plan, binding, comparisons, used))
        {
            rangeMember = TryRange(plan, binding, comparisons, used);
        }

        var orderingSatisfied = useSecondaryIndexes && TrySatisfyOrdering(plan, binding, orderings, rangeMember);

        return new BlazeDbIndexSelection(BlazeDbConjuncts.Rebuild(conjuncts, used, parameter, predicates), orderingSatisfied);
    }

    private static bool TryPrimaryKey(
        BlazeDbQueryPlan plan, IBlazeDbTableBinding binding, List<(int Index, BlazeDbComparison? Comparison)> comparisons,
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
        BlazeDbQueryPlan plan, IBlazeDbTableBinding binding, List<(int Index, BlazeDbComparison? Comparison)> comparisons,
        HashSet<int> used)
    {
        foreach (var index in binding.Indexes.Where(i => i.Members.Count > 1).OrderByDescending(i => i.Members.Count))
        {
            var matched = new List<(int Index, BlazeDbComparison Comparison)>();
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
        BlazeDbQueryPlan plan, IBlazeDbTableBinding binding, List<(int Index, BlazeDbComparison? Comparison)> comparisons,
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
        BlazeDbQueryPlan plan, IBlazeDbTableBinding binding, List<(int Index, BlazeDbComparison? Comparison)> comparisons,
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
        BlazeDbQueryPlan plan, IBlazeDbTableBinding binding, List<MethodCallExpression> orderings, string? rangeMember)
    {
        // Only a single OrderBy can be answered by an index: a ThenBy would need the index to be
        // compound over exactly the same members, which the ordering syntax cannot express here.
        if (orderings.Count != 1)
        {
            return false;
        }

        var call = orderings[0];
        var selector = BlazeDbComparison.StripQuotes(call.Arguments[1]) as LambdaExpression;
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
