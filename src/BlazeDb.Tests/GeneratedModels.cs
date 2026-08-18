namespace BlazeDb.Tests;

public enum Priority
{
    Low = 0,
    Normal = 1,
    High = 2,
}

/// <summary>Exercises the source generator across the supported type surface.</summary>
[Table("todos")]
public partial class TodoItem
{
    [Key]
    public Guid Id { get; set; }

    public string Title { get; set; } = "";

    public string? Notes { get; set; }

    [Index]
    public bool Done { get; set; }

    [Index]
    public string? Category { get; set; }

    [OrderedIndex]
    public DateTime CreatedAt { get; set; }

    public Priority Priority { get; set; }

    public int? Rating { get; set; }

    public decimal Cost { get; set; }

    public double Weight { get; set; }

    public TimeSpan Duration { get; set; }

    public DateTimeOffset? DueAt { get; set; }

    public List<string> Tags { get; set; } = [];

    public int[] Scores { get; set; } = [];

    public byte[] Payload { get; set; } = [];

    [Ignore]
    public string Transient { get; set; } = "not persisted";
}

/// <summary>Exercises explicit field numbers and a string key.</summary>
[Table]
public partial class Setting
{
    [Field(5)]
    [Key]
    public string Name { get; set; } = "";

    [Field(9)]
    public string Value { get; set; } = "";
}

/// <summary>Exercises unique constraints and compound indexes.</summary>
[Table("accounts")]
[CompoundIndex("TenantEmail", nameof(TenantId), nameof(Email), Unique = true)]
[CompoundOrderedIndex("TenantCreated", nameof(TenantId), nameof(CreatedAt))]
public partial class Account
{
    [Key]
    public int Id { get; set; }

    [Index(Unique = true)]
    public string Username { get; set; } = "";

    [Index]
    public int TenantId { get; set; }

    public string Email { get; set; } = "";

    [OrderedIndex(Unique = true)]
    public int? Slot { get; set; }

    public DateTime CreatedAt { get; set; }
}
