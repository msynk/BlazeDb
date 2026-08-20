using BlazeDb;
using BlazeDb.Storage;
using Xunit;

namespace BlazeDb.Tests;

/// <summary>
/// Read-replica semantics. In the browser these instances live in different tabs and share the
/// origin's storage; here two BlazeDbDatabase instances share one IBlazeDbStorage, which exercises the same
/// paths - the tabs only ever communicate through storage anyway.
/// </summary>
public class ReplicaTests
{
    private static Task<BlazeDbDatabase> OpenWriterAsync(IBlazeDbStorage storage, Action<BlazeDbDatabaseOptions>? configure = null)
    {
        var options = new BlazeDbDatabaseOptions
        {
            Storage = storage,
            FlushInterval = TimeSpan.FromHours(1),
        }.AddTable(TodoItem.Table);
        configure?.Invoke(options);
        return BlazeDbDatabase.OpenAsync(options).AsTask();
    }

    private static Task<BlazeDbDatabase> OpenReplicaAsync(IBlazeDbStorage storage) =>
        BlazeDbDatabase.OpenAsync(new BlazeDbDatabaseOptions
        {
            Storage = storage,
            ReadOnly = true,
            FlushInterval = TimeSpan.FromHours(1),
        }.AddTable(TodoItem.Table)).AsTask();

    private static TodoItem Make(string title) => new() { Id = Guid.NewGuid(), Title = title };

