using BlazeDb;
using BlazeDb.Storage;
using Xunit;

namespace BlazeDb.Tests;

/// <summary>An in-memory backend with a settable allowance, standing in for the browser's estimate.</summary>
internal sealed class QuotaLimitedStorage : IQuotaAwareStorage
{
    private readonly InMemoryStorage _inner = new();
    private readonly Dictionary<string, int> _sizes = [];

    public long QuotaBytes { get; set; } = long.MaxValue;

    public int EstimateCalls { get; private set; }

    public long UsageBytes
    {
        get
        {
            lock (_sizes)
            {
                return _sizes.Values.Sum(static size => (long)size);
            }
        }
    }

    public ValueTask<StorageQuota?> GetQuotaAsync(CancellationToken cancellationToken = default)
    {
        EstimateCalls++;
        return new ValueTask<StorageQuota?>(new StorageQuota(UsageBytes, QuotaBytes));
    }

    public ValueTask<byte[]?> ReadAsync(string name, CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(name, cancellationToken);

    public async ValueTask WriteAtomicAsync(string name, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        lock (_sizes)
        {
            EnsureRoom(data.Length - _sizes.GetValueOrDefault(name));
        }
        await _inner.WriteAtomicAsync(name, data, cancellationToken);
        lock (_sizes)
        {
            _sizes[name] = data.Length;
        }
    }

    public async ValueTask AppendAsync(string name, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        lock (_sizes)
        {
            EnsureRoom(data.Length);
        }
        await _inner.AppendAsync(name, data, cancellationToken);
        lock (_sizes)
        {
            _sizes[name] = _sizes.GetValueOrDefault(name) + data.Length;
        }
    }

    /// <summary>Mimics the browser rejecting a write with QuotaExceededError.</summary>
    private void EnsureRoom(long extraBytes)
    {
        if (UsageBytes + extraBytes > QuotaBytes)
        {
            throw new IOException("QuotaExceededError");
        }
    }

    public async ValueTask DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        await _inner.DeleteAsync(name, cancellationToken);
        lock (_sizes)
        {
            _sizes.Remove(name);
        }
    }
}

public class QuotaTests
{
    private static async Task<(Database Db, Table<Guid, TodoItem> Todos)> OpenAsync(
        QuotaLimitedStorage storage, Action<DatabaseOptions>? configure = null)
    {
        var options = new DatabaseOptions
        {
            Storage = storage,
            FlushInterval = TimeSpan.FromHours(1),
            QuotaReserveBytes = 0,
        }.AddTable(TodoItem.Table);
        configure?.Invoke(options);
        var db = await Database.OpenAsync(options);
        return (db, db.GetTable(TodoItem.Table));
    }

    private static void Fill(Table<Guid, TodoItem> todos, int rows)
    {
        for (var i = 0; i < rows; i++)
        {
            todos.Insert(new TodoItem { Id = Guid.NewGuid(), Title = new string('x', 200) });
        }
    }

    [Fact]
    public async Task Quota_Is_Surfaced_Through_The_Database()
    {
        var storage = new QuotaLimitedStorage { QuotaBytes = 10_000 };
        var (db, todos) = await OpenAsync(storage);
        await using var owned = db;
        Fill(todos, 5);
        await db.FlushAsync();

        var quota = await db.GetStorageQuotaAsync();

        Assert.NotNull(quota);
        Assert.Equal(10_000, quota!.Value.QuotaBytes);
        Assert.True(quota.Value.UsageBytes > 0);
        Assert.True(quota.Value.UsedFraction is > 0 and < 1);
    }

    [Fact]
    public async Task Quota_Is_Null_For_Backends_That_Cannot_Report_It()
    {
        var db = await Database.OpenAsync(new DatabaseOptions
        {
            Storage = new InMemoryStorage(),
            FlushInterval = TimeSpan.FromHours(1),
        }.AddTable(TodoItem.Table));
        await using var owned = db;

        Assert.Null(await db.GetStorageQuotaAsync());
    }

    [Fact]
    public async Task Flush_Fails_Deliberately_Instead_Of_Overrunning_The_Quota()
    {
        var storage = new QuotaLimitedStorage { QuotaBytes = 4_000 };
        var (db, todos) = await OpenAsync(storage);
        await using var owned = db;

        Fill(todos, 100);

        var ex = await Assert.ThrowsAsync<StorageQuotaExceededException>(async () => await db.FlushAsync());
        Assert.True(ex.RequiredBytes > 0);
        Assert.Equal(4_000, ex.Quota.QuotaBytes);

        // Disposal flushes one last time; give it room so the test ends on the assertion above
        // rather than on the (correct) failure to persist.
        storage.QuotaBytes = long.MaxValue;
    }

