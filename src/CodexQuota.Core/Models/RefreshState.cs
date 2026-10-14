using CodexQuota.Core.Services;

namespace CodexQuota.Core.Models;

/// <summary>
/// Full snapshot of refresh health published by the refresh coordinator. One
/// transition is published at a time; the desktop dispatcher applies it to the UI.
/// </summary>
public sealed record RefreshState(
    RefreshStatus Status,
    QuotaSnapshot? LastQuota,
    DailyUsageResult? DailyUsage,
    DateTimeOffset? LastAttempt,
    DateTimeOffset? LastSuccess,
    DateTimeOffset? NextScheduledFetch,
    string? StatusText,
    ProviderFailureKind FailureKind = ProviderFailureKind.None)
{
    public AppMessage? StatusMessage { get; init; }
    /// <summary>Initial state before the first fetch completes.</summary>
    public static RefreshState Initial(DateOnly today) => new(
        RefreshStatus.Loading,
        LastQuota: null,
        DailyUsage: DailyUsageResult.Missing(today),
        LastAttempt: null,
        LastSuccess: null,
        NextScheduledFetch: null,
        StatusText: AppText.Format(AppLanguage.SimplifiedChinese, "Fetching"),
        FailureKind: ProviderFailureKind.None)
    { StatusMessage = new AppMessage("Fetching") };
}
