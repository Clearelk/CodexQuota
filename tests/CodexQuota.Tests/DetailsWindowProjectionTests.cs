using CodexQuota.Core.Models;
using CodexQuota.Core.Services;

namespace CodexQuota.Tests;

public sealed class DetailsWindowProjectionTests
{
    private static readonly QuotaWindow FiveHour = new("primary", 300, 20, 80, DateTimeOffset.UnixEpoch.AddHours(5));
    private static readonly QuotaWindow Weekly = new("secondary", 10080, 90, 10, DateTimeOffset.UnixEpoch.AddDays(7));

    private static QuotaSnapshot Snapshot(params QuotaWindow[] windows) =>
        new("codex", "plus", [new QuotaBucket("codex", null, windows, false, null, null, null, null)], DateTimeOffset.UnixEpoch);

    [Fact]
    public void DetailsIncludeBothPeriodsWhileBallKeepsFiveHourValue()
    {
        QuotaSnapshot snapshot = Snapshot(Weekly, FiveHour);
        Assert.Equal(new[] { FiveHour, Weekly }, QuotaProjection.SelectDetailsWindows(snapshot));
        Assert.Equal(80, QuotaProjection.SelectFiveHourWindow(snapshot)!.RemainingPercent);
        Assert.Equal(10, QuotaProjection.GetRemainingPercent(snapshot));
    }

    [Fact]
    public void WeeklyOnlyDataStillAppearsInDetailsButCannotSupplyBall()
    {
        QuotaSnapshot snapshot = Snapshot(Weekly);
        Assert.Equal(Weekly, Assert.Single(QuotaProjection.SelectDetailsWindows(snapshot)));
        Assert.Null(QuotaProjection.SelectFiveHourWindow(snapshot));
    }

    [Fact]
    public void DetailsKeepEachPeriodsActualSlotAndReset()
    {
        QuotaWindow fiveHourSecondary = FiveHour with { Slot = "secondary" };
        QuotaWindow weeklyPrimary = Weekly with { Slot = "primary" };
        Assert.Equal(new[] { fiveHourSecondary, weeklyPrimary }, QuotaProjection.SelectDetailsWindows(Snapshot(weeklyPrimary, fiveHourSecondary)));
    }

    [Fact]
    public void UnknownDurationsInvalidMainAndAuxiliaryWeekCannotInventCards()
    {
        Assert.Empty(QuotaProjection.SelectDetailsWindows(Snapshot(Weekly with { DurationMinutes = null })));
        Assert.Empty(QuotaProjection.SelectDetailsWindows(null));
        QuotaSnapshot invalid = Snapshot(FiveHour, Weekly);
        Assert.Empty(QuotaProjection.SelectDetailsWindows(invalid with { Buckets = [invalid.Buckets[0] with { ContainsInvalidWindow = true }] }));
        QuotaSnapshot auxiliary = Snapshot(FiveHour);
        Assert.Equal(FiveHour, Assert.Single(QuotaProjection.SelectDetailsWindows(auxiliary with
        {
            Buckets = [auxiliary.Buckets[0], new QuotaBucket("extra", null, [Weekly], false, null, null, null, null)],
        })));
    }
}
