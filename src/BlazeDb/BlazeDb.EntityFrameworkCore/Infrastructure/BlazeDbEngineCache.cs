using BlazeDb.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BlazeDb.EntityFrameworkCore.Infrastructure;

/// <summary>
/// Holds opened engines keyed by store identity so scoped contexts share one in-memory dataset,
/// the way a SQLite file outlives any one connection.
/// </summary>
internal sealed class BlazeDbEngineCache
{
    private readonly Dictionary<object, Slot> _slots = [];
    private readonly object _gate = new();

    public BlazeDbDatabase GetOrCreate(BlazeDbOptionsExtension extension, IModel model)
    {
        if (extension.ExistingDatabase is { } existing)
        {
            EnsureTables(existing, model);
            return existing;
        }

        var key = extension.StoreKey;
        lock (_gate)
        {
            var slot = GetSlot(key);
            if (slot.Database is not null)
            {
                EnsureTables(slot.Database, model);
                return slot.Database;
            }

            slot.Database = Open(extension, model);
            return slot.Database;
        }
    }

    public async ValueTask<BlazeDbDatabase> GetOrCreateAsync(
        BlazeDbOptionsExtension extension, IModel model, CancellationToken cancellationToken)
    {
        if (extension.ExistingDatabase is { } existing)
        {
            EnsureTables(existing, model);
            return existing;
        }

        var key = extension.StoreKey;
        Slot slot;
        lock (_gate)
        {
            slot = GetSlot(key);
            if (slot.Database is not null)
            {
                EnsureTables(slot.Database, model);
                return slot.Database;
            }
        }

        var opened = await OpenAsync(extension, model, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (slot.Database is null)
            {
                slot.Database = opened;
                return slot.Database;
            }

            EnsureTables(slot.Database, model);
        }

        if (!ReferenceEquals(slot.Database, opened))
        {
            await opened.DisposeAsync().ConfigureAwait(false);
        }

        return slot.Database!;
    }

    public async ValueTask ReleaseAsync(object key)
    {
        Slot? slot;
        lock (_gate)
        {
            if (!_slots.Remove(key, out slot) || slot.Database is null)
            {
                return;
            }
        }

        await slot.Database.DisposeAsync().ConfigureAwait(false);
    }

    private Slot GetSlot(object key)
    {
        if (!_slots.TryGetValue(key, out var slot))
        {
            _slots[key] = slot = new Slot();
        }
        return slot;
    }

    private static BlazeDbDatabase Open(BlazeDbOptionsExtension extension, IModel model)
    {
        var open = OpenAsync(extension, model, CancellationToken.None);
        if (open.IsCompletedSuccessfully)
        {
            return open.Result;
        }

        if (SynchronizationContext.Current is not null)
        {
            throw new InvalidOperationException(
                "Opening this BlazeDb storage requires an asynchronous start. Call " +
                "await context.Database.EnsureCreatedAsync() before the first synchronous query.");
        }

        return open.AsTask().GetAwaiter().GetResult();
    }

    private static ValueTask<BlazeDbDatabase> OpenAsync(
        BlazeDbOptionsExtension extension, IModel model, CancellationToken cancellationToken)
    {
        var tables = TablesFrom(model);
        return BlazeDbDatabase.OpenAsync(extension.CreateEngineOptions(tables), cancellationToken);
    }

    internal static void EnsureTables(BlazeDbDatabase database, IModel model)
    {
        foreach (var table in TablesFrom(model))
        {
            database.EnsureTable(table);
        }
    }

    internal static List<BlazeDbTableDescriptor> TablesFrom(IModel model)
    {
        var tables = new List<BlazeDbTableDescriptor>();
        foreach (var entityType in model.GetEntityTypes())
        {
            if (entityType.IsOwned())
            {
                continue;
            }
            tables.Add(BlazeDbTableResolver.Resolve(entityType));
        }
        return tables;
    }

    private sealed class Slot
    {
        public BlazeDbDatabase? Database;
    }
}
