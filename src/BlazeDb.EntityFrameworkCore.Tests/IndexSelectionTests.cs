using System.Linq.Expressions;
using BlazeDb.EntityFrameworkCore.Metadata;
using BlazeDb.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BlazeDb.EntityFrameworkCore.Tests;

/// <summary>
/// Index selection is the one part of the provider whose effect is invisible from the outside: a
/// scan and a lookup return the same rows. These tests read the plan the translator produced, so a
/// query that quietly stops using its index fails here rather than just getting slower.
/// </summary>
public class IndexSelectionTests
{
    private static async Task<(TestDatabase Test, PeopleContext Context)> OpenAsync()
    {
        var test = await TestDatabase.OpenAsync();
        return (test, test.CreateContext());
    }

    private static QueryPlan PlanFor(TestDatabase test, IQueryable<Person> query)
    {
        var binding = BlazeDbTableResolver.CreateBinding(test.Database, Person.Table);
        var prepared = QueryPreparer.Prepare(query.Expression, out _);
        return QueryTranslator.Translate(prepared, binding, typeof(Person)).Plan;
    }

    [Fact]
    public async Task Equality_On_An_Indexed_Property_Uses_Its_Index()
    {
        var (test, context) = await OpenAsync();
        await using var owned = test;
        using var session = context;

        var plan = PlanFor(test, context.People.Where(p => p.City == "London"));

        Assert.Equal("City", plan.IndexName);
    }

    [Fact]
    public async Task Equality_Written_Backwards_Still_Matches()
    {
        var (test, context) = await OpenAsync();
        await using var owned = test;
        using var session = context;

        var plan = PlanFor(test, context.People.Where(p => "London" == p.City));

        Assert.Equal("City", plan.IndexName);
    }

    [Fact]
    public async Task A_Range_Uses_The_Ordered_Index()
    {
        var (test, context) = await OpenAsync();
        await using var owned = test;
        using var session = context;

        var plan = PlanFor(test, context.People.Where(p => p.Age >= 40 && p.Age <= 50));

        Assert.Equal("Age", plan.IndexName);
    }

    [Fact]
    public async Task Two_Equalities_Matching_A_Compound_Index_Use_It()
    {
        var (test, context) = await OpenAsync();
        await using var owned = test;
        using var session = context;

        var plan = PlanFor(test, context.People.Where(p => p.City == "London" && p.Age == 36));

        Assert.Equal("CityAge", plan.IndexName);
    }

    [Fact]
    public async Task Ordering_By_An_Indexed_Property_Reads_The_Index_Instead_Of_Sorting()
    {
        var (test, context) = await OpenAsync();
        await using var owned = test;
        using var session = context;

        var plan = PlanFor(test, context.People.OrderBy(p => p.Age));

        Assert.Equal("Age", plan.IndexName);
        Assert.True(plan.IndexProvidesOrder);
        Assert.False(plan.HasOrdering);
    }

    [Fact]
    public async Task An_Unindexed_Predicate_Falls_Back_To_A_Scan()
    {
        var (test, context) = await OpenAsync();
        await using var owned = test;
        using var session = context;

        var plan = PlanFor(test, context.People.Where(p => p.Name == "Ada"));

        Assert.Null(plan.IndexName);
    }

    [Fact]
    public async Task Paging_Is_Carried_By_The_Plan()
    {
        var (test, context) = await OpenAsync();
        await using var owned = test;
        using var session = context;

        var plan = PlanFor(test, context.People.Skip(2).Take(3));

        Assert.Equal(2, plan.Skip);
        Assert.Equal(3, plan.Take);
    }

    [Fact]
    public async Task A_Where_After_Paging_Is_Left_To_Linq()
    {
        var (test, context) = await OpenAsync();
        await using var owned = test;
        using var session = context;

        // Filtering after a Take means something different from filtering before it, so the plan
        // has to stop consuming at the Take and let the rest run over its results.
        var binding = BlazeDbTableResolver.CreateBinding(test.Database, Person.Table);
        var query = context.People.Take(2).Where(p => p.City == "London");
        var translated = QueryTranslator.Translate(
            QueryPreparer.Prepare(query.Expression, out _), binding, typeof(Person));

        Assert.Equal(2, translated.Plan.Take);
        Assert.NotNull(translated.Remainder);
    }

