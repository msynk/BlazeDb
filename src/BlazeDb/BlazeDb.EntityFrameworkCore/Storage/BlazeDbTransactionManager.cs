using Microsoft.EntityFrameworkCore.Storage;

namespace BlazeDb.EntityFrameworkCore.Storage;

/// <summary>
/// BlazeDb has one ambient transaction at a time, opened at <c>SaveChanges</c>. An explicit
/// <c>BeginTransaction</c> spanning several saves is not supported, matching the engine's
/// single-writer, no-nesting model.
/// </summary>
public sealed class BlazeDbTransactionManager : IDbContextTransactionManager
{
    public IDbContextTransaction? CurrentTransaction => null;

    public IDbContextTransaction BeginTransaction() => throw NotSupported();

    public Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
        throw NotSupported();

    public void CommitTransaction() => throw NotSupported();

    public Task CommitTransactionAsync(CancellationToken cancellationToken = default) => throw NotSupported();

    public void RollbackTransaction() => throw NotSupported();

    public Task RollbackTransactionAsync(CancellationToken cancellationToken = default) => throw NotSupported();

    public void ResetState()
    {
    }

    public Task ResetStateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    private static NotSupportedException NotSupported() =>
        new("BlazeDb does not support transactions spanning several SaveChanges calls. " +
            "Each SaveChanges is already atomic: it commits as one engine transaction and one WAL record.");
}
