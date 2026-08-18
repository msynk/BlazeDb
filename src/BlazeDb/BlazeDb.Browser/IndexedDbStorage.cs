using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using BlazeDb.Storage;

namespace BlazeDb.Browser;

/// <summary>Bindings to the blazedb-idb.js ES module.</summary>
[SupportedOSPlatform("browser")]
internal static partial class IndexedDbInterop
{
    private const string ModuleName = BrowserModules.IndexedDb;

    public static Task EnsureModuleAsync(string? moduleUrl) => BrowserModules.EnsureAsync(ModuleName, moduleUrl);

    [JSImport("openDatabase", ModuleName)]
    public static partial Task<JSObject> OpenDatabase(string databaseName);

    [JSImport("readFile", ModuleName)]
    public static partial Task<JSObject?> ReadFile(JSObject db, string name);

    [JSImport("copyBytes", ModuleName)]
    public static partial void CopyBytes(JSObject source, [JSMarshalAs<JSType.MemoryView>] Span<byte> destination);

    [JSImport("writeAtomic", ModuleName)]
    public static partial Task WriteAtomic(JSObject db, string name, byte[] bytes);

    [JSImport("appendFile", ModuleName)]
    public static partial Task AppendFile(JSObject db, string name, byte[] bytes);

    [JSImport("deleteFile", ModuleName)]
    public static partial Task DeleteFile(JSObject db, string name);

    [JSImport("closeDatabase", ModuleName)]
    public static partial void CloseDatabase(JSObject db);

    [JSImport("isOpfsAvailable", ModuleName)]
    public static partial Task<bool> IsOpfsAvailable();
}

/// <summary>
/// IndexedDB storage backend, for browsers and modes where OPFS is unavailable — Firefox private
/// windows being the common case. Every BlazeDb file becomes one record in a single object store.
/// <para>
/// IndexedDB offers no append, so extending the WAL means rewriting its record; the cost grows
/// with WAL size, which makes this slower than <see cref="OpfsStorage"/> under write-heavy loads.
/// Lower <see cref="DatabaseOptions.CheckpointWalSize"/> to compact more often when using it.
/// Reads are unaffected, since BlazeDb serves them from memory.
/// </para>
/// </summary>
[SupportedOSPlatform("browser")]
public sealed class IndexedDbStorage : IQuotaAwareStorage, IDisposable
{
    private readonly JSObject _db;
    private readonly string _lockName;
    private bool _disposed;

    private IndexedDbStorage(JSObject db, string lockName)
    {
        _db = db;
        _lockName = lockName;
    }

    /// <summary>
    /// Whether OPFS can be used in this context. Use it to choose a backend at startup and fall
    /// back to IndexedDB only when needed.
    /// </summary>
    public static async ValueTask<bool> IsOpfsAvailableAsync(string? moduleUrl = null)
    {
        await IndexedDbInterop.EnsureModuleAsync(moduleUrl).ConfigureAwait(false);
        return await IndexedDbInterop.IsOpfsAvailable().ConfigureAwait(false);
    }

    /// <summary>
    /// Opens the IndexedDB database backing <paramref name="databaseName"/> and takes its
    /// cross-tab write lock, so the single-writer guarantee matches the OPFS backend's.
    /// </summary>
    public static async ValueTask<IndexedDbStorage> CreateAsync(string databaseName, string? moduleUrl = null)
    {
        await IndexedDbInterop.EnsureModuleAsync(moduleUrl).ConfigureAwait(false);
        // The lock lives in the OPFS module, which owns the Web Locks helpers.
        await OpfsInterop.EnsureModuleAsync(null).ConfigureAwait(false);

        var lockName = "blazedb:" + databaseName;
        if (!await OpfsInterop.AcquireLock(lockName).ConfigureAwait(false))
        {
            throw new DatabaseLockedException(
                $"Database '{databaseName}' is already open in another tab. " +
                "BlazeDb allows a single writer tab per database.");
        }

        try
        {
            var db = await IndexedDbInterop.OpenDatabase(databaseName).ConfigureAwait(false);
            return new IndexedDbStorage(db, lockName);
        }
        catch
        {
            OpfsInterop.ReleaseLock(lockName);
            throw;
        }
    }

    public async ValueTask<byte[]?> ReadAsync(string name, CancellationToken cancellationToken = default)
    {
        using var bytes = await IndexedDbInterop.ReadFile(_db, name).ConfigureAwait(false);
        if (bytes is null)
        {
            return null;
        }
        var result = new byte[bytes.GetPropertyAsInt32("length")];
        IndexedDbInterop.CopyBytes(bytes, result);
        return result;
    }

    public async ValueTask WriteAtomicAsync(string name, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
        await IndexedDbInterop.WriteAtomic(_db, name, data.ToArray()).ConfigureAwait(false);

    public async ValueTask AppendAsync(string name, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
        await IndexedDbInterop.AppendFile(_db, name, data.ToArray()).ConfigureAwait(false);

    public async ValueTask DeleteAsync(string name, CancellationToken cancellationToken = default) =>
        await IndexedDbInterop.DeleteFile(_db, name).ConfigureAwait(false);

    /// <summary>Origin usage and allowance; the same estimate OPFS reports, since it spans the origin.</summary>
    public async ValueTask<StorageQuota?> GetQuotaAsync(CancellationToken cancellationToken = default)
    {
        await OpfsInterop.EnsureModuleAsync(null).ConfigureAwait(false);
        using var estimate = await OpfsInterop.StorageEstimate().ConfigureAwait(false);
        return OpfsStorage.ReadEstimate(estimate);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        OpfsInterop.ReleaseLock(_lockName);
        IndexedDbInterop.CloseDatabase(_db);
        _db.Dispose();
    }
}