    [Fact]
    public async Task An_Index_Lookup_Returns_The_Same_Rows_As_A_Scan()
    {
        var (test, context) = await OpenAsync();
        await using var owned = test;
        using var session = context;

        for (var i = 1; i <= 50; i++)
        {
            test.People.Insert(new Person
            {
                Id = i,
                Name = $"P{i}",
                City = i % 3 == 0 ? "London" : "Paris",
                Age = 20 + (i % 40),
                Email = $"p{i}@x.com",
            });
        }

        var viaIndex = context.People.Where(p => p.City == "London").Select(p => p.Id).Order().ToList();
        var viaScan = test.People.Scan().Where(p => p.City == "London").Select(p => p.Id).Order().ToList();

        Assert.Equal(viaScan, viaIndex);
        Assert.NotEmpty(viaIndex);
    }

    [Fact]
    public async Task A_Strict_Range_Keeps_Its_Boundary_Filter()
    {
        var (test, context) = await OpenAsync();
        await using var owned = test;
        using var session = context;

        for (var i = 1; i <= 10; i++)
        {
            test.People.Insert(new Person { Id = i, Name = $"P{i}", City = "X", Age = i, Email = $"p{i}@x.com" });
        }

        // The engine's ranges include both ends, so `> 3` must still exclude the row at 3.
        var ages = context.People.Where(p => p.Age > 3 && p.Age < 6).Select(p => p.Age).Order().ToList();

        Assert.Equal([4, 5], ages);
    }

    [Fact]
    public async Task An_Ordered_Index_Scan_Comes_Back_Sorted()
    {
        var (test, context) = await OpenAsync();
        await using var owned = test;
        using var session = context;

        foreach (var age in (int[])[40, 10, 30, 20])
        {
            test.People.Insert(new Person { Id = age, Name = $"P{age}", City = "X", Age = age, Email = $"p{age}@x.com" });
        }

        Assert.Equal([10, 20, 30, 40], context.People.OrderBy(p => p.Age).Select(p => p.Age).ToList());
        Assert.Equal([40, 30, 20, 10], context.People.OrderByDescending(p => p.Age).Select(p => p.Age).ToList());
    }

    [Fact]
    public async Task A_Compound_Lookup_Returns_Only_Exact_Tuple_Matches()
    {
        var (test, context) = await OpenAsync();
        await using var owned = test;
        using var session = context;

        test.People.Insert(new Person { Id = 1, Name = "a", City = "London", Age = 30, Email = "a@x.com" });
        test.People.Insert(new Person { Id = 2, Name = "b", City = "London", Age = 40, Email = "b@x.com" });
        test.People.Insert(new Person { Id = 3, Name = "c", City = "Paris", Age = 30, Email = "c@x.com" });

        var names = context.People.Where(p => p.City == "London" && p.Age == 30).Select(p => p.Name).ToList();

        Assert.Equal(["a"], names);
    }

    [Fact]
    public async Task A_Null_Valued_Equality_Does_Not_Use_An_Index()
    {
        var (test, context) = await OpenAsync();
        await using var owned = test;
        using var session = context;

        test.People.Insert(new Person { Id = 1, Name = "a", City = "X", Age = 1, Email = null });
        test.People.Insert(new Person { Id = 2, Name = "b", City = "X", Age = 2, Email = "b@x.com" });

        // Nulls are not indexed, so a null-valued lookup has to fall back to a scan or it would
        // silently return nothing.
        var plan = PlanFor(test, context.People.Where(p => p.Email == null));
        Assert.Null(plan.IndexName);
        Assert.Single(context.People.Where(p => p.Email == null).ToList());
    }

    [Fact]
    public void The_Translator_Reads_A_Constant_Without_Compiling_It()
    {
        var captured = 42;
        Expression<Func<int>> expression = () => captured;

        Assert.True(ExpressionEvaluator.TryEvaluate(expression.Body, out var value));
        Assert.Equal(42, value);
    }
}
