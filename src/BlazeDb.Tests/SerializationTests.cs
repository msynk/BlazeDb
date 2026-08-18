using BlazeDb.Serialization;
using Xunit;

namespace BlazeDb.Tests;

public class SerializationTests
{
    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(-1L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    [InlineData(300L)]
    [InlineData(-300L)]
    public void VarInt_Roundtrips(long value)
    {
        var writer = new BufferWriter();
        writer.WriteVarInt(value);
        var reader = new BufferReader(writer.WrittenSpan);
        Assert.Equal(value, reader.ReadVarInt());
        Assert.Equal(0, reader.Remaining);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(127UL)]
    [InlineData(128UL)]
    [InlineData(ulong.MaxValue)]
    public void VarUInt_Roundtrips(ulong value)
    {
        var writer = new BufferWriter();
        writer.WriteVarUInt(value);
        var reader = new BufferReader(writer.WrittenSpan);
        Assert.Equal(value, reader.ReadVarUInt());
    }

    [Fact]
    public void All_Primitive_Types_Roundtrip()
    {
        var guid = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var local = DateTime.Now;
        var dto = DateTimeOffset.Now;
        var ts = TimeSpan.FromMilliseconds(123456.789);

        var writer = new BufferWriter();
        writer.WriteBool(true);
        writer.WriteSingle(3.14f);
        writer.WriteDouble(Math.E);
        writer.WriteString("hello \u00e9\u00fc\u4e16\u754c");
        writer.WriteBytes([1, 2, 3]);
        writer.WriteGuid(guid);
        writer.WriteDecimal(1234567.891m);
        writer.WriteDateTime(now);
        writer.WriteDateTime(local);
        writer.WriteDateTimeOffset(dto);
        writer.WriteTimeSpan(ts);

        var reader = new BufferReader(writer.WrittenSpan);
        Assert.True(reader.ReadBool());
        Assert.Equal(3.14f, reader.ReadSingle());
        Assert.Equal(Math.E, reader.ReadDouble());
        Assert.Equal("hello \u00e9\u00fc\u4e16\u754c", reader.ReadString());
        Assert.Equal(new byte[] { 1, 2, 3 }, reader.ReadBytes().ToArray());
        Assert.Equal(guid, reader.ReadGuid());
        Assert.Equal(1234567.891m, reader.ReadDecimal());
        Assert.Equal(now, reader.ReadDateTime());
        Assert.Equal(local, reader.ReadDateTime());
        Assert.Equal(DateTimeKind.Local, local.Kind);
        Assert.Equal(dto, reader.ReadDateTimeOffset());
        Assert.Equal(ts, reader.ReadTimeSpan());
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void Unknown_Fields_Can_Be_Skipped()
    {
        var writer = new BufferWriter();
        writer.WriteTag(1, WireType.VarInt);
        writer.WriteVarInt(42);
        writer.WriteTag(99, WireType.LengthDelimited);
        writer.WriteString("future field");
        writer.WriteTag(100, WireType.Fixed64);
        writer.WriteFixed64(123);
        writer.WriteTag(101, WireType.Fixed32);
        writer.WriteFixed32(7);
        writer.WriteTag(2, WireType.VarInt);
        writer.WriteVarInt(-7);

        var reader = new BufferReader(writer.WrittenSpan);
        long? first = null;
        long? second = null;
        while (reader.Remaining > 0)
        {
            var (field, wireType) = reader.ReadTag();
            switch (field)
            {
                case 1:
                    first = reader.ReadVarInt();
                    break;
                case 2:
                    second = reader.ReadVarInt();
                    break;
                default:
                    reader.SkipField(wireType);
                    break;
            }
        }

        Assert.Equal(42, first);
        Assert.Equal(-7, second);
    }

    [Fact]
    public void Row_Serialization_Roundtrips_Through_Descriptor()
    {
        var person = new Person(7, "Ada", 36);
        var writer = new BufferWriter();
        PersonTable.Descriptor.RowWriter(writer, person);

        var reader = new BufferReader(writer.WrittenSpan);
        var decoded = PersonTable.Descriptor.RowReader(ref reader);

        Assert.Equal(person, decoded);
    }

    [Fact]
    public void Truncated_Buffer_Throws_InvalidData()
    {
        var writer = new BufferWriter();
        writer.WriteString("hello");
        var bytes = writer.ToArray().AsSpan(0, 3);

        var thrown = false;
        try
        {
            var reader = new BufferReader(bytes);
            reader.ReadString();
        }
        catch (InvalidDataException)
        {
            thrown = true;
        }
        Assert.True(thrown);
    }
}
