using System.Reflection;
using BlazeDb.EntityFrameworkCore.Query;
using BlazeDb.Querying;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Update;

namespace BlazeDb.EntityFrameworkCore.Metadata;

/// <summary>
/// The non-generic face of one mapped table, so the provider can work with entity types without
/// knowing their key or row type statically.
/// </summary>
internal interface IBlazeDbTableBinding
{
    Type RowType { get; }

    string TableName { get; }

    IReadOnlyList<BlazeDbIndex> Indexes { get; }

    /// <summary>Creates an empty plan typed to this table's row type.</summary>
    QueryPlan CreatePlan();

    /// <summary>Runs a translated plan and returns the live row objects it selects.</summary>
    IEnumerable<object> Execute(QueryPlan plan);

    /// <summary>Wraps the plan's rows in a queryable, for the LINQ the plan could not absorb.</summary>
    IQueryable AsQueryable(IEnumerable<object> rows);

    void Insert(object row);

    /// <summary>
    /// Applies an update where <paramref name="row"/> is the instance the table already holds and
    /// has been mutated in place. <paramref name="previousValues"/> carries the values the indexes
    /// were built from, without which stale index entries would be left behind.
    /// </summary>
    void UpdateInPlace(object row, object previousValues);

    /// <summary>Deletes a row, using <paramref name="previousValues"/> to retract index entries.</summary>
    void Delete(object row, object previousValues);
}

/// <summary>
/// One secondary index, described in the terms the translator reasons about: which properties it
/// covers, whether it can answer ranges as well as equality, and the type of its key - a tuple
/// when the index is compound, which is what a lookup against it has to be given.
/// </summary>
internal sealed record BlazeDbIndex(
    string Name, IReadOnlyList<string> Members, bool Ordered, Type KeyType, object Definition);

internal sealed class BlazeDbTableBinding<TKey, TRow> : IBlazeDbTableBinding
    where TKey : notnull
    where TRow : class
{
    private readonly Table<TKey, TRow> _table;

    public BlazeDbTableBinding(Table<TKey, TRow> table)
    {
        _table = table;
        Indexes = table.Descriptor.Indexes
            .Select(i => new BlazeDbIndex(
                i.Name,
                i.Members,
                Ordered: i.GetType().GetGenericTypeDefinition() == typeof(OrderedIndexDefinition<,>),
                KeyType: i.GetType().GetGenericArguments()[1],
                Definition: i))
            .ToArray();
    }

    public Type RowType => typeof(TRow);

    public string TableName => _table.Name;

    public IReadOnlyList<BlazeDbIndex> Indexes { get; }

    public QueryPlan CreatePlan() => new QueryPlan<TRow>();

    public IEnumerable<object> Execute(QueryPlan plan) =>
        ((QueryPlan<TRow>)plan).Build(Query<TKey, TRow>.From(_table)).Execute();

    public IQueryable AsQueryable(IEnumerable<object> rows) => rows.Cast<TRow>().AsQueryable();

    public void Insert(object row) => _table.Insert((TRow)row);

    public void UpdateInPlace(object row, object previousValues) =>
        _table.UpdateInPlace((TRow)row, (TRow)previousValues);

    public void Delete(object row, object previousValues) =>
        _table.DeleteInPlace(_table.Descriptor.KeySelector((TRow)row), (TRow)previousValues);
}

/// <summary>
/// Resolves the <see cref="TableDescriptor{TKey,TRow}"/> for an entity type. Descriptors are
/// generated as a static <c>Table</c> member on each <c>[Table]</c> type, so the convention is to
/// look for that; <c>UseBlazeDbTable</c> overrides it.
/// </summary>
internal static class BlazeDbTableResolver
{
    public const string DescriptorAnnotation = "BlazeDb:Descriptor";

    public static TableDescriptor Resolve(IEntityType entityType)
    {
        if (entityType.FindAnnotation(DescriptorAnnotation)?.Value is TableDescriptor configured)
        {
            return configured;
        }

        var clrType = entityType.ClrType;
        var descriptor = FindConventionalDescriptor(clrType);
        if (descriptor is not null)
        {
            return descriptor;
        }

        throw new InvalidOperationException(
            $"No BlazeDb table descriptor was found for entity type '{clrType.Name}'. Annotate the " +
            "type with [BlazeDb.Table] so the source generator emits one, or call " +
            $"modelBuilder.Entity<{clrType.Name}>().UseBlazeDbTable(descriptor).");
    }

    private static TableDescriptor? FindConventionalDescriptor(Type clrType)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy;

        foreach (var field in clrType.GetFields(flags))
        {
            if (Matches(field.FieldType, clrType))
            {
                return (TableDescriptor?)field.GetValue(null);
            }
        }
        foreach (var property in clrType.GetProperties(flags))
        {
            if (Matches(property.PropertyType, clrType))
            {
                return (TableDescriptor?)property.GetValue(null);
            }
        }
        return null;
    }

    private static bool Matches(Type memberType, Type clrType) =>
        memberType.IsGenericType &&
        memberType.GetGenericTypeDefinition() == typeof(TableDescriptor<,>) &&
        memberType.GetGenericArguments()[1] == clrType;

    /// <summary>Creates the strongly-typed binding for a descriptor whose type arguments are only known at runtime.</summary>
    public static IBlazeDbTableBinding CreateBinding(Database database, TableDescriptor descriptor)
    {
        var arguments = descriptor.GetType().GetGenericArguments();
        var bindingType = typeof(BlazeDbTableBinding<,>).MakeGenericType(arguments);
        var table = GetTableMethod
            .MakeGenericMethod(arguments)
            .Invoke(database, [descriptor])!;
        return (IBlazeDbTableBinding)Activator.CreateInstance(bindingType, table)!;
    }

    private static readonly MethodInfo GetTableMethod =
        typeof(Database).GetMethod(nameof(Database.GetTable))!;
}

/// <summary>
/// Rebuilds an entity as it was when the change tracker last saw it. Rows are live objects, so a
/// modified entity no longer carries the values its index entries were built from - this restores
/// them into a throwaway copy the engine can use to retract those entries.
/// </summary>
internal static class OriginalValueFactory
{
    public static object Create(IUpdateEntry entry)
    {
        var entityType = entry.EntityType;
        var original = Activator.CreateInstance(entityType.ClrType)
            ?? throw new InvalidOperationException(
                $"Entity type '{entityType.ClrType.Name}' could not be instantiated. BlazeDb needs a " +
                "parameterless constructor to reconstruct an entity's original values.");

        foreach (var property in entityType.GetProperties())
        {
            var value = entry.GetOriginalValue(property);
            switch (property.PropertyInfo, property.FieldInfo)
            {
                case ({ } info, _) when info.CanWrite:
                    info.SetValue(original, value);
                    break;
                case (_, { } field):
                    field.SetValue(original, value);
                    break;
            }
        }
        return original;
    }
}
