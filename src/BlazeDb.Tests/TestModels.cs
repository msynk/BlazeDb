using BlazeDb;
using BlazeDb.Serialization;

namespace BlazeDb.Tests;

/// <summary>
/// A hand-written descriptor equivalent to what the source generator emits, so engine tests
/// do not depend on the generator.
/// </summary>
public sealed record Person(int Id, string Name, int Age);

public static class PersonTable
{
    public static readonly TableDescriptor<int, Person> Descriptor = new(
        "people",
        static p => p.Id,
        WriteRow,
        ReadRow,
        static (w, k) => w.WriteVarInt(k),
        static (ref BufferReader r) => (int)r.ReadVarInt());

    private static void WriteRow(BufferWriter writer, Person row)
    {
        writer.WriteTag(1, WireType.VarInt);
        writer.WriteVarInt(row.Id);
        writer.WriteTag(2, WireType.LengthDelimited);
        writer.WriteString(row.Name);
        writer.WriteTag(3, WireType.VarInt);
        writer.WriteVarInt(row.Age);
    }

    private static Person ReadRow(ref BufferReader reader)
    {
        var id = 0;
        var name = "";
        var age = 0;
        while (reader.Remaining > 0)
        {
            var (field, wireType) = reader.ReadTag();
            switch (field)
            {
                case 1:
                    id = (int)reader.ReadVarInt();
                    break;
                case 2:
                    name = reader.ReadString();
                    break;
                case 3:
                    age = (int)reader.ReadVarInt();
                    break;
                default:
                    reader.SkipField(wireType);
                    break;
            }
        }
        return new Person(id, name, age);
    }
}

public static class TestDb
{
    public static async Task<Database> OpenInMemoryAsync() =>
        await Database.OpenAsync(new DatabaseOptions().AddTable(PersonTable.Descriptor));
}
