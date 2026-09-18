using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BlazeDb.EntityFrameworkCore.Tests;

public class DbContextApiTests
{
    private static DbContextOptions<PeopleContext> IsolatedOptions() =>
        new DbContextOptionsBuilder<PeopleContext>()
            .UseBlazeDb("test-" + Guid.NewGuid().ToString("N"))
            .Options;

    [Fact]
    public void AddDbContext_Resolves_A_Working_Context()
    {
        var name = "di-" + Guid.NewGuid().ToString("N");
        var services = new ServiceCollection();
        services.AddDbContext<PeopleContext>(options => options.UseBlazeDb(name));
        using var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<PeopleContext>();
            context.People.Add(new Person { Id = 1, Name = "Ada", City = "London", Age = 36 });
            Assert.Equal(1, context.SaveChanges());
        }

        using (var scope = provider.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<PeopleContext>();
            Assert.Equal("Ada", context.People.Single().Name);
        }
    }

    [Fact]
    public void OnConfiguring_Is_Enough_To_Construct_A_Context()
    {
        var name = "on-config-" + Guid.NewGuid().ToString("N");
        using (var context = new OnConfiguringContext(name))
        {
            context.People.Add(new Person { Id = 1, Name = "Ada", City = "London", Age = 36 });
            context.SaveChanges();
        }

        using var fresh = new OnConfiguringContext(name);
        Assert.Equal("Ada", fresh.People.Single().Name);
    }

    [Fact]
    public void Contexts_From_The_Same_Options_Share_The_Store()
    {
        var options = IsolatedOptions();

        using (var context = new PeopleContext(options))
        {
            context.People.Add(new Person { Id = 1, Name = "Ada", City = "London", Age = 36 });
            context.SaveChanges();
        }

        using var next = new PeopleContext(options);
        Assert.Equal(1, next.People.Count());
    }

    [Fact]
    public void Separate_Store_Names_Isolate_Data()
    {
        using var first = new PeopleContext(IsolatedOptions());
        using var second = new PeopleContext(IsolatedOptions());

        first.People.Add(new Person { Id = 1, Name = "Ada", City = "London", Age = 36 });
        first.SaveChanges();

        Assert.Empty(second.People);
    }

    [Fact]
    public void A_Named_Store_Is_Shared_Across_Builders()
    {
        var name = "named-" + Guid.NewGuid().ToString("N");
        using var first = new PeopleContext(new DbContextOptionsBuilder<PeopleContext>().UseBlazeDb(name).Options);
        using var second = new PeopleContext(new DbContextOptionsBuilder<PeopleContext>().UseBlazeDb(name).Options);

        first.People.Add(new Person { Id = 1, Name = "Ada", City = "London", Age = 36 });
        first.SaveChanges();

        Assert.Equal("Ada", second.People.Single().Name);
    }

    [Fact]
    public void BeginTransaction_Spans_Several_Saves()
    {
        using var context = new PeopleContext(IsolatedOptions());

        using (var transaction = context.Database.BeginTransaction())
        {
            context.People.Add(new Person { Id = 1, Name = "Ada", City = "London", Age = 36 });
            context.SaveChanges();
            context.Orders.Add(new Order { Id = 10, PersonId = 1, Total = 4m });
            context.SaveChanges();
            transaction.Rollback();
        }

        Assert.Empty(context.People);
        Assert.Empty(context.Orders);
    }

    [Fact]
    public void Tables_Come_From_The_Model_Not_AddTable()
    {
        using var context = new PeopleContext(IsolatedOptions());

        context.People.Add(new Person { Id = 1, Name = "Ada", City = "London", Age = 36 });
        context.Tags.Add(new Tag { Slug = "wal", Kind = TagKind.Topic, Weight = 3 });
        context.SaveChanges();

        Assert.Equal("Ada", context.People.Find(1)!.Name);
        Assert.Equal("wal", context.Tags.Find("wal")!.Slug);
    }

    private sealed class OnConfiguringContext : DbContext
    {
        private readonly string _name;

        public OnConfiguringContext(string name) => _name = name;

        public DbSet<Person> People => Set<Person>();

        protected override void OnConfiguring(DbContextOptionsBuilder options) =>
            options.UseBlazeDb(_name);
    }
}
