using BlazeDb.Serialization;
using BlazeDb.Storage;

namespace BlazeDb.Wal;

/// <summary>
/// Owns the durability pipeline: encodes committed operations into WAL records buffered in
/// memory, flushes them to storage on a group-commit interval, checkpoints (snapshot + fresh
/// WAL) when the log grows, and recovers state on open.
///
/// File layout (all referenced from an atomic manifest):
///   manifest.blz     magic BLZM, version, generation, snapshot file, wal file, snapshot LSN, CRC
///   snapshot-N.blz   magic BLZS, version, LSN, per-table row dumps, CRC
///   wal-N.blz        sequence of records: [len:4][lsn:8][crc:4][payload]
/// Recovery = load snapshot, then replay WAL records with LSN &gt; snapshot LSN, stopping at the
/// first torn/corrupt record.
/// </summary>
internal sealed class WalManager : IAsyncDisposable
{
    private const string ManifestFile = "manifest.blz";
    private static ReadOnlySpan<byte> ManifestMagic => "BLZM"u8;
    private static ReadOnlySpan<byte> SnapshotMagic => "BLZS"u8;
    private const byte FormatVersion = 1;
    private const int RecordHeaderSize = 16;
    private const int MaxRecordSize = 256 * 1024 * 1024;

    private readonly Database _db;
    private readonly IStorage _storage;
    private readonly DatabaseOptions _options;

    // Pending WAL bytes not yet flushed; guarded by _db.SyncRoot.
    private readonly BufferWriter _pending = new(16 * 1024);
    private readonly BufferWriter _payloadScratch = new(4 * 1024);
    private readonly BufferWriter _valueScratch = new(1024);

    // Serializes all storage I/O (flushes vs checkpoints).
    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private Task _loop = Task.CompletedTask;
    private volatile Exception? _fault;

    private ulong _generation;
    private string _walFile = "";
    private string _snapshotFile = "";
    private long _snapshotLsn;
    private long _lastLsn;
    private long _walSize;

    private StorageQuota? _lastQuota;
    private bool _quotaPressureReported;
    private long _bytesSinceQuotaCheck;

    private WalManager(Database db, DatabaseOptions options)
    {
        _db = db;
        _storage = options.Storage!;
        _options = options;
    }

    public static async ValueTask<WalManager> OpenAsync(Database db, DatabaseOptions options, CancellationToken ct)
    {
        var manager = new WalManager(db, options);
        await manager.RecoverAsync(ct).ConfigureAwait(false);
        if (!options.ReadOnly)
        {
            manager._loop = manager.RunFlusherLoopAsync();
        }
        return manager;
    }

    /// <summary>
    /// Rebuilds in-memory state from what is currently on storage. Used by replica tabs to catch
    /// up with the writer; recovery is re-run from scratch rather than diffed, which keeps the
    /// path identical to opening the database and so avoids a second, rarely exercised code path.
    /// </summary>
    public async ValueTask ReloadAsync(CancellationToken ct = default)
    {
        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_db.SyncRoot)
            {
                _db.ResetTables();
                _pending.Reset();
                _generation = 0;
                _snapshotFile = "";
                _walFile = "";
                _snapshotLsn = 0;
                _lastLsn = 0;
                _walSize = 0;
            }
            await RecoverAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    // ---- Commit path (called under _db.SyncRoot) ----

    public void AppendCommit(IReadOnlyList<ITxnOp> ops)
    {
        var lsn = ++_lastLsn;

        _payloadScratch.Reset();
        _payloadScratch.WriteVarUInt((ulong)ops.Count);
        foreach (var op in ops)
        {
            op.EncodeRedo(_payloadScratch, _valueScratch);
        }

        var payload = _payloadScratch.WrittenSpan;
        _pending.WriteFixed32((uint)payload.Length);
        _pending.WriteFixed64((ulong)lsn);
        _pending.WriteFixed32(Crc32.Compute(payload));
        _pending.WriteRaw(payload);
    }

    // ---- Flush / checkpoint ----

    public async ValueTask FlushAsync(CancellationToken ct = default)
    {
        await EnsureQuotaHeadroomAsync(ct).ConfigureAwait(false);
        await FlushPendingAsync(ct).ConfigureAwait(false);
        _fault = null;
    }

    /// <summary>
    /// The error the most recent background flush or checkpoint failed with, or null once one has
    /// since succeeded. Lets an application notice that durability is lagging without waiting for
    /// an explicit flush to hit the same problem.
    /// </summary>
    public Exception? LastBackgroundError => _fault;

