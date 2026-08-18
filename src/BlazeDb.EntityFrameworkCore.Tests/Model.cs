using Microsoft.EntityFrameworkCore;

namespace BlazeDb.EntityFrameworkCore.Tests;

/// <summary>
/// A plain BlazeDb table type. Nothing about it is EF-specific: the provider maps entity types
/// onto the descriptor the source generator emits from these attributes.
/// </summary>
[Table("people")]
[CompoundIndex("CityAge", nameof(City), nameof(Age))]
public partial class Person
{
    [Key]
    public int Id { get; set; }

    public string Name { get; set; } = "";

    [Index]
    public string City { get; set; } = "";

    [OrderedIndex]
    public int Age { get; set; }

    [Index(Unique = true)]
    public string? Email { get; set; }
}

/// <summary>A second table, so saves spanning two tables can be shown to be one transaction.</summary>
[Table("orders")]
public partial class Order
{
    [Key]
    public int Id { get; set; }

    [Index]
    public int PersonId { get; set; }

    public decimal Total { get; set; }
}

public sealed class PeopleContext : DbContext
{
    public PeopleContext(DbContextOptions<PeopleContext> options) : base(options)
    {
    }

    public DbSet<Person> People => Set<Person>();

    public DbSet<Order> Orders => Set<Order>();
}
