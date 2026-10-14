using CodexQuota.Core.Models;
using CodexQuota.Core.Services;

namespace CodexQuota.Tests;

public sealed class LowQuotaAlertTrackerTests
{
    private static readonly WidgetSettings Enabled = WidgetSettings.Default with { NotificationsEnabled = true };
    private static RefreshState Sample(int remaining, RefreshStatus status = RefreshStatus.Fresh, int epoch = 5)
    {
        DateTimeOffset time = new(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);
        var window = new QuotaWindow("primary", 300, 100 - remaining, remaining, time.AddHours(epoch));
        var quota = new QuotaSnapshot("codex", "plus", [new QuotaBucket("codex", null, [window], false, null, false, false, null)], time);
        return RefreshState.Initial(new DateOnly(2026, 10, 12)) with { Status = status, LastQuota = quota, LastSuccess = time };
    }

    [Fact]
    public void AlertsOnceBelowThresholdAndRearmsAfterFreshRecovery()
    {
        var tracker = new LowQuotaAlertTracker();
        Assert.False(tracker.ShouldNotify(Enabled, Sample(20)));
        Assert.True(tracker.ShouldNotify(Enabled, Sample(19)));
        Assert.False(tracker.ShouldNotify(Enabled, Sample(18)));
        Assert.False(tracker.ShouldNotify(Enabled, Sample(3, RefreshStatus.Stale)));
        Assert.False(tracker.ShouldNotify(Enabled, Sample(15)));
        Assert.False(tracker.ShouldNotify(Enabled, Sample(25)));
        Assert.True(tracker.ShouldNotify(Enabled, Sample(10)));
    }

    [Fact]
    public void DisabledUnknownAndFailureNeverAlert()
    {
        var tracker = new LowQuotaAlertTracker();
        Assert.False(tracker.ShouldNotify(WidgetSettings.Default, Sample(1)));
        Assert.False(tracker.ShouldNotify(Enabled, Sample(1, RefreshStatus.Error)));
        Assert.False(tracker.ShouldNotify(Enabled, Sample(1) with { LastQuota = null }));
        Assert.False(tracker.ShouldNotify(Enabled, Sample(1) with { FailureKind = ProviderFailureKind.Transport }));
        Assert.True(tracker.ShouldNotify(Enabled, Sample(1)));
    }

    [Fact]
    public void AccountBudgetAndConfigurationBoundariesStartNewEpisodes()
    {
        var tracker = new LowQuotaAlertTracker();
        Assert.True(tracker.ShouldNotify(Enabled, Sample(10)));
        Assert.False(tracker.ShouldNotify(Enabled, Sample(10)));
        Assert.True(tracker.ShouldNotify(Enabled, Sample(10, epoch: 6)));
        Assert.False(tracker.ShouldNotify(Enabled, Sample(10) with { LastQuota = null }));
        Assert.True(tracker.ShouldNotify(Enabled, Sample(10)));
        Assert.True(tracker.ShouldNotify(Enabled with { LowQuotaThreshold = 30 }, Sample(10)));
    }

    [Fact]
    public void StaleRecoveryAndExpiredWindowCannotRearmOrNotify()
    {
        var tracker = new LowQuotaAlertTracker();
        Assert.True(tracker.ShouldNotify(Enabled, Sample(10)));
        Assert.False(tracker.ShouldNotify(Enabled, Sample(80, RefreshStatus.Stale)));
        Assert.False(tracker.ShouldNotify(Enabled, Sample(10)));
        Assert.False(new LowQuotaAlertTracker().ShouldNotify(Enabled, Sample(10, epoch: 0)));
        Assert.False(new LowQuotaAlertTracker().ShouldNotify(Enabled, Sample(10, epoch: -1)));
    }

    [Fact]
    public void NotificationUsesFiveHourWindowAndIgnoresWeeklyAndAuxiliaryBuckets()
    {
        RefreshState state = Sample(80);
        QuotaBucket main = state.LastQuota!.Buckets[0];
        var secondary = main.Windows[0] with { Slot = "secondary", DurationMinutes = 10080, UsedPercent = 90, RemainingPercent = 10 };
        var auxiliary = main with { LimitId = "extra", Windows = [main.Windows[0] with { UsedPercent = 100, RemainingPercent = 0 }] };
        Assert.False(new LowQuotaAlertTracker().ShouldNotify(Enabled, state with { LastQuota = state.LastQuota with { Buckets = [main, auxiliary] } }));
        Assert.False(new LowQuotaAlertTracker().ShouldNotify(Enabled, state with { LastQuota = state.LastQuota with { Buckets = [main with { Windows = [main.Windows[0], secondary] }, auxiliary] } }));
        Assert.False(new LowQuotaAlertTracker().ShouldNotify(Enabled, state with { LastQuota = state.LastQuota with { Buckets = [main with { ContainsInvalidWindow = true }, auxiliary] } }));
    }

    private static RefreshState WithWindows(RefreshState state, params QuotaWindow[] windows) => state with
    {
        LastQuota = state.LastQuota! with { Buckets = [state.LastQuota!.Buckets[0] with { Windows = windows }] },
    };

