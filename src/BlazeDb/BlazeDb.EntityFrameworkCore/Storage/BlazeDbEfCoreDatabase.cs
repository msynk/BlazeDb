using System.Linq.Expressions;
using BlazeDb.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update;

namespace BlazeDb.EntityFrameworkCore.Storage;

/// <summary>
/// Applies a <c>SaveChanges</c> to the engine. Every entry goes into one BlazeDb transaction, so
/// the whole save either lands in memory and in the write-ahead log or leaves both untouched.
///
/// Rows are the live objects the queries handed out, which makes an update a subtlety rather than
/// a copy: by the time <c>SaveChanges</c> runs, the row inside the table has already been mutated
/// and its index entries describe values that no longer exist on it. The change tracker still
/// holds the original values, so the provider rebuilds them and hands both versions to the engine,
/// which retracts the old index entries before adding the new ones.
/// </summary>
internal sealed class BlazeDbEfCoreDatabase : IDatabase
{
    private readonly IBlazeDbTableCache _tables;
    private readonly IDbContextTransactionManager _transactions;

    public BlazeDbEfCoreDatabase(IBlazeDbTableCache tables, IDbContextTransactionManager transactions)
    {
        _tables = tables;
        _transactions = transactions;
    }

    public int SaveChanges(IList<IUpdateEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return 0;
        }

        // The engine's transactions are ambient - while one is open every table write on the
        // database joins it - so a save made inside a transaction this context opened, or one the
        // application opened on the engine directly, becomes part of it instead of failing as a
        // nested one. It is still atomic; the boundary is just the outer scope's, which is what the
        // caller asked for. A transaction another context opened is a different matter: joining it
        // would report this save as done while leaving its fate to that context's commit or
        // rollback, with this tracker none the wiser. That is refused.
        var database = _tables.Database;
        var joins = database.HasActiveTransaction;
        if (joins && database.ActiveTransactionOwner is { } owner && !ReferenceEquals(owner, _transactions))
        {
            throw new InvalidOperationException(
                "Another DbContext on the same BlazeDb store has a transaction open. A save cannot join " +
                "it: the engine allows one transaction at a time, so commit or dispose that one first.");
        }

        using var transaction = joins ? null : database.BeginTransaction();
        var count = 0;

        foreach (var entry in entries)
        {
            var binding = _tables.GetBinding(entry.EntityType);
            var entity = entry.ToEntityEntry().Entity;
            try
            {
                switch (entry.EntityState)
                {
                    case EntityState.Added:
                        binding.Insert(entity);
                        break;
                    case EntityState.Modified:
                        binding.UpdateInPlace(entity, BlazeDbOriginalValueFactory.Create(entry));
                        break;
                    case EntityState.Deleted:
                        binding.Delete(entity, BlazeDbOriginalValueFactory.Create(entry));
                        break;
                    default:
                        continue;
                }
            }
            catch (BlazeDbStaleRowException ex)
            {
                // The originals this tracker holds describe a state the row has moved past - another
                // context saved it in between - which is what a concurrency exception is for.
                throw new DbUpdateConcurrencyException(
                    "The row was changed by another context after this one loaded it, so its original " +
                    "values no longer describe the database. Reload the entity and apply the change again.",
                    ex, [entry]);
            }
            count++;
        }

        transaction?.Commit();
        return count;
    }

    /// <summary>
    /// Saves synchronously and returns a completed task. The engine's writes are memory operations;
    /// persistence is the write-ahead log's job and happens on its own schedule, so there is
    /// nothing here to await. Call <c>BlazeDbDatabase.FlushAsync</c> for a hard durability point.
    /// </summary>
    public Task<int> SaveChangesAsync(IList<IUpdateEntry> entries, CancellationToken cancellationToken = default) =>
        cancellationToken.IsCancellationRequested
            ? Task.FromCanceled<int>(cancellationToken)
            : Task.FromResult(SaveChanges(entries));

    public Func<QueryContext, TResult> CompileQuery<TResult>(Expression query, bool async) => throw NoPipeline();

    public Expression<Func<QueryContext, TResult>> CompileQueryExpression<TResult>(Expression query, bool async) =>
        throw NoPipeline();

    private static InvalidOperationException NoPipeline() =>
        new("BlazeDb executes queries through its own compiler and never builds an EF query pipeline. " +
            "Reaching this method means the provider's IQueryCompiler was replaced.");
}
