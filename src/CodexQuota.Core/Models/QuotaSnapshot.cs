namespace CodexQuota.Core.Models;

/// <summary>
/// One quota period inside a bucket. Slot is a stable identifier (primary /
/// secondary) so UI rows can be matched across refreshes.
/// </summary>
public sealed record QuotaWindow(
    string Slot,
    long? DurationMinutes,
    int UsedPercent,
    int RemainingPercent,
    DateTimeOffset? ResetsAt);

/// <summary>
/// A metered quota bucket. ContainsInvalidWindow is set when a returned period
/// carried an unusable percentage; the owner must then refuse to publish a
/// remaining value for that bucket instead of silently dropping the period.
/// </summary>
public sealed record QuotaBucket(
    string? LimitId,
    string? Name,
    IReadOnlyList<QuotaWindow> Windows,
    bool ContainsInvalidWindow,
    string? CreditBalance,
    bool? HasCredits,
    bool? UnlimitedCredits,
    string? ReachedLimitReason)
{
    /// <summary>Service-provided plan label carried by the bucket, when present.</summary>
    public string? PlanType { get; init; }
}

/// <summary>Normalized, immutable quota observation for one refresh.</summary>
public sealed record QuotaSnapshot(
    string ProviderId,
    string? Plan,
    IReadOnlyList<QuotaBucket> Buckets,
    DateTimeOffset FetchedAt);
