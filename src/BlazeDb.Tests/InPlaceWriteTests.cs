using Xunit;

namespace BlazeDb.Tests;

/// <summary>
/// Covers the write path for rows that were mutated through the instance the table holds, rather
/// than replaced with a new one. A change tracker works that way, so the table has to be told what
/// the row used to look like or its index entries would be left pointing at values that no longer
/// exist.
/// </summary>
public class InPlaceWriteTests
{
    private static async Task<(Database Db, Table<int, Account> Accounts)> OpenAsync()
    {
        var db = await Database.OpenAsync(new DatabaseOptions
        {
            FlushInterval = TimeSpan.FromHours(1),
        }.AddTable(Account.Table));
        return (db, db.GetTable(Account.Table));
    }

    private static Account Make(int id, string username = "u", int tenant = 1) => new()
    {
        Id = id,
        Username = username + id,
        TenantId = tenant,
        Email = $"user{id}@example.com",
        CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public async Task An_In_Place_Update_Moves_The_Index_Entry()
    {
        var (db, accounts) = await OpenAsync();
        await using var owned = db;
        accounts.Insert(Make(1, tenant: 1));

        var row = accounts.Get(1)!;
        var before = new Account { Id = row.Id, Username = row.Username, TenantId = row.TenantId, Email = row.Email };
        row.TenantId = 2;
        accounts.UpdateInPlace(row, before);

        Assert.Empty(accounts.Lookup(Account.Indexes.TenantId, 1));
        Assert.Single(accounts.Lookup(Account.Indexes.TenantId, 2));
    }

    [Fact]
    public async Task A_Plain_Update_Would_Have_Stranded_The_Old_Entry()
    {
        var (db, accounts) = await OpenAsync();
        await using var owned = db;
        accounts.Insert(Make(1, tenant: 1));

        // Mutating the stored instance and then calling the ordinary Update leaves the table with
        // no record of the old value, so the tenant 1 entry survives. This is exactly the trap
        // UpdateInPlace exists to avoid, and pinning it here keeps the reason visible.
        var row = accounts.Get(1)!;
        row.TenantId = 2;
        accounts.Update(row);

        Assert.Single(accounts.Lookup(Account.Indexes.TenantId, 1));
    }

    [Fact]
    public async Task An_In_Place_Update_Still_Enforces_Unique_Constraints()
    {
        var (db, accounts) = await OpenAsync();
        await using var owned = db;
        accounts.Insert(Make(1));
        accounts.Insert(Make(2));

        var row = accounts.Get(2)!;
        var before = new Account { Id = row.Id, Username = row.Username, TenantId = row.TenantId, Email = row.Email };
        row.Username = accounts.Get(1)!.Username;

        Assert.Throws<UniqueConstraintViolationException>(() => accounts.UpdateInPlace(row, before));
    }

    [Fact]
    public async Task An_In_Place_Update_Cannot_Change_The_Primary_Key()
    {
        var (db, accounts) = await OpenAsync();
        await using var owned = db;
        accounts.Insert(Make(1));

        var row = accounts.Get(1)!;
        var before = new Account { Id = 1, Username = row.Username, TenantId = row.TenantId, Email = row.Email };
        row.Id = 99;

        Assert.Throws<KeyNotFoundException>(() => accounts.UpdateInPlace(row, before));
    }

    [Fact]
    public async Task An_In_Place_Delete_Retracts_The_Old_Index_Entry()
    {
        var (db, accounts) = await OpenAsync();
        await using var owned = db;
        accounts.Insert(Make(1, tenant: 1));

        var row = accounts.Get(1)!;
        var before = new Account { Id = row.Id, Username = row.Username, TenantId = row.TenantId, Email = row.Email };
        row.TenantId = 2;
        Assert.True(accounts.DeleteInPlace(1, before));

        Assert.Null(accounts.Get(1));
        Assert.Empty(accounts.Lookup(Account.Indexes.TenantId, 1));
        Assert.Empty(accounts.Lookup(Account.Indexes.TenantId, 2));
    }

    [Fact]
    public async Task An_In_Place_Update_Rolls_Back_With_Its_Transaction()
    {
        var (db, accounts) = await OpenAsync();
        await using var owned = db;
        accounts.Insert(Make(1, tenant: 1));

        var row = accounts.Get(1)!;
        var before = new Account { Id = row.Id, Username = row.Username, TenantId = 1, Email = row.Email };
        using (var transaction = db.BeginTransaction())
        {
            row.TenantId = 2;
            accounts.UpdateInPlace(row, before);
            transaction.Rollback();
        }

        // The rollback restores the values the table was given, so the index agrees with the row.
        Assert.Equal(1, accounts.Get(1)!.TenantId);
        Assert.Single(accounts.Lookup(Account.Indexes.TenantId, 1));
        Assert.Empty(accounts.Lookup(Account.Indexes.TenantId, 2));
    }

    [Fact]
    public void Index_Definitions_Name_The_Members_They_Cover()
    {
        Assert.Equal(["TenantId"], Account.Indexes.TenantId.Members);
        Assert.Equal(["TenantId", "Email"], Account.Indexes.TenantEmail.Members);
        Assert.Equal(["TenantId", "CreatedAt"], Account.Indexes.TenantCreated.Members);
    }
}
