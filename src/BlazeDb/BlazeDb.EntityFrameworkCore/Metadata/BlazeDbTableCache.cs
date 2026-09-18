using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using BlazeDb.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BlazeDb.EntityFrameworkCore.Metadata;

internal sealed class BlazeDbTableCache : IBlazeDbTableCache
{
    // Bindings are reflection-built and immutable, and a store outlives any one context, so
    // they are cached against the engine rather than rebuilt for every context instance.
    private static readonly ConditionalWeakTable<BlazeDbDatabase, ConcurrentDictionary<IEntityType, IBlazeDbTableBinding>>
        Bindings = new();

    private readonly ConcurrentDictionary<IEntityType, IBlazeDbTableBinding> _bindings;

    public BlazeDbTableCache(IDbContextOptions options, IModel model, BlazeDbEngineCache cache)
    {
        var extension = options.FindExtension<BlazeDbOptionsExtension>()
                        ?? throw new InvalidOperationException(
                            "No BlazeDb store was configured. Call optionsBuilder.UseBlazeDb().");
        Database = cache.GetOrCreate(extension, model);
        _bindings = Bindings.GetOrCreateValue(Database);
    }

    public BlazeDbDatabase Database { get; }

    public IBlazeDbTableBinding GetBinding(IEntityType entityType) =>
        _bindings.GetOrAdd(
            entityType,
            static (type, database) =>
                BlazeDbTableResolver.CreateBinding(database, type),
            Database);
}
