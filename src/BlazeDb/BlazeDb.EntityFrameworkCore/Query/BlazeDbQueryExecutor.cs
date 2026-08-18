using System.Collections;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using BlazeDb.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;

namespace BlazeDb.EntityFrameworkCore.Query;

/// <summary>
/// Runs a LINQ query against the engine.
///
/// A query is translated as far as the engine's plan model reaches - index source, filters,
/// ordering, paging - and whatever is left runs as LINQ to Objects over the rows that come back.
/// Nothing is materialized on the way: the objects the query yields are the ones the table holds,
/// so a tracked entity mutated afterwards has already changed the database in memory, and
/// <c>SaveChanges</c> is what commits that change to the log.
/// </summary>
internal sealed class BlazeDbQueryExecutor
{
    private readonly IBlazeDbTableCache _tables;

    public BlazeDbQueryExecutor(IBlazeDbTableCache tables) => _tables = tables;

    public object? Execute(Expression query, DbContext context)
    {
        query = QueryPreparer.Prepare(query, out var trackingOverride);

        var root = QueryPreparer.FindRoot(query);
        var entityType = root.EntityType;
        var binding = _tables.GetBinding(entityType);

        var translated = QueryTranslator.Translate(query, binding, entityType.ClrType);
        var rows = binding.Execute(translated.Plan);

        if (ShouldTrack(query, entityType, context, trackingOverride))
        {
            rows = Track(rows, context);
        }

        var queryable = binding.AsQueryable(rows);
        if (translated.Remainder is null)
        {
            return queryable;
        }

        var source = Expression.Constant(queryable, typeof(IQueryable<>).MakeGenericType(binding.RowType));
        var remainder = new RowsPlaceholderReplacer(source).Visit(translated.Remainder)!;

        // A remainder that still describes a sequence has to be handed back as a query rather than
        // run: LINQ to Objects only evaluates the operators that reduce one to a value.
        return typeof(IQueryable).IsAssignableFrom(remainder.Type)
            ? queryable.Provider.CreateQuery(remainder)
            : queryable.Provider.Execute(remainder);
    }

    /// <summary>
    /// Streams the same result, handing control back to the browser periodically. WebAssembly has
    /// one thread, so a long scan on it would freeze rendering until it finished.
    /// </summary>
    public async IAsyncEnumerable<T> ExecuteAsync<T>(
        Expression query, DbContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var result = Execute(query, context);
        if (result is not IEnumerable enumerable)
        {
            yield return (T)result!;
            yield break;
        }

        var sinceYield = 0;
        foreach (var item in enumerable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return (T)item;
            if (++sinceYield == YieldEvery)
            {
                sinceYield = 0;
                await Task.Yield();
            }
        }
    }

    private const int YieldEvery = 512;

    /// <summary>
    /// Entities are tracked only when the query actually returns them. A projection, a count or an
    /// <c>Any</c> yields something else, and tracking the rows it happened to read would put
    /// entities in the change tracker the caller never asked for.
    /// </summary>
    private static bool ShouldTrack(Expression query, IEntityType entityType, DbContext context, bool? trackingOverride)
    {
        if (trackingOverride == false)
        {
            return false;
        }
        if (trackingOverride != true &&
            context.ChangeTracker.QueryTrackingBehavior == QueryTrackingBehavior.NoTracking)
        {
            return false;
        }
        return ResultElementType(query.Type) == entityType.ClrType;
    }

    private static Type ResultElementType(Type type)
    {
        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            if (definition == typeof(IQueryable<>) || definition == typeof(IEnumerable<>) ||
                definition == typeof(IOrderedQueryable<>) || definition == typeof(IAsyncEnumerable<>))
            {
                return type.GetGenericArguments()[0];
            }
        }
        return type;
    }

    private static IEnumerable<object> Track(IEnumerable<object> rows, DbContext context)
    {
        foreach (var row in rows)
        {
            var entry = context.Entry(row);
            if (entry.State == EntityState.Detached)
            {
                // Unchanged, not Added: the row is already in the table, and this snapshots its
                // current values as the originals a later SaveChanges compares against.
                entry.State = EntityState.Unchanged;
            }
            yield return row;
        }
    }
}
