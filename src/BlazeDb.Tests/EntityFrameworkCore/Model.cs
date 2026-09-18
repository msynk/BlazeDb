using Microsoft.EntityFrameworkCore;

namespace BlazeDb.EntityFrameworkCore.Tests;

/// <summary>
/// A plain BlazeDb table type. Nothing about it is EF-specific: the provider maps entity types
/// onto the descriptor the source generator emits from these attributes.
/// </summary>
[BlazeDbTable("people")]
[BlazeDbCompoundIndex("CityAge", nameof(City), nameof(Age))]
public partial class Person
{
    [BlazeDbKey]
    public int Id { get; set; }

    public string Name { get; set; } = "";

    [BlazeDbIndex]
    public string City { get; set; } = "";

    [BlazeDbOrderedIndex]
    public int Age { get; set; }

    [BlazeDbIndex(Unique = true)]
    public string? Email { get; set; }

    /// <summary>A bool under an index, so <c>Where(p =&gt; !p.Active)</c> has one to reach for.</summary>
    [BlazeDbIndex]
    public bool Active { get; set; }
}

/// <summary>A second table, so saves spanning two tables can be shown to be one transaction.</summary>
[BlazeDbTable("orders")]
public partial class Order
{
    [BlazeDbKey]
    public int Id { get; set; }

    [BlazeDbIndex]
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

/// <summary>A context that knows only part of the store, for sharing it with <see cref="PeopleContext"/>.</summary>
public sealed class PeopleOnlyContext : DbContext
{
    public PeopleOnlyContext(DbContextOptions<PeopleOnlyContext> options) : base(options)
    {
    }

    public DbSet<Person> People => Set<Person>();
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
[BlazeDbTable("tags")]
public partial class Tag
{
    [BlazeDbKey]
    public string Slug { get; set; } = "";

    [BlazeDbIndex]
    public TagKind Kind { get; set; }

    [BlazeDbOrderedIndex]
    public byte Weight { get; set; }

    [BlazeDbIgnore]
    public string Transient { get; set; } = "";
}
