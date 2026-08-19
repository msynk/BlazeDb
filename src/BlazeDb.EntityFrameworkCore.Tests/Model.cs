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

    public DbSet<Tag> Tags => Set<Tag>();
}

public enum TagKind : byte
{
    Topic,
    Person,
    Place,
}

/// <summary>
/// A table whose key is not called <c>Id</c>, with an enum and a narrow integer under its indexes
/// and a property the serializer ignores - the shapes that need the provider to read BlazeDb's own
/// attributes and to convert predicate values before handing them to an index.
/// </summary>
[Table("tags")]
public partial class Tag
{
    [Key]
    public string Slug { get; set; } = "";

    [Index]
    public TagKind Kind { get; set; }

    [OrderedIndex]
    public byte Weight { get; set; }

    [Ignore]
    public string Transient { get; set; } = "";
}
