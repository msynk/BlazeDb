using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BlazeDb.EntityFrameworkCore.Tests;

public class SaveChangesTests
{
    [Fact]
    public async Task Adding_An_Entity_Puts_A_Row_In_The_Table()
    {
        await using var test = await TestDatabase.OpenAsync();
        using var context = test.CreateContext();

        context.People.Add(new Person { Id = 1, Name = "Ada", City = "London", Age = 36 });
        var saved = context.SaveChanges();

        Assert.Equal(1, saved);
        Assert.Equal("Ada", test.People.Get(1)!.Name);
    }

    [Fact]
    public async Task A_Saved_Entity_Becomes_Unchanged()
    {
        await using var test = await TestDatabase.OpenAsync();
        using var context = test.CreateContext();

        var person = new Person { Id = 1, Name = "Ada", City = "London", Age = 36 };
        context.People.Add(person);
        context.SaveChanges();

        Assert.Equal(EntityState.Unchanged, context.Entry(person).State);
    }

    [Fact]
    public async Task A_Save_Inside_An_Engine_Transaction_Joins_It()
    {
        // The engine's transactions are ambient, so a save made inside one belongs to it rather
        // than failing as a nested transaction - which is what mixing the two APIs would hit.
        await using var test = await TestDatabase.OpenAsync();
        using var context = test.CreateContext();

        using (var transaction = test.Database.BeginTransaction())
        {
            test.Orders.Insert(new Order { Id = 1, PersonId = 1, Total = 5m });
            context.People.Add(new Person { Id = 1, Name = "Ada", City = "London", Age = 36 });
            Assert.Equal(1, context.SaveChanges());
            transaction.Commit();
        }

        Assert.Equal("Ada", test.People.Get(1)!.Name);
        Assert.Equal(1, test.Orders.Count);
    }

    [Fact]
    public async Task Rolling_Back_The_Engine_Transaction_Undoes_The_Save_Inside_It()
    {
        await using var test = await TestDatabase.OpenAsync();
        using var context = test.CreateContext();

        using (var transaction = test.Database.BeginTransaction())
        {
            context.People.Add(new Person { Id = 1, Name = "Ada", City = "London", Age = 36 });
            context.SaveChanges();
            Assert.Equal(1, test.People.Count);
            transaction.Rollback();
        }

        Assert.Equal(0, test.People.Count);
        Assert.Empty(test.People.Lookup(Person.Indexes.City, "London"));
    }

    [Fact]
    public async Task A_Tracked_Entity_Is_The_Row_The_Table_Holds()
    {
        await using var test = await TestDatabase.OpenAsync();
        test.People.Insert(new Person { Id = 1, Name = "Ada", City = "London", Age = 36 });

        using var context = test.CreateContext();
        var person = context.People.Single();

        Assert.Same(test.People.Get(1), person);
    }

    [Fact]
    public async Task Mutating_A_Tracked_Entity_Changes_Memory_Before_SaveChanges()
    {
        await using var test = await TestDatabase.OpenAsync();
        test.People.Insert(new Person { Id = 1, Name = "Ada", City = "London", Age = 36 });

        using var context = test.CreateContext();
        var person = context.People.Single();
        person.Name = "Ada Lovelace";

        // The row is the entity, so memory has already moved; SaveChanges is what journals it.
        Assert.Equal("Ada Lovelace", test.People.Get(1)!.Name);
        Assert.Equal(EntityState.Modified, context.Entry(person).State);

        Assert.Equal(1, context.SaveChanges());
        Assert.Equal(EntityState.Unchanged, context.Entry(person).State);
    }

    [Fact]
    public async Task Saving_A_Modified_Entity_Moves_Its_Index_Entries()
    {
        await using var test = await TestDatabase.OpenAsync();
        test.People.Insert(new Person { Id = 1, Name = "Ada", City = "London", Age = 36 });

        using var context = test.CreateContext();
        context.People.Single().City = "Paris";
        context.SaveChanges();

        // The row was mutated in place, so without the original values the old index entry would
        // still claim London and the new one would be missing.
        Assert.Empty(test.People.Lookup(Person.Indexes.City, "London"));
        Assert.Single(test.People.Lookup(Person.Indexes.City, "Paris"));
    }

    [Fact]
    public async Task Deleting_An_Entity_Removes_The_Row_And_Its_Index_Entries()
    {
        await using var test = await TestDatabase.OpenAsync();
        test.People.Insert(new Person { Id = 1, Name = "Ada", City = "London", Age = 36 });

        using var context = test.CreateContext();
        context.People.Remove(context.People.Single());
        context.SaveChanges();

        Assert.Null(test.People.Get(1));
        Assert.Empty(test.People.Lookup(Person.Indexes.City, "London"));
    }

