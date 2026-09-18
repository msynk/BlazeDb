using BlazeDb.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlazeDb.EntityFrameworkCore.Storage;

/// <summary>
/// Maps <c>Database.BeginTransaction</c> onto the engine's single ambient transaction. Saves made
/// inside that scope join it rather than opening a nested one.
/// </summary>
internal sealed class BlazeDbTransactionManager : IDbContextTransactionManager
{
    private readonly IBlazeDbTableCache _tables;
    private BlazeDbEfTransaction? _current;

    public BlazeDbTransactionManager(IBlazeDbTableCache tables) => _tables = tables;

    public IDbContextTransaction? CurrentTransaction => _current;

    public IDbContextTransaction BeginTransaction()
    {
        if (_current is not null)
        {
            throw new InvalidOperationException("A transaction is already in progress on this context.");
        }

        var transaction = _tables.Database.BeginTransaction();
        _current = new BlazeDbEfTransaction(transaction, () => _current = null);
        return _current;
    }

    public Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<IDbContextTransaction>(cancellationToken);
        }
        return Task.FromResult(BeginTransaction());
    }

    public void CommitTransaction() => CurrentOrThrow().Commit();

    public Task CommitTransactionAsync(CancellationToken cancellationToken = default) =>
        CurrentOrThrow().CommitAsync(cancellationToken);

    public void RollbackTransaction() => CurrentOrThrow().Rollback();

    public Task RollbackTransactionAsync(CancellationToken cancellationToken = default) =>
        CurrentOrThrow().RollbackAsync(cancellationToken);

    public void ResetState()
    {
        _current?.Dispose();
        _current = null;
    }

    public Task ResetStateAsync(CancellationToken cancellationToken = default)
    {
        ResetState();
        return Task.CompletedTask;
    }

    private BlazeDbEfTransaction CurrentOrThrow() =>
        _current ?? throw new InvalidOperationException("No transaction is in progress on this context.");
}
