using BlazeDb.Serialization;

namespace BlazeDb;

/// <summary>
/// A single buffered write operation: applied to memory immediately, revertible on rollback,
/// and encodable into a WAL commit record.
/// </summary>
internal interface ITxnOp
{
    void Apply();

    void Revert();

    void EncodeRedo(BufferWriter payload, BufferWriter scratch);
}

/// <summary>Op codes used in WAL commit payloads.</summary>
internal static class WalOp
{
    public const byte Set = 1;
    public const byte Delete = 2;
}

/// <summary>
/// A single-writer transaction. Writes are applied to memory immediately (so reads inside the
/// transaction see them) and recorded for undo. <see cref="Commit"/> makes the batch atomic in
/// the WAL; <see cref="Rollback"/> (or disposing without committing) reverts all writes.
/// While a transaction is active, all table writes on the database — from any call site —
/// become part of it (ambient transaction semantics).
/// </summary>
public sealed class Transaction : IDisposable
{
    private readonly Database _db;
    private readonly List<ITxnOp> _ops = [];
    private bool _completed;

    internal Transaction(Database db) => _db = db;

    internal IReadOnlyList<ITxnOp> Ops => _ops;

    internal void Record(ITxnOp op) => _ops.Add(op);

    public void Commit()
    {
        EnsureActive();
        _db.CommitTransaction(this);
        _completed = true;
    }

    public void Rollback()
    {
        EnsureActive();
        _db.RollbackTransaction(this);
        _completed = true;
    }

    public void Dispose()
    {
        if (!_completed)
        {
            Rollback();
        }
    }

    private void EnsureActive()
    {
        if (_completed)
        {
            throw new InvalidOperationException("Transaction has already been committed or rolled back.");
        }
    }
}
