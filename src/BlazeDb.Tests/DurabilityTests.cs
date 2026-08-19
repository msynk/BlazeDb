using BlazeDb;
using BlazeDb.Storage;
using Xunit;

namespace BlazeDb.Tests;

public class DurabilityTests
{
    private static readonly TimeSpan Never = TimeSpan.FromHours(1);

    private static Task<Database> OpenAsync(IStorage storage, TimeSpan? flushInterval = null) =>
        Database.OpenAsync(new DatabaseOptions
        {
            Storage = storage,
            FlushInterval = flushInterval ?? Never,
        }.AddTable(PersonTable.Descriptor)).AsTask();

    [Fact]
    public async Task Flushed_Data_Survives_Reopen_After_Clean_Dispose()
    {
        var storage = new InMemoryStorage();

        await using (var db = await OpenAsync(storage))
        {
            var people = db.GetTable(PersonTable.Descriptor);
            people.Insert(new Person(1, "Ada", 36));
            people.Insert(new Person(2, "Grace", 45));
            people.Update(new Person(2, "Grace Hopper", 46));
            people.Insert(new Person(3, "Alan", 41));
            people.Delete(3);
        }

        await using var reopened = await OpenAsync(storage);
        var recovered = reopened.GetTable(PersonTable.Descriptor);
        Assert.Equal(2, recovered.Count);
        Assert.Equal(new Person(1, "Ada", 36), recovered.Get(1));
        Assert.Equal(new Person(2, "Grace Hopper", 46), recovered.Get(2));
        Assert.False(recovered.Contains(3));
    }

    [Fact]
    public async Task Flushed_Data_Survives_Crash_Without_Dispose()
    {
        var storage = new InMemoryStorage();

        var db = await OpenAsync(storage);
        db.GetTable(PersonTable.Descriptor).Insert(new Person(1, "Ada", 36));
        await db.FlushAsync();
        // Simulated crash: the instance is abandoned without DisposeAsync.

        await using var reopened = await OpenAsync(storage);
        Assert.Equal(new Person(1, "Ada", 36), reopened.GetTable(PersonTable.Descriptor).Get(1));
    }

    [Fact]
    public async Task Unflushed_Data_Is_Lost_On_Crash_But_Flushed_Data_Survives()
    {
        var storage = new InMemoryStorage();

        var db = await OpenAsync(storage);
        var people = db.GetTable(PersonTable.Descriptor);
        people.Insert(new Person(1, "Durable", 1));
        await db.FlushAsync();
        people.Insert(new Person(2, "Volatile", 2));
        // Crash before the group-commit interval elapses.

        await using var reopened = await OpenAsync(storage);
        var recovered = reopened.GetTable(PersonTable.Descriptor);
        Assert.True(recovered.Contains(1));
        Assert.False(recovered.Contains(2));
    }

