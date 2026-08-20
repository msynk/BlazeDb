using BlazeDb;
using BlazeDb.Storage;
using Xunit;

namespace BlazeDb.Tests;

public class TableCrudTests
{
    [Fact]
    public async Task Insert_And_Get_Roundtrips()
    {
        await using var db = await TestDb.OpenInMemoryAsync();
        var people = db.GetTable(PersonTable.Descriptor);

        people.Insert(new Person(1, "Ada", 36));

        Assert.Equal(1, people.Count);
        Assert.True(people.Contains(1));
        Assert.Equal(new Person(1, "Ada", 36), people.Get(1));
        Assert.True(people.TryGet(1, out var row));
        Assert.Equal("Ada", row.Name);
    }

    [Fact]
    public async Task Get_Missing_Returns_Default()
    {
        await using var db = await TestDb.OpenInMemoryAsync();
        var people = db.GetTable(PersonTable.Descriptor);

        Assert.Null(people.Get(42));
        Assert.False(people.TryGet(42, out _));
        Assert.False(people.Contains(42));
    }

    [Fact]
    public async Task Insert_Duplicate_Key_Throws()
    {
        await using var db = await TestDb.OpenInMemoryAsync();
        var people = db.GetTable(PersonTable.Descriptor);
        people.Insert(new Person(1, "Ada", 36));

        var ex = Assert.Throws<BlazeDbDuplicateKeyException>(() => people.Insert(new Person(1, "Bob", 40)));
        Assert.Equal("people", ex.TableName);
        Assert.Equal(new Person(1, "Ada", 36), people.Get(1));
    }

    [Fact]
    public async Task Update_Replaces_Existing_Row()
    {
        await using var db = await TestDb.OpenInMemoryAsync();
        var people = db.GetTable(PersonTable.Descriptor);
        people.Insert(new Person(1, "Ada", 36));

        people.Update(new Person(1, "Ada Lovelace", 37));

        Assert.Equal(1, people.Count);
        Assert.Equal("Ada Lovelace", people.Get(1)!.Name);
    }

    [Fact]
    public async Task Update_Missing_Throws()
    {
        await using var db = await TestDb.OpenInMemoryAsync();
        var people = db.GetTable(PersonTable.Descriptor);

        Assert.Throws<KeyNotFoundException>(() => people.Update(new Person(9, "Nobody", 0)));
    }

    [Fact]
    public async Task Upsert_Inserts_Then_Replaces()
    {
        await using var db = await TestDb.OpenInMemoryAsync();
        var people = db.GetTable(PersonTable.Descriptor);

        people.Upsert(new Person(1, "Ada", 36));
        Assert.Equal("Ada", people.Get(1)!.Name);

        people.Upsert(new Person(1, "Grace", 45));
        Assert.Equal(1, people.Count);
        Assert.Equal("Grace", people.Get(1)!.Name);
    }

    [Fact]
    public async Task Delete_Removes_Row_And_Reports_Missing()
    {
        await using var db = await TestDb.OpenInMemoryAsync();
        var people = db.GetTable(PersonTable.Descriptor);
        people.Insert(new Person(1, "Ada", 36));

        Assert.True(people.Delete(1));
        Assert.Equal(0, people.Count);
        Assert.False(people.Delete(1));
    }

    [Fact]
    public async Task Scan_Enumerates_All_Rows()
    {
        await using var db = await TestDb.OpenInMemoryAsync();
        var people = db.GetTable(PersonTable.Descriptor);
        people.Insert(new Person(1, "Ada", 36));
        people.Insert(new Person(2, "Grace", 45));
        people.Insert(new Person(3, "Alan", 41));

        var names = people.Scan().Select(p => p.Name).OrderBy(n => n).ToList();

        Assert.Equal(["Ada", "Alan", "Grace"], names);
    }

    [Fact]
    public async Task GetTable_Unregistered_Descriptor_Throws()
    {
        await using var db = await BlazeDbDatabase.OpenAsync(new BlazeDbDatabaseOptions());

        Assert.Throws<BlazeDbException>(() => db.GetTable(PersonTable.Descriptor));
    }

    [Fact]
    public async Task Invalid_Options_Are_Rejected_Before_Opening()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await BlazeDbDatabase.OpenAsync(new BlazeDbDatabaseOptions { FlushInterval = TimeSpan.Zero }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await BlazeDbDatabase.OpenAsync(new BlazeDbDatabaseOptions { CheckpointWalSize = 0 }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await BlazeDbDatabase.OpenAsync(new BlazeDbDatabaseOptions { QuotaReserveBytes = -1 }));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await BlazeDbDatabase.OpenAsync(new BlazeDbDatabaseOptions { ReadOnly = true }));
    }

    [Fact]
    public async Task A_Disposed_Database_Refuses_Further_Use()
    {
        var db = await BlazeDbDatabase.OpenAsync(new BlazeDbDatabaseOptions { Storage = new BlazeDbInMemoryStorage() }.AddTable(PersonTable.Descriptor));
        await db.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => db.BeginTransaction());
        Assert.Throws<ObjectDisposedException>(() => db.GetTable(PersonTable.Descriptor).Insert(new Person(1, "a", 1)));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await db.FlushAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await db.CheckpointAsync());
        await db.DisposeAsync(); // idempotent
    }
}
