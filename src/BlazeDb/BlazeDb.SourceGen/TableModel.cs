using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace BlazeDb.SourceGen;

internal enum ScalarKind
{
    Bool,
    SByte,
    Byte,
    Int16,
    UInt16,
    Int32,
    UInt32,
    Int64,
    UInt64,
    Char,
    Single,
    Double,
    Decimal,
    String,
    ByteArray,
    Guid,
    DateTime,
    DateTimeOffset,
    TimeSpan,
    Enum,
}

internal enum PropCategory
{
    Scalar,
    NullableValueScalar,
    NullableRefScalar,
    ListCollection,
    ArrayCollection,
}

internal sealed class PropModel
{
    public string Name = "";
    public int FieldNumber;
    public PropCategory Category;
    public ScalarKind Kind;

    /// <summary>Fully-qualified display of the property type as declared (for locals).</summary>
    public string TypeDisplay = "";

    /// <summary>Fully-qualified display of the non-nullable scalar (or element) type.</summary>
    public string ScalarTypeDisplay = "";

    public bool IsKey;
    public string? HashIndexName;
    public bool HashIndexUnique;
    public string? OrderedIndexName;
    public bool OrderedIndexUnique;
}

/// <summary>An index over a tuple of properties, declared at type level.</summary>
internal sealed class CompoundIndexModel
{
    public string Name = "";
    public bool Ordered;
    public bool Unique;
    public List<PropModel> Props = new List<PropModel>();

    /// <summary>The value-tuple type used as the index key, e.g. "(string A, int B)".</summary>
    public string TupleType =>
        "(" + string.Join(", ", Props.ConvertAll(p => p.TypeDisplay + " " + p.Name)) + ")";
}

internal sealed class TableModel
{
    public string? Namespace;
    public string TypeName = "";
    public string TypeKeyword = "class";
    public string TableName = "";
    public string HintName = "";
    public PropModel? Key;
    public List<PropModel> Props = new List<PropModel>();
    public List<CompoundIndexModel> CompoundIndexes = new List<CompoundIndexModel>();
}

internal sealed class DiagnosticInfo
{
    public DiagnosticInfo(DiagnosticDescriptor descriptor, Location? location, params object[] args)
    {
        Descriptor = descriptor;
        Location = location;
        Args = args;
    }

    public DiagnosticDescriptor Descriptor { get; }

    public Location? Location { get; }

    public object[] Args { get; }
}

internal sealed class GenerationResult
{
    public TableModel? Model;
    public List<DiagnosticInfo> Diagnostics = new List<DiagnosticInfo>();
}
