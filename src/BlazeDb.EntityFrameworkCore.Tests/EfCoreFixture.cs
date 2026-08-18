using Microsoft.EntityFrameworkCore;

namespace BlazeDb.EntityFrameworkCore.Tests;

/// <summary>
/// Opens an in-memory-only engine and a context over it. The database is the long-lived thing —
/// it holds the data and the writer lock — and contexts come and go against it, which is how the
/// provider is meant to be used in a Blazor app.
/// </summary>
internal sealed class TestDatabase : IAsyncDisposable
{
    private TestDatabase(Database database) => Database = database;

    public Database Database { get; }

    public static async Task<TestDatabase> OpenAsync()
    {
        var database = await Database.OpenAsync(new DatabaseOptions
            {
                FlushInterval = TimeSpan.FromHours(1),
            }
            .AddTable(Person.Table)
            .AddTable(Order.Table));
        return new TestDatabase(database);
    }

    public PeopleContext CreateContext() =>
        new(new DbContextOptionsBuilder<PeopleContext>().UseBlazeDb(Database).Options);

    public Table<int, Person> People => Database.GetTable(Person.Table);

    public Table<int, Order> Orders => Database.GetTable(Order.Table);

    public ValueTask DisposeAsync() => Database.DisposeAsync();
}
