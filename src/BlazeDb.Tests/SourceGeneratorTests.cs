using BlazeDb;
using BlazeDb.Serialization;
using Xunit;

namespace BlazeDb.Tests;

public class SourceGeneratorTests
{
    private static TodoItem SampleTodo() => new()
    {
        Id = Guid.NewGuid(),
        Title = "Write tests",
        Notes = "some notes",
        Done = true,
        CreatedAt = new DateTime(2026, 8, 17, 10, 30, 0, DateTimeKind.Utc),
        Priority = Priority.High,
        Rating = 4,
        Cost = 12.34m,
        Weight = 1.75,
        Duration = TimeSpan.FromMinutes(90),
        DueAt = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.FromHours(3.5)),
        Tags = ["work", "urgent"],
        Scores = [1, 2, 3],
        Payload = [0xDE, 0xAD, 0xBE, 0xEF],
        Transient = "should not survive",
    };

    private static TodoItem Roundtrip(TodoItem item)
    {
        var writer = new BufferWriter();
        TodoItem.Table.RowWriter(writer, item);
        var reader = new BufferReader(writer.WrittenSpan);
        return TodoItem.Table.RowReader(ref reader);
    }

    [Fact]
    public void Generated_Descriptor_Has_Table_Name_And_Key_Selector()
    {
        Assert.Equal("todos", TodoItem.Table.Name);
        var item = SampleTodo();
        Assert.Equal(item.Id, TodoItem.Table.KeySelector(item));
    }

    [Fact]
    public void Generated_Serializer_Roundtrips_All_Supported_Types()
    {
        var item = SampleTodo();
        var decoded = Roundtrip(item);

        Assert.Equal(item.Id, decoded.Id);
        Assert.Equal(item.Title, decoded.Title);
        Assert.Equal(item.Notes, decoded.Notes);
        Assert.Equal(item.Done, decoded.Done);
        Assert.Equal(item.CreatedAt, decoded.CreatedAt);
        Assert.Equal(item.Priority, decoded.Priority);
        Assert.Equal(item.Rating, decoded.Rating);
        Assert.Equal(item.Cost, decoded.Cost);
        Assert.Equal(item.Weight, decoded.Weight);
        Assert.Equal(item.Duration, decoded.Duration);
        Assert.Equal(item.DueAt, decoded.DueAt);
        Assert.Equal(item.Tags, decoded.Tags);
        Assert.Equal(item.Scores, decoded.Scores);
        Assert.Equal(item.Payload, decoded.Payload);
    }

    [Fact]
    public void Ignored_Property_Is_Not_Serialized()
    {
        var decoded = Roundtrip(SampleTodo());
        Assert.Equal("not persisted", decoded.Transient);
    }

    [Fact]
    public void Null_Optionals_And_Empty_Collections_Roundtrip()
    {
        var item = new TodoItem { Id = Guid.NewGuid(), Title = "minimal" };
        var decoded = Roundtrip(item);

        Assert.Null(decoded.Notes);
        Assert.Null(decoded.Rating);
        Assert.Null(decoded.DueAt);
        Assert.Empty(decoded.Tags);
        Assert.Empty(decoded.Scores);
        Assert.Empty(decoded.Payload);
    }

    [Fact]
    public void Generated_Key_Serializer_Roundtrips()
    {
        var id = Guid.NewGuid();
        var writer = new BufferWriter();
        TodoItem.Table.KeyWriter(writer, id);
        var reader = new BufferReader(writer.WrittenSpan);
        Assert.Equal(id, TodoItem.Table.KeyReader(ref reader));
    }

    [Fact]
    public void Generated_Index_Definitions_Are_Wired_Into_Descriptor()
    {
        Assert.Equal(3, TodoItem.Table.Indexes.Count);
        Assert.Contains(TodoItem.Indexes.Done, TodoItem.Table.Indexes);
        Assert.Contains(TodoItem.Indexes.Category, TodoItem.Table.Indexes);
        Assert.Contains(TodoItem.Indexes.CreatedAt, TodoItem.Table.Indexes);
        Assert.IsType<HashIndexDefinition<TodoItem, bool>>(TodoItem.Indexes.Done);
        Assert.IsType<HashIndexDefinition<TodoItem, string?>>(TodoItem.Indexes.Category);
        Assert.IsType<OrderedIndexDefinition<TodoItem, DateTime>>(TodoItem.Indexes.CreatedAt);
    }

    [Fact]
    public async Task Generated_Table_Works_End_To_End_In_A_Database()
    {
        await using var db = await Database.OpenAsync(new DatabaseOptions().AddTable(TodoItem.Table));
        var todos = db.GetTable(TodoItem.Table);
        var item = SampleTodo();

        todos.Insert(item);

        Assert.Same(item, todos.Get(item.Id));
    }

    [Fact]
    public void Explicit_Field_Numbers_And_String_Key_Work()
    {
        Assert.Equal("Setting", Setting.Table.Name);

        var setting = new Setting { Name = "theme", Value = "dark" };
        var writer = new BufferWriter();
        Setting.Table.RowWriter(writer, setting);

        // Verify the explicit field numbers are on the wire.
        var raw = new BufferReader(writer.WrittenSpan);
        var (field1, _) = raw.ReadTag();
        Assert.Equal(5, field1);
        raw.SkipField(WireType.LengthDelimited);
        var (field2, _) = raw.ReadTag();
        Assert.Equal(9, field2);

        var reader = new BufferReader(writer.WrittenSpan);
        var decoded = Setting.Table.RowReader(ref reader);
        Assert.Equal("theme", decoded.Name);
        Assert.Equal("dark", decoded.Value);
    }

    [Fact]
    public void Old_Readers_Skip_Fields_Added_By_Newer_Writers()
    {
        // Simulate a newer schema writing an extra unknown field before known data.
        var writer = new BufferWriter();
        writer.WriteTag(200, WireType.LengthDelimited);
        writer.WriteString("from-the-future");
        var setting = new Setting { Name = "a", Value = "b" };
        Setting.Table.RowWriter(writer, setting);

        var reader = new BufferReader(writer.WrittenSpan);
        var decoded = Setting.Table.RowReader(ref reader);
        Assert.Equal("a", decoded.Name);
        Assert.Equal("b", decoded.Value);
    }
}
