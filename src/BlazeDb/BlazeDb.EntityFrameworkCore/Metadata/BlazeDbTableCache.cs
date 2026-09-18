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

    private readonly IDbContextOptions _options;
    private readonly IModel _model;
    private readonly BlazeDbEngineCache _cache;
    private BlazeDbDatabase? _database;
    private int _version;
    private ConcurrentDictionary<IEntityType, IBlazeDbTableBinding>? _bindings;

    public BlazeDbTableCache(IDbContextOptions options, IModel model, BlazeDbEngineCache cache)
    {
        _options = options;
        _model = model;
        _cache = cache;
    }

    public BlazeDbDatabase Database
    {
        get
        {
            // The engine is looked up again once any store has been released: another context's
            // EnsureDeleted disposes the engine this one cached, and queries against a disposed
            // engine fail in ways that say nothing about why. A changed version costs a dictionary
            // lookup; an unchanged one costs a compare.
            if (_database is not null && _version == _cache.Version)
            {
                return _database;
            }

            var extension = Extension();
            _version = _cache.Version;
            _database = _cache.GetOrCreate(extension, _model, out _);
            _bindings = Bindings.GetOrCreateValue(_database);
            return _database;
        }
    }

    public IBlazeDbTableBinding GetBinding(IEntityType entityType)
    {
        // Through the property, so the bindings are the current engine's and not those of one
        // another context has since released.
        var database = Database;
        return _bindings!.GetOrAdd(
            entityType,
            static (type, database) => BlazeDbTableResolver.CreateBinding(database, type),
            database);
    }

    public void Reset()
    {
        _database = null;
        _bindings = null;
    }

    private BlazeDbOptionsExtension Extension() =>
        _options.FindExtension<BlazeDbOptionsExtension>()
        ?? throw new InvalidOperationException(
            "No BlazeDb store was configured. Call optionsBuilder.UseBlazeDb().");
}
