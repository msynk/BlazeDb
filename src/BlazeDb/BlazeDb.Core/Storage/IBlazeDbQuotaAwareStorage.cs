namespace BlazeDb.Storage;

/// <summary>
/// Implemented by storage backends that can report the origin's remaining allowance - in the
/// browser, <c>navigator.storage.estimate()</c>. The engine consults this before flushing so it
/// can compact or stop deliberately, rather than discovering the limit as a failed append with
/// committed data already gone from the log buffer.
/// </summary>
public interface IBlazeDbQuotaAwareStorage : IBlazeDbStorage
{
    /// <summary>Current usage and allowance, or null when unavailable.</summary>
    ValueTask<BlazeDbStorageQuota?> GetQuotaAsync(CancellationToken cancellationToken = default);
}
