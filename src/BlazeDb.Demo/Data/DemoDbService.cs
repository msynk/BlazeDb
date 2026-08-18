using System.Runtime.Versioning;
using BlazeDb;
using BlazeDb.Browser;
using BlazeDb.Storage;

namespace BlazeDb.Demo.Data;

public enum StorageMode
{
    Opening,
    Opfs,
    LockedFallback,
    UnavailableFallback,
}

/// <summary>
/// Opens the demo's primary database once per app lifetime.
///
/// Storage is OPFS (Origin Private File System) when the browser allows it and
/// this tab wins the Web Locks election. Otherwise the demo degrades to
/// <see cref="InMemoryStorage"/> so every page still works — just without
/// durability across reloads.
/// </summary>
[SupportedOSPlatform("browser")]
public sealed class DemoDbService : IAsyncDisposable
{
    public const string DatabaseName = "blazedb-demo";

    private Task<Database>? _opening;
    private Database? _db;
    private OpfsStorage? _opfs;

    public StorageMode Mode { get; private set; } = StorageMode.Opening;

    /// <summary>Set when OPFS could not be used, so the UI can explain why.</summary>
    public string? StorageNote { get; private set; }

    /// <summary>Records every <see cref="IStorage"/> call the engine makes.</summary>
    public InstrumentedStorage? Trace { get; private set; }

    public bool IsOpen => _db is not null;

    public bool IsPersistent => Mode == StorageMode.Opfs;

    public string StorageLabel => Mode switch
    {
        StorageMode.Opfs => "OPFS",
        StorageMode.LockedFallback => "In-memory (another tab owns OPFS)",
        StorageMode.UnavailableFallback => "In-memory (OPFS unavailable)",
        _ => "Opening…",
    };

    public Database Db => _db ?? throw new InvalidOperationException("The database is not open yet.");

    public Table<Guid, TodoEntry> Todos => Db.GetTable(TodoEntry.Table);

    public Table<string, Setting> Settings => Db.GetTable(Setting.Table);

    /// <summary>Idempotent and safe to call concurrently from several components.</summary>
    public Task<Database> GetDatabaseAsync() => _opening ??= OpenAsync();

    private async Task<Database> OpenAsync()
    {
        IStorage backend;

        try
        {
            _opfs = await OpfsStorage.CreateAsync(DatabaseName);
            backend = _opfs;
            Mode = StorageMode.Opfs;
        }
        catch (DatabaseLockedException ex)
        {
            Mode = StorageMode.LockedFallback;
            StorageNote = ex.Message;
            backend = new InMemoryStorage();
        }
        catch (Exception ex)
        {
            Mode = StorageMode.UnavailableFallback;
            StorageNote = ex.Message;
            backend = new InMemoryStorage();
        }

        Trace = new InstrumentedStorage(backend);

        try
        {
            _db = await DemoSandbox.OpenAsync(
                Trace,
                flushInterval: TimeSpan.FromMilliseconds(100),
                checkpointWalSize: 1 * 1024 * 1024);
        }
        catch (CorruptDatabaseException ex)
        {
            // Persisted data from an older or interrupted session cannot be read.
            // Rather than dead-ending the demo, start clean in memory.
            Mode = StorageMode.UnavailableFallback;
            StorageNote = $"Persisted data could not be recovered ({ex.Message}). Started a fresh in-memory database.";
            _opfs?.Dispose();
            _opfs = null;
            Trace = new InstrumentedStorage(new InMemoryStorage());
            _db = await DemoSandbox.OpenAsync(
                Trace,
                flushInterval: TimeSpan.FromMilliseconds(100),
                checkpointWalSize: 1 * 1024 * 1024);
        }

        var todos = _db.GetTable(TodoEntry.Table);
        if (todos.Count == 0)
        {
            DemoData.Seed(_db, todos);
            await _db.FlushAsync();
        }

        return _db;
    }

    public string GetSetting(string name, string fallback) =>
        Settings.Get(name)?.Value ?? fallback;

    public void SetSetting(string name, string value) =>
        Settings.Upsert(new Setting { Name = name, Value = value, UpdatedAt = DateTime.UtcNow });

    public async ValueTask DisposeAsync()
    {
        if (_db is not null)
        {
            await _db.DisposeAsync();
            _db = null;
        }

        _opfs?.Dispose();
        _opfs = null;
    }
}
