using System.Buffers.Binary;
using System.Text;

namespace BlazeDb.Serialization;

/// <summary>
/// Span-based binary reader mirroring <see cref="BufferWriter"/>. A ref struct so rows can be
/// decoded with zero intermediate allocations.
/// </summary>
public ref struct BufferReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;

    public BufferReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _pos = 0;
    }

    public int Position => _pos;

    public int Remaining => _data.Length - _pos;

    public byte ReadByte()
    {
        CheckAvailable(1);
        return _data[_pos++];
    }

    public ReadOnlySpan<byte> ReadRaw(int count)
    {
        CheckAvailable(count);
        var span = _data.Slice(_pos, count);
        _pos += count;
        return span;
    }

    public ulong ReadVarUInt()
    {
        ulong result = 0;
        var shift = 0;
        while (true)
        {
            CheckAvailable(1);
            var b = _data[_pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }
            shift += 7;
            if (shift >= 64)
            {
                throw new InvalidDataException("Malformed varint.");
            }
        }
    }

    public long ReadVarInt()
    {
        var value = ReadVarUInt();
        return (long)(value >> 1) ^ -(long)(value & 1);
    }

    public uint ReadFixed32()
    {
        CheckAvailable(4);
        var value = BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(_pos));
        _pos += 4;
        return value;
    }

    public ulong ReadFixed64()
    {
        CheckAvailable(8);
        var value = BinaryPrimitives.ReadUInt64LittleEndian(_data.Slice(_pos));
        _pos += 8;
        return value;
    }

    public bool ReadBool() => ReadVarUInt() != 0;

    public float ReadSingle() => BitConverter.UInt32BitsToSingle(ReadFixed32());

    public double ReadDouble() => BitConverter.UInt64BitsToDouble(ReadFixed64());

    public string ReadString()
    {
        var length = checked((int)ReadVarUInt());
        return Encoding.UTF8.GetString(ReadRaw(length));
    }

    /// <summary>Reads a length-delimited byte block.</summary>
    public ReadOnlySpan<byte> ReadBytes()
    {
        var length = checked((int)ReadVarUInt());
        return ReadRaw(length);
    }

    public Guid ReadGuid() => new(ReadBytes());

    public decimal ReadDecimal()
    {
        var span = ReadBytes();
        Span<int> bits = stackalloc int[4];
        for (var i = 0; i < 4; i++)
        {
            bits[i] = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(i * 4));
        }
        return new decimal(bits);
    }

    public DateTime ReadDateTime() => DateTime.FromBinary((long)ReadFixed64());

    public DateTimeOffset ReadDateTimeOffset()
    {
        var span = ReadBytes();
        var ticks = BinaryPrimitives.ReadInt64LittleEndian(span);
        var offsetTicks = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(8));
        return new DateTimeOffset(ticks, new TimeSpan(offsetTicks));
    }

    public TimeSpan ReadTimeSpan() => new((long)ReadFixed64());

    public (int FieldNumber, WireType WireType) ReadTag()
    {
        var tag = ReadVarUInt();
        return ((int)(tag >> 3), (WireType)(tag & 0x7));
    }

    /// <summary>Skips a field value of the given wire type (unknown field tolerance).</summary>
    public void SkipField(WireType wireType)
    {
        switch (wireType)
        {
            case WireType.VarInt:
                ReadVarUInt();
                break;
            case WireType.Fixed64:
                ReadRaw(8);
                break;
            case WireType.LengthDelimited:
                ReadRaw(checked((int)ReadVarUInt()));
                break;
            case WireType.Fixed32:
                ReadRaw(4);
                break;
            default:
                throw new InvalidDataException($"Unknown wire type {wireType}.");
        }
    }

    private readonly void CheckAvailable(int count)
    {
        if (_pos + count > _data.Length)
        {
            throw new InvalidDataException("Unexpected end of buffer.");
        }
    }
}
