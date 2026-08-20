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

    public BlazeDbEfCoreDatabase(IBlazeDbTableCache tables) => _tables = tables;

    public int SaveChanges(IList<IUpdateEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return 0;
        }

        using var transaction = _tables.Database.BeginTransaction();
        var count = 0;

        foreach (var entry in entries)
        {
            var binding = _tables.GetBinding(entry.EntityType);
            var entity = entry.ToEntityEntry().Entity;
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
            count++;
        }

        transaction.Commit();
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
