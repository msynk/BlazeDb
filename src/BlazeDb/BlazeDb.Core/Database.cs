using BlazeDb.Storage;
using BlazeDb.Wal;

namespace BlazeDb;

/// <summary>
/// A memory-first database instance. All live data resides in managed memory; reads never touch
/// storage. Writes are applied to memory synchronously and (when a storage backend is
/// configured) appended to a write-ahead log flushed in the background.
/// The engine assumes a single logical writer (Blazor WASM's single-threaded model); mutations
/// and snapshot serialization are briefly locked so the background flusher stays consistent on
/// multi-threaded hosts.
/// </summary>
public sealed class Database : IAsyncDisposable
{
    private readonly Dictionary<string, ITableInternal> _tablesByName = new();
    private readonly Dictionary<TableDescriptor, ITableInternal> _tablesByDescriptor = new();
    private readonly List<ITableInternal> _tableList = [];
    private readonly ITxnOp[] _singleOpScratch = new ITxnOp[1];
    private Transaction? _activeTransaction;
    private WalManager? _wal;
    private long _nextSeq;
    private bool _disposed;

    internal readonly object SyncRoot = new();

    private Database(DatabaseOptions options)
    {
        foreach (var descriptor in options.Tables)
        {
            var table = descriptor.CreateTable(this);
            if (!_tablesByName.TryAdd(table.Name, table))
            {
                throw new BlazeDbException($"Duplicate table name '{table.Name}'.");
            }
            _tablesByDescriptor.Add(descriptor, table);
            _tableList.Add(table);
        }
    }

    /// <summary>
    /// Opens a database: creates tables, recovers persisted state (snapshot + WAL replay) when
    /// a storage backend is configured, and starts the background flusher.
    /// </summary>
    public static async ValueTask<Database> OpenAsync(DatabaseOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var db = new Database(options) { IsReadOnly = options.ReadOnly };
        if (options.Storage is not null)
        {
            db._wal = await WalManager.OpenAsync(db, options, cancellationToken).ConfigureAwait(false);
        }
        return db;
    }

    /// <summary>True when this instance is a read-only replica and refuses all mutations.</summary>
    public bool IsReadOnly { get; private init; }

    /// <summary>
    /// Discards the in-memory state and rebuilds it from storage (snapshot plus WAL replay),
    /// picking up everything another tab has flushed. Intended for read-only replicas; the
    /// writer's own memory is already the newest copy. Existing row references are not updated -
    /// re-query after reloading.
    /// </summary>
    public async ValueTask ReloadAsync(CancellationToken cancellationToken = default)
    {
        CheckDisposed();
        if (!IsReadOnly)
        {
            // The writer's memory is the newest state there is; rebuilding it from storage would
            // silently drop every commit the flusher has not written yet.
            throw new InvalidOperationException(
                "ReloadAsync is for read-only replicas. This database holds the writer's state, " +
                "which is already newer than anything on storage.");
        }
        if (_wal is null)
        {
            return;
        }
        if (_activeTransaction is not null)
        {
            throw new InvalidOperationException("Cannot reload while a transaction is active.");
        }
        await _wal.ReloadAsync(cancellationToken).ConfigureAwait(false);
    }

    public Table<TKey, TRow> GetTable<TKey, TRow>(TableDescriptor<TKey, TRow> descriptor)
        where TKey : notnull
    {
        if (_tablesByDescriptor.TryGetValue(descriptor, out var table))
        {
            return (Table<TKey, TRow>)table;
        }
        throw new BlazeDbException($"Table '{descriptor.Name}' was not registered in DatabaseOptions.");
    }

    /// <summary>
    /// Starts an explicit transaction. Only one can be active at a time (single-writer model);
    /// while active, every table write on this database becomes part of it.
    /// </summary>
    public Transaction BeginTransaction()
    {
        CheckDisposed();
        CheckWritable();
        if (_activeTransaction is not null)
        {
            throw new InvalidOperationException("A transaction is already active; nested transactions are not supported.");
        }
        return _activeTransaction = new Transaction(this);
    }

