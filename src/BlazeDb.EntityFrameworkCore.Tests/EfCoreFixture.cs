using Microsoft.EntityFrameworkCore;

namespace BlazeDb.EntityFrameworkCore.Tests;

/// <summary>
/// Opens an in-memory-only engine and a context over it. The database is the long-lived thing -
/// it holds the data and the writer lock - and contexts come and go against it, which is how the
/// provider is meant to be used in a Blazor app.
/// </summary>
internal sealed class TestDatabase : IAsyncDisposable
{
    private TestDatabase(BlazeDbDatabase database) => Database = database;

    public BlazeDbDatabase Database { get; }

    public static async Task<TestDatabase> OpenAsync()
    {
        var database = await BlazeDbDatabase.OpenAsync(new BlazeDbDatabaseOptions
            {
                FlushInterval = TimeSpan.FromHours(1),
            }
            .AddTable(Person.Table)
            .AddTable(Order.Table)
            .AddTable(Tag.Table));
        return new TestDatabase(database);
    }

    public PeopleContext CreateContext() =>
        new(new DbContextOptionsBuilder<PeopleContext>().UseBlazeDb(Database).Options);

    public BlazeDbTable<int, Person> People => Database.GetTable(Person.Table);

    public BlazeDbTable<int, Order> Orders => Database.GetTable(Order.Table);

    public BlazeDbTable<string, Tag> Tags => Database.GetTable(Tag.Table);

    public ValueTask DisposeAsync() => Database.DisposeAsync();
}
