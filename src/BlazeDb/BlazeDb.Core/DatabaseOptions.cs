using BlazeDb.Storage;

namespace BlazeDb;

public sealed class DatabaseOptions
{
    /// <summary>
    /// Storage backend for durability. When null the database is purely in-memory (useful for
    /// tests and caches); all data is lost when the instance goes away.
    /// </summary>
    public IStorage? Storage { get; set; }

    /// <summary>
    /// How often the background flusher writes buffered WAL commits to storage.
    /// Committed data lives in memory immediately; this is only the durability lag.
    /// </summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// When the WAL file grows beyond this size, the flusher writes a full snapshot and starts
    /// a fresh WAL (checkpoint/compaction).
    /// </summary>
    public long CheckpointWalSize { get; set; } = 4 * 1024 * 1024;

    /// <summary>
    /// Headroom to leave free in the origin's storage allowance. The engine refuses to flush once
    /// a write would eat into this margin, so there is room left for the browser and for the
    /// snapshot a later checkpoint has to write. Only consulted when the backend implements
    /// <see cref="IQuotaAwareStorage"/>.
    /// </summary>
    public long QuotaReserveBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>
    /// Invoked when a flush is refused because storage is nearly full, after the engine has
    /// already tried compacting. The application can use it to prompt the user, prune data, or
    /// request persistent storage. Called from the background flusher, so keep it quick and
    /// marshal to the UI thread yourself if needed.
    /// </summary>
    public Action<StorageQuota>? OnQuotaPressure { get; set; }

    /// <summary>
    /// Opens the database as a read-only replica: state is recovered from storage as usual, but
    /// nothing is ever written back and any attempt to mutate a table throws. This is what a
    /// second browser tab uses — only one tab may hold the writer lock, and the others follow
    /// along by reloading (see <see cref="Database.ReloadAsync"/>).
    /// </summary>
    public bool ReadOnly { get; set; }

    /// <summary>
    /// Invoked after a checkpoint has been made durable, with the new generation number. A writer
    /// tab uses this to tell replicas that a fresh snapshot is available to reload from.
    /// </summary>
    public Action<ulong>? OnCheckpoint { get; set; }

    public List<TableDescriptor> Tables { get; } = [];

    public DatabaseOptions AddTable(TableDescriptor descriptor)
    {
        Tables.Add(descriptor);
        return this;
    }
}
