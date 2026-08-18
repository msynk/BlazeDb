using BlazeDb;
using BlazeDb.Storage;
using Xunit;

namespace BlazeDb.Tests;

public class ConstraintTests
{
    private static async Task<(Database Db, Table<int, Account> Accounts)> OpenAsync()
    {
        var db = await Database.OpenAsync(new DatabaseOptions
        {
            FlushInterval = TimeSpan.FromHours(1),
        }.AddTable(Account.Table));
        return (db, db.GetTable(Account.Table));
    }

    // Email defaults to something per-row unique so tests that aren't about the compound
    // TenantEmail constraint don't trip over it.
    private static Account Make(int id, string username, int tenant = 1, string? email = null, int? slot = null) =>
        new()
        {
            Id = id,
            Username = username,
            TenantId = tenant,
            Email = email ?? $"user{id}@b.c",
            Slot = slot,
            CreatedAt = new DateTime(2026, 1, id, 0, 0, 0, DateTimeKind.Utc),
        };

    [Fact]
    public async Task Unique_Hash_Index_Rejects_Duplicate()
    {
        var (db, accounts) = await OpenAsync();
        await using var _ = db;
        accounts.Insert(Make(1, "ada"));

        var ex = Assert.Throws<UniqueConstraintViolationException>(() => accounts.Insert(Make(2, "ada")));

        Assert.Equal("Username", ex.IndexName);
        Assert.Equal("accounts", ex.TableName);
    }

    [Fact]
    public async Task Rejected_Write_Leaves_Table_Untouched()
    {
        var (db, accounts) = await OpenAsync();
        await using var owned = db;
        accounts.Insert(Make(1, "ada"));

        Assert.Throws<UniqueConstraintViolationException>(() => accounts.Insert(Make(2, "ada")));

        Assert.Equal(1, accounts.Count);
        Assert.False(accounts.TryGet(2, out _));
        Assert.Single(accounts.Lookup(Account.Indexes.Username, "ada"));
    }

    [Fact]
    public async Task Updating_A_Row_Does_Not_Conflict_With_Itself()
    {
        var (db, accounts) = await OpenAsync();
        await using var _ = db;
        accounts.Insert(Make(1, "ada"));

        accounts.Update(Make(1, "ada", email: "new@b.c"));

        Assert.Equal("new@b.c", accounts.Get(1)!.Email);
    }

    [Fact]
    public async Task Unique_Constraint_Frees_Value_After_Delete()
    {
        var (db, accounts) = await OpenAsync();
        await using var _ = db;
        accounts.Insert(Make(1, "ada"));
        accounts.Delete(1);

        accounts.Insert(Make(2, "ada"));

        Assert.Equal(2, accounts.Lookup(Account.Indexes.Username, "ada").Single().Id);
    }

    [Fact]
    public async Task Unique_Ordered_Index_Rejects_Duplicates_But_Allows_Nulls()
    {
        var (db, accounts) = await OpenAsync();
        await using var _ = db;
        accounts.Insert(Make(1, "ada", slot: 7));
        accounts.Insert(Make(2, "bob"));
        accounts.Insert(Make(3, "cleo"));

        Assert.Throws<UniqueConstraintViolationException>(() => accounts.Insert(Make(4, "dan", slot: 7)));
        Assert.Equal(3, accounts.Count);
    }

    [Fact]
    public async Task Unique_Violation_Inside_Transaction_Can_Be_Rolled_Back()
    {
        var (db, accounts) = await OpenAsync();
        await using var _ = db;
        accounts.Insert(Make(1, "ada"));

        using (var tx = db.BeginTransaction())
        {
            accounts.Insert(Make(2, "bob"));
            Assert.Throws<UniqueConstraintViolationException>(() => accounts.Insert(Make(3, "ada")));
            tx.Rollback();
        }

        Assert.Equal(1, accounts.Count);
    }

    [Fact]
    public async Task Compound_Hash_Index_Looks_Up_By_Tuple()
    {
        var (db, accounts) = await OpenAsync();
        await using var _ = db;
        accounts.Insert(Make(1, "ada", tenant: 1, email: "x@b.c"));
        accounts.Insert(Make(2, "bob", tenant: 2, email: "x@b.c"));
        accounts.Insert(Make(3, "cleo", tenant: 1, email: "y@b.c"));

        var hit = accounts.Lookup(Account.Indexes.TenantEmail, (1, "x@b.c")).Single();

        Assert.Equal(1, hit.Id);
        Assert.Equal(1, accounts.CountBy(Account.Indexes.TenantEmail, (2, "x@b.c")));
        Assert.Equal(0, accounts.CountBy(Account.Indexes.TenantEmail, (2, "y@b.c")));
    }

    [Fact]
    public async Task Compound_Unique_Index_Rejects_Only_Duplicate_Tuples()
    {
        var (db, accounts) = await OpenAsync();
        await using var _ = db;
        accounts.Insert(Make(1, "ada", tenant: 1, email: "x@b.c"));

        // The same email under a different tenant is fine: the constraint spans both members.
        accounts.Insert(Make(2, "bob", tenant: 2, email: "x@b.c"));

        Assert.Throws<UniqueConstraintViolationException>(
            () => accounts.Insert(Make(3, "cleo", tenant: 1, email: "x@b.c")));
    }

    [Fact]
    public async Task Compound_Ordered_Index_Scans_A_Leading_Key_Prefix()
    {
        var (db, accounts) = await OpenAsync();
        await using var _ = db;
        for (var i = 1; i <= 6; i++)
        {
            accounts.Insert(Make(i, "user" + i, tenant: i % 2 == 0 ? 2 : 1, email: "e" + i));
        }

        var tenant1 = accounts.Range(
            Account.Indexes.TenantCreated,
            (1, DateTime.MinValue),
            (1, DateTime.MaxValue)).ToList();

        Assert.Equal([1, 3, 5], tenant1.Select(a => a.Id));
        Assert.True(tenant1.SequenceEqual(tenant1.OrderBy(a => a.CreatedAt)));
    }

    [Fact]
    public async Task Compound_Indexes_Track_Updates_And_Deletes()
    {
        var (db, accounts) = await OpenAsync();
        await using var _ = db;
        accounts.Insert(Make(1, "ada", tenant: 1, email: "x@b.c"));
        accounts.Upsert(Make(1, "ada", tenant: 9, email: "x@b.c"));

        Assert.Equal(0, accounts.CountBy(Account.Indexes.TenantEmail, (1, "x@b.c")));
        Assert.Equal(1, accounts.CountBy(Account.Indexes.TenantEmail, (9, "x@b.c")));

        accounts.Delete(1);
        Assert.Equal(0, accounts.CountBy(Account.Indexes.TenantEmail, (9, "x@b.c")));
    }

    [Fact]
    public async Task Unique_Constraints_Are_Reenforced_After_Reload()
    {
        var storage = new InMemoryStorage();
        var options = new DatabaseOptions { FlushInterval = TimeSpan.FromHours(1), Storage = storage }
            .AddTable(Account.Table);

        var db = await Database.OpenAsync(options);
        db.GetTable(Account.Table).Insert(Make(1, "ada"));
        await db.FlushAsync();
        await db.DisposeAsync();

        var reopened = await Database.OpenAsync(
            new DatabaseOptions { FlushInterval = TimeSpan.FromHours(1), Storage = storage }.AddTable(Account.Table));
        await using var _ = reopened;
        var accounts = reopened.GetTable(Account.Table);

        Assert.Equal(1, accounts.Count);
        Assert.Throws<UniqueConstraintViolationException>(() => accounts.Insert(Make(2, "ada")));
    }
}
