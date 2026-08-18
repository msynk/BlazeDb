using Microsoft.CodeAnalysis;

namespace BlazeDb.SourceGen;

/// <summary>Maps C# types to scalar kinds and their wire encodings.</summary>
internal static class TypeMap
{
    private static readonly SymbolDisplayFormat FullFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    public static string Display(ITypeSymbol type) => type.ToDisplayString(FullFormat);

    public static ScalarKind? GetScalarKind(ITypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Enum)
        {
            return ScalarKind.Enum;
        }

        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean: return ScalarKind.Bool;
            case SpecialType.System_SByte: return ScalarKind.SByte;
            case SpecialType.System_Byte: return ScalarKind.Byte;
            case SpecialType.System_Int16: return ScalarKind.Int16;
            case SpecialType.System_UInt16: return ScalarKind.UInt16;
            case SpecialType.System_Int32: return ScalarKind.Int32;
            case SpecialType.System_UInt32: return ScalarKind.UInt32;
            case SpecialType.System_Int64: return ScalarKind.Int64;
            case SpecialType.System_UInt64: return ScalarKind.UInt64;
            case SpecialType.System_Char: return ScalarKind.Char;
            case SpecialType.System_Single: return ScalarKind.Single;
            case SpecialType.System_Double: return ScalarKind.Double;
            case SpecialType.System_Decimal: return ScalarKind.Decimal;
            case SpecialType.System_String: return ScalarKind.String;
            case SpecialType.System_DateTime: return ScalarKind.DateTime;
        }

        if (type is IArrayTypeSymbol array && array.ElementType.SpecialType == SpecialType.System_Byte)
        {
            return ScalarKind.ByteArray;
        }

        return type.ToDisplayString() switch
        {
            "System.Guid" => ScalarKind.Guid,
            "System.DateTimeOffset" => ScalarKind.DateTimeOffset,
            "System.TimeSpan" => ScalarKind.TimeSpan,
            _ => null,
        };
    }

    public static bool IsReferenceScalar(ScalarKind kind) =>
        kind == ScalarKind.String || kind == ScalarKind.ByteArray;

    public static string WireType(ScalarKind kind)
    {
        switch (kind)
        {
            case ScalarKind.Single:
                return "global::BlazeDb.Serialization.WireType.Fixed32";
            case ScalarKind.Double:
            case ScalarKind.DateTime:
            case ScalarKind.TimeSpan:
                return "global::BlazeDb.Serialization.WireType.Fixed64";
            case ScalarKind.Decimal:
            case ScalarKind.String:
            case ScalarKind.ByteArray:
            case ScalarKind.Guid:
            case ScalarKind.DateTimeOffset:
                return "global::BlazeDb.Serialization.WireType.LengthDelimited";
            default:
                return "global::BlazeDb.Serialization.WireType.VarInt";
        }
    }

    /// <summary>Expression writing a non-null scalar value.</summary>
    public static string WriteExpr(ScalarKind kind, string writer, string value)
    {
        switch (kind)
        {
            case ScalarKind.Bool: return $"{writer}.WriteBool({value})";
            case ScalarKind.SByte:
            case ScalarKind.Int16:
            case ScalarKind.Int32:
            case ScalarKind.Int64: return $"{writer}.WriteVarInt({value})";
            case ScalarKind.Byte:
            case ScalarKind.UInt16:
            case ScalarKind.UInt32:
            case ScalarKind.UInt64:
            case ScalarKind.Char: return $"{writer}.WriteVarUInt({value})";
            case ScalarKind.Single: return $"{writer}.WriteSingle({value})";
            case ScalarKind.Double: return $"{writer}.WriteDouble({value})";
            case ScalarKind.Decimal: return $"{writer}.WriteDecimal({value})";
            case ScalarKind.String: return $"{writer}.WriteString({value})";
            case ScalarKind.ByteArray: return $"{writer}.WriteBytes({value})";
            case ScalarKind.Guid: return $"{writer}.WriteGuid({value})";
            case ScalarKind.DateTime: return $"{writer}.WriteDateTime({value})";
            case ScalarKind.DateTimeOffset: return $"{writer}.WriteDateTimeOffset({value})";
            case ScalarKind.TimeSpan: return $"{writer}.WriteTimeSpan({value})";
            case ScalarKind.Enum: return $"{writer}.WriteVarInt((long){value})";
            default: return "";
        }
    }

    /// <summary>Expression reading a scalar value. <paramref name="scalarTypeDisplay"/> is used for casts.</summary>
    public static string ReadExpr(ScalarKind kind, string reader, string scalarTypeDisplay)
    {
        switch (kind)
        {
            case ScalarKind.Bool: return $"{reader}.ReadBool()";
            case ScalarKind.SByte: return $"(sbyte){reader}.ReadVarInt()";
            case ScalarKind.Int16: return $"(short){reader}.ReadVarInt()";
            case ScalarKind.Int32: return $"(int){reader}.ReadVarInt()";
            case ScalarKind.Int64: return $"{reader}.ReadVarInt()";
            case ScalarKind.Byte: return $"(byte){reader}.ReadVarUInt()";
            case ScalarKind.UInt16: return $"(ushort){reader}.ReadVarUInt()";
            case ScalarKind.UInt32: return $"(uint){reader}.ReadVarUInt()";
            case ScalarKind.UInt64: return $"{reader}.ReadVarUInt()";
            case ScalarKind.Char: return $"(char){reader}.ReadVarUInt()";
            case ScalarKind.Single: return $"{reader}.ReadSingle()";
            case ScalarKind.Double: return $"{reader}.ReadDouble()";
            case ScalarKind.Decimal: return $"{reader}.ReadDecimal()";
            case ScalarKind.String: return $"{reader}.ReadString()";
            case ScalarKind.ByteArray: return $"{reader}.ReadBytes().ToArray()";
            case ScalarKind.Guid: return $"{reader}.ReadGuid()";
            case ScalarKind.DateTime: return $"{reader}.ReadDateTime()";
            case ScalarKind.DateTimeOffset: return $"{reader}.ReadDateTimeOffset()";
            case ScalarKind.TimeSpan: return $"{reader}.ReadTimeSpan()";
            case ScalarKind.Enum: return $"({scalarTypeDisplay}){reader}.ReadVarInt()";
            default: return "";
        }
    }

    /// <summary>Default value expression for read locals of non-nullable scalars.</summary>
    public static string DefaultExpr(ScalarKind kind, string scalarTypeDisplay)
    {
        switch (kind)
        {
            case ScalarKind.String: return "\"\"";
            case ScalarKind.ByteArray: return "global::System.Array.Empty<byte>()";
            default: return "default";
        }
    }
}
