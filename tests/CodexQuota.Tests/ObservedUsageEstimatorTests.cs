using CodexQuota.Core.Models;
using CodexQuota.Core.Services;
using CodexQuota.Tests.Support;

namespace CodexQuota.Tests;

public sealed class ObservedUsageEstimatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);

    private static QuotaWindow Primary(int usedPercent, long? duration = 300, DateTimeOffset? resetsAt = null) =>
        new("primary", duration, usedPercent, 100 - usedPercent, resetsAt ?? T0.AddHours(5));

    private static QuotaWindow Secondary(int usedPercent, long? duration = 10080, DateTimeOffset? resetsAt = null) =>
        new("secondary", duration, usedPercent, 100 - usedPercent, resetsAt ?? T0.AddDays(5));

    private static RefreshState Fresh(
        IReadOnlyList<QuotaWindow> windows,
        DateTimeOffset observedAt,
        RefreshStatus status = RefreshStatus.Fresh,
        string? plan = "plus",
        string provider = "codex")
    {
        var bucket = new QuotaBucket("codex", null, windows, ContainsInvalidWindow: false, null, false, false, null) { PlanType = plan };
        var snapshot = new QuotaSnapshot(provider, plan, [bucket], observedAt);
        return new RefreshState(
            status,
            snapshot,
            DailyUsageResult.Missing(DateOnly.FromDateTime(observedAt.Date)),
            observedAt,
            status == RefreshStatus.Fresh ? observedAt : null,
            observedAt.AddSeconds(120),
            null);
    }

    private static ObservedPeriodEstimate Period(ObservedUsageSnapshot snapshot, string slot) =>
        snapshot.Periods.Single(period => period.Slot == slot);

    [Fact]
    public void FirstObservationEstablishesAWaitingBaselineNotAZero()
    {
        var estimator = new ObservedUsageEstimator(new FakeTimeProvider(T0));

        ObservedUsageSnapshot snapshot = estimator.Observe(Fresh([Primary(10)], T0.AddSeconds(1)));

        Assert.Equal(ObservedUsageState.Waiting, snapshot.State);
        ObservedPeriodEstimate primary = Period(snapshot, "primary");
        Assert.Equal(ObservedPeriodState.Waiting, primary.State);
        Assert.Equal(0, primary.ObservedPercentPoints);
        Assert.Equal(1, primary.ComparableSamples);
        Assert.False(primary.HasGap);
    }

    [Fact]
    public void TwoEqualComparableSamplesProveAKnownZero()
    {
        var estimator = new ObservedUsageEstimator(new FakeTimeProvider(T0));
        estimator.Observe(Fresh([Primary(10)], T0.AddSeconds(1)));

        ObservedUsageSnapshot snapshot = estimator.Observe(Fresh([Primary(10)], T0.AddSeconds(130)));

        Assert.Equal(ObservedUsageState.Available, snapshot.State);
        ObservedPeriodEstimate primary = Period(snapshot, "primary");
        Assert.Equal(ObservedPeriodState.Available, primary.State);
        Assert.Equal(0, primary.ObservedPercentPoints);
        Assert.Equal(2, primary.ComparableSamples);
    }

    [Fact]
    public void MonotonicIncreasesAccumulateOnceEach()
    {
        var estimator = new ObservedUsageEstimator(new FakeTimeProvider(T0));
        estimator.Observe(Fresh([Primary(10)], T0.AddSeconds(1)));
        estimator.Observe(Fresh([Primary(14)], T0.AddSeconds(130)));

        ObservedUsageSnapshot snapshot = estimator.Observe(Fresh([Primary(20)], T0.AddSeconds(260)));

        ObservedPeriodEstimate primary = Period(snapshot, "primary");
        Assert.Equal(10, primary.ObservedPercentPoints);
        Assert.Equal(3, primary.ComparableSamples);
        Assert.Equal(ObservedUsageState.Available, snapshot.State);
    }

    [Fact]
    public void DuplicatePublicationsOfTheSameCycleAreCountedOnce()
    {
        var estimator = new ObservedUsageEstimator(new FakeTimeProvider(T0));
        estimator.Observe(Fresh([Primary(10)], T0.AddSeconds(1)));
        RefreshState completed = Fresh([Primary(25)], T0.AddSeconds(130));
        estimator.Observe(completed);

        // A theme/interval publication republishes the same completed cycle.
        estimator.Observe(completed);
        ObservedUsageSnapshot snapshot = estimator.Observe(completed);

        ObservedPeriodEstimate primary = Period(snapshot, "primary");
        Assert.Equal(15, primary.ObservedPercentPoints);
        Assert.Equal(2, primary.ComparableSamples);
    }

    [Fact]
    public void OutOfOrderObservationIsIgnored()
    {
        var estimator = new ObservedUsageEstimator(new FakeTimeProvider(T0));
        estimator.Observe(Fresh([Primary(10)], T0.AddSeconds(200)));

        ObservedUsageSnapshot snapshot = estimator.Observe(Fresh([Primary(80)], T0.AddSeconds(10)));

        ObservedPeriodEstimate primary = Period(snapshot, "primary");
        Assert.Equal(0, primary.ObservedPercentPoints);
        Assert.Equal(1, primary.ComparableSamples);
    }

    [Fact]
    public void TwoPeriodsAccumulateIndependently()
    {
        var estimator = new ObservedUsageEstimator(new FakeTimeProvider(T0));
        estimator.Observe(Fresh([Primary(10), Secondary(2)], T0.AddSeconds(1)));

        ObservedUsageSnapshot snapshot = estimator.Observe(Fresh([Primary(16), Secondary(5)], T0.AddSeconds(130)));

        Assert.Equal(6, Period(snapshot, "primary").ObservedPercentPoints);
        Assert.Equal(3, Period(snapshot, "secondary").ObservedPercentPoints);
        Assert.Equal(ObservedUsageState.Available, snapshot.State);
        Assert.Equal("primary", snapshot.Periods[0].Slot);
        Assert.Equal(300, snapshot.Periods[0].DurationMinutes);
    }

    [Fact]
    public void DecreasingCounterRebaselinesAndPreservesTheKnownTotalAsPartial()
    {
        var estimator = new ObservedUsageEstimator(new FakeTimeProvider(T0));
        estimator.Observe(Fresh([Primary(10)], T0.AddSeconds(1)));
        estimator.Observe(Fresh([Primary(30)], T0.AddSeconds(130)));

        ObservedUsageSnapshot snapshot = estimator.Observe(Fresh([Primary(4)], T0.AddSeconds(260)));

        ObservedPeriodEstimate primary = Period(snapshot, "primary");
        Assert.Equal(20, primary.ObservedPercentPoints);
        Assert.True(primary.HasGap);
        Assert.Equal(ObservedPeriodState.Partial, primary.State);
        Assert.Equal(ObservedUsageState.Partial, snapshot.State);
    }

    [Theory]
    [InlineData(600, "plus")]  // changed duration
    [InlineData(300, "pro")]   // changed plan
    public void ChangedDurationOrPlanRebaselinesWithoutGuessing(long duration, string plan)
    {
        var estimator = new ObservedUsageEstimator(new FakeTimeProvider(T0));
        estimator.Observe(Fresh([Primary(10)], T0.AddSeconds(1)));
        estimator.Observe(Fresh([Primary(30)], T0.AddSeconds(130)));

        ObservedUsageSnapshot snapshot = estimator.Observe(
            Fresh([Primary(31, duration)], T0.AddSeconds(260), plan: plan));

        ObservedPeriodEstimate primary = Period(snapshot, "primary");
        Assert.Equal(0, primary.ObservedPercentPoints);
        Assert.Equal(ObservedPeriodState.Waiting, primary.State);
        Assert.True(primary.HasGap);
        Assert.Equal(1, primary.ComparableSamples);
    }

    [Fact]
    public void ChangedResetEpochRebaselinesWithoutCountingAcrossTheReset()
    {
        var estimator = new ObservedUsageEstimator(new FakeTimeProvider(T0));
        estimator.Observe(Fresh([Primary(10, resetsAt: T0.AddHours(5))], T0.AddSeconds(1)));
        estimator.Observe(Fresh([Primary(30, resetsAt: T0.AddHours(5))], T0.AddSeconds(130)));

        ObservedUsageSnapshot snapshot = estimator.Observe(
            Fresh([Primary(31, resetsAt: T0.AddHours(10))], T0.AddSeconds(260)));

        ObservedPeriodEstimate primary = Period(snapshot, "primary");
        Assert.Equal(20, primary.ObservedPercentPoints);
        Assert.True(primary.HasGap);
    }

    [Fact]
    public void ChangedProviderRebaselines()
    {
        var estimator = new ObservedUsageEstimator(new FakeTimeProvider(T0));
        estimator.Observe(Fresh([Primary(10)], T0.AddSeconds(1)));
        estimator.Observe(Fresh([Primary(30)], T0.AddSeconds(130)));

        ObservedUsageSnapshot snapshot = estimator.Observe(
            Fresh([Primary(31)], T0.AddSeconds(260), provider: "different-provider"));

        Assert.True(Period(snapshot, "primary").HasGap);
        Assert.Equal(0, Period(snapshot, "primary").ObservedPercentPoints);
        Assert.Equal(ObservedPeriodState.Waiting, Period(snapshot, "primary").State);
    }

    [Fact]
    public void MissingDurationMetadataRebaselines()
    {
        var estimator = new ObservedUsageEstimator(new FakeTimeProvider(T0));
        estimator.Observe(Fresh([Primary(10)], T0.AddSeconds(1)));
        estimator.Observe(Fresh([Primary(30)], T0.AddSeconds(130)));

        ObservedUsageSnapshot snapshot = estimator.Observe(Fresh([Primary(31, duration: null)], T0.AddSeconds(260)));

        ObservedPeriodEstimate primary = Period(snapshot, "primary");
        Assert.True(primary.HasGap);
        Assert.Equal(0, primary.ObservedPercentPoints);
        Assert.Equal(ObservedPeriodState.Unavailable, primary.State);
    }

    [Fact]
    public void MissingWindowMarksAGapAndKeepsItsEarlierTotal()
    {
        var estimator = new ObservedUsageEstimator(new FakeTimeProvider(T0));
        estimator.Observe(Fresh([Primary(10), Secondary(1)], T0.AddSeconds(1)));
        estimator.Observe(Fresh([Primary(20), Secondary(4)], T0.AddSeconds(130)));

        ObservedUsageSnapshot snapshot = estimator.Observe(Fresh([Primary(22)], T0.AddSeconds(260)));

        ObservedPeriodEstimate secondary = Period(snapshot, "secondary");
        Assert.Equal(3, secondary.ObservedPercentPoints);
        Assert.True(secondary.HasGap);
        Assert.Equal(ObservedPeriodState.Partial, secondary.State);
        Assert.Equal(ObservedUsageState.Partial, snapshot.State);
        Assert.Equal(12, Period(snapshot, "primary").ObservedPercentPoints);
    }

    [Fact]
    public void ReappearingWindowDoesNotCountTheUnobservedInterval()
    {
        var estimator = new ObservedUsageEstimator(new FakeTimeProvider(T0));
        estimator.Observe(Fresh([Primary(10), Secondary(1)], T0.AddSeconds(1)));
        estimator.Observe(Fresh([Primary(20), Secondary(2)], T0.AddSeconds(130)));
        estimator.Observe(Fresh([Secondary(3)], T0.AddSeconds(260))); // primary missing -> gap

        ObservedUsageSnapshot snapshot = estimator.Observe(Fresh([Primary(90), Secondary(4)], T0.AddSeconds(390)));

        ObservedPeriodEstimate primary = Period(snapshot, "primary");
        Assert.Equal(10, primary.ObservedPercentPoints); // the +70 cross-gap jump is not invented
        Assert.True(primary.HasGap);
        Assert.Equal(1, primary.ComparableSamples);
    }

    [Fact]
    public void StaleAndLoadingPublicationsNeverAdd()
    {
        var estimator = new ObservedUsageEstimator(new FakeTimeProvider(T0));
        estimator.Observe(Fresh([Primary(10)], T0.AddSeconds(1)));
        estimator.Observe(Fresh([Primary(20)], T0.AddSeconds(130)));

        ObservedUsageSnapshot stale = estimator.Observe(Fresh([Primary(90)], T0.AddSeconds(260), status: RefreshStatus.Stale));
        ObservedUsageSnapshot loading = estimator.Observe(Fresh([Primary(95)], T0.AddSeconds(390), status: RefreshStatus.Loading));

        Assert.Equal(10, Period(stale, "primary").ObservedPercentPoints);
        Assert.Equal(10, Period(loading, "primary").ObservedPercentPoints);
        Assert.Equal(ObservedUsageState.Partial, loading.State);
    }

    [Fact]
    public void AccountInvalidationClearsTheEstimate()
    {
        var estimator = new ObservedUsageEstimator(new FakeTimeProvider(T0));
        estimator.Observe(Fresh([Primary(10)], T0.AddSeconds(1)));
        estimator.Observe(Fresh([Primary(20)], T0.AddSeconds(130)));

        ObservedUsageSnapshot snapshot = estimator.Observe(new RefreshState(
            RefreshStatus.SignedOut,
            null,
            null,
            T0.AddSeconds(260),
            null,
            null,
            "signed out"));

        Assert.Equal(ObservedUsageState.Unavailable, snapshot.State);
        Assert.Empty(snapshot.Periods);
    }

    [Fact]
    public void LocalDayRolloverStartsANewBaseline()
    {
        var clock = new FakeTimeProvider(T0);
        var estimator = new ObservedUsageEstimator(clock);
        estimator.Observe(Fresh([Primary(10)], T0.AddSeconds(1)));
        estimator.Observe(Fresh([Primary(20)], T0.AddSeconds(130)));

        clock.Advance(TimeSpan.FromHours(20)); // next local day
        DateTimeOffset nextDay = clock.GetUtcNow();
        ObservedUsageSnapshot snapshot = estimator.Observe(Fresh([Primary(60, resetsAt: nextDay.AddHours(5))], nextDay));

        Assert.Equal(ObservedUsageState.Waiting, snapshot.State);
        Assert.Equal(0, Period(snapshot, "primary").ObservedPercentPoints);
        Assert.Equal(DateOnly.FromDateTime(nextDay.Date), snapshot.LocalDate);
    }

    [Fact]
    public void TotalCanExceedOneHundredAcrossObservedSegments()
    {
        var estimator = new ObservedUsageEstimator(new FakeTimeProvider(T0));
        estimator.Observe(Fresh([Primary(10)], T0.AddSeconds(1)));
        estimator.Observe(Fresh([Primary(95)], T0.AddSeconds(130))); // +85
        estimator.Observe(Fresh([Primary(5)], T0.AddSeconds(260)));  // reset -> keep 85, gap
        ObservedUsageSnapshot snapshot = estimator.Observe(Fresh([Primary(40)], T0.AddSeconds(390))); // +35

        ObservedPeriodEstimate primary = Period(snapshot, "primary");
        Assert.Equal(120, primary.ObservedPercentPoints);
        Assert.True(primary.HasGap);
        Assert.Equal(ObservedPeriodState.Partial, primary.State);
    }

    [Fact]
    public void AuxiliaryBucketsAndCreditsDoNotContribute()
    {
        var estimator = new ObservedUsageEstimator(new FakeTimeProvider(T0));
        var main = new QuotaBucket("codex", null, [Primary(10)], false, "12.5", true, false, null);
        var auxiliary = new QuotaBucket("gpt-4o", "辅助桶", [new QuotaWindow("primary", 1440, 99, 1, T0.AddHours(6))], false, null, null, null, null);

        ObservedUsageSnapshot snapshot = estimator.Observe(new RefreshState(
            RefreshStatus.Fresh,
            new QuotaSnapshot("codex", "plus", [main, auxiliary], T0.AddSeconds(1)),
            DailyUsageResult.Missing(DateOnly.FromDateTime(T0.Date)),
            T0.AddSeconds(1),
            T0.AddSeconds(1),
            T0.AddSeconds(121),
            null));

        Assert.Single(snapshot.Periods);
        Assert.Equal("primary", snapshot.Periods[0].Slot);
    }

    [Fact]
    public void InvalidReadKeepsKnownTotalButBreaksContinuity()
    {
        var estimator = new ObservedUsageEstimator(new FakeTimeProvider(T0));
        estimator.Observe(Fresh([Primary(10)], T0.AddSeconds(1)));
        estimator.Observe(Fresh([Primary(20)], T0.AddSeconds(130)));

        var invalidBucket = new QuotaBucket("codex", null, [Primary(20)], ContainsInvalidWindow: true, null, false, false, null);
        ObservedUsageSnapshot snapshot = estimator.Observe(new RefreshState(
            RefreshStatus.Fresh,
            new QuotaSnapshot("codex", "plus", [invalidBucket], T0.AddSeconds(260)),
            DailyUsageResult.Missing(DateOnly.FromDateTime(T0.Date)),
            T0.AddSeconds(260),
            T0.AddSeconds(260),
            T0.AddSeconds(380),
            null));

        ObservedPeriodEstimate primary = Period(snapshot, "primary");
        Assert.Equal(10, primary.ObservedPercentPoints);
        Assert.True(primary.HasGap);
        Assert.Equal(ObservedUsageState.Partial, snapshot.State);
    }

    [Fact]
    public void CurrentReflectsTheLatestObservation()
    {
        var estimator = new ObservedUsageEstimator(new FakeTimeProvider(T0));
        estimator.Observe(Fresh([Primary(10)], T0.AddSeconds(1)));

        Assert.Equal(ObservedUsageState.Waiting, estimator.Current.State);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(0L, false)]
    [InlineData(-1L, false)]
    [InlineData(300L, true)]
    public void UnusableMetadataCannotProveEvenZero(long? duration, bool missingReset)
    {
        var estimator = new ObservedUsageEstimator(new FakeTimeProvider(T0));
        QuotaWindow window = Primary(10, duration) with { ResetsAt = missingReset ? null : T0.AddHours(5) };
        estimator.Observe(Fresh([window], T0.AddSeconds(1)));
        ObservedPeriodEstimate period = Period(estimator.Observe(Fresh([window], T0.AddSeconds(130))), "primary");

        Assert.Equal(ObservedPeriodState.Unavailable, period.State);
        Assert.Equal(0, period.ComparableSamples);
        Assert.Equal(0, period.ObservedPercentPoints);
    }

    [Fact]
    public void EmptyFreshWindowListMarksAllPriorPeriodsPartialAndRebaselinesRecovery()
    {
        var estimator = new ObservedUsageEstimator(new FakeTimeProvider(T0));
        estimator.Observe(Fresh([Primary(10), Secondary(2)], T0.AddSeconds(1)));
        estimator.Observe(Fresh([Primary(20), Secondary(5)], T0.AddSeconds(130)));

        ObservedUsageSnapshot empty = estimator.Observe(Fresh([], T0.AddSeconds(260)));
        Assert.Equal(ObservedUsageState.Partial, empty.State);
        Assert.All(empty.Periods, period => Assert.True(period.HasGap));
        ObservedUsageSnapshot recovery = estimator.Observe(Fresh([Primary(90), Secondary(40)], T0.AddSeconds(390)));
        Assert.Equal(10, Period(recovery, "primary").ObservedPercentPoints);
        Assert.Equal(3, Period(recovery, "secondary").ObservedPercentPoints);
        Assert.Equal(1, Period(recovery, "primary").ComparableSamples);
    }

    [Fact]
    public void OrdinaryLoadingWithRetainedQuotaDoesNotBreakTheComparableInterval()
    {
        var estimator = new ObservedUsageEstimator(new FakeTimeProvider(T0));
        RefreshState baseline = Fresh([Primary(10)], T0.AddSeconds(1));
        estimator.Observe(baseline);
        estimator.Observe(baseline with { Status = RefreshStatus.Loading, LastAttempt = T0.AddSeconds(120) });
        ObservedPeriodEstimate period = Period(estimator.Observe(Fresh([Primary(20)], T0.AddSeconds(130))), "primary");

        Assert.Equal(10, period.ObservedPercentPoints);
        Assert.Equal(ObservedPeriodState.Available, period.State);
        Assert.False(period.HasGap);
    }

    [Theory]
    [InlineData(RefreshStatus.Stale, ProviderFailureKind.None)]
    [InlineData(RefreshStatus.Stale, ProviderFailureKind.Transport)]
    [InlineData(RefreshStatus.Error, ProviderFailureKind.Timeout)]
    [InlineData(RefreshStatus.Fresh, ProviderFailureKind.Canceled)]
    public void InterruptedObservationsExcludeTheCrossGapJump(RefreshStatus status, ProviderFailureKind failure)
    {
        var estimator = new ObservedUsageEstimator(new FakeTimeProvider(T0));
        estimator.Observe(Fresh([Primary(10)], T0.AddSeconds(1)));
        RefreshState prior = Fresh([Primary(20)], T0.AddSeconds(130));
        estimator.Observe(prior);
        estimator.Observe(prior with { Status = status, FailureKind = failure, LastAttempt = T0.AddSeconds(260) });
        ObservedPeriodEstimate recovery = Period(estimator.Observe(Fresh([Primary(90)], T0.AddSeconds(390))), "primary");

        Assert.Equal(10, recovery.ObservedPercentPoints);
        Assert.Equal(1, recovery.ComparableSamples);
        Assert.Equal(ObservedPeriodState.Partial, recovery.State);
        ObservedPeriodEstimate next = Period(estimator.Observe(Fresh([Primary(95)], T0.AddSeconds(520))), "primary");
        Assert.Equal(15, next.ObservedPercentPoints);
    }

    [Fact]
    public void MissingResetRetainsOnlyThePriorSafeTotalAndRequiresANewBaseline()
    {
        var estimator = new ObservedUsageEstimator(new FakeTimeProvider(T0));
        estimator.Observe(Fresh([Primary(10)], T0.AddSeconds(1)));
        estimator.Observe(Fresh([Primary(20)], T0.AddSeconds(130)));
        QuotaWindow missingReset = Primary(40) with { ResetsAt = null };
        estimator.Observe(Fresh([missingReset], T0.AddSeconds(260)));
        estimator.Observe(Fresh([missingReset with { UsedPercent = 60 }], T0.AddSeconds(390)));
        ObservedPeriodEstimate recovery = Period(estimator.Observe(Fresh([Primary(90)], T0.AddSeconds(520))), "primary");

        Assert.Equal(10, recovery.ObservedPercentPoints);
        Assert.Equal(ObservedPeriodState.Partial, recovery.State);
        Assert.Equal(1, recovery.ComparableSamples);
    }

    [Fact]
    public void PassedResetCannotAddEvenWhenStatusClaimsFresh()
    {
        var estimator = new ObservedUsageEstimator(new FakeTimeProvider(T0));
        DateTimeOffset reset = T0.AddSeconds(200);
        estimator.Observe(Fresh([Primary(10, resetsAt: reset)], T0.AddSeconds(1)));
        estimator.Observe(Fresh([Primary(20, resetsAt: reset)], T0.AddSeconds(130)));
        ObservedPeriodEstimate expired = Period(estimator.Observe(Fresh([Primary(90, resetsAt: reset)], T0.AddSeconds(260))), "primary");

        Assert.Equal(10, expired.ObservedPercentPoints);
        Assert.Equal(ObservedPeriodState.Partial, expired.State);
    }

    [Fact]
    public void BudgetChangeCannotSumOldAndNewDenominatorsAfterRecovery()
    {
        var estimator = new ObservedUsageEstimator(new FakeTimeProvider(T0));
        estimator.Observe(Fresh([Primary(10), Secondary(1)], T0.AddSeconds(1)));
        estimator.Observe(Fresh([Primary(30), Secondary(4)], T0.AddSeconds(130)));
        estimator.Observe(Fresh([Primary(40, 600), Secondary(5)], T0.AddSeconds(260)));
        ObservedUsageSnapshot next = estimator.Observe(Fresh([Primary(47, 600), Secondary(6)], T0.AddSeconds(390)));

        Assert.Equal(7, Period(next, "primary").ObservedPercentPoints);
        Assert.Equal(600, Period(next, "primary").DurationMinutes);
        Assert.Equal(ObservedPeriodState.Partial, Period(next, "primary").State);
        Assert.Equal(5, Period(next, "secondary").ObservedPercentPoints);
        Assert.Equal(ObservedPeriodState.Available, Period(next, "secondary").State);
    }

    [Fact]
    public void PlanChangeClearsEvenAnAbsentSlotBeforeItReappears()
    {
        var estimator = new ObservedUsageEstimator(new FakeTimeProvider(T0));
        estimator.Observe(Fresh([Primary(10), Secondary(1)], T0.AddSeconds(1)));
        estimator.Observe(Fresh([Primary(30), Secondary(4)], T0.AddSeconds(130)));
        ObservedUsageSnapshot changed = estimator.Observe(Fresh([Primary(40)], T0.AddSeconds(260), plan: "pro"));

        Assert.Equal(0, Period(changed, "primary").ObservedPercentPoints);
        Assert.DoesNotContain(changed.Periods, period => period.Slot == "secondary");
        estimator.Observe(Fresh([Primary(45), Secondary(50)], T0.AddSeconds(390), plan: "pro"));
        ObservedUsageSnapshot next = estimator.Observe(Fresh([Primary(47), Secondary(52)], T0.AddSeconds(520), plan: "pro"));
        Assert.Equal(7, Period(next, "primary").ObservedPercentPoints);
        Assert.Equal(2, Period(next, "secondary").ObservedPercentPoints);
    }

    [Fact]
    public void RepublishedSnapshotWithANewerSuccessTimeCannotProveASecondSample()
    {
        var estimator = new ObservedUsageEstimator(new FakeTimeProvider(T0));
        RefreshState original = Fresh([Primary(10)], T0.AddSeconds(1));
        estimator.Observe(original);
        ObservedPeriodEstimate period = Period(estimator.Observe(original with { LastSuccess = T0.AddSeconds(130) }), "primary");

        Assert.Equal(ObservedPeriodState.Waiting, period.State);
        Assert.Equal(1, period.ComparableSamples);
    }

    [Fact]
    public void DayRolloverRejectsYesterdayRepublishedAsFreshAndRequiresTwoTodaySamples()
    {
        var clock = new FakeTimeProvider(T0);
        var estimator = new ObservedUsageEstimator(clock);
        RefreshState yesterday = Fresh([Primary(10)], T0.AddSeconds(1));
        estimator.Observe(yesterday);
        clock.Advance(TimeSpan.FromHours(20));

        ObservedUsageSnapshot old = estimator.Observe(yesterday);
        Assert.Equal(ObservedUsageState.Unavailable, old.State);
        Assert.Empty(old.Periods);
        // A fetch begun before midnight and completed after it is also ambiguous.
        Assert.Empty(estimator.Observe(yesterday with { LastSuccess = clock.GetUtcNow() }).Periods);
        DateTimeOffset now = clock.GetUtcNow();
        DateTimeOffset reset = now.AddHours(5);
        ObservedUsageSnapshot first = estimator.Observe(Fresh([Primary(60, resetsAt: reset)], now));
        Assert.Equal(ObservedUsageState.Waiting, first.State);
        ObservedUsageSnapshot second = estimator.Observe(Fresh([Primary(65, resetsAt: reset)], now.AddSeconds(130)));
        Assert.Equal(5, Period(second, "primary").ObservedPercentPoints);
    }

    [Fact]
    public void CurrentCannotRelabelYesterdayAsTodayWithoutANewObservation()
    {
        var clock = new FakeTimeProvider(T0);
        var estimator = new ObservedUsageEstimator(clock);
        estimator.Observe(Fresh([Primary(10)], T0.AddSeconds(1)));
        clock.Advance(TimeSpan.FromHours(20));

        Assert.Equal(DateOnly.FromDateTime(clock.GetUtcNow().Date), estimator.Current.LocalDate);
        Assert.Empty(estimator.Current.Periods);
    }

    [Theory]
    [InlineData(RefreshStatus.Fresh)]
    [InlineData(RefreshStatus.Loading)]
    [InlineData(RefreshStatus.Unsupported)]
    public void NullQuotaInvalidatesAccountEvenWithoutSignedOutStatus(RefreshStatus status)
    {
        var estimator = new ObservedUsageEstimator(new FakeTimeProvider(T0));
        estimator.Observe(Fresh([Primary(10)], T0.AddSeconds(1)));
        estimator.Observe(Fresh([Primary(20)], T0.AddSeconds(130)));
        ObservedUsageSnapshot cleared = estimator.Observe(Fresh([], T0.AddSeconds(260)) with { Status = status, LastQuota = null });

        Assert.Empty(cleared.Periods);
        Assert.Equal(ObservedUsageState.Waiting, estimator.Observe(Fresh([Primary(90)], T0.AddSeconds(390))).State);
    }

    [Theory]
    [InlineData(null, "codex")]
    [InlineData("", "codex")]
    [InlineData("plus", "")]
    public void UnknownBudgetIdentityCannotProveComparableNumbers(string? plan, string provider)
    {
        var estimator = new ObservedUsageEstimator(new FakeTimeProvider(T0));
        estimator.Observe(Fresh([Primary(10)], T0.AddSeconds(1), plan: plan, provider: provider));
        ObservedPeriodEstimate period = Period(estimator.Observe(Fresh([Primary(20)], T0.AddSeconds(130), plan: plan, provider: provider)), "primary");

        Assert.Equal(ObservedPeriodState.Unavailable, period.State);
        Assert.Equal(0, period.ObservedPercentPoints);
    }

    [Fact]
    public void MissingDurationThenRecoveryCannotRestoreOldBudgetTotals()
    {
        var estimator = new ObservedUsageEstimator(new FakeTimeProvider(T0));
        estimator.Observe(Fresh([Primary(10)], T0.AddSeconds(1)));
        estimator.Observe(Fresh([Primary(30)], T0.AddSeconds(130)));
        estimator.Observe(Fresh([Primary(50, duration: null)], T0.AddSeconds(260)));
        estimator.Observe(Fresh([Primary(80)], T0.AddSeconds(390)));
        ObservedPeriodEstimate period = Period(estimator.Observe(Fresh([Primary(85)], T0.AddSeconds(520))), "primary");

        Assert.Equal(5, period.ObservedPercentPoints);
        Assert.Equal(ObservedPeriodState.Partial, period.State);
    }

    [Fact]
    public void MissingWindowWithoutAnyKnownDeltaIsIndividuallyUnavailable()
    {
        var estimator = new ObservedUsageEstimator(new FakeTimeProvider(T0));
        estimator.Observe(Fresh([Primary(10), Secondary(1)], T0.AddSeconds(1)));
        ObservedUsageSnapshot snapshot = estimator.Observe(Fresh([Primary(20)], T0.AddSeconds(130)));

        Assert.Equal(ObservedPeriodState.Unavailable, Period(snapshot, "secondary").State);
        Assert.Equal(ObservedUsageState.Partial, snapshot.State);
    }

    [Fact]
    public void LocalTimezoneControlsRolloverRatherThanTheUtcCalendar()
    {
        DateTimeOffset beforeMidnight = new(2026, 10, 12, 15, 59, 0, TimeSpan.Zero);
        var clock = new ZonedClock(beforeMidnight);
        var estimator = new ObservedUsageEstimator(clock);
        estimator.Observe(Fresh([Primary(10, resetsAt: beforeMidnight.AddHours(5))], beforeMidnight));
        clock.Now = beforeMidnight.AddMinutes(2); // still Oct 12 UTC, Oct 13 in UTC+8
        Assert.Equal(new DateOnly(2026, 10, 13), estimator.Current.LocalDate);
        Assert.Empty(estimator.Observe(Fresh([Primary(10)], beforeMidnight)).Periods);
        ObservedUsageSnapshot first = estimator.Observe(Fresh([Primary(20, resetsAt: clock.Now.AddHours(5))], clock.Now));
        Assert.Equal(ObservedUsageState.Waiting, first.State);
        Assert.Equal(0, Period(first, "primary").ObservedPercentPoints);
    }

    [Fact]
    public void ChangedEpochCanAccumulateSafeSegmentsAboveOneHundredWithoutResetGuessing()
    {
        var estimator = new ObservedUsageEstimator(new FakeTimeProvider(T0));
        estimator.Observe(Fresh([Primary(10)], T0.AddSeconds(1)));
        estimator.Observe(Fresh([Primary(95)], T0.AddSeconds(130)));
        estimator.Observe(Fresh([Primary(5, resetsAt: T0.AddHours(10))], T0.AddSeconds(260)));
        ObservedPeriodEstimate period = Period(estimator.Observe(Fresh([Primary(40, resetsAt: T0.AddHours(10))], T0.AddSeconds(390))), "primary");

        Assert.Equal(120, period.ObservedPercentPoints);
        Assert.Equal(ObservedPeriodState.Partial, period.State);
    }

    [Fact]
    public void OlderSnapshotCannotMoveTheBaselineBackwardsEvenWithANewerCompletion()
    {
        var estimator = new ObservedUsageEstimator(new FakeTimeProvider(T0));
        estimator.Observe(Fresh([Primary(10)], T0.AddSeconds(1)));
        estimator.Observe(Fresh([Primary(20)], T0.AddSeconds(130)));
        estimator.Observe(Fresh([Primary(15)], T0.AddSeconds(60)) with { LastSuccess = T0.AddSeconds(260) });
        ObservedPeriodEstimate period = Period(estimator.Observe(Fresh([Primary(25)], T0.AddSeconds(390))), "primary");

        Assert.Equal(15, period.ObservedPercentPoints);
        Assert.False(period.HasGap);
    }

    private sealed class ZonedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("test-utc8", TimeSpan.FromHours(8), "test", "test");
    }
}