    [Fact]
    public void WeeklyValuesMetadataAndOrderDoNotRearmSelectedEpisode()
    {
        var tracker = new LowQuotaAlertTracker();
        RefreshState state = Sample(19);
        QuotaWindow selected = state.LastQuota!.Buckets[0].Windows[0];
        QuotaWindow week = selected with { Slot = "secondary", DurationMinutes = 10080, RemainingPercent = 1, UsedPercent = 99 };
        Assert.True(tracker.ShouldNotify(Enabled, WithWindows(state, selected, week)));
        week = week with { ResetsAt = week.ResetsAt!.Value.AddDays(1), DurationMinutes = 20160, RemainingPercent = 90, UsedPercent = 10 };
        Assert.False(tracker.ShouldNotify(Enabled, WithWindows(state, week, selected)));
        Assert.False(tracker.ShouldNotify(Enabled, WithWindows(state, selected)));
        Assert.False(tracker.ShouldNotify(Enabled, WithWindows(state, selected with { UsedPercent = 80, RemainingPercent = 20 }, week)));
        Assert.True(tracker.ShouldNotify(Enabled, WithWindows(state, selected, week)));
    }

    [Fact]
    public void SecondaryFiveHourSelectionRetainsItsOwnIdentity()
    {
        var tracker = new LowQuotaAlertTracker();
        RefreshState state = Sample(80);
        QuotaWindow primary = state.LastQuota!.Buckets[0].Windows[0] with { DurationMinutes = 1440 };
        QuotaWindow selected = primary with { Slot = "secondary", DurationMinutes = 300, RemainingPercent = 19, UsedPercent = 81 };
        Assert.True(tracker.ShouldNotify(Enabled, WithWindows(state, primary, selected)));
        Assert.False(tracker.ShouldNotify(Enabled, WithWindows(state, primary with { ResetsAt = primary.ResetsAt!.Value.AddDays(1) }, selected)));
        Assert.True(tracker.ShouldNotify(Enabled, WithWindows(state, primary, selected with { ResetsAt = selected.ResetsAt!.Value.AddHours(1) })));
        Assert.False(new LowQuotaAlertTracker().ShouldNotify(Enabled, WithWindows(state, state.LastQuota.Buckets[0].Windows[0], selected)));
    }

    [Fact]
    public void TransientUnavailableSelectionAndUnhealthyRecoveryPreserveLatch()
    {
        var tracker = new LowQuotaAlertTracker();
        RefreshState state = Sample(19);
        QuotaWindow selected = state.LastQuota!.Buckets[0].Windows[0];
        Assert.True(tracker.ShouldNotify(Enabled, state));
        foreach (long? duration in new long?[] { null, 0, -1, 1440, 10080 })
        {
            Assert.False(tracker.ShouldNotify(Enabled, WithWindows(state, selected with { DurationMinutes = duration, RemainingPercent = 80, UsedPercent = 20 })));
            Assert.False(tracker.ShouldNotify(Enabled, state));
        }
        foreach (RefreshStatus status in new[] { RefreshStatus.Stale, RefreshStatus.Loading, RefreshStatus.Error })
        {
            Assert.False(tracker.ShouldNotify(Enabled, Sample(80, status)));
            Assert.False(tracker.ShouldNotify(Enabled, state));
        }
        Assert.False(tracker.ShouldNotify(Enabled, WithWindows(state, selected with { ResetsAt = state.LastQuota.FetchedAt })));
        Assert.False(tracker.ShouldNotify(Enabled, state));
        Assert.False(tracker.ShouldNotify(Enabled, state with { LastQuota = state.LastQuota with { Buckets = [state.LastQuota.Buckets[0] with { ContainsInvalidWindow = true }] } }));
        Assert.False(tracker.ShouldNotify(Enabled, state));
        Assert.False(tracker.ShouldNotify(Enabled, WithWindows(state)));
        Assert.False(tracker.ShouldNotify(Enabled, state));
    }

    [Fact]
    public void SelectedIdentityConfigurationAndAccountBoundariesRearm()
    {
        var tracker = new LowQuotaAlertTracker();
        RefreshState state = Sample(19);
        QuotaWindow selected = state.LastQuota!.Buckets[0].Windows[0];
        Assert.True(tracker.ShouldNotify(Enabled, state));
        Assert.True(tracker.ShouldNotify(Enabled, state with { LastQuota = state.LastQuota with { ProviderId = "other" } }));
        Assert.True(tracker.ShouldNotify(Enabled, state with { LastQuota = state.LastQuota with { Plan = "pro" } }));
        Assert.True(tracker.ShouldNotify(Enabled, WithWindows(state, selected with { Slot = "secondary" })));
        Assert.True(tracker.ShouldNotify(Enabled with { LowQuotaThreshold = 30 }, state));
        Assert.False(tracker.ShouldNotify(WidgetSettings.Default, state));
        Assert.True(tracker.ShouldNotify(Enabled, state));
        Assert.False(tracker.ShouldNotify(Enabled, state with { LastQuota = null }));
        Assert.True(tracker.ShouldNotify(Enabled, state));
    }

    [Fact]
    public void NullSelectedResetIsAllowedAndExpiredWeeklyStillVetoes()
    {
        RefreshState state = Sample(19);
        QuotaWindow selected = state.LastQuota!.Buckets[0].Windows[0] with { ResetsAt = null };
        var tracker = new LowQuotaAlertTracker();
        Assert.True(tracker.ShouldNotify(Enabled, WithWindows(state, selected)));
        Assert.False(tracker.ShouldNotify(Enabled, WithWindows(state, selected)));
        QuotaWindow expiredWeek = selected with { Slot = "secondary", DurationMinutes = 10080, ResetsAt = state.LastQuota.FetchedAt };
        Assert.False(new LowQuotaAlertTracker().ShouldNotify(Enabled, WithWindows(state, selected, expiredWeek)));
        Assert.False(new LowQuotaAlertTracker().ShouldNotify(Enabled, WithWindows(state, selected with { RemainingPercent = -1 })));
        Assert.False(new LowQuotaAlertTracker().ShouldNotify(Enabled, WithWindows(state, selected with { RemainingPercent = 101 })));
    }
}
