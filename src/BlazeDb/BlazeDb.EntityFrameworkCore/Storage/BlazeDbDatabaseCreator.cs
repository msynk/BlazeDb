using BlazeDb.EntityFrameworkCore.Infrastructure;
using BlazeDb.EntityFrameworkCore.Metadata;
using BlazeDb.Storage;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlazeDb.EntityFrameworkCore.Storage;

/// <summary>
/// Opens the engine from the context model (EnsureCreated) and drops the shared store
/// (EnsureDeleted), matching the contract other providers implement for a file or in-memory database.
/// </summary>
internal sealed class BlazeDbDatabaseCreator : IDatabaseCreator
{
    private readonly IDbContextOptions _options;
    private readonly IModel _model;
    private readonly BlazeDbEngineCache _cache;
    private readonly IBlazeDbTableCache _tables;

    public BlazeDbDatabaseCreator(
        IDbContextOptions options, IModel model, BlazeDbEngineCache cache, IBlazeDbTableCache tables)
    {
        _options = options;
        _model = model;
        _cache = cache;
        _tables = tables;
    }

    public bool EnsureCreated()
    {
        _ = _tables.Database;
        return false;
    }

    public async Task<bool> EnsureCreatedAsync(CancellationToken cancellationToken = default)
    {
        var extension = Extension();
        await _cache.GetOrCreateAsync(extension, _model, cancellationToken).ConfigureAwait(false);
        return false;
    }

    public bool CanConnect() => _tables.Database is not null;

    public Task<bool> CanConnectAsync(CancellationToken cancellationToken = default) =>
        cancellationToken.IsCancellationRequested
            ? Task.FromCanceled<bool>(cancellationToken)
            : Task.FromResult(CanConnect());

    public bool EnsureDeleted() => EnsureDeletedAsync().GetAwaiter().GetResult();

    public async Task<bool> EnsureDeletedAsync(CancellationToken cancellationToken = default)
    {
        var extension = Extension();
        if (!extension.OwnsEngine)
        {
            return false;
        }

        var storage = extension.Storage;
        await _cache.ReleaseAsync(extension.StoreKey).ConfigureAwait(false);
        _tables.Reset();
        if (storage is not null)
        {
            await WipeAsync(storage, cancellationToken).ConfigureAwait(false);
        }
        return true;
    }

    private BlazeDbOptionsExtension Extension() =>
        _options.FindExtension<BlazeDbOptionsExtension>()
        ?? throw new InvalidOperationException("No BlazeDb store was configured. Call optionsBuilder.UseBlazeDb().");

    private static async Task WipeAsync(IBlazeDbStorage storage, CancellationToken cancellationToken)
    {
        if (storage is BlazeDbInMemoryStorage memory)
        {
            foreach (var name in memory.FileNames)
            {
                await storage.DeleteAsync(name, cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        await storage.DeleteAsync("manifest.blz", cancellationToken).ConfigureAwait(false);
    }
}
