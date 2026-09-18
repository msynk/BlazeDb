using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BlazeDb.EntityFrameworkCore.Tests;

public class QueryTests
{
    private static TestDatabase Seeded()
    {
        var test = TestDatabase.Open();
        var people = test.People;
        people.Insert(new Person { Id = 1, Name = "Ada", City = "London", Age = 36, Email = "ada@x.com" });
        people.Insert(new Person { Id = 2, Name = "Grace", City = "New York", Age = 45, Email = "grace@x.com" });
        people.Insert(new Person { Id = 3, Name = "Alan", City = "London", Age = 41, Email = "alan@x.com" });
        people.Insert(new Person { Id = 4, Name = "Edsger", City = "Austin", Age = 72, Email = "edsger@x.com" });
        people.Insert(new Person { Id = 5, Name = "Barbara", City = "London", Age = 81, Email = "barbara@x.com" });
        return test;
    }

    [Fact]
    public void A_Bare_Set_Returns_Every_Row()
    {
        using var test = Seeded();
        using var context = test.CreateContext();

        Assert.Equal(5, context.People.ToList().Count);
    }

    [Fact]
    public void Where_Filters()
    {
        using var test = Seeded();
        using var context = test.CreateContext();

        var names = context.People.Where(p => p.City == "London").Select(p => p.Name).Order().ToList();

        Assert.Equal(["Ada", "Alan", "Barbara"], names);
    }

    [Fact]
    public void Several_Wheres_Are_Combined()
    {
        using var test = Seeded();
        using var context = test.CreateContext();

        var names = context.People
            .Where(p => p.City == "London")
            .Where(p => p.Age > 40)
            .Select(p => p.Name)
            .Order()
            .ToList();

        Assert.Equal(["Alan", "Barbara"], names);
    }

    [Fact]
    public void A_Captured_Variable_Is_Read_As_A_Value()
    {
        using var test = Seeded();
        using var context = test.CreateContext();

        var city = "London";
        Assert.Equal(3, context.People.Count(p => p.City == city));
    }

    [Fact]
    public void OrderBy_Then_Skip_And_Take_Pages()
    {
        using var test = Seeded();
        using var context = test.CreateContext();

        var names = context.People.OrderBy(p => p.Age).Skip(1).Take(2).Select(p => p.Name).ToList();

        Assert.Equal(["Alan", "Grace"], names);
    }

    [Fact]
    public void OrderByDescending_Reverses()
    {
        using var test = Seeded();
        using var context = test.CreateContext();

        Assert.Equal("Barbara", context.People.OrderByDescending(p => p.Age).First().Name);
    }

    [Fact]
    public void ThenBy_Breaks_Ties()
    {
        using var test = Seeded();
        using var context = test.CreateContext();

        var names = context.People.OrderBy(p => p.City).ThenByDescending(p => p.Age).Select(p => p.Name).ToList();

        Assert.Equal(["Edsger", "Barbara", "Alan", "Ada", "Grace"], names);
    }

    [Fact]
    public void First_And_Single_Pick_One_Row()
    {
        using var test = Seeded();
        using var context = test.CreateContext();

        Assert.Equal("Grace", context.People.Single(p => p.Id == 2).Name);
        Assert.Equal("Ada", context.People.First(p => p.City == "London").Name);
        Assert.Null(context.People.FirstOrDefault(p => p.City == "Paris"));
    }

    [Fact]
    public void Count_And_Any_Answer_Without_Materializing()
    {
        using var test = Seeded();
        using var context = test.CreateContext();

        Assert.Equal(3, context.People.Count(p => p.City == "London"));
        Assert.True(context.People.Any(p => p.Age > 80));
        Assert.False(context.People.Any(p => p.Age > 100));
    }

    [Fact]
    public void Projections_Fall_Through_To_Linq()
    {
        using var test = Seeded();
        using var context = test.CreateContext();

        var summaries = context.People
            .Where(p => p.City == "London")
            .Select(p => new { p.Name, Decade = p.Age / 10 })
            .OrderBy(x => x.Name)
            .ToList();

        Assert.Equal(3, summaries.Count);
        Assert.Equal("Ada", summaries[0].Name);
        Assert.Equal(3, summaries[0].Decade);
    }

