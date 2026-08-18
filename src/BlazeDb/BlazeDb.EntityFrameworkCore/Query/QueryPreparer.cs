using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace BlazeDb.EntityFrameworkCore.Query;

/// <summary>
/// Cleans a query expression up before translation: reads off the tracking operators and removes
/// them, and rejects the ones that describe a shape the engine has no concept of.
/// </summary>
internal static class QueryPreparer
{
    public static Expression Prepare(Expression query, out bool? tracking)
    {
        var stripper = new TrackingStripper();
        var prepared = stripper.Visit(query)!;
        tracking = stripper.Tracking;
        return prepared;
    }

    public static EntityQueryRootExpression FindRoot(Expression query)
    {
        var current = query;
        while (current is MethodCallExpression { Arguments.Count: > 0 } call)
        {
            current = call.Arguments[0];
        }
        return current as EntityQueryRootExpression
            ?? throw new NotSupportedException(
                "BlazeDb can only run queries that start from a DbSet. Queries spanning several " +
                "sets, or rooted at a local collection, are not supported.");
    }

    private sealed class TrackingStripper : ExpressionVisitor
    {
        public bool? Tracking { get; private set; }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.DeclaringType == typeof(EntityFrameworkQueryableExtensions))
            {
                switch (node.Method.Name)
                {
                    case nameof(EntityFrameworkQueryableExtensions.AsTracking):
                        Tracking = true;
                        return Visit(node.Arguments[0]);
                    case nameof(EntityFrameworkQueryableExtensions.AsNoTracking):
                    case nameof(EntityFrameworkQueryableExtensions.AsNoTrackingWithIdentityResolution):
                        Tracking = false;
                        return Visit(node.Arguments[0]);
                    case nameof(EntityFrameworkQueryableExtensions.Include):
                    case nameof(EntityFrameworkQueryableExtensions.ThenInclude):
                        throw new NotSupportedException(
                            "BlazeDb rows have no navigations, so Include has nothing to load. " +
                            "Model relationships as key properties and query the other table directly.");
                    case nameof(EntityFrameworkQueryableExtensions.IgnoreQueryFilters):
                    case nameof(EntityFrameworkQueryableExtensions.TagWith):
                        return Visit(node.Arguments[0]);
                }
            }
            return base.VisitMethodCall(node);
        }
    }
}
