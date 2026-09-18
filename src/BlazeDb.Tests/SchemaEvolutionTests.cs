using BlazeDb.Storage;
using Xunit;

namespace BlazeDb.Tests;

/// <summary>
/// A database is opened with whatever tables the current code registers, which need not be every
/// table the files hold: a context type that owns part of a shared store, or an application that
/// removed a table type. Neither may fail to open, and neither may lose the rows it cannot see.
/// </summary>
public class SchemaEvolutionTests
{
    private static readonly TimeSpan Never = TimeSpan.FromHours(1);

    private static Task<BlazeDbDatabase> OpenAsync(IBlazeDbStorage storage, params BlazeDbTableDescriptor[] tables)
    {
        var options = new BlazeDbDatabaseOptions { Storage = storage, FlushInterval = Never };
        foreach (var table in tables)
        {
            options.AddTable(table);
        }
        return BlazeDbDatabase.OpenAsync(options).AsTask();
    }

    private static TodoItem Todo(string title) => new() { Id = Guid.NewGuid(), Title = title, CreatedAt = DateTime.UtcNow };

    [Fact]
    public async Task Rows_Of_An_Unregistered_Table_Survive_Open_Replay_And_Checkpoint()
    {
        var storage = new BlazeDbInMemoryStorage();
        var kept = Todo("keep");
        var dropped = Todo("drop");

        // Session 1: both tables, and only the log reaches storage - no snapshot yet - so the next
        // session has to carry the unknown table's rows through WAL replay, deletes included.
        await using (var db = await OpenAsync(storage, PersonTable.Descriptor, TodoItem.Table))
        {
            db.GetTable(PersonTable.Descriptor).Insert(new Person(1, "Ada", 36));
            var todos = db.GetTable(TodoItem.Table);
            todos.Insert(kept);
            todos.Insert(dropped);
            todos.Delete(dropped.Id);
        }

        // Session 2: the todos table is not registered. The database still opens, says so, and its
        // checkpoint writes the carried rows back out instead of dropping them with the old log.
        await using (var db = await OpenAsync(storage, PersonTable.Descriptor))
        {
            Assert.Equal(["todos"], db.UnregisteredTables);
            Assert.Equal(new Person(1, "Ada", 36), db.GetTable(PersonTable.Descriptor).Get(1));
            Assert.Throws<BlazeDbException>(() => db.GetTable(TodoItem.Table));

            db.GetTable(PersonTable.Descriptor).Insert(new Person(2, "Grace", 45));
            await db.CheckpointAsync();
            db.GetTable(PersonTable.Descriptor).Insert(new Person(3, "Alan", 41));
        }
        Assert.DoesNotContain("wal-1.blz", storage.FileNames); // The checkpoint really did replace the log.

        // Session 3: the table is back, and so are exactly the rows it had.
        await using (var db = await OpenAsync(storage, PersonTable.Descriptor, TodoItem.Table))
        {
            Assert.Empty(db.UnregisteredTables);
            Assert.Equal(3, db.GetTable(PersonTable.Descriptor).Count);
            var todos = db.GetTable(TodoItem.Table);
            Assert.Equal(1, todos.Count);
            Assert.Equal("keep", todos.Get(kept.Id)!.Title);
            Assert.False(todos.Contains(dropped.Id));
        }
    }

    [Fact]
    public async Task A_Descriptor_Registered_On_An_Open_Database_Takes_Over_The_Carried_Rows()
    {
        var storage = new BlazeDbInMemoryStorage();
        var todo = Todo("later");
        await using (var db = await OpenAsync(storage, PersonTable.Descriptor, TodoItem.Table))
        {
            db.GetTable(TodoItem.Table).Insert(todo);
        }

        await using var reopened = await OpenAsync(storage, PersonTable.Descriptor);
        Assert.Equal(["todos"], reopened.UnregisteredTables);

        // What the EF Core provider does when a second context type brings the table's descriptor.
        reopened.EnsureTable(TodoItem.Table);

        Assert.Empty(reopened.UnregisteredTables);
        var todos = reopened.GetTable(TodoItem.Table);
        Assert.Equal("later", todos.Get(todo.Id)!.Title);
        Assert.Single(todos.Lookup(TodoItem.Indexes.Done, false)); // Indexes were built from the decoded rows.

        // And the table is a normal one from here on: writes are journaled and survive.
        todos.Insert(Todo("after"));
        await reopened.FlushAsync();
        await using var third = await OpenAsync(storage, PersonTable.Descriptor, TodoItem.Table);
        Assert.Equal(2, third.GetTable(TodoItem.Table).Count);
    }

    [Fact]
    public async Task A_Replica_Carries_Unregistered_Tables_Too()
    {
        var storage = new BlazeDbInMemoryStorage();
        await using var writer = await OpenAsync(storage, PersonTable.Descriptor, TodoItem.Table);
        writer.GetTable(TodoItem.Table).Insert(Todo("x"));
        writer.GetTable(PersonTable.Descriptor).Insert(new Person(1, "Ada", 36));
        await writer.FlushAsync();

        await using var replica = await BlazeDbDatabase.OpenAsync(new BlazeDbDatabaseOptions
        {
            Storage = storage,
            ReadOnly = true,
        }.AddTable(PersonTable.Descriptor));
        Assert.Equal(["todos"], replica.UnregisteredTables);
        Assert.Equal(1, replica.GetTable(PersonTable.Descriptor).Count);

        await writer.CheckpointAsync();
        await replica.ReloadAsync();
        Assert.Equal(["todos"], replica.UnregisteredTables);
        Assert.Equal(1, replica.GetTable(PersonTable.Descriptor).Count);
    }
}
