namespace BlazeDb;

/// <summary>
/// Marks a partial class or record as a BlazeDb table. The source generator emits a
/// <c>TableDescriptor</c> (binary serializer, key extractor, index definitions) as a static
/// <c>Table</c> member on the type.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class TableAttribute : Attribute
{
    public TableAttribute(string? name = null) => Name = name;

    /// <summary>Table name. Defaults to the type name.</summary>
    public string? Name { get; }
}

/// <summary>Marks the primary key property of a table row.</summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class KeyAttribute : Attribute;

/// <summary>
/// Assigns an explicit, stable field number used in the binary format. Without it, field
/// numbers are assigned by declaration order (1-based). Use explicit numbers once a schema
/// is deployed, so reordering properties does not break persisted data.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class FieldAttribute : Attribute
{
    public FieldAttribute(int number) => Number = number;

    public int Number { get; }
}

/// <summary>Excludes a property from serialization.</summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class IgnoreAttribute : Attribute;

/// <summary>Declares a hash (equality) secondary index over this property.</summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class IndexAttribute : Attribute
{
    /// <summary>Index name. Defaults to the property name.</summary>
    public string? Name { get; set; }

    /// <summary>Rejects writes that would duplicate an indexed value. Null values are exempt.</summary>
    public bool Unique { get; set; }
}

/// <summary>Declares an ordered secondary index over this property, enabling range scans.</summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class OrderedIndexAttribute : Attribute
{
    /// <summary>Index name. Defaults to the property name.</summary>
    public string? Name { get; set; }

    /// <summary>Rejects writes that would duplicate an indexed value. Null values are exempt.</summary>
    public bool Unique { get; set; }
}

/// <summary>
/// Declares a hash index over several properties at once, so a filter on all of them resolves
/// to a single lookup instead of a lookup plus residual predicates. The index key is a value
/// tuple of the listed properties, in the order given.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true, Inherited = false)]
public sealed class CompoundIndexAttribute : Attribute
{
    public CompoundIndexAttribute(string name, params string[] propertyNames)
    {
        Name = name;
        PropertyNames = propertyNames;
    }

    public string Name { get; }

    public string[] PropertyNames { get; }

    /// <summary>Rejects writes that would duplicate the tuple of indexed values.</summary>
    public bool Unique { get; set; }
}

/// <summary>
/// Declares an ordered index over several properties at once. Rows sort lexicographically by
/// the tuple, so a range over a leading-property prefix is still a single contiguous scan.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true, Inherited = false)]
public sealed class CompoundOrderedIndexAttribute : Attribute
{
    public CompoundOrderedIndexAttribute(string name, params string[] propertyNames)
    {
        Name = name;
        PropertyNames = propertyNames;
    }

    public string Name { get; }

    public string[] PropertyNames { get; }

    /// <summary>Rejects writes that would duplicate the tuple of indexed values.</summary>
    public bool Unique { get; set; }
}