    [Fact]
    public async Task A_Replica_Refuses_Writes()
    {
        var storage = new BlazeDbInMemoryStorage();
        await using var writer = await OpenWriterAsync(storage);
        await using var replica = await OpenReplicaAsync(storage);

        var todos = replica.GetTable(TodoItem.Table);

        Assert.True(replica.IsReadOnly);
        Assert.Throws<InvalidOperationException>(() => todos.Insert(Make("nope")));
        Assert.Throws<InvalidOperationException>(() => replica.BeginTransaction());
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await replica.CheckpointAsync());
    }

    [Fact]
    public async Task A_Replica_Sees_Flushed_Writes_After_Reloading()
    {
        var storage = new BlazeDbInMemoryStorage();
        await using var writer = await OpenWriterAsync(storage);
        await using var replica = await OpenReplicaAsync(storage);

        writer.GetTable(TodoItem.Table).Insert(Make("from the writer"));
        await writer.FlushAsync();

        // Nothing arrives until the replica goes back to storage.
        Assert.Equal(0, replica.GetTable(TodoItem.Table).Count);

        await replica.ReloadAsync();

        Assert.Equal("from the writer", replica.GetTable(TodoItem.Table).Scan().Single().Title);
    }

    [Fact]
    public async Task A_Replica_Never_Sees_Unflushed_Writes()
    {
        var storage = new BlazeDbInMemoryStorage();
        await using var writer = await OpenWriterAsync(storage);
        await using var replica = await OpenReplicaAsync(storage);

        writer.GetTable(TodoItem.Table).Insert(Make("committed but not yet durable"));
        await replica.ReloadAsync();

        Assert.Equal(0, replica.GetTable(TodoItem.Table).Count);
    }

    [Fact]
    public async Task Reloading_Picks_Up_Deletes_And_Updates()
    {
        var storage = new BlazeDbInMemoryStorage();
        await using var writer = await OpenWriterAsync(storage);
        var writerTodos = writer.GetTable(TodoItem.Table);
        var keep = Make("keep");
        var drop = Make("drop");
        writerTodos.Insert(keep);
        writerTodos.Insert(drop);
        await writer.FlushAsync();

        await using var replica = await OpenReplicaAsync(storage);
        Assert.Equal(2, replica.GetTable(TodoItem.Table).Count);

        writerTodos.Delete(drop.Id);
        writerTodos.Upsert(new TodoItem { Id = keep.Id, Title = "renamed", Done = true });
        await writer.FlushAsync();
        await replica.ReloadAsync();

        var rows = replica.GetTable(TodoItem.Table);
        Assert.Equal(1, rows.Count);
        Assert.Equal("renamed", rows.Get(keep.Id)!.Title);
        Assert.False(rows.TryGet(drop.Id, out _));
    }

    [Fact]
    public async Task Reloading_Rebuilds_Secondary_Indexes()
    {
        var storage = new BlazeDbInMemoryStorage();
        await using var writer = await OpenWriterAsync(storage);
        var writerTodos = writer.GetTable(TodoItem.Table);
        writerTodos.Insert(new TodoItem { Id = Guid.NewGuid(), Title = "a", Done = true, Category = "work" });
        await writer.FlushAsync();

        await using var replica = await OpenReplicaAsync(storage);
        Assert.Equal(1, replica.GetTable(TodoItem.Table).CountBy(TodoItem.Indexes.Done, true));

        writerTodos.Insert(new TodoItem { Id = Guid.NewGuid(), Title = "b", Done = true, Category = "work" });
        await writer.FlushAsync();
        await replica.ReloadAsync();

        var rows = replica.GetTable(TodoItem.Table);
        Assert.Equal(2, rows.CountBy(TodoItem.Indexes.Done, true));
        Assert.Equal(2, rows.Lookup(TodoItem.Indexes.Category, "work").Count());
    }

    [Fact]
    public async Task Reloading_Twice_Does_Not_Duplicate_Rows()
    {
        var storage = new BlazeDbInMemoryStorage();
        await using var writer = await OpenWriterAsync(storage);
        writer.GetTable(TodoItem.Table).Insert(Make("only once"));
        await writer.FlushAsync();

        await using var replica = await OpenReplicaAsync(storage);
        await replica.ReloadAsync();
        await replica.ReloadAsync();

        Assert.Equal(1, replica.GetTable(TodoItem.Table).Count);
        Assert.Equal(1, replica.GetTable(TodoItem.Table).CountBy(TodoItem.Indexes.Done, false));
    }

    [Fact]
    public async Task A_Replica_Follows_The_Writer_Across_A_Checkpoint()
    {
        var storage = new BlazeDbInMemoryStorage();
        await using var writer = await OpenWriterAsync(storage);
        var writerTodos = writer.GetTable(TodoItem.Table);
        for (var i = 0; i < 20; i++)
        {
            writerTodos.Insert(Make("row " + i));
        }
        await writer.FlushAsync();

        await using var replica = await OpenReplicaAsync(storage);
        await writer.CheckpointAsync();
        writerTodos.Insert(Make("after the checkpoint"));
        await writer.FlushAsync();

        await replica.ReloadAsync();

        // The replica now reads a different snapshot and WAL generation than it opened with.
        Assert.Equal(21, replica.GetTable(TodoItem.Table).Count);
    }

    [Fact]
    public async Task Checkpoints_Are_Announced_Once_They_Are_Durable()
    {
        var storage = new BlazeDbInMemoryStorage();
        var announced = new List<ulong>();
        await using var writer = await OpenWriterAsync(storage, o => o.OnCheckpoint = g => announced.Add(g));

        writer.GetTable(TodoItem.Table).Insert(Make("row"));
        await writer.FlushAsync();
        await writer.CheckpointAsync();
        await writer.CheckpointAsync();

        // Generations increase, which is what a replica uses to tell announcements apart.
        Assert.Equal(2, announced.Count);
        Assert.True(announced[1] > announced[0]);
    }

    [Fact]
    public async Task A_Replica_Leaves_Storage_Untouched()
    {
        var storage = new RecordingStorage();
        await using var writer = await OpenWriterAsync(storage);
        writer.GetTable(TodoItem.Table).Insert(Make("row"));
        await writer.FlushAsync();

        storage.Writes.Clear();
        await using (var replica = await OpenReplicaAsync(storage))
        {
            await replica.ReloadAsync();
        }

        Assert.Empty(storage.Writes);
    }

    [Fact]
    public async Task A_Replica_Opens_On_An_Empty_Database_Without_Creating_One()
    {
        var storage = new RecordingStorage();
        await using var replica = await OpenReplicaAsync(storage);

        Assert.Equal(0, replica.GetTable(TodoItem.Table).Count);
        Assert.Empty(storage.Writes);
    }

    /// <summary>Records every mutating call so a test can assert a replica stays passive.</summary>
    private sealed class RecordingStorage : IBlazeDbStorage
    {
        private readonly BlazeDbInMemoryStorage _inner = new();

        public List<string> Writes { get; } = [];

        public ValueTask<byte[]?> ReadAsync(string name, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(name, cancellationToken);

        public ValueTask WriteAtomicAsync(string name, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            Writes.Add("write:" + name);
            return _inner.WriteAtomicAsync(name, data, cancellationToken);
        }

        public ValueTask AppendAsync(string name, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            Writes.Add("append:" + name);
            return _inner.AppendAsync(name, data, cancellationToken);
        }

        public ValueTask DeleteAsync(string name, CancellationToken cancellationToken = default)
        {
            Writes.Add("delete:" + name);
            return _inner.DeleteAsync(name, cancellationToken);
        }
    }
}
