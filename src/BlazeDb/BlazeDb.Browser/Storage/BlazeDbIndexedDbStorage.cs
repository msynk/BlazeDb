using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using BlazeDb.Storage;

namespace BlazeDb.Browser;

/// <summary>
/// IndexedDB storage backend, for browsers and modes where OPFS is unavailable - Firefox private
/// windows being the common case. Every BlazeDb file becomes one record in a single object store.
/// <para>
/// IndexedDB offers no append, so extending the WAL means rewriting its record; the cost grows
/// with WAL size, which makes this slower than <see cref="BlazeDbOpfsStorage"/> under write-heavy loads.
/// Lower <see cref="BlazeDbDatabaseOptions.CheckpointWalSize"/> to compact more often when using it.
/// Reads are unaffected, since BlazeDb serves them from memory.
/// </para>
/// </summary>
[SupportedOSPlatform("browser")]
public sealed class BlazeDbIndexedDbStorage : IBlazeDbQuotaAwareStorage, IDisposable
{
    private readonly JSObject _db;
    private readonly string _lockName;
    private readonly string? _opfsModuleUrl;
    private bool _disposed;

    private BlazeDbIndexedDbStorage(JSObject db, string lockName, string? opfsModuleUrl)
    {
        _db = db;
        _lockName = lockName;
        _opfsModuleUrl = opfsModuleUrl;
    }

    /// <summary>
    /// Whether OPFS can be used in this context. Use it to choose a backend at startup and fall
    /// back to IndexedDB only when needed.
    /// </summary>
    public static async ValueTask<bool> IsOpfsAvailableAsync(string? moduleUrl = null)
    {
        await BlazeDbIndexedDbInterop.EnsureModuleAsync(moduleUrl).ConfigureAwait(false);
        return await BlazeDbIndexedDbInterop.IsOpfsAvailable().ConfigureAwait(false);
    }

    /// <summary>
    /// Opens the IndexedDB database backing <paramref name="databaseName"/> and takes its
    /// cross-tab write lock, so the single-writer guarantee matches the OPFS backend's.
    /// </summary>
    /// <param name="databaseName">Name of the IndexedDB database (and of the Web Lock).</param>
    /// <param name="moduleUrl">Where to load <c>blazedb-idb.js</c> from; by default it is resolved relative to the document base.</param>
    /// <param name="opfsModuleUrl">
    /// Where to load <c>blazedb-opfs.js</c> from. This backend needs it as well, because the Web
    /// Locks election and the storage estimate live in that module; an app that overrides one URL
    /// almost always has to override both.
    /// </param>
    public static async ValueTask<BlazeDbIndexedDbStorage> CreateAsync(
        string databaseName, string? moduleUrl = null, string? opfsModuleUrl = null)
    {
        await BlazeDbIndexedDbInterop.EnsureModuleAsync(moduleUrl).ConfigureAwait(false);
        // The lock lives in the OPFS module, which owns the Web Locks helpers.
        await BlazeDbOpfsInterop.EnsureModuleAsync(opfsModuleUrl).ConfigureAwait(false);

        var lockName = "blazedb:" + databaseName;
        if (!await BlazeDbOpfsInterop.AcquireLock(lockName).ConfigureAwait(false))
        {
            throw new BlazeDbDatabaseLockedException(
                $"BlazeDbDatabase '{databaseName}' is already open in another tab. " +
                "BlazeDb allows a single writer tab per database.");
        }

        try
        {
            var db = await BlazeDbIndexedDbInterop.OpenDatabase(databaseName).ConfigureAwait(false);
            return new BlazeDbIndexedDbStorage(db, lockName, opfsModuleUrl);
        }
        catch
        {
            BlazeDbOpfsInterop.ReleaseLock(lockName);
            throw;
        }
    }

    public async ValueTask<byte[]?> ReadAsync(string name, CancellationToken cancellationToken = default)
    {
        using var bytes = await BlazeDbIndexedDbInterop.ReadFile(_db, name).ConfigureAwait(false);
        if (bytes is null)
        {
            return null;
        }
        var result = new byte[bytes.GetPropertyAsInt32("length")];
        BlazeDbIndexedDbInterop.CopyBytes(bytes, result);
        return result;
    }

    public async ValueTask WriteAtomicAsync(string name, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
        await BlazeDbIndexedDbInterop.WriteAtomic(_db, name, data.ToArray()).ConfigureAwait(false);

    public async ValueTask AppendAsync(string name, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
        await BlazeDbIndexedDbInterop.AppendFile(_db, name, data.ToArray()).ConfigureAwait(false);

    public async ValueTask DeleteAsync(string name, CancellationToken cancellationToken = default) =>
        await BlazeDbIndexedDbInterop.DeleteFile(_db, name).ConfigureAwait(false);

    /// <summary>Origin usage and allowance; the same estimate OPFS reports, since it spans the origin.</summary>
    public async ValueTask<BlazeDbStorageQuota?> GetQuotaAsync(CancellationToken cancellationToken = default)
    {
        await BlazeDbOpfsInterop.EnsureModuleAsync(_opfsModuleUrl).ConfigureAwait(false);
        using var estimate = await BlazeDbOpfsInterop.StorageEstimate().ConfigureAwait(false);
        return BlazeDbOpfsStorage.ReadEstimate(estimate);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        BlazeDbOpfsInterop.ReleaseLock(_lockName);
        BlazeDbIndexedDbInterop.CloseDatabase(_db);
        _db.Dispose();
    }
}
