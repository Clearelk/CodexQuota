using CodexQuota.Core.Models;

namespace CodexQuota.Core.Services;

/// <summary>Owns one notification per fresh below-threshold quota episode.</summary>
public sealed class LowQuotaAlertTracker
{
    private string? _budget;
    private int _threshold;
    private bool _notified;

    public bool ShouldNotify(WidgetSettings settings, RefreshState refresh)
    {
        if (!settings.NotificationsEnabled || refresh.LastQuota is null)
        {
            _budget = null;
            _notified = false;
            return false;
        }
        if (refresh.Status != RefreshStatus.Fresh || refresh.FailureKind != ProviderFailureKind.None) { return false; }
        QuotaBucket? main = QuotaProjection.SelectMainBucket(refresh.LastQuota);
        QuotaWindow? selected = QuotaProjection.SelectFiveHourWindow(refresh.LastQuota);
        int? remaining = selected?.RemainingPercent;
        if (main is null || remaining is null || remaining is < 0 or > 100 ||
            main.Windows.Any(window => window.ResetsAt <= refresh.LastQuota.FetchedAt)) { return false; }
        string budget = refresh.LastQuota.ProviderId + "|" + refresh.LastQuota.Plan + "|" +
            $"{selected!.Slot}:{selected.DurationMinutes}:{selected.ResetsAt:O}";
        if (_budget != budget || _threshold != settings.LowQuotaThreshold)
        {
            _budget = budget;
            _threshold = settings.LowQuotaThreshold;
            _notified = false;
        }
        if (remaining >= settings.LowQuotaThreshold) { _notified = false; return false; }
        if (_notified) { return false; }
        _notified = true;
        return true;
    }
}
