using CodexQuota.Core.Models;

namespace CodexQuota.Core.Services;

/// <summary>
/// The single owner of quota interpretation: main-bucket selection, the conservative
/// remaining percentage, and severity thresholds. Nothing else in the app computes a
/// remaining percentage or a color bucket.
/// </summary>
public static class QuotaProjection
{
    /// <summary>Official main Codex bucket key.</summary>
    public const string MainLimitId = "codex";

    /// <summary>
    /// Selects the main Codex bucket. Map key <c>codex</c> wins; a legacy single
    /// bucket is accepted only when its limit id is <c>codex</c> or absent. Auxiliary
    /// buckets are never selected as the main bucket.
    /// </summary>
    public static QuotaBucket? SelectMainBucket(QuotaSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return null;
        }

        foreach (QuotaBucket bucket in snapshot.Buckets)
        {
            if (string.Equals(bucket.LimitId, MainLimitId, StringComparison.OrdinalIgnoreCase))
            {
                return bucket;
            }
        }

        if (snapshot.Buckets.Count == 1 && string.IsNullOrEmpty(snapshot.Buckets[0].LimitId))
        {
            return snapshot.Buckets[0];
        }

        return null;
    }

    /// <summary>Auxiliary buckets that are shown separately and never folded into the ball.</summary>
    public static IReadOnlyList<QuotaBucket> GetAuxiliaryBuckets(QuotaSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return [];
        }

        QuotaBucket? main = SelectMainBucket(snapshot);
        return snapshot.Buckets.Where(bucket => !ReferenceEquals(bucket, main)).ToList();
    }

    /// <summary>
    /// The conservative ingestion value: the smallest remaining percentage across the available periods
    /// of the main bucket. Returns null when the main bucket is missing, empty, or
    /// contains an invalid period. It never falls back to a more favorable value.
    /// </summary>
    public static int? GetRemainingPercent(QuotaSnapshot? snapshot) =>
        SelectLimitingWindow(snapshot)?.RemainingPercent;

    /// <summary>
    /// The main-bucket period that currently limits the account: the lowest remaining
    /// percentage, with ties resolved in favour of the earlier (primary) period. Returns
    /// null when the main bucket is missing, invalid, or has no periods, so callers never
    /// invent a limiting period.
    /// </summary>
    public static QuotaWindow? SelectLimitingWindow(QuotaSnapshot? snapshot)
    {
        QuotaBucket? main = SelectMainBucket(snapshot);
        if (main is null || main.ContainsInvalidWindow || main.Windows.Count == 0)
        {
            return null;
        }

        QuotaWindow? limiting = null;
        foreach (QuotaWindow window in main.Windows)
        {
            if (limiting is null || window.RemainingPercent < limiting.RemainingPercent)
            {
                limiting = window;
            }
        }

        return limiting;
    }

    /// <summary>
    /// Selects an actual five-hour main window, preferring primary over secondary.
    /// Missing duration, invalid main data and auxiliary buckets never provide a fallback.
    /// </summary>
    public static QuotaWindow? SelectFiveHourWindow(QuotaSnapshot? snapshot) =>
        SelectWindowByDuration(snapshot, 300);

    /// <summary>
    /// Actual five-hour and weekly main periods shown independently in Details.
    /// Missing periods stay absent and never substitute for the ball's five-hour value.
    /// </summary>
    public static IReadOnlyList<QuotaWindow> SelectDetailsWindows(QuotaSnapshot? snapshot)
    {
        var windows = new List<QuotaWindow>(2);
        if (SelectFiveHourWindow(snapshot) is { } fiveHour) { windows.Add(fiveHour); }
        if (SelectWindowByDuration(snapshot, 10080) is { } weekly) { windows.Add(weekly); }
        return windows;
    }

    private static QuotaWindow? SelectWindowByDuration(QuotaSnapshot? snapshot, long durationMinutes)
    {
        QuotaBucket? main = SelectMainBucket(snapshot);
        if (main is null || main.ContainsInvalidWindow) { return null; }
        QuotaWindow? primary = null;
        QuotaWindow? secondary = null;
        foreach (QuotaWindow window in main.Windows)
        {
            if (window.DurationMinutes != durationMinutes) { continue; }
            if (window.RemainingPercent is < 0 or > 100 || window.UsedPercent is < 0 or > 100) { return null; }
            switch (window.Slot)
            {
                case "primary":
                    if (primary is not null) { return null; }
                    primary = window;
                    break;
                case "secondary":
                    if (secondary is not null) { return null; }
                    secondary = window;
                    break;
            }
        }
        return primary ?? secondary;
    }

    /// <summary>
    /// Battery-style palette for a fresh remaining percentage: 0–33 red, 34–66 yellow,
    /// 67–100 green. A boundary value takes the higher tier.
    /// This is the single owner of the user-facing remaining-quota color policy.
    /// </summary>
    public static QuotaSeverity GetFreshSeverity(int? remainingPercent) => remainingPercent switch
    {
        null => QuotaSeverity.Unknown,
        < 34 => QuotaSeverity.Red,
        < 67 => QuotaSeverity.Yellow,
        _ => QuotaSeverity.Green,
    };

    /// <summary>
    /// Severity a UI should render for a published refresh state; stale or non-fresh
    /// state is neutral regardless of the last cached value.
    /// </summary>
    public static QuotaSeverity GetDisplaySeverity(RefreshState state)
    {
        if (state.Status != RefreshStatus.Fresh)
        {
            return state.LastQuota is null ? QuotaSeverity.Unknown : QuotaSeverity.Stale;
        }

        return GetFreshSeverity(GetRemainingPercent(state.LastQuota));
    }
}