    /// <summary>
    /// Forces all buffered WAL commits to storage now (hard durability point). Without an
    /// explicit flush, durability lags commits by at most <see cref="DatabaseOptions.FlushInterval"/>.
    /// </summary>
    public ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        CheckDisposed();
        return _wal?.FlushAsync(cancellationToken) ?? default;
    }

    /// <summary>
    /// The error the most recent background flush or checkpoint failed with, or null when the last
    /// one succeeded (or no storage is configured). Committed data is safe in memory and the flusher
    /// keeps retrying; this exists so an application can show that durability is lagging - for
    /// example after a quota refusal - without waiting for an explicit <see cref="FlushAsync"/>.
    /// </summary>
    public Exception? LastBackgroundError => _wal?.LastBackgroundError;

    /// <summary>
    /// How much of the origin's storage allowance is in use, or null when the backend cannot
    /// report it (everything except the browser backends). Useful for showing headroom in a UI
    /// before the engine has to refuse a flush.
    /// </summary>
    public ValueTask<StorageQuota?> GetStorageQuotaAsync(CancellationToken cancellationToken = default)
    {
        CheckDisposed();
        return _wal?.GetQuotaAsync(cancellationToken) ?? default;
    }

    /// <summary>Writes a full snapshot and truncates the WAL (compaction).</summary>
    public async ValueTask CheckpointAsync(CancellationToken cancellationToken = default)
    {
        CheckDisposed();
        if (_wal is not null)
        {
            await _wal.CheckpointAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_wal is not null)
        {
            await _wal.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ---- Internals used by tables, transactions and the WAL manager ----

    internal bool HasActiveTransaction => _activeTransaction is not null;

    internal List<ITableInternal> TableList => _tableList;

    internal ITableInternal GetTableByName(string name) =>
        _tablesByName.TryGetValue(name, out var table)
            ? table
            : throw new BlazeDbException(
                $"Persisted data references table '{name}', which is not registered in DatabaseOptions.");

    internal long NextSeq() => ++_nextSeq;

    /// <summary>Clears every table, for a replica about to rebuild from a newer snapshot.</summary>
    internal void ResetTables()
    {
        lock (SyncRoot)
        {
            foreach (var table in _tableList)
            {
                table.Clear();
            }
            _nextSeq = 0;
        }
    }

    /// <summary>
    /// The pre-flight checks every write makes, exposed so a table can run them before it touches
    /// its own state on the paths that have to adjust the row map ahead of the write itself.
    /// </summary>
    internal void EnsureWritable()
    {
        CheckDisposed();
        CheckWritable();
    }

    internal void ApplyWrite(ITxnOp op)
    {
        EnsureWritable();
        lock (SyncRoot)
        {
            op.Apply();
            if (_activeTransaction is not null)
            {
                _activeTransaction.Record(op);
            }
            else if (_wal is not null)
            {
                _singleOpScratch[0] = op;
                try
                {
                    _wal.AppendCommit(_singleOpScratch);
                }
                catch
                {
                    // Memory must never run ahead of what the log will replay: an autocommit
                    // write that could not be journaled is undone rather than left in place.
                    op.Revert();
                    throw;
                }
                finally
                {
                    _singleOpScratch[0] = null!;
                }
            }
        }
    }

    internal void CommitTransaction(Transaction transaction)
    {
        VerifyActive(transaction);
        lock (SyncRoot)
        {
            if (transaction.Ops.Count > 0)
            {
                _wal?.AppendCommit(transaction.Ops);
            }
            _activeTransaction = null;
        }
    }

    internal void RollbackTransaction(Transaction transaction)
    {
        VerifyActive(transaction);
        lock (SyncRoot)
        {
            var ops = transaction.Ops;
            for (var i = ops.Count - 1; i >= 0; i--)
            {
                ops[i].Revert();
            }
            _activeTransaction = null;
        }
    }

    private void VerifyActive(Transaction transaction)
    {
        if (!ReferenceEquals(_activeTransaction, transaction))
        {
            throw new InvalidOperationException("Transaction is not the active transaction of this database.");
        }
    }

    private void CheckDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private void CheckWritable()
    {
        if (IsReadOnly)
        {
            throw new InvalidOperationException(
                "This database was opened as a read-only replica. Writes belong to the tab holding " +
                "the writer lock; call ReloadAsync to pick up its changes.");
        }
    }
}
