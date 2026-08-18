using BlazeDb;
using Xunit;

namespace BlazeDb.Tests;

public class TransactionTests
{
    [Fact]
    public async Task Commit_Keeps_All_Writes()
    {
        await using var db = await TestDb.OpenInMemoryAsync();
        var people = db.GetTable(PersonTable.Descriptor);

        using (var txn = db.BeginTransaction())
        {
            people.Insert(new Person(1, "Ada", 36));
            people.Insert(new Person(2, "Grace", 45));
            txn.Commit();
        }

        Assert.Equal(2, people.Count);
    }

    [Fact]
    public async Task Writes_Are_Visible_Inside_Transaction()
    {
        await using var db = await TestDb.OpenInMemoryAsync();
        var people = db.GetTable(PersonTable.Descriptor);

        using var txn = db.BeginTransaction();
        people.Insert(new Person(1, "Ada", 36));

        Assert.Equal("Ada", people.Get(1)!.Name);
        txn.Commit();
    }

    [Fact]
    public async Task Rollback_Reverts_Insert_Update_And_Delete()
    {
        await using var db = await TestDb.OpenInMemoryAsync();
        var people = db.GetTable(PersonTable.Descriptor);
        people.Insert(new Person(1, "Ada", 36));
        people.Insert(new Person(2, "Grace", 45));

        using (var txn = db.BeginTransaction())
        {
            people.Insert(new Person(3, "Alan", 41));
            people.Update(new Person(1, "Ada Lovelace", 37));
            people.Delete(2);
            txn.Rollback();
        }

        Assert.Equal(2, people.Count);
        Assert.Equal(new Person(1, "Ada", 36), people.Get(1));
        Assert.Equal(new Person(2, "Grace", 45), people.Get(2));
        Assert.False(people.Contains(3));
    }

    [Fact]
    public async Task Rollback_Restores_Multiple_Writes_To_Same_Key()
    {
        await using var db = await TestDb.OpenInMemoryAsync();
        var people = db.GetTable(PersonTable.Descriptor);
        people.Insert(new Person(1, "Original", 10));

        using (var txn = db.BeginTransaction())
        {
            people.Update(new Person(1, "First", 11));
            people.Update(new Person(1, "Second", 12));
            people.Delete(1);
            people.Insert(new Person(1, "Third", 13));
            txn.Rollback();
        }

        Assert.Equal(new Person(1, "Original", 10), people.Get(1));
    }

    [Fact]
    public async Task Dispose_Without_Commit_Rolls_Back()
    {
        await using var db = await TestDb.OpenInMemoryAsync();
        var people = db.GetTable(PersonTable.Descriptor);

        using (db.BeginTransaction())
        {
            people.Insert(new Person(1, "Ada", 36));
        }

        Assert.Equal(0, people.Count);
    }

    [Fact]
    public async Task Nested_Transactions_Are_Rejected()
    {
        await using var db = await TestDb.OpenInMemoryAsync();

        using var txn = db.BeginTransaction();
        Assert.Throws<InvalidOperationException>(() => db.BeginTransaction());
        txn.Commit();
    }

    [Fact]
    public async Task Ambient_Writes_Join_The_Active_Transaction()
    {
        await using var db = await TestDb.OpenInMemoryAsync();
        var people = db.GetTable(PersonTable.Descriptor);

        using (db.BeginTransaction())
        {
            // Write does not reference the transaction object, but must still be reverted.
            people.Insert(new Person(1, "Ada", 36));
        }

        Assert.False(people.Contains(1));
    }

    [Fact]
    public async Task Completed_Transaction_Cannot_Be_Reused()
    {
        await using var db = await TestDb.OpenInMemoryAsync();

        var txn = db.BeginTransaction();
        txn.Commit();

        Assert.Throws<InvalidOperationException>(txn.Commit);
        Assert.Throws<InvalidOperationException>(txn.Rollback);
    }

    [Fact]
    public async Task New_Transaction_Can_Start_After_Previous_Completes()
    {
        await using var db = await TestDb.OpenInMemoryAsync();
        var people = db.GetTable(PersonTable.Descriptor);

        using (var txn = db.BeginTransaction())
        {
            people.Insert(new Person(1, "Ada", 36));
            txn.Commit();
        }

        using (var txn = db.BeginTransaction())
        {
            people.Insert(new Person(2, "Grace", 45));
            txn.Rollback();
        }

        Assert.True(people.Contains(1));
        Assert.False(people.Contains(2));
    }

    [Fact]
    public async Task Failed_Operation_Inside_Transaction_Leaves_Prior_Writes_Applied_Until_Rollback()
    {
        await using var db = await TestDb.OpenInMemoryAsync();
        var people = db.GetTable(PersonTable.Descriptor);
        people.Insert(new Person(1, "Ada", 36));

        using (var txn = db.BeginTransaction())
        {
            people.Insert(new Person(2, "Grace", 45));
            Assert.Throws<DuplicateKeyException>(() => people.Insert(new Person(1, "Dup", 0)));
            txn.Rollback();
        }

        Assert.Equal(1, people.Count);
        Assert.True(people.Contains(1));
    }
}
