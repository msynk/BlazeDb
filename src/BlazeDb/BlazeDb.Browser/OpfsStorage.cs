using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using BlazeDb.Storage;

namespace BlazeDb.Browser;

/// <summary>
/// OPFS (Origin Private File System) storage backend for Blazor WebAssembly. Uses the async
/// OPFS API from the main thread - good enough for BlazeDb's background group-commit flushes -
/// and the Web Locks API to guarantee a single writer tab per database.
/// </summary>
[SupportedOSPlatform("browser")]
public sealed class OpfsStorage : IQuotaAwareStorage, IDisposable
{
    private readonly JSObject _directory;
    private readonly string? _lockName;
    private bool _disposed;

    private OpfsStorage(JSObject directory, string? lockName)
    {
        _directory = directory;
        _lockName = lockName;
    }

    /// <summary>
    /// Opens (or creates) the OPFS directory for <paramref name="databaseName"/> and acquires
    /// its cross-tab write lock. Throws <see cref="DatabaseLockedException"/> when another tab
    /// already owns the database. <paramref name="moduleUrl"/> overrides where the JS module is
    /// loaded from; by default it is resolved relative to the document base.
    /// </summary>
    public static async ValueTask<OpfsStorage> CreateAsync(string databaseName, string? moduleUrl = null)
    {
        await OpfsInterop.EnsureModuleAsync(moduleUrl).ConfigureAwait(false);

        var lockName = "blazedb:" + databaseName;
        if (!await OpfsInterop.AcquireLock(lockName).ConfigureAwait(false))
        {
            throw new DatabaseLockedException(
                $"Database '{databaseName}' is already open in another tab. " +
                "BlazeDb allows a single writer tab per database.");
        }

        try
        {
            var directory = await OpfsInterop.OpenDatabaseDirectory(databaseName).ConfigureAwait(false);
            return new OpfsStorage(directory, lockName);
        }
        catch
        {
            OpfsInterop.ReleaseLock(lockName);
            throw;
        }
    }

    /// <summary>
    /// Opens the same OPFS directory without taking the writer lock, for a replica tab that only
    /// reads. Returns null when the database does not exist yet, so a replica can wait for the
    /// writer instead of creating an empty one. Pair with <see cref="DatabaseOptions.ReadOnly"/>.
    /// </summary>
    public static async ValueTask<OpfsStorage?> CreateReadOnlyAsync(string databaseName, string? moduleUrl = null)
    {
        await OpfsInterop.EnsureModuleAsync(moduleUrl).ConfigureAwait(false);
        var directory = await OpfsInterop.OpenExistingDatabaseDirectory(databaseName).ConfigureAwait(false);
        return directory is null ? null : new OpfsStorage(directory, lockName: null);
    }

    public async ValueTask<byte[]?> ReadAsync(string name, CancellationToken cancellationToken = default)
    {
        using var bytes = await OpfsInterop.ReadFile(_directory, name).ConfigureAwait(false);
        if (bytes is null)
        {
            return null;
        }
        var length = bytes.GetPropertyAsInt32("length");
        var result = new byte[length];
        OpfsInterop.CopyBytes(bytes, result);
        return result;
    }

    public async ValueTask WriteAtomicAsync(string name, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
        await OpfsInterop.WriteAtomic(_directory, name, data.ToArray()).ConfigureAwait(false);

    public async ValueTask AppendAsync(string name, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
        await OpfsInterop.AppendFile(_directory, name, data.ToArray()).ConfigureAwait(false);

    public async ValueTask DeleteAsync(string name, CancellationToken cancellationToken = default) =>
        await OpfsInterop.DeleteFile(_directory, name).ConfigureAwait(false);

    /// <summary>
    /// Usage and allowance for the whole origin, from <c>navigator.storage.estimate()</c>.
    /// Browsers deliberately blur these numbers to limit fingerprinting, so treat them as a
    /// guide rather than an exact byte count.
    /// </summary>
    public async ValueTask<StorageQuota?> GetQuotaAsync(CancellationToken cancellationToken = default)
    {
        using var estimate = await OpfsInterop.StorageEstimate().ConfigureAwait(false);
        return ReadEstimate(estimate);
    }

    /// <summary>Shared with the IndexedDB backend: the estimate covers the origin, not one backend.</summary>
    internal static StorageQuota? ReadEstimate(JSObject? estimate) =>
        estimate is null
            ? null
            : new StorageQuota(
                (long)estimate.GetPropertyAsDouble("usage"),
                (long)estimate.GetPropertyAsDouble("quota"));

    /// <summary>
    /// Asks the browser to mark this origin's storage as persistent, exempting the database from
    /// eviction when the device runs low on space. Returns whether persistence is in effect.
    /// Without it, OPFS data is "best-effort" and the browser may clear it.
    /// </summary>
    public static async ValueTask<bool> RequestPersistenceAsync(string? moduleUrl = null)
    {
        await OpfsInterop.EnsureModuleAsync(moduleUrl).ConfigureAwait(false);
        return await OpfsInterop.RequestPersistence().ConfigureAwait(false);
    }

    /// <summary>Releases the cross-tab write lock and the directory handle.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_lockName is not null)
        {
            OpfsInterop.ReleaseLock(_lockName);
        }
        _directory.Dispose();
    }
}
