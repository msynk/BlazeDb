using Microsoft.EntityFrameworkCore.Storage;

namespace BlazeDb.EntityFrameworkCore.Storage;

internal sealed class BlazeDbEfTransaction : IDbContextTransaction
{
    private readonly BlazeDbTransaction _transaction;
    private readonly Action _onDisposed;
    private bool _completed;

    public BlazeDbEfTransaction(BlazeDbTransaction transaction, Action onDisposed)
    {
        _transaction = transaction;
        _onDisposed = onDisposed;
        TransactionId = Guid.NewGuid();
    }

    public Guid TransactionId { get; }

    public void Commit()
    {
        _transaction.Commit();
        _completed = true;
    }

    public Task CommitAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }
        Commit();
        return Task.CompletedTask;
    }

    public void Rollback()
    {
        _transaction.Rollback();
        _completed = true;
    }

    public Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }
        Rollback();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (!_completed)
        {
            _transaction.Dispose();
        }
        _onDisposed();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