    [Fact]
    public void Operators_The_Plan_Cannot_Absorb_Still_Run()
    {
        using var test = Seeded();
        using var context = test.CreateContext();

        // GroupBy has no plan equivalent, so it runs as LINQ to Objects over the rows the plan
        // produced - which is free, because those rows are already objects in memory.
        var byCity = context.People
            .GroupBy(p => p.City)
            .Select(g => new { City = g.Key, Count = g.Count() })
            .OrderBy(x => x.City)
            .ToList();

        Assert.Equal(["Austin", "London", "New York"], byCity.Select(x => x.City));
        Assert.Equal([1, 3, 1], byCity.Select(x => x.Count));
    }

    [Fact]
    public void Aggregates_Work()
    {
        using var test = Seeded();
        using var context = test.CreateContext();

        Assert.Equal(81, context.People.Max(p => p.Age));
        Assert.Equal(275, context.People.Sum(p => p.Age));
    }

    [Fact]
    public void Take_Before_Where_Keeps_Linq_Semantics()
    {
        using var test = Seeded();
        using var context = test.CreateContext();

        // Take then Where means "of the first two rows, the London ones" - the plan must not
        // reorder that into "the first two London rows".
        var expected = test.People.Scan().Take(2).Count(p => p.City == "London");

        Assert.Equal(expected, context.People.Take(2).Count(p => p.City == "London"));
    }

    [Fact]
    public void Queried_Entities_Are_Tracked()
    {
        using var test = Seeded();
        using var context = test.CreateContext();

        var person = context.People.First(p => p.Id == 1);

        Assert.Equal(EntityState.Unchanged, context.Entry(person).State);
    }

    [Fact]
    public void AsNoTracking_Leaves_The_Change_Tracker_Empty()
    {
        using var test = Seeded();
        using var context = test.CreateContext();

        var people = context.People.AsNoTracking().ToList();

        Assert.Equal(5, people.Count);
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Fact]
    public void A_Context_Wide_No_Tracking_Setting_Is_Honored()
    {
        using var test = Seeded();
        using var context = test.CreateContext();
        context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTrackingWithIdentityResolution;

        Assert.Equal(5, context.People.ToList().Count);
        Assert.Empty(context.ChangeTracker.Entries());

        // An explicit AsTracking on the query still wins over the context default.
        Assert.Equal(5, context.People.AsTracking().ToList().Count);
        Assert.Equal(5, context.ChangeTracker.Entries().Count());
    }

    [Fact]
    public void Counting_Does_Not_Track_The_Rows_It_Reads()
    {
        using var test = Seeded();
        using var context = test.CreateContext();

        _ = context.People.Count();

        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Fact]
    public void The_Same_Row_Queried_Twice_Is_One_Tracked_Entity()
    {
        using var test = Seeded();
        using var context = test.CreateContext();

        var first = context.People.Single(p => p.Id == 1);
        var second = context.People.Single(p => p.Id == 1);

        Assert.Same(first, second);
        Assert.Single(context.ChangeTracker.Entries<Person>(), e => e.Entity.Id == 1);
    }

    [Fact]
    public async Task Queries_Run_Asynchronously()
    {
        using var test = Seeded();
        await using var context = test.CreateContext();

        Assert.Equal(3, (await context.People.Where(p => p.City == "London").ToListAsync()).Count);
        Assert.Equal("Grace", (await context.People.FirstOrDefaultAsync(p => p.Id == 2))!.Name);
        Assert.Equal(5, await context.People.CountAsync());
    }

    [Fact]
    public void Include_Is_Refused_With_An_Explanation()
    {
        using var test = Seeded();
        using var context = test.CreateContext();

        var error = Assert.Throws<NotSupportedException>(() => context.People.Include("Orders").ToList());

        Assert.Contains("no navigations", error.Message);
    }
}
