using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using BlazeDb.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BlazeDb.EntityFrameworkCore.Metadata;

/// <summary>Maps entity types onto the engine's tables.</summary>
internal interface IBlazeDbTableCache
{
    /// <summary>The engine instance the context was configured with.</summary>
    Database Database { get; }

    IBlazeDbTableBinding GetBinding(IEntityType entityType);
}

internal sealed class BlazeDbTableCache : IBlazeDbTableCache
{
    // Bindings are reflection-built and immutable, and a database outlives any one context, so
    // they are cached against the database rather than rebuilt for every context instance. The
    // weak table lets a closed database and its bindings be collected together.
    private static readonly ConditionalWeakTable<Database, ConcurrentDictionary<IEntityType, IBlazeDbTableBinding>>
        Bindings = new();

    private readonly ConcurrentDictionary<IEntityType, IBlazeDbTableBinding> _bindings;

    public BlazeDbTableCache(IDbContextOptions options)
    {
        Database = options.FindExtension<BlazeDbOptionsExtension>()?.Database
            ?? throw new InvalidOperationException(
                "No BlazeDb database was configured. Call optionsBuilder.UseBlazeDb(database).");
        _bindings = Bindings.GetOrCreateValue(Database);
    }

    public Database Database { get; }

    public IBlazeDbTableBinding GetBinding(IEntityType entityType) =>
        _bindings.GetOrAdd(
            entityType,
            static (type, database) =>
                BlazeDbTableResolver.CreateBinding(database, BlazeDbTableResolver.Resolve(type)),
            Database);
}
