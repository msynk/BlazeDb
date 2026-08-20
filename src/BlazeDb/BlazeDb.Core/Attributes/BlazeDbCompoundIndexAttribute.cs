namespace BlazeDb;

/// <summary>
/// Declares a hash index over several properties at once, so a filter on all of them resolves
/// to a single lookup instead of a lookup plus residual predicates. The index key is a value
/// tuple of the listed properties, in the order given.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true, Inherited = false)]
public sealed class BlazeDbCompoundIndexAttribute : Attribute
{
    public BlazeDbCompoundIndexAttribute(string name, params string[] propertyNames)
    {
        Name = name;
        PropertyNames = propertyNames;
    }

    public string Name { get; }

    public string[] PropertyNames { get; }

    /// <summary>Rejects writes that would duplicate the tuple of indexed values.</summary>
    public bool Unique { get; set; }
}