    /// <summary>Current storage usage, or null when the backend cannot report it.</summary>
    public async ValueTask<StorageQuota?> GetQuotaAsync(CancellationToken ct = default)
    {
        if (_storage is not IQuotaAwareStorage quotaAware)
        {
            return null;
        }
        var quota = await quotaAware.GetQuotaAsync(ct).ConfigureAwait(false);
        _lastQuota = quota;
        _bytesSinceQuotaCheck = 0;
        return quota;
    }

    /// <summary>
    /// Refuses to flush when the write would eat into the configured reserve, compacting first in
    /// case that frees enough. Called before <see cref="FlushPendingAsync"/> rather than inside it
    /// because a checkpoint needs the same I/O lock.
    /// </summary>
    private async ValueTask EnsureQuotaHeadroomAsync(CancellationToken ct)
    {
        if (_storage is not IQuotaAwareStorage quotaAware)
        {
            return;
        }

        long pendingBytes;
        lock (_db.SyncRoot)
        {
            pendingBytes = _pending.Length;
        }
        if (pendingBytes == 0 || await HasRoomForAsync(quotaAware, pendingBytes, ct).ConfigureAwait(false))
        {
            return;
        }

        // A checkpoint replaces the WAL with a compact snapshot and deletes the superseded files,
        // which is usually enough to get back under the limit.
        try
        {
            if (await CheckpointCoreAsync(ct).ConfigureAwait(false))
            {
                _lastQuota = null;
                lock (_db.SyncRoot)
                {
                    pendingBytes = _pending.Length;
                }
                if (pendingBytes == 0 || await HasRoomForAsync(quotaAware, pendingBytes, ct).ConfigureAwait(false))
                {
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not BlazeDbException)
        {
            // Compaction itself ran out of room; fall through to the deliberate failure below. (An
            // engine-level error - a checkpoint that succeeded but whose announcement failed - is
            // not a quota problem and propagates as itself.)
        }

        var quota = _lastQuota ?? default;
        _lastQuota = null; // Force a fresh estimate next time; space may have been freed since.
        if (!_quotaPressureReported)
        {
            // Once per episode: the flusher retries every tick, and an application that is asked to
            // prompt the user does not want to be asked again 100 ms later. A successful flush ends
            // the episode, so a later refusal is reported afresh.
            _quotaPressureReported = true;
            _options.OnQuotaPressure?.Invoke(quota);
        }
        throw new StorageQuotaExceededException(quota, pendingBytes);
    }

    /// <summary>
    /// Estimates are comparatively expensive - in the browser it is an async trip through
    /// <c>navigator.storage.estimate()</c> - so the last one is carried forward by adding the bytes
    /// written since. That projection only ever overstates usage (deletes are ignored), so it is
    /// safe to trust until it says we are nearing the limit, which is when a fresh reading is taken.
    /// </summary>
    private async ValueTask<bool> HasRoomForAsync(IQuotaAwareStorage storage, long bytes, CancellationToken ct)
    {
        if (_lastQuota is not { } quota || quota.QuotaBytes <= 0 || WouldCrowd(quota, bytes))
        {
            _lastQuota = await storage.GetQuotaAsync(ct).ConfigureAwait(false);
            _bytesSinceQuotaCheck = 0;
            if (_lastQuota is not { } refreshed || refreshed.QuotaBytes <= 0)
            {
                return true; // Backend cannot say; do not stand in the way of the write.
            }
            quota = refreshed;
        }

        if (WouldCrowd(quota, bytes))
        {
            return false;
        }
        _bytesSinceQuotaCheck += bytes;
        return true;
    }

    private bool WouldCrowd(StorageQuota quota, long bytes) =>
        quota.UsageBytes + _bytesSinceQuotaCheck + bytes + _options.QuotaReserveBytes > quota.QuotaBytes;

    public async ValueTask<bool> CheckpointAsync(CancellationToken ct = default)
    {
        if (_options.ReadOnly)
        {
            throw new InvalidOperationException("A read-only replica cannot checkpoint.");
        }
        if (_db.HasActiveTransaction)
        {
            throw new InvalidOperationException("Cannot checkpoint while a transaction is active.");
        }
        return await CheckpointCoreAsync(ct).ConfigureAwait(false);
    }

    private async Task RunFlusherLoopAsync()
    {
        using var timer = new PeriodicTimer(_options.FlushInterval);
        while (true)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
                {
                    return;
                }
                // Only the wait is cancellable. Once a flush has started it runs to completion:
                // interrupting an append mid-write and re-issuing it later could leave a torn
                // record in front of the retry, and replay stops at the first torn record.
                await EnsureQuotaHeadroomAsync(CancellationToken.None).ConfigureAwait(false);
                // A standing error clears only when something actually succeeded - a tick with
                // nothing to write proves nothing and must not hide a checkpoint that keeps failing.
                if (await FlushPendingAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    _fault = null;
                }
                if (Volatile.Read(ref _walSize) > _options.CheckpointWalSize)
                {
                    await CheckpointCoreAsync(CancellationToken.None).ConfigureAwait(false);
                    _fault = null;
                }
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Keep the loop alive and retry on the next tick; un-flushed bytes were put back.
                // The failure is visible through LastBackgroundError until a later flush or
                // checkpoint succeeds, and an explicit FlushAsync throws if it hits the same problem.
                _fault = ex;
            }
        }
    }

