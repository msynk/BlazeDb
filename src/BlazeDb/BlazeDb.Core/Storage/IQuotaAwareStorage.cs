namespace BlazeDb.Storage;

/// <summary>How much of the origin's storage allowance is used, as reported by the backend.</summary>
/// <param name="UsageBytes">Bytes currently attributed to this origin.</param>
/// <param name="QuotaBytes">Total bytes the origin may use, or 0 when the backend cannot say.</param>
public readonly record struct StorageQuota(long UsageBytes, long QuotaBytes)
{
    /// <summary>Bytes still available before the origin hits its limit.</summary>
    public long AvailableBytes => QuotaBytes <= 0 ? long.MaxValue : Math.Max(0, QuotaBytes - UsageBytes);

    /// <summary>Fraction of the allowance used, 0 when the quota is unknown.</summary>
    public double UsedFraction => QuotaBytes <= 0 ? 0 : (double)UsageBytes / QuotaBytes;
}

/// <summary>
/// Implemented by storage backends that can report the origin's remaining allowance - in the
/// browser, <c>navigator.storage.estimate()</c>. The engine consults this before flushing so it
/// can compact or stop deliberately, rather than discovering the limit as a failed append with
/// committed data already gone from the log buffer.
/// </summary>
public interface IQuotaAwareStorage : IStorage
{
    /// <summary>Current usage and allowance, or null when unavailable.</summary>
    ValueTask<StorageQuota?> GetQuotaAsync(CancellationToken cancellationToken = default);
}
