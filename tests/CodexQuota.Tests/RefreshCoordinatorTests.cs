using CodexQuota.Core.Models;
using CodexQuota.Core.Providers;
using CodexQuota.Core.Services;
using CodexQuota.Tests.Support;

namespace CodexQuota.Tests;

public sealed class RefreshCoordinatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(120);
    private static readonly DateOnly Today = new(2026, 10, 11);

    private static RefreshCoordinator CreateCoordinator(IQuotaProvider provider, FakeTimeProvider clock) =>
        new(provider, clock, Interval);

    [Fact]
    public async Task DailyMethodFailureDoesNotDiscardFreshQuota()
    {
        var provider = new FakeQuotaProvider(
            quota: Fixtures.MainQuota(usedPercent: 54),
            daily: DailyUsageResult.Unsupported(Today));

        await using RefreshCoordinator coordinator = CreateCoordinator(provider, new FakeTimeProvider(Start));
        await coordinator.RefreshOnceAsync(CancellationToken.None);

        Assert.Equal(RefreshStatus.Fresh, coordinator.State.Status);
        Assert.Equal(46, QuotaProjection.GetRemainingPercent(coordinator.State.LastQuota!));
        Assert.Equal(DailyUsageAvailability.Unsupported, coordinator.State.DailyUsage!.Availability);
    }

    [Fact]
    public async Task ImmediateFirstFetchHappensOnStart()
    {
        var provider = new FakeQuotaProvider(quota: Fixtures.MainQuota(10));
        await using RefreshCoordinator coordinator = CreateCoordinator(provider, new FakeTimeProvider(Start));

        var firstFresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.StateChanged += state =>
        {
            if (state.Status == RefreshStatus.Fresh)
            {
                firstFresh.TrySetResult();
            }
        };

        await coordinator.StartAsync();
        Task completed = await Task.WhenAny(firstFresh.Task, Task.Delay(TimeSpan.FromSeconds(10)));

        await coordinator.StopAsync();

        Assert.Same(firstFresh.Task, completed);
        Assert.Equal(1, provider.CallCount);
        Assert.Equal(RefreshStatus.Fresh, coordinator.State.Status);
    }

    [Fact]
    public async Task ManualRequestIsDroppedWhileACycleIsRunning()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeQuotaProvider(quota: Fixtures.MainQuota(50))
        {
            OnFetch = async (_, _) =>
            {
                entered.TrySetResult();
                await gate.Task.ConfigureAwait(false);
            },
        };

        await using RefreshCoordinator coordinator = CreateCoordinator(provider, new FakeTimeProvider(Start));

        Task first = coordinator.RefreshOnceAsync(CancellationToken.None);
        await entered.Task;

        bool ran = await coordinator.RequestRefreshAsync(CancellationToken.None);

        gate.SetResult();
        await first;

        Assert.False(ran);
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task BackoffSequenceIsBoundedAtFiveMinutes()
    {
        var clock = new FakeTimeProvider(Start);
        var provider = new FakeQuotaProvider(exception: QuotaProviderException.Transport("boom"));

        await using RefreshCoordinator coordinator = CreateCoordinator(provider, clock);

        int[] expected = [30, 60, 120, 240, 300, 300];
        foreach (int seconds in expected)
        {
            await coordinator.RefreshOnceAsync(CancellationToken.None);
            TimeSpan delay = coordinator.State.NextScheduledFetch!.Value - coordinator.State.LastAttempt!.Value;
            Assert.Equal(seconds, (int)delay.TotalSeconds);
            clock.Advance(delay);
        }

        Assert.Equal(RefreshStatus.Error, coordinator.State.Status);
        Assert.Equal(ProviderFailureKind.Transport, coordinator.State.FailureKind);
    }

    [Fact]
    public async Task SuccessResetsBackoff()
    {
        var clock = new FakeTimeProvider(Start);
        var provider = new ScriptedProvider();
        provider.EnqueueThrow(QuotaProviderException.Transport("boom"));
        provider.EnqueueThrow(QuotaProviderException.Transport("boom"));
        provider.EnqueueResult(new ProviderRefreshResult(Fixtures.MainQuota(20), DailyUsageResult.Missing(Today), null));
        provider.EnqueueResult(new ProviderRefreshResult(Fixtures.MainQuota(20), DailyUsageResult.Missing(Today), null));

        await using RefreshCoordinator coordinator = CreateCoordinator(provider, clock);

        await coordinator.RefreshOnceAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(30));
        await coordinator.RefreshOnceAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(60));

        await coordinator.RefreshOnceAsync(CancellationToken.None);
        Assert.Equal(RefreshStatus.Fresh, coordinator.State.Status);
        TimeSpan successDelay = coordinator.State.NextScheduledFetch!.Value - coordinator.State.LastAttempt!.Value;
        Assert.Equal(Interval, successDelay);

        clock.Advance(Interval);
        await coordinator.RefreshOnceAsync(CancellationToken.None);
        TimeSpan followUp = coordinator.State.NextScheduledFetch!.Value - coordinator.State.LastAttempt!.Value;
        Assert.Equal(Interval, followUp);
    }

    [Fact]
    public async Task FailureAfterSuccessKeepsTheSnapshotAndMarksItStale()
    {
        var clock = new FakeTimeProvider(Start);
        var provider = new ScriptedProvider();
        provider.EnqueueResult(new ProviderRefreshResult(Fixtures.MainQuota(54), DailyUsageResult.Missing(Today), null));
        provider.EnqueueThrow(QuotaProviderException.Transport("boom"));

        await using RefreshCoordinator coordinator = CreateCoordinator(provider, clock);

        await coordinator.RefreshOnceAsync(CancellationToken.None);
        Assert.Equal(RefreshStatus.Fresh, coordinator.State.Status);
        QuotaSnapshot snapshot = coordinator.State.LastQuota!;

        clock.Advance(TimeSpan.FromSeconds(30));
        await coordinator.RefreshOnceAsync(CancellationToken.None);

        Assert.Equal(RefreshStatus.Stale, coordinator.State.Status);
        Assert.Same(snapshot, coordinator.State.LastQuota);
        Assert.Equal(46, QuotaProjection.GetRemainingPercent(coordinator.State.LastQuota!));
    }

    [Fact]
    public async Task AgeBeyondThreeIntervalsMarksDataStaleWithoutAFailure()
    {
        var clock = new FakeTimeProvider(Start);
        var provider = new FakeQuotaProvider(quota: Fixtures.MainQuota(50));
        await using RefreshCoordinator coordinator = CreateCoordinator(provider, clock);

        await coordinator.RefreshOnceAsync(CancellationToken.None);
        Assert.Equal(RefreshStatus.Fresh, coordinator.State.Status);

        clock.Advance((Interval * 3) + TimeSpan.FromSeconds(1));
        coordinator.EvaluateStaleness();

        Assert.Equal(RefreshStatus.Stale, coordinator.State.Status);
        Assert.NotNull(coordinator.State.LastQuota);
    }

    [Fact]
    public async Task UnsupportedAccountUsesTheNormalInterval()
    {
        var clock = new FakeTimeProvider(Start);
        var provider = new FakeQuotaProvider(exception: QuotaProviderException.UnsupportedAccount("nope"));

        await using RefreshCoordinator coordinator = CreateCoordinator(provider, clock);

        await coordinator.RefreshOnceAsync(CancellationToken.None);

        Assert.Equal(RefreshStatus.Unsupported, coordinator.State.Status);
        TimeSpan delay = coordinator.State.NextScheduledFetch!.Value - coordinator.State.LastAttempt!.Value;
        Assert.Equal(Interval, delay);
    }

    [Fact]
    public async Task NotSignedInIsReportedAsSignedOut()
    {
        var provider = new FakeQuotaProvider(exception: QuotaProviderException.NotSignedIn("nope"));
        await using RefreshCoordinator coordinator = CreateCoordinator(provider, new FakeTimeProvider(Start));

        await coordinator.RefreshOnceAsync(CancellationToken.None);

        Assert.Equal(RefreshStatus.SignedOut, coordinator.State.Status);
        Assert.Equal(ProviderFailureKind.NotSignedIn, coordinator.State.FailureKind);
    }

    [Fact]
    public async Task AccountChangeClearsThePreviousSnapshotBeforePublishingTheNewOne()
    {
        var clock = new FakeTimeProvider(Start);
        var provider = new ScriptedProvider();
        provider.EnqueueResult(new ProviderRefreshResult(Fixtures.MainQuota(50), DailyUsageResult.Missing(Today), "AAAA"));
        provider.EnqueueResult(new ProviderRefreshResult(Fixtures.MainQuota(90), DailyUsageResult.Missing(Today), "BBBB"));

        await using RefreshCoordinator coordinator = CreateCoordinator(provider, clock);
        var observed = new List<QuotaSnapshot?>();
        coordinator.StateChanged += state => observed.Add(state.LastQuota);

        await coordinator.RefreshOnceAsync(CancellationToken.None);
        await coordinator.RefreshOnceAsync(CancellationToken.None);

        int firstNonNull = observed.FindIndex(snapshot => snapshot is not null);
        int clearedAfterFirst = observed
            .Skip(firstNonNull)
            .ToList()
            .FindIndex(snapshot => snapshot is null);

        Assert.True(firstNonNull >= 0, "the first account snapshot must be published");
        Assert.True(clearedAfterFirst >= 0, "an account change must publish a cleared snapshot first");
        Assert.Equal(10, QuotaProjection.GetRemainingPercent(coordinator.State.LastQuota!));
    }

    [Fact]
    public async Task CrossingMidnightFetchesTheNewLocalDate()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 11, 23, 59, 30, TimeSpan.Zero));
        var provider = new FakeQuotaProvider(quota: Fixtures.MainQuota(50));
        await using RefreshCoordinator coordinator = CreateCoordinator(provider, clock);

        await coordinator.RefreshOnceAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(2));
        await coordinator.RefreshOnceAsync(CancellationToken.None);

        Assert.Equal([new DateOnly(2026, 10, 11), new DateOnly(2026, 10, 12)], provider.RequestedDates);
    }

    [Fact]
    public async Task StateIsPublishedToSubscribers()
    {
        var provider = new FakeQuotaProvider(quota: Fixtures.MainQuota(50));
        await using RefreshCoordinator coordinator = CreateCoordinator(provider, new FakeTimeProvider(Start));

        var states = new List<RefreshState>();
        coordinator.StateChanged += states.Add;

        await coordinator.RefreshOnceAsync(CancellationToken.None);

        Assert.NotEmpty(states);
        Assert.Equal(RefreshStatus.Fresh, states[^1].Status);
    }

    [Fact]
    public async Task IntervalUpdatesDoNotCreateASecondLoop()
    {
        var provider = new FakeQuotaProvider(quota: Fixtures.MainQuota(50));
        await using RefreshCoordinator coordinator = CreateCoordinator(provider, new FakeTimeProvider(Start));

        coordinator.UpdateInterval(TimeSpan.FromSeconds(400));
        await coordinator.StartAsync();
        await coordinator.StartAsync();

        await coordinator.RefreshOnceAsync(CancellationToken.None);
        await coordinator.StopAsync();

        Assert.Equal(TimeSpan.FromSeconds(400), coordinator.Interval);
    }

    [Fact]
    public async Task CanceledReadPublishesATypedGapAndRetainsTheCachedQuota()
    {
        var clock = new FakeTimeProvider(Start);
        using var cancellation = new CancellationTokenSource();
        int reads = 0;
        var provider = new FakeQuotaProvider(quota: Fixtures.MainQuota(10) with { FetchedAt = Start })
        {
            OnFetch = (_, token) =>
            {
                if (++reads == 2) { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
                return Task.CompletedTask;
            },
        };
        await using RefreshCoordinator coordinator = CreateCoordinator(provider, clock);
        var estimate = new ObservedUsageEstimator(clock);
        coordinator.StateChanged += state => estimate.Observe(state);
        await coordinator.RefreshOnceAsync(CancellationToken.None);
        QuotaSnapshot? retained = coordinator.State.LastQuota;
        clock.Advance(Interval);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.RefreshOnceAsync(cancellation.Token));

        Assert.Same(retained, coordinator.State.LastQuota);
        Assert.Equal(ProviderFailureKind.Canceled, coordinator.State.FailureKind);
        Assert.Equal(RefreshStatus.Stale, coordinator.State.Status);
        Assert.True(estimate.Current.Periods.Single().HasGap);

        clock.Advance(Interval);
        Assert.True(await coordinator.RequestRefreshAsync(CancellationToken.None));
        Assert.Equal(3, provider.CallCount);
        Assert.Equal(RefreshStatus.Fresh, coordinator.State.Status);
        Assert.Equal(ProviderFailureKind.None, coordinator.State.FailureKind);
        Assert.Same(retained, coordinator.State.LastQuota);
        await coordinator.DisposeAsync();
        Assert.True(provider.Disposed);
    }

    private sealed class ScriptedProvider : IQuotaProvider
    {
        private readonly Queue<Func<ProviderRefreshResult>> _steps = new();

        public string Id => "scripted";

        public void EnqueueResult(ProviderRefreshResult result) => _steps.Enqueue(() => result);

        public void EnqueueThrow(Exception exception) => _steps.Enqueue(() => throw exception);

        public Task<ProviderRefreshResult> FetchAsync(DateOnly date, CancellationToken cancellationToken)
        {
            if (_steps.Count == 0)
            {
                throw new InvalidOperationException("No scripted step remains.");
            }

            return Task.FromResult(_steps.Dequeue()());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
