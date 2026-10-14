namespace CodexQuota.Core.Models;

/// <summary>
/// Outcome of a single provider refresh session. Quota is mandatory; the daily
/// statistic is independent so an optional daily failure never discards good quota.
/// AccountKey is a non-reversible, non-sensitive fingerprint used only in memory to
/// reject cached state from a different account; it is never persisted or logged.
/// </summary>
public sealed record ProviderRefreshResult(
    QuotaSnapshot Quota,
    DailyUsageResult DailyUsage,
    string? AccountKey = null);