    private async ValueTask<bool> FlushPendingAsync(CancellationToken ct)
    {
        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            byte[]? chunk = null;
            lock (_db.SyncRoot)
            {
                if (_pending.Length > 0)
                {
                    chunk = _pending.ToArray();
                    _pending.Reset();
                }
            }
            if (chunk is null)
            {
                return false;
            }
            try
            {
                await _storage.AppendAsync(_walFile, chunk, ct).ConfigureAwait(false);
                _walSize += chunk.Length;
                _quotaPressureReported = false;
            }
            catch
            {
                // Put the un-flushed bytes back in front of anything committed meanwhile.
                lock (_db.SyncRoot)
                {
                    var newer = _pending.ToArray();
                    _pending.Reset();
                    _pending.WriteRaw(chunk);
                    _pending.WriteRaw(newer);
                }
                throw;
            }
            return true;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <summary>Best-effort cleanup of a half-written generation; never masks the original error.</summary>
    private async ValueTask TryDeleteAsync(string name)
    {
        try
        {
            await _storage.DeleteAsync(name, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // The file is unreferenced by any manifest, so leaving it behind is harmless.
        }
    }

    private async ValueTask<bool> CheckpointCoreAsync(CancellationToken ct)
    {
        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            byte[] snapshotBytes;
            byte[] supersededPending;
            long snapshotLsn;
            ulong newGeneration;
            lock (_db.SyncRoot)
            {
                if (_db.HasActiveTransaction)
                {
                    return false;
                }
                snapshotLsn = _lastLsn;
                newGeneration = _generation + 1;
                snapshotBytes = BuildSnapshot(snapshotLsn);
                // Everything buffered so far has LSN <= snapshotLsn and is superseded by the
                // snapshot - but only once that snapshot is actually durable, so keep a copy.
                supersededPending = _pending.ToArray();
                _pending.Reset();
            }

            var newSnapshotFile = $"snapshot-{newGeneration}.blz";
            var newWalFile = $"wal-{newGeneration}.blz";

            try
            {
                await _storage.WriteAtomicAsync(newSnapshotFile, snapshotBytes, ct).ConfigureAwait(false);
                await _storage.WriteAtomicAsync(
                    ManifestFile,
                    BuildManifest(newGeneration, newSnapshotFile, newWalFile, snapshotLsn),
                    ct).ConfigureAwait(false);
            }
            catch
            {
                // The manifest still points at the previous generation, so those records are the
                // only record of these commits: put them back ahead of anything committed since.
                lock (_db.SyncRoot)
                {
                    var newer = _pending.ToArray();
                    _pending.Reset();
                    _pending.WriteRaw(supersededPending);
                    _pending.WriteRaw(newer);
                }
                await TryDeleteAsync(newSnapshotFile).ConfigureAwait(false);
                throw;
            }

            var oldWalFile = _walFile;
            var oldSnapshotFile = _snapshotFile;
            _generation = newGeneration;
            _snapshotFile = newSnapshotFile;
            _walFile = newWalFile;
            _snapshotLsn = snapshotLsn;
            _walSize = 0;
            _quotaPressureReported = false; // Compaction freed space; the next refusal is a new episode.

            await _storage.DeleteAsync(oldWalFile, ct).ConfigureAwait(false);
            if (oldSnapshotFile.Length > 0)
            {
                await _storage.DeleteAsync(oldSnapshotFile, ct).ConfigureAwait(false);
            }

            // Announced only once the new generation is durable and the old one is gone, so a
            // replica that reloads on this signal cannot land on a half-published generation. The
            // checkpoint itself has succeeded by now, so a failing announcement is reported as its
            // own error rather than as a failed checkpoint - a caller (or the flusher) must not redo
            // a checkpoint that is already in place.
            try
            {
                _options.OnCheckpoint?.Invoke(newGeneration);
            }
            catch (Exception ex)
            {
                throw new BlazeDbException(
                    $"Checkpoint {newGeneration} is durable, but the OnCheckpoint callback failed.", ex);
            }
            return true;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    // ---- Recovery ----

    private async ValueTask RecoverAsync(CancellationToken ct)
    {
        var manifestBytes = await _storage.ReadAsync(ManifestFile, ct).ConfigureAwait(false);
        if (manifestBytes is null)
        {
            _generation = 1;
            _snapshotFile = "";
            _walFile = "wal-1.blz";
            _snapshotLsn = 0;
            if (!_options.ReadOnly)
            {
                await _storage.WriteAtomicAsync(
                    ManifestFile, BuildManifest(_generation, _snapshotFile, _walFile, _snapshotLsn), ct).ConfigureAwait(false);
            }
            return;
        }

        ParseManifest(manifestBytes);
        _lastLsn = _snapshotLsn;

        if (_snapshotFile.Length > 0)
        {
            var snapshotBytes = await _storage.ReadAsync(_snapshotFile, ct).ConfigureAwait(false)
                ?? throw new CorruptDatabaseException($"Manifest references missing snapshot '{_snapshotFile}'.");
            try
            {
                LoadSnapshot(snapshotBytes);
            }
            catch (Exception ex) when (IsDecodeFailure(ex))
            {
                throw new CorruptDatabaseException(
                    $"Snapshot '{_snapshotFile}' passed its checksum but could not be decoded; the row " +
                    "format may not match the registered table descriptors.", ex);
            }
        }

        var walBytes = await _storage.ReadAsync(_walFile, ct).ConfigureAwait(false);
        if (walBytes is not null)
        {
            int validLength;
            try
            {
                validLength = ReplayWal(walBytes);
            }
            catch (Exception ex) when (IsDecodeFailure(ex))
            {
                throw new CorruptDatabaseException(
                    $"A record in '{_walFile}' passed its checksum but could not be decoded; the row " +
                    "format may not match the registered table descriptors.", ex);
            }
            if (validLength < walBytes.Length && !_options.ReadOnly)
            {
                // Torn tail detected: truncate so future appends continue from a clean point.
                // A replica leaves it alone - repair is the writer's job, and the tail may simply
                // be a commit the writer is in the middle of appending.
                await _storage.WriteAtomicAsync(_walFile, walBytes.AsMemory(0, validLength), ct).ConfigureAwait(false);
            }
            _walSize = validLength;
        }
    }

    /// <summary>Replays valid records and returns the length of the valid prefix.</summary>
    private int ReplayWal(byte[] walBytes)
    {
        var offset = 0;
        while (walBytes.Length - offset >= RecordHeaderSize)
        {
            var header = new BufferReader(walBytes.AsSpan(offset, RecordHeaderSize));
            var payloadLength = (int)header.ReadFixed32();
            var lsn = (long)header.ReadFixed64();
            var expectedCrc = header.ReadFixed32();

            if (payloadLength < 0 || payloadLength > MaxRecordSize ||
                walBytes.Length - offset - RecordHeaderSize < payloadLength)
            {
                break;
            }

            var payload = walBytes.AsSpan(offset + RecordHeaderSize, payloadLength);
            if (Crc32.Compute(payload) != expectedCrc)
            {
                break;
            }

            if (lsn > _snapshotLsn)
            {
                ReplayCommit(payload);
                _lastLsn = Math.Max(_lastLsn, lsn);
            }
            offset += RecordHeaderSize + payloadLength;
        }
        return offset;
    }

    /// <summary>
    /// Decoding errors that a checksum cannot catch: a well-formed file whose contents do not match
    /// the descriptors this database was opened with. Readers report malformed bytes as
    /// <see cref="InvalidDataException"/>; the checked count casts in the loaders overflow instead.
    /// </summary>
    private static bool IsDecodeFailure(Exception ex) => ex is InvalidDataException or OverflowException;

    private void ReplayCommit(ReadOnlySpan<byte> payload)
    {
        var reader = new BufferReader(payload);
        var count = checked((int)reader.ReadVarUInt());
        for (var i = 0; i < count; i++)
        {
            var opType = reader.ReadByte();
            var tableName = reader.ReadString();
            var table = _db.GetTableByName(tableName);
            var keyBytes = reader.ReadBytes();
            switch (opType)
            {
                case WalOp.Set:
                    var rowBytes = reader.ReadBytes();
                    table.ReplaySet(keyBytes, rowBytes);
                    break;
                case WalOp.Delete:
                    table.ReplayDelete(keyBytes);
                    break;
                default:
                    throw new CorruptDatabaseException($"Unknown WAL op code {opType}.");
            }
        }
    }

    // ---- Snapshot / manifest encoding ----

    private byte[] BuildSnapshot(long lsn)
    {
        var writer = new BufferWriter(64 * 1024);
        writer.WriteRaw(SnapshotMagic);
        writer.WriteByte(FormatVersion);
        writer.WriteVarUInt((ulong)lsn);
        var tables = _db.TableList;
        writer.WriteVarUInt((ulong)tables.Count);
        foreach (var table in tables)
        {
            writer.WriteString(table.Name);
            table.WriteSnapshot(writer, _valueScratch);
        }
        writer.WriteFixed32(Crc32.Compute(writer.WrittenSpan));
        return writer.ToArray();
    }

    private void LoadSnapshot(byte[] bytes)
    {
        if (bytes.Length < SnapshotMagic.Length + 1 + 4 ||
            !bytes.AsSpan(0, 4).SequenceEqual(SnapshotMagic))
        {
            throw new CorruptDatabaseException("Snapshot file has an invalid header.");
        }
        var body = bytes.AsSpan(0, bytes.Length - 4);
        var crcReader = new BufferReader(bytes.AsSpan(bytes.Length - 4));
        if (Crc32.Compute(body) != crcReader.ReadFixed32())
        {
            throw new CorruptDatabaseException("Snapshot file failed checksum validation.");
        }

        var reader = new BufferReader(body.Slice(4));
        var version = reader.ReadByte();
        if (version != FormatVersion)
        {
            throw new CorruptDatabaseException($"Unsupported snapshot format version {version}.");
        }
        reader.ReadVarUInt(); // snapshot LSN; authoritative value comes from the manifest
        var tableCount = checked((int)reader.ReadVarUInt());
        for (var i = 0; i < tableCount; i++)
        {
            var tableName = reader.ReadString();
            _db.GetTableByName(tableName).LoadSnapshot(ref reader);
        }
    }

    private static byte[] BuildManifest(ulong generation, string snapshotFile, string walFile, long snapshotLsn)
    {
        var writer = new BufferWriter(128);
        writer.WriteRaw(ManifestMagic);
        writer.WriteByte(FormatVersion);
        writer.WriteVarUInt(generation);
        writer.WriteString(snapshotFile);
        writer.WriteString(walFile);
        writer.WriteVarUInt((ulong)snapshotLsn);
        writer.WriteFixed32(Crc32.Compute(writer.WrittenSpan));
        return writer.ToArray();
    }

    private void ParseManifest(byte[] bytes)
    {
        if (bytes.Length < ManifestMagic.Length + 1 + 4 ||
            !bytes.AsSpan(0, 4).SequenceEqual(ManifestMagic))
        {
            throw new CorruptDatabaseException("Manifest file has an invalid header.");
        }
        var body = bytes.AsSpan(0, bytes.Length - 4);
        var crcReader = new BufferReader(bytes.AsSpan(bytes.Length - 4));
        if (Crc32.Compute(body) != crcReader.ReadFixed32())
        {
            throw new CorruptDatabaseException("Manifest file failed checksum validation.");
        }

        var reader = new BufferReader(body.Slice(4));
        var version = reader.ReadByte();
        if (version != FormatVersion)
        {
            throw new CorruptDatabaseException($"Unsupported manifest format version {version}.");
        }
        _generation = reader.ReadVarUInt();
        _snapshotFile = reader.ReadString();
        _walFile = reader.ReadString();
        _snapshotLsn = (long)reader.ReadVarUInt();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        if (!_options.ReadOnly)
        {
            await FlushPendingAsync(CancellationToken.None).ConfigureAwait(false);
        }
        _cts.Dispose();
        _ioLock.Dispose();
    }
}
