using BlazeDb;
using BlazeDb.Serialization;

namespace BlazeDb.Demo.Data;

public enum Priority
{
    Low = 0,
    Normal = 1,
    High = 2,
    Critical = 3,
}

/// <summary>
/// The demo's main table. It deliberately covers the whole surface the source
/// generator supports: every scalar kind, nullable values, collections, an
/// ignored property, both index kinds, a custom index name and explicit field
/// numbers are all represented somewhere across this file.
/// </summary>
[Table("todos")]
public partial class TodoEntry
{
    [Key]
    public Guid Id { get; set; }

    public string Title { get; set; } = "";

    public string? Notes { get; set; }

    [Index]
    public bool Done { get; set; }

    [Index]
    public string? Category { get; set; }

    [Index(Name = "ByPriority")]
    public Priority Priority { get; set; }

    [OrderedIndex]
    public DateTime CreatedAt { get; set; }

    /// <summary>Estimated minutes of work - the numeric ordered index.</summary>
    [OrderedIndex]
    public int Effort { get; set; }

    public int? Rating { get; set; }

    public decimal Cost { get; set; }

    public double Weight { get; set; }

    public TimeSpan Duration { get; set; }

    public DateTimeOffset? DueAt { get; set; }

    public List<string> Tags { get; set; } = [];

    public int[] Scores { get; set; } = [];

    public byte[] Payload { get; set; } = [];

    /// <summary>Never written to the WAL or a snapshot.</summary>
    [Ignore]
    public string Transient { get; set; } = "computed at runtime";

    public TodoEntry Clone() => new()
    {
        Id = Id,
        Title = Title,
        Notes = Notes,
        Done = Done,
        Category = Category,
        Priority = Priority,
        CreatedAt = CreatedAt,
        Effort = Effort,
        Rating = Rating,
        Cost = Cost,
        Weight = Weight,
        Duration = Duration,
        DueAt = DueAt,
        Tags = [.. Tags],
        Scores = [.. Scores],
        Payload = [.. Payload],
    };
}

/// <summary>
/// A string-keyed table with explicit, stable field numbers. Used by the demo to
/// persist its own UI preferences, which proves multi-table databases work.
/// </summary>
[Table("settings")]
public partial class Setting
{
    [Field(1)]
    [Key]
    public string Name { get; set; } = "";

    [Field(2)]
    public string Value { get; set; } = "";

    [Field(3)]
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Proves the source generator is optional: this table's descriptor is written by
/// hand, so a positional record (which the generator rejects) works fine.
/// </summary>
public sealed record Metric(int Id, string Name, double Value, DateTime At);

public static class MetricTable
{
    // Declared before the descriptor: static initializers run in declaration order.
    public static readonly HashIndexDefinition<Metric, string> ByName =
        new("ByName", static m => m.Name, StringComparer.OrdinalIgnoreCase);

    public static readonly OrderedIndexDefinition<Metric, double> ByValue =
        new("ByValue", static m => m.Value);

    public static readonly TableDescriptor<int, Metric> Descriptor = new(
        "metrics",
        static m => m.Id,
        WriteRow,
        ReadRow,
        static (w, k) => w.WriteVarInt(k),
        static (ref BufferReader r) => (int)r.ReadVarInt(),
        [ByName, ByValue]);

    private static void WriteRow(BufferWriter writer, Metric row)
    {
        writer.WriteTag(1, WireType.VarInt);
        writer.WriteVarInt(row.Id);
        writer.WriteTag(2, WireType.LengthDelimited);
        writer.WriteString(row.Name);
        writer.WriteTag(3, WireType.Fixed64);
        writer.WriteDouble(row.Value);
        writer.WriteTag(4, WireType.Fixed64);
        writer.WriteDateTime(row.At);
    }

    private static Metric ReadRow(ref BufferReader reader)
    {
        var id = 0;
        var name = "";
        var value = 0d;
        var at = default(DateTime);

        while (reader.Remaining > 0)
        {
            var (field, wireType) = reader.ReadTag();
            switch (field)
            {
                case 1: id = (int)reader.ReadVarInt(); break;
                case 2: name = reader.ReadString(); break;
                case 3: value = reader.ReadDouble(); break;
                case 4: at = reader.ReadDateTime(); break;
                default: reader.SkipField(wireType); break;
            }
        }

        return new Metric(id, name, value, at);
    }
}