    [Fact]
    public async Task Data_Survives_In_Memory_After_A_Refused_Flush()
    {
        var storage = new QuotaLimitedStorage { QuotaBytes = 4_000 };
        var (db, todos) = await OpenAsync(storage);
        await using var owned = db;
        Fill(todos, 100);

        await Assert.ThrowsAsync<StorageQuotaExceededException>(async () => await db.FlushAsync());

        // The engine degraded rather than failing the application: reads and writes still work.
        Assert.Equal(100, todos.Count);
        todos.Insert(new TodoItem { Id = Guid.NewGuid(), Title = "still writable" });
        Assert.Equal(101, todos.Count);

        storage.QuotaBytes = long.MaxValue;
    }

    [Fact]
    public async Task A_Refused_Flush_Succeeds_Once_Space_Is_Freed()
    {
        var storage = new QuotaLimitedStorage { QuotaBytes = 4_000 };
        var (db, todos) = await OpenAsync(storage);
        await using var owned = db;
        Fill(todos, 100);
        await Assert.ThrowsAsync<StorageQuotaExceededException>(async () => await db.FlushAsync());

        storage.QuotaBytes = 10_000_000;
        await db.FlushAsync();

        // Nothing was dropped on the way: a reopen sees every row.
        await db.DisposeAsync();
        var reopened = await Database.OpenAsync(new DatabaseOptions
        {
            Storage = storage,
            FlushInterval = TimeSpan.FromHours(1),
        }.AddTable(TodoItem.Table));
        await using var owned2 = reopened;
        Assert.Equal(100, reopened.GetTable(TodoItem.Table).Count);
    }

    [Fact]
    public async Task Pressure_Callback_Reports_The_Offending_Quota()
    {
        StorageQuota? reported = null;
        var storage = new QuotaLimitedStorage { QuotaBytes = 4_000 };
        var (db, todos) = await OpenAsync(storage, o => o.OnQuotaPressure = q => reported = q);
        await using var owned = db;
        Fill(todos, 100);

        await Assert.ThrowsAsync<StorageQuotaExceededException>(async () => await db.FlushAsync());

        Assert.NotNull(reported);
        Assert.Equal(4_000, reported!.Value.QuotaBytes);

        storage.QuotaBytes = long.MaxValue;
    }

    [Fact]
    public async Task The_Reserve_Keeps_Headroom_Free()
    {
        var storage = new QuotaLimitedStorage { QuotaBytes = 1_000_000 };
        // Unrelated origin data, which counts toward the browser's estimate just the same.
        await storage.WriteAtomicAsync("other-origin-data", new byte[970_000]);

        var (db, todos) = await OpenAsync(storage, o => o.QuotaReserveBytes = 25_000);
        await using var owned = db;
        Fill(todos, 50);

        // An open transaction rules out compaction, leaving the reserve as the only thing standing
        // between the flush and the limit. The bytes would physically fit; they would just leave
        // less than the reserve behind.
        using var tx = db.BeginTransaction();
        await Assert.ThrowsAsync<StorageQuotaExceededException>(async () => await db.FlushAsync());

        tx.Rollback();
        storage.QuotaBytes = long.MaxValue;
    }

    [Fact]
    public async Task Compaction_Runs_Before_Giving_Up()
    {
        var storage = new QuotaLimitedStorage { QuotaBytes = 100_000 };
        // The reserve is what leaves room to write the compacted snapshot before the old WAL goes.
        var (db, todos) = await OpenAsync(storage, o => o.QuotaReserveBytes = 20_000);
        await using var owned = db;

        // Repeatedly rewriting one row makes the WAL far bigger than the data it represents,
        // so checkpointing reclaims almost all of it.
        var id = Guid.NewGuid();
        for (var i = 0; i < 400; i++)
        {
            todos.Upsert(new TodoItem { Id = id, Title = new string('y', 200) });
            await db.FlushAsync();
        }

        Assert.Equal(1, todos.Count);
        Assert.True(storage.UsageBytes < 100_000, $"usage was {storage.UsageBytes}");

        // Everything is durable: reopening replays what compaction left behind.
        await db.FlushAsync();
        await db.DisposeAsync();
        var reopened = await Database.OpenAsync(new DatabaseOptions
        {
            Storage = storage,
            FlushInterval = TimeSpan.FromHours(1),
        }.AddTable(TodoItem.Table));
        await using var owned2 = reopened;
        Assert.Equal(1, reopened.GetTable(TodoItem.Table).Count);
    }

    [Fact]
    public async Task Estimates_Are_Not_Re_Read_On_Every_Flush()
    {
        var storage = new QuotaLimitedStorage { QuotaBytes = 100_000_000 };
        var (db, todos) = await OpenAsync(storage);
        await using var owned = db;

        for (var i = 0; i < 20; i++)
        {
            todos.Insert(new TodoItem { Id = Guid.NewGuid(), Title = "row" });
            await db.FlushAsync();
        }

        // The estimate is cached and only refreshed after enough bytes have gone by.
        Assert.True(storage.EstimateCalls < 20, $"expected caching, saw {storage.EstimateCalls} estimate calls");
    }
}