    [Fact]
    public async Task A_Deleted_Entity_Stops_Being_Tracked()
    {
        await using var test = await TestDatabase.OpenAsync();
        test.People.Insert(new Person { Id = 1, Name = "Ada", City = "London", Age = 36 });

        using var context = test.CreateContext();
        var person = context.People.Single();
        context.People.Remove(person);
        context.SaveChanges();

        Assert.Equal(EntityState.Detached, context.Entry(person).State);
    }

    [Fact]
    public async Task Mutating_And_Deleting_In_One_Save_Still_Clears_The_Old_Index_Entry()
    {
        await using var test = await TestDatabase.OpenAsync();
        test.People.Insert(new Person { Id = 1, Name = "Ada", City = "London", Age = 36 });

        using var context = test.CreateContext();
        var person = context.People.Single();
        person.City = "Paris";
        context.People.Remove(person);
        context.SaveChanges();

        Assert.Null(test.People.Get(1));
        Assert.Empty(test.People.Lookup(Person.Indexes.City, "London"));
        Assert.Empty(test.People.Lookup(Person.Indexes.City, "Paris"));
    }

    [Fact]
    public async Task A_Save_Spanning_Two_Tables_Is_One_Transaction()
    {
        await using var test = await TestDatabase.OpenAsync();
        using var context = test.CreateContext();

        context.People.Add(new Person { Id = 1, Name = "Ada", City = "London", Age = 36 });
        context.Orders.Add(new Order { Id = 10, PersonId = 1, Total = 42m });

        Assert.Equal(2, context.SaveChanges());
        Assert.NotNull(test.People.Get(1));
        Assert.NotNull(test.Orders.Get(10));
    }

    [Fact]
    public async Task A_Failing_Save_Leaves_Every_Table_Untouched()
    {
        await using var test = await TestDatabase.OpenAsync();
        test.People.Insert(new Person { Id = 1, Name = "Ada", City = "London", Age = 36, Email = "ada@example.com" });

        using var context = test.CreateContext();
        context.Orders.Add(new Order { Id = 10, PersonId = 1, Total = 42m });
        context.People.Add(new Person { Id = 2, Name = "Grace", City = "NY", Age = 45, Email = "ada@example.com" });

        Assert.Throws<BlazeDbUniqueConstraintViolationException>(() => context.SaveChanges());

        // The order was applied before the conflicting person, so the rollback has to undo it.
        Assert.Null(test.Orders.Get(10));
        Assert.Null(test.People.Get(2));
    }

    [Fact]
    public async Task Saving_Nothing_Costs_Nothing()
    {
        await using var test = await TestDatabase.OpenAsync();
        using var context = test.CreateContext();

        Assert.Equal(0, context.SaveChanges());
    }

    [Fact]
    public async Task SaveChangesAsync_Applies_The_Same_Changes()
    {
        await using var test = await TestDatabase.OpenAsync();
        await using var context = test.CreateContext();

        context.People.Add(new Person { Id = 1, Name = "Ada", City = "London", Age = 36 });
        Assert.Equal(1, await context.SaveChangesAsync());
        Assert.NotNull(test.People.Get(1));
    }

    [Fact]
    public async Task Changes_Survive_A_Reopen()
    {
        var storage = new BlazeDb.Storage.BlazeDbInMemoryStorage();
        var database = await BlazeDbDatabase.OpenAsync(new BlazeDbDatabaseOptions
        {
            Storage = storage,
            FlushInterval = TimeSpan.FromHours(1),
        }.AddTable(Person.Table).AddTable(Order.Table));

        using (var context = new PeopleContext(
                   new DbContextOptionsBuilder<PeopleContext>().UseBlazeDb(database).Options))
        {
            context.People.Add(new Person { Id = 1, Name = "Ada", City = "London", Age = 36 });
            context.SaveChanges();
        }
        await database.FlushAsync();
        await database.DisposeAsync();

        await using var reopened = await BlazeDbDatabase.OpenAsync(new BlazeDbDatabaseOptions
        {
            Storage = storage,
            FlushInterval = TimeSpan.FromHours(1),
        }.AddTable(Person.Table).AddTable(Order.Table));

        using var fresh = new PeopleContext(
            new DbContextOptionsBuilder<PeopleContext>().UseBlazeDb(reopened).Options);
        Assert.Equal("Ada", fresh.People.Single().Name);
    }
}
