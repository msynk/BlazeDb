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
    private static (TestDatabase Test, PeopleContext Context) Open()
    {
        var test = TestDatabase.Open();
        return (test, test.CreateContext());
    }

    private static BlazeDbQueryPlan PlanFor<TEntity>(TestDatabase test, PeopleContext context, IQueryable<TEntity> query) =>
        TranslateFor(test, context, query).Plan;

    private static BlazeDbTranslatedQuery TranslateFor<TEntity>(TestDatabase test, PeopleContext context, IQueryable<TEntity> query)
    {
        var binding = BlazeDbTableResolver.CreateBinding(test.Database, context.Model.FindEntityType(typeof(TEntity))!);
        var prepared = BlazeDbQueryPreparer.Prepare(query.Expression, out _);
        return BlazeDbQueryTranslator.Translate(prepared, binding, typeof(TEntity));
    }

    [Fact]
    public void Equality_On_An_Indexed_Property_Uses_Its_Index()
    {
        var (test, context) = Open();
        using var owned = test;
        using var session = context;

        var plan = PlanFor(test, context, context.People.Where(p => p.City == "London"));

        Assert.Equal("City", plan.IndexName);
    }

    [Fact]
    public void Equality_Written_Backwards_Still_Matches()
    {
        var (test, context) = Open();
        using var owned = test;
        using var session = context;

        var plan = PlanFor(test, context, context.People.Where(p => "London" == p.City));

        Assert.Equal("City", plan.IndexName);
    }

    [Fact]
    public void A_Range_Uses_The_Ordered_Index()
    {
        var (test, context) = Open();
        using var owned = test;
        using var session = context;

        var plan = PlanFor(test, context, context.People.Where(p => p.Age >= 40 && p.Age <= 50));

        Assert.Equal("Age", plan.IndexName);
    }

    [Fact]
    public void Two_Equalities_Matching_A_Compound_Index_Use_It()
    {
        var (test, context) = Open();
        using var owned = test;
        using var session = context;

        var plan = PlanFor(test, context, context.People.Where(p => p.City == "London" && p.Age == 36));

        Assert.Equal("CityAge", plan.IndexName);
    }

    [Fact]
    public void Ordering_By_An_Indexed_Property_Reads_The_Index_Instead_Of_Sorting()
    {
        var (test, context) = Open();
        using var owned = test;
        using var session = context;

        var plan = PlanFor(test, context, context.People.OrderBy(p => p.Age));

        Assert.Equal("Age", plan.IndexName);
        Assert.True(plan.IndexProvidesOrder);
        Assert.False(plan.HasOrdering);
    }

    [Fact]
    public void An_Unindexed_Predicate_Falls_Back_To_A_Scan()
    {
        var (test, context) = Open();
        using var owned = test;
        using var session = context;

        var plan = PlanFor(test, context, context.People.Where(p => p.Name == "Ada"));

        Assert.Null(plan.IndexName);
    }

    [Fact]
    public void Paging_Is_Carried_By_The_Plan()
    {
        var (test, context) = Open();
        using var owned = test;
        using var session = context;

        var plan = PlanFor(test, context, context.People.Skip(2).Take(3));

        Assert.Equal(2, plan.Skip);
        Assert.Equal(3, plan.Take);
    }

    [Fact]
    public void A_Where_After_Paging_Is_Left_To_Linq()
    {
        var (test, context) = Open();
        using var owned = test;
        using var session = context;

        // Filtering after a Take means something different from filtering before it, so the plan
        // has to stop consuming at the Take and let the rest run over its results.
        var translated = TranslateFor(test, context, context.People.Take(2).Where(p => p.City == "London"));

        Assert.Equal(2, translated.Plan.Take);
        Assert.NotNull(translated.Remainder);
    }

    [Fact]
    public void An_Index_Lookup_Returns_The_Same_Rows_As_A_Scan()
    {
        var (test, context) = Open();
        using var owned = test;
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
    public void A_Strict_Range_Keeps_Its_Boundary_Filter()
    {
        var (test, context) = Open();
        using var owned = test;
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
    public void An_Ordered_Index_Scan_Comes_Back_Sorted()
    {
        var (test, context) = Open();
        using var owned = test;
        using var session = context;

        foreach (var age in (int[])[40, 10, 30, 20])
        {
            test.People.Insert(new Person { Id = age, Name = $"P{age}", City = "X", Age = age, Email = $"p{age}@x.com" });
        }

        Assert.Equal([10, 20, 30, 40], context.People.OrderBy(p => p.Age).Select(p => p.Age).ToList());
        Assert.Equal([40, 30, 20, 10], context.People.OrderByDescending(p => p.Age).Select(p => p.Age).ToList());
    }

    [Fact]
    public void A_Compound_Lookup_Returns_Only_Exact_Tuple_Matches()
    {
        var (test, context) = Open();
        using var owned = test;
        using var session = context;

        test.People.Insert(new Person { Id = 1, Name = "a", City = "London", Age = 30, Email = "a@x.com" });
        test.People.Insert(new Person { Id = 2, Name = "b", City = "London", Age = 40, Email = "b@x.com" });
        test.People.Insert(new Person { Id = 3, Name = "c", City = "Paris", Age = 30, Email = "c@x.com" });

        var names = context.People.Where(p => p.City == "London" && p.Age == 30).Select(p => p.Name).ToList();

        Assert.Equal(["a"], names);
    }

    [Fact]
    public void A_Null_Valued_Equality_Does_Not_Use_An_Index()
    {
        var (test, context) = Open();
        using var owned = test;
        using var session = context;

        test.People.Insert(new Person { Id = 1, Name = "a", City = "X", Age = 1, Email = null });
        test.People.Insert(new Person { Id = 2, Name = "b", City = "X", Age = 2, Email = "b@x.com" });

        // Nulls are not indexed, so a null-valued lookup has to fall back to a scan or it would
        // silently return nothing.
        var plan = PlanFor(test, context, context.People.Where(p => p.Email == null));
        Assert.Null(plan.IndexName);
        Assert.Single(context.People.Where(p => p.Email == null).ToList());
    }

    [Fact]
    public void The_Translator_Reads_A_Constant_Without_Compiling_It()
    {
        var captured = 42;
        Expression<Func<int>> expression = () => captured;

        Assert.True(BlazeDbExpressionEvaluator.TryEvaluate(expression.Body, out var value));
        Assert.Equal(42, value);
    }

    [Fact]
    public void The_Translator_Evaluates_Row_Independent_Expressions_But_Not_Row_Dependent_Ones()
    {
        var (a, b) = (3, 9);
        Expression<Func<int>> independent = () => Math.Max(a, b) + 1;
        Expression<Func<Person, int>> dependent = p => Math.Max(p.Age, b);

        Assert.True(BlazeDbExpressionEvaluator.TryEvaluate(independent.Body, out var value));
        Assert.Equal(10, value);
        Assert.False(BlazeDbExpressionEvaluator.TryEvaluate(dependent.Body, out _));
    }

    [Fact]
    public void A_Computed_Value_Still_Drives_An_Index()
    {
        var (test, context) = Open();
        using var owned = test;
        using var session = context;

        var (lo, hi) = (10, 30);
        var plan = PlanFor(test, context, context.People.Where(p => p.Age >= Math.Max(lo, hi)));

        Assert.Equal("Age", plan.IndexName);
    }

    [Fact]
    public void Equality_On_The_Primary_Key_Reads_The_Key_Dictionary()
    {
        var (test, context) = Open();
        using var owned = test;
        using var session = context;

        test.People.Insert(new Person { Id = 1, Name = "a", City = "X", Age = 1, Email = "a@x.com" });
        test.People.Insert(new Person { Id = 2, Name = "b", City = "X", Age = 2, Email = "b@x.com" });

        var id = 2;
        var plan = PlanFor(test, context, context.People.Where(p => p.Id == id && p.City == "X"));

        Assert.True(plan.UsesPrimaryKey);
        Assert.Null(plan.IndexName);
        Assert.Equal("b", context.People.Single(p => p.Id == id && p.City == "X").Name);
        Assert.Null(context.People.SingleOrDefault(p => p.Id == 3));
        Assert.Equal("b", context.People.Find(2)!.Name);

        // Find on an entity the context has not seen goes through the query pipeline, where EF
        // spells the key as EF.Property<T>(e, "Id"); that must land on the key dictionary too.
        using var fresh = test.CreateContext();
        Assert.Equal("a", fresh.People.Find(1)!.Name);
        Assert.Null(fresh.People.Find(42));
    }

    [Fact]
    public void A_Key_Declared_With_The_Engine_Attribute_Is_The_Primary_Key()
    {
        var (test, context) = Open();
        using var owned = test;
        using var session = context;

        var tags = context.Model.FindEntityType(typeof(Tag))!;
        Assert.Equal(["Slug"], tags.FindPrimaryKey()!.Properties.Select(p => p.Name));
        Assert.Null(tags.FindProperty(nameof(Tag.Transient)));

        test.Tags.Insert(new Tag { Slug = "wal", Kind = TagKind.Topic, Weight = 5 });
        var plan = PlanFor(test, context, context.Tags.Where(t => t.Slug == "wal"));
        Assert.True(plan.UsesPrimaryKey);
        Assert.Equal("wal", context.Tags.Find("wal")!.Slug);
    }

    [Fact]
    public void Predicate_Values_Are_Converted_To_The_Index_Key_Type()
    {
        var (test, context) = Open();
        using var owned = test;
        using var session = context;

        test.Tags.Insert(new Tag { Slug = "ada", Kind = TagKind.Person, Weight = 9 });
        test.Tags.Insert(new Tag { Slug = "wal", Kind = TagKind.Topic, Weight = 3 });
        test.Tags.Insert(new Tag { Slug = "opfs", Kind = TagKind.Topic, Weight = 7 });

        // C# compares the enum and the byte as ints, so the values in the tree are ints, not the
        // types the indexes are keyed on.
        var byKind = PlanFor(test, context, context.Tags.Where(t => t.Kind == TagKind.Topic));
        Assert.Equal("Kind", byKind.IndexName);
        Assert.Equal(["opfs", "wal"], context.Tags.Where(t => t.Kind == TagKind.Topic).Select(t => t.Slug).Order().ToList());

        var byWeight = PlanFor(test, context, context.Tags.Where(t => t.Weight >= 5));
        Assert.Equal("Weight", byWeight.IndexName);
        Assert.Equal(["ada", "opfs"], context.Tags.Where(t => t.Weight >= 5).Select(t => t.Slug).Order().ToList());

        // An integer that no enum member's underlying value can hold must not be truncated onto one.
        var kindCode = 300;
        var noSuchKind = PlanFor(test, context, context.Tags.Where(t => (int)t.Kind == kindCode));
        Assert.Null(noSuchKind.IndexName);
        Assert.Empty(context.Tags.Where(t => (int)t.Kind == kindCode).ToList());

        // A value the key type cannot hold exactly is left to a residual filter, which is correct.
        var limit = 300;
        var overflow = PlanFor(test, context, context.Tags.Where(t => t.Weight >= limit));
        Assert.Null(overflow.IndexName);
        Assert.Empty(context.Tags.Where(t => t.Weight >= limit).ToList());
    }

    [Fact]
    public void An_Ordering_On_The_Range_Member_Sets_The_Direction_Instead_Of_Sorting()
    {
        var (test, context) = Open();
        using var owned = test;
        using var session = context;

        foreach (var age in (int[])[40, 10, 30, 20])
        {
            test.People.Insert(new Person { Id = age, Name = $"P{age}", City = "X", Age = age, Email = $"p{age}@x.com" });
        }

        var query = context.People.Where(p => p.Age >= 20).OrderByDescending(p => p.Age);
        var plan = PlanFor(test, context, query);

        Assert.Equal("Age", plan.IndexName);
        Assert.True(plan.IndexProvidesOrder);
        Assert.False(plan.HasOrdering);
        Assert.Equal([40, 30, 20], query.Select(p => p.Age).ToList());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_Bool_Predicate_Uses_Its_Index_However_It_Is_Written(bool active)
    {
        // "Where(p => !p.Active)" is how a bool filter is actually written, and it means the same
        // as "== false"; the everyday spelling must not be the one that falls back to a scan.
        var (test, context) = Open();
        using var owned = test;
        using var session = context;

        for (var id = 1; id <= 4; id++)
        {
            test.People.Insert(new Person
            {
                Id = id, Name = $"P{id}", City = "X", Age = id, Email = $"p{id}@x.com", Active = id % 2 == 0,
            });
        }

        var query = active ? context.People.Where(p => p.Active) : context.People.Where(p => !p.Active);
        var translated = TranslateFor(test, context, query);

        Assert.Equal("Active", translated.Plan.IndexName);
        // The index answers the whole predicate, so nothing is left to re-test per row.
        Assert.False(translated.Plan.HasOrdering);
        Assert.Equal(active ? [2, 4] : [1, 3], query.Select(p => p.Id).Order().ToList());
    }

    [Fact]
    public void A_Negated_Non_Member_Is_Left_As_A_Filter()
    {
        var (test, context) = Open();
        using var owned = test;
        using var session = context;

        var plan = PlanFor(test, context, context.People.Where(p => !(p.Age > 5 && p.Active)));

        Assert.Null(plan.IndexName);
    }

    [Fact]
    public void A_Where_After_An_OrderBy_Is_Still_Absorbed()
    {
        var (test, context) = Open();
        using var owned = test;
        using var session = context;

        var translated = TranslateFor(test, context, context.People.OrderBy(p => p.Name).Where(p => p.City == "London").Take(3));

        Assert.Null(translated.Remainder);
        Assert.Equal("City", translated.Plan.IndexName);
        Assert.Equal(3, translated.Plan.Take);
    }

    [Fact]
    public void A_Membership_Test_On_The_Key_Or_An_Index_Becomes_A_Batch_Of_Lookups()
    {
        var (test, context) = Open();
        using var owned = test;
        using var session = context;

        for (var i = 1; i <= 6; i++)
        {
            test.People.Insert(new Person
            {
                Id = i, Name = $"P{i}", City = i % 2 == 0 ? "London" : "Paris", Age = 20 + i,
                Email = i == 3 ? null : $"p{i}@x.com",
            });
        }

        // ids.Contains(p.Id): one dictionary probe per id, duplicates and misses ignored.
        var ids = new List<int> { 2, 5, 5, 42 };
        var byIds = PlanFor(test, context, context.People.Where(p => ids.Contains(p.Id)));
        Assert.True(byIds.UsesPrimaryKey);
        Assert.Equal([2, 5], context.People.Where(p => ids.Contains(p.Id)).Select(p => p.Id).Order().ToList());

        // An inline array against a hash index becomes the union of its lookups.
        var byCity = PlanFor(test, context, context.People.Where(p => new[] { "London", "Rome" }.Contains(p.City)));
        Assert.Equal("City", byCity.IndexName);
        Assert.Equal([2, 4, 6], context.People.Where(p => new[] { "London", "Rome" }.Contains(p.City)).Select(p => p.Id).Order().ToList());

        // A null in the list must match rows whose value is null, which no index holds - so it scans.
        var emails = new[] { "p1@x.com", null };
        var withNull = PlanFor(test, context, context.People.Where(p => emails.Contains(p.Email)));
        Assert.Null(withNull.IndexName);
        Assert.Equal([1, 3], context.People.Where(p => emails.Contains(p.Email)).Select(p => p.Id).Order().ToList());

        // A set with its own comparer may match values the (ordinal) index would not, so it scans.
        var caseless = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "london" };
        var custom = PlanFor(test, context, context.People.Where(p => caseless.Contains(p.City)));
        Assert.Null(custom.IndexName);
        Assert.Equal([2, 4, 6], context.People.Where(p => caseless.Contains(p.City)).Select(p => p.Id).Order().ToList());

        // A substring test on a string is not membership.
        var text = "P1P2";
        var substring = PlanFor(test, context, context.People.Where(p => text.Contains(p.Name)));
        Assert.Null(substring.IndexName);
        Assert.Equal([1, 2], context.People.Where(p => text.Contains(p.Name)).Select(p => p.Id).Order().ToList());
    }

    [Fact]
    public void Repeated_Paging_Operators_Compose_Correctly()
    {
        var (test, context) = Open();
        using var owned = test;
        using var session = context;

        for (var i = 1; i <= 10; i++)
        {
            test.People.Insert(new Person { Id = i, Name = $"P{i}", City = "X", Age = i, Email = $"p{i}@x.com" });
        }

        var skips = TranslateFor(test, context, context.People.OrderBy(p => p.Age).Skip(2).Skip(3));
        Assert.Equal(5, skips.Plan.Skip);
        Assert.Null(skips.Remainder);

        var takes = TranslateFor(test, context, context.People.OrderBy(p => p.Age).Take(5).Take(2));
        Assert.Equal(2, takes.Plan.Take);
        Assert.Null(takes.Remainder);

        // A Skip after a Take reorders the paging and cannot be folded into the plan.
        var takeThenSkip = TranslateFor(test, context, context.People.OrderBy(p => p.Age).Take(5).Skip(2));
        Assert.Equal(5, takeThenSkip.Plan.Take);
        Assert.Equal(0, takeThenSkip.Plan.Skip);
        Assert.NotNull(takeThenSkip.Remainder);

        Assert.Equal([6, 7, 8, 9, 10], context.People.OrderBy(p => p.Age).Skip(2).Skip(3).Select(p => p.Age).ToList());
        Assert.Equal([1, 2], context.People.OrderBy(p => p.Age).Take(5).Take(2).Select(p => p.Age).ToList());
        Assert.Equal([3, 4, 5], context.People.OrderBy(p => p.Age).Take(5).Skip(2).Select(p => p.Age).ToList());
    }
}