    [Fact]
    public async Task Background_Flusher_Persists_Without_Explicit_Flush()
    {
        var storage = new InMemoryStorage();

        var db = await OpenAsync(storage, flushInterval: TimeSpan.FromMilliseconds(20));
        db.GetTable(PersonTable.Descriptor).Insert(new Person(1, "Ada", 36));

        // Wait for at least one flusher tick.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
            await using var probe = await OpenAsync(storage);
            if (probe.GetTable(PersonTable.Descriptor).Contains(1))
            {
                return;
            }
        }
        Assert.Fail("Background flusher did not persist the write within 5 seconds.");
    }

    [Fact]
    public async Task A_Transient_Background_Failure_Is_Reported_Then_Retried_And_Cleared()
    {
        var storage = new FlakyStorage { FailAppends = true };

        var db = await OpenAsync(storage, flushInterval: TimeSpan.FromMilliseconds(20));
        db.GetTable(PersonTable.Descriptor).Insert(new Person(1, "Ada", 36));

        await WaitUntilAsync(() => db.LastBackgroundError is not null, "the flusher never reported its failure");
        Assert.IsType<IOException>(db.LastBackgroundError);

        // Storage recovers: the flusher retries on its own, the record lands, and the error clears.
        storage.FailAppends = false;
        await WaitUntilAsync(() => db.LastBackgroundError is null, "the flusher never recovered");

        // A clean shutdown after a recovered failure must not throw a stale error.
        await db.DisposeAsync();

        await using var reopened = await OpenAsync(storage);
        Assert.True(reopened.GetTable(PersonTable.Descriptor).Contains(1));
    }

    [Fact]
    public async Task Disposing_Surfaces_A_Failure_That_Is_Still_Standing()
    {
        var storage = new FlakyStorage { FailAppends = true };

        var db = await OpenAsync(storage);
        db.GetTable(PersonTable.Descriptor).Insert(new Person(1, "Ada", 36));

        // The final flush cannot get the record onto storage, and says so.
        await Assert.ThrowsAsync<IOException>(async () => await db.DisposeAsync());
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string failure)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(20);
        }
        Assert.Fail($"Timed out: {failure}.");
    }

    /// <summary>An in-memory backend whose appends can be made to fail on demand.</summary>
    private sealed class FlakyStorage : IStorage
    {
        private readonly InMemoryStorage _inner = new();

        public bool FailAppends { get; set; }

        public ValueTask<byte[]?> ReadAsync(string name, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(name, cancellationToken);

        public ValueTask WriteAtomicAsync(string name, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
            _inner.WriteAtomicAsync(name, data, cancellationToken);

        public ValueTask AppendAsync(string name, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
            FailAppends
                ? throw new IOException("Simulated storage failure.")
                : _inner.AppendAsync(name, data, cancellationToken);

        public ValueTask DeleteAsync(string name, CancellationToken cancellationToken = default) =>
            _inner.DeleteAsync(name, cancellationToken);
    }

    [Fact]
    public async Task Torn_Tail_Is_Discarded_And_Wal_Keeps_Working()
    {
        var storage = new InMemoryStorage();

        await using (var db = await OpenAsync(storage))
        {
            var people = db.GetTable(PersonTable.Descriptor);
            people.Insert(new Person(1, "Ada", 36));
            people.Insert(new Person(2, "Grace", 45));
        }

        // Simulate a crash mid-append: garbage / half a record at the end of the WAL.
        var wal = storage.GetFile("wal-1.blz")!;
        var torn = wal.Concat(new byte[] { 0x10, 0x00, 0x00, 0x00, 0xAA, 0xBB }).ToArray();
        storage.SetFile("wal-1.blz", torn);

        await using (var reopened = await OpenAsync(storage))
        {
            var recovered = reopened.GetTable(PersonTable.Descriptor);
            Assert.Equal(2, recovered.Count);
            recovered.Insert(new Person(3, "Alan", 41));
        }

        await using var final = await OpenAsync(storage);
        Assert.Equal(3, final.GetTable(PersonTable.Descriptor).Count);
    }

    [Fact]
    public async Task Corrupted_Record_Payload_Stops_Replay_At_Last_Valid_Record()
    {
        var storage = new InMemoryStorage();

        await using (var db = await OpenAsync(storage))
        {
            db.GetTable(PersonTable.Descriptor).Insert(new Person(1, "Ada", 36));
            await db.FlushAsync();
            db.GetTable(PersonTable.Descriptor).Insert(new Person(2, "Grace", 45));
        }

        // Flip a byte in the last record's payload; its CRC no longer matches.
        var wal = storage.GetFile("wal-1.blz")!;
        wal[^1] ^= 0xFF;
        storage.SetFile("wal-1.blz", wal);

        await using var reopened = await OpenAsync(storage);
        var recovered = reopened.GetTable(PersonTable.Descriptor);
        Assert.True(recovered.Contains(1));
        Assert.False(recovered.Contains(2));
    }

    [Fact]
    public async Task Checkpoint_Compacts_Wal_Into_Snapshot_And_Recovers()
    {
        var storage = new InMemoryStorage();

        await using (var db = await OpenAsync(storage))
        {
            var people = db.GetTable(PersonTable.Descriptor);
            for (var i = 1; i <= 50; i++)
            {
                people.Upsert(new Person(i, $"Person {i}", i));
            }
            people.Delete(50);
            await db.CheckpointAsync();

            Assert.Contains("snapshot-2.blz", storage.FileNames);
            Assert.DoesNotContain("wal-1.blz", storage.FileNames);

            // Writes after the checkpoint land in the new WAL.
            people.Insert(new Person(100, "After Checkpoint", 1));
        }

        await using var reopened = await OpenAsync(storage);
        var recovered = reopened.GetTable(PersonTable.Descriptor);
        Assert.Equal(50, recovered.Count);
        Assert.Equal(new Person(49, "Person 49", 49), recovered.Get(49));
        Assert.False(recovered.Contains(50));
        Assert.Equal(new Person(100, "After Checkpoint", 1), recovered.Get(100));
    }

    [Fact]
    public async Task Automatic_Checkpoint_Triggers_When_Wal_Grows()
    {
        var storage = new InMemoryStorage();

        var db = await Database.OpenAsync(new DatabaseOptions
        {
            Storage = storage,
            FlushInterval = TimeSpan.FromMilliseconds(20),
            CheckpointWalSize = 2 * 1024,
        }.AddTable(PersonTable.Descriptor));
        await using (db)
        {
            var people = db.GetTable(PersonTable.Descriptor);
            for (var i = 1; i <= 500; i++)
            {
                people.Upsert(new Person(i, $"Person number {i} with a reasonably long name", i));
            }

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && !storage.FileNames.Any(f => f.StartsWith("snapshot-")))
            {
                await Task.Delay(50);
            }
            Assert.Contains(storage.FileNames, f => f.StartsWith("snapshot-"));
        }

        await using var reopened = await OpenAsync(storage);
        Assert.Equal(500, reopened.GetTable(PersonTable.Descriptor).Count);
    }

    [Fact]
    public async Task Committed_Transaction_Is_Atomic_Across_Recovery()
    {
        var storage = new InMemoryStorage();

        await using (var db = await OpenAsync(storage))
        {
            var people = db.GetTable(PersonTable.Descriptor);
            using var txn = db.BeginTransaction();
            people.Insert(new Person(1, "Ada", 36));
            people.Insert(new Person(2, "Grace", 45));
            txn.Commit();
        }

        await using var reopened = await OpenAsync(storage);
        Assert.Equal(2, reopened.GetTable(PersonTable.Descriptor).Count);
    }

    [Fact]
    public async Task RolledBack_Transaction_Leaves_No_Trace_After_Recovery()
    {
        var storage = new InMemoryStorage();

        await using (var db = await OpenAsync(storage))
        {
            var people = db.GetTable(PersonTable.Descriptor);
            people.Insert(new Person(1, "Keep", 1));
            using (db.BeginTransaction())
            {
                people.Insert(new Person(2, "Discard", 2));
                // Disposed without commit -> rollback.
            }
            await db.FlushAsync();
        }

        await using var reopened = await OpenAsync(storage);
        var recovered = reopened.GetTable(PersonTable.Descriptor);
        Assert.True(recovered.Contains(1));
        Assert.False(recovered.Contains(2));
    }

    [Fact]
    public async Task Corrupt_Manifest_Throws_CorruptDatabaseException()
    {
        var storage = new InMemoryStorage();
        await using (var db = await OpenAsync(storage))
        {
            db.GetTable(PersonTable.Descriptor).Insert(new Person(1, "Ada", 36));
        }

        var manifest = storage.GetFile("manifest.blz")!;
        manifest[^1] ^= 0xFF;
        storage.SetFile("manifest.blz", manifest);

        await Assert.ThrowsAsync<CorruptDatabaseException>(async () => await OpenAsync(storage));
    }

    [Fact]
    public async Task Corrupt_Snapshot_Throws_CorruptDatabaseException()
    {
        var storage = new InMemoryStorage();
        await using (var db = await OpenAsync(storage))
        {
            db.GetTable(PersonTable.Descriptor).Insert(new Person(1, "Ada", 36));
            await db.CheckpointAsync();
        }

        var snapshot = storage.GetFile("snapshot-2.blz")!;
        snapshot[^1] ^= 0xFF;
        storage.SetFile("snapshot-2.blz", snapshot);

        await Assert.ThrowsAsync<CorruptDatabaseException>(async () => await OpenAsync(storage));
    }

    [Fact]
    public async Task Checkpoint_Inside_Transaction_Is_Rejected()
    {
        var storage = new InMemoryStorage();
        await using var db = await OpenAsync(storage);

        using var txn = db.BeginTransaction();
        db.GetTable(PersonTable.Descriptor).Insert(new Person(1, "Ada", 36));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await db.CheckpointAsync());
        txn.Commit();
    }

    [Fact]
    public async Task FileStorage_Full_Cycle_Persists_Across_Reopen()
    {
        var directory = Path.Combine(Path.GetTempPath(), "blazedb-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new FileStorage(directory);
            await using (var db = await OpenAsync(storage))
            {
                var people = db.GetTable(PersonTable.Descriptor);
                for (var i = 1; i <= 20; i++)
                {
                    people.Insert(new Person(i, $"Person {i}", i));
                }
                await db.CheckpointAsync();
                people.Insert(new Person(21, "After", 21));
            }

            await using var reopened = await OpenAsync(new FileStorage(directory));
            var recovered = reopened.GetTable(PersonTable.Descriptor);
            Assert.Equal(21, recovered.Count);
            Assert.Equal(new Person(21, "After", 21), recovered.Get(21));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Generated_Model_Persists_Through_Wal_And_Snapshot()
    {
        var storage = new InMemoryStorage();
        var options = () => new DatabaseOptions { Storage = storage, FlushInterval = Never }
            .AddTable(TodoItem.Table);

        var id = Guid.NewGuid();
        await using (var db = await Database.OpenAsync(options()))
        {
            var todos = db.GetTable(TodoItem.Table);
            todos.Insert(new TodoItem
            {
                Id = id,
                Title = "persist me",
                Done = true,
                CreatedAt = DateTime.UtcNow,
                Priority = Priority.High,
                Tags = ["a", "b"],
                Scores = [9, 8],
            });
            await db.CheckpointAsync();
        }

        await using (var reopened = await Database.OpenAsync(options()))
        {
            var todo = reopened.GetTable(TodoItem.Table).Get(id);
            Assert.NotNull(todo);
            Assert.Equal("persist me", todo.Title);
            Assert.True(todo.Done);
            Assert.Equal(Priority.High, todo.Priority);
            Assert.Equal(["a", "b"], todo.Tags);
            Assert.Equal([9, 8], todo.Scores);
        }
    }
}
