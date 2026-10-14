using System.Text.Json;
using CodexQuota.Core.Models;
using CodexQuota.Core.Providers;
using CodexQuota.Core.Services;
using CodexQuota.Core.Transport;
using CodexQuota.Tests.Support;

namespace CodexQuota.Tests;

public sealed class AuditRegressionTests
{
    [Theory]
    [InlineData("42")]
    [InlineData("\"bad\"")]
    [InlineData("[]")]
    public void MalformedReturnedPeriodCannotImproveTheBall(string period)
    {
        using var json = JsonDocument.Parse("{\"rateLimits\":{\"limitId\":\"codex\",\"primary\":{\"usedPercent\":20},\"secondary\":" + period + "}}");
        Assert.Null(QuotaProjection.GetRemainingPercent(CodexResponseDecoder.DecodeQuota(json.RootElement, DateTimeOffset.UtcNow)));
    }

    [Fact]
    public void InvalidMappedCodexBucketCannotFallBackToFavorableLegacy()
    {
        using var json = JsonDocument.Parse("""{"rateLimits":{"limitId":"codex","primary":{"usedPercent":5}},"rateLimitsByLimitId":{"codex":42}}""");
        Assert.Null(QuotaProjection.GetRemainingPercent(CodexResponseDecoder.DecodeQuota(json.RootElement, DateTimeOffset.UtcNow)));
    }

    [Fact]
    public void LegacyWithoutIdRemainsUsableBesideAuxiliaryMapEntries()
    {
        using var json = JsonDocument.Parse("""{"rateLimits":{"primary":{"usedPercent":40}},"rateLimitsByLimitId":{"other":{"primary":{"usedPercent":1}}}}""");
        Assert.Equal(60, QuotaProjection.GetRemainingPercent(CodexResponseDecoder.DecodeQuota(json.RootElement, DateTimeOffset.UtcNow)));
    }

    [Fact]
    public void MapKeyIsAuthoritativeOverAnInconsistentLimitId()
    {
        using var json = JsonDocument.Parse("""{"rateLimitsByLimitId":{"codex":{"limitId":"other","primary":{"usedPercent":95}},"other":{"limitId":"codex","primary":{"usedPercent":5}}}}""");
        Assert.Equal(5, QuotaProjection.GetRemainingPercent(CodexResponseDecoder.DecodeQuota(json.RootElement, DateTimeOffset.UtcNow)));
    }

    [Fact]
    public void SchemaAccountWithoutIdStillHasAnInMemoryFingerprint()
    {
        using var a = JsonDocument.Parse("""{"account":{"type":"chatgpt","email":"synthetic-a@example.invalid","planType":"plus"}}""");
        using var b = JsonDocument.Parse("""{"account":{"type":"chatgpt","email":"synthetic-b@example.invalid","planType":"plus"}}""");
        string? key = CodexResponseDecoder.DecodeAccountKey(a.RootElement);
        Assert.NotNull(key);
        Assert.NotEqual(key, CodexResponseDecoder.DecodeAccountKey(b.RootElement));
        Assert.DoesNotContain("@", key);
    }

    [Fact]
    public async Task SignedOutAfterSuccessClearsAccountData()
    {
        var provider = new MutableProvider();
        await using var coordinator = new RefreshCoordinator(provider);
        await coordinator.RefreshOnceAsync(CancellationToken.None);
        provider.Failure = QuotaProviderException.NotSignedIn("signed out");
        await coordinator.RefreshOnceAsync(CancellationToken.None);
        Assert.Null(coordinator.State.LastQuota);
        Assert.Null(coordinator.State.LastSuccess);
        Assert.NotEqual(DailyUsageAvailability.Available, coordinator.State.DailyUsage?.Availability);
    }

    [Fact]
    public async Task MidnightFailureCannotKeepYesterdayUnderToday()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 11, 23, 59, 59, TimeSpan.Zero));
        var provider = new MutableProvider();
        await using var coordinator = new RefreshCoordinator(provider, clock);
        await coordinator.RefreshOnceAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(2));
        provider.Failure = QuotaProviderException.Transport("offline");
        await coordinator.RefreshOnceAsync(CancellationToken.None);
        Assert.NotEqual(DailyUsageAvailability.Available, coordinator.State.DailyUsage?.Availability);
    }

    [Fact]
    public async Task SuccessTimestampAndScheduleUseCompletionTime()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var provider = new FakeQuotaProvider { OnFetch = (_, _) => { clock.Advance(TimeSpan.FromSeconds(8)); return Task.CompletedTask; } };
        await using var coordinator = new RefreshCoordinator(provider, clock);
        await coordinator.RefreshOnceAsync(CancellationToken.None);
        Assert.Equal(clock.GetUtcNow(), coordinator.State.LastSuccess);
        Assert.Equal(clock.GetUtcNow().AddSeconds(120), coordinator.State.NextScheduledFetch);
    }

    [Fact]
    public async Task ConcurrentSettingsSavesRemainValidAndAtomic()
    {
        string root = Path.Combine(Path.GetTempPath(), "codexquota-audit-" + Guid.NewGuid().ToString("N"));
        var store = new SettingsStore(root);
        try
        {
            await Task.WhenAll(Enumerable.Range(0, 20).Select(i => store.SaveAsync(WidgetSettings.Default with { Opacity = 0.4 + (i / 100d) })));
            Assert.InRange(Math.Round((await store.LoadAsync()).Opacity, 2), 0.4, 0.59);
            Assert.Null(store.LastWarning);
            Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DailyTimeoutKeepsQuotaAndDisposesHelper()
    {
        var factory = new JsonRpcProcessClientFactory(new ProcessTransportProcessFactory(), TimeSpan.FromMilliseconds(800));
        await using var provider = new CodexQuotaProvider(factory, FakeServerHarness.Spec("usage-hang"), includeDailyUsage: true);
        ProviderRefreshResult result = await provider.FetchAsync(DateOnly.FromDateTime(DateTime.Now), CancellationToken.None);
        Assert.Equal(46, QuotaProjection.GetRemainingPercent(result.Quota));
        Assert.Equal(DailyUsageAvailability.Failed, result.DailyUsage.Availability);
    }

    [Fact]
    public async Task NormalPathDoesNotRequestTheOptionalOfficialDailyMethod()
    {
        var factory = new JsonRpcProcessClientFactory(new ProcessTransportProcessFactory());
        await using var provider = new CodexQuotaProvider(factory, FakeServerHarness.Spec("usage-hang"));
        ProviderRefreshResult result = await provider.FetchAsync(DateOnly.FromDateTime(DateTime.Now), CancellationToken.None);
        Assert.Equal(46, QuotaProjection.GetRemainingPercent(result.Quota));
        // Without the opt-in the hanging daily method must never be called, so the fetch
        // completes without the daily timeout and reports no official daily data.
        Assert.Equal(DailyUsageAvailability.Missing, result.DailyUsage.Availability);
    }

    [Fact]
    public async Task ManualRefreshResetsTheActualSleepingSchedule()
    {
        var provider = new MutableProvider();
        await using var coordinator = new RefreshCoordinator(provider, interval: TimeSpan.FromMilliseconds(500));
        await coordinator.StartAsync();
        await WaitUntilAsync(() => provider.Reads == 1);
        await Task.Delay(300);
        await coordinator.RequestRefreshAsync(CancellationToken.None);
        await Task.Delay(300);
        Assert.Equal(2, provider.Reads); // original sleeping deadline must not fire at +500ms
        await WaitUntilAsync(() => provider.Reads >= 3);
    }

    [Fact]
    public async Task ShorteningIntervalWakesTheActualSleepingLoop()
    {
        var provider = new MutableProvider();
        await using var coordinator = new RefreshCoordinator(provider, interval: TimeSpan.FromSeconds(20));
        await coordinator.StartAsync();
        await WaitUntilAsync(() => provider.Reads == 1);
        coordinator.UpdateInterval(TimeSpan.FromMilliseconds(100));
        await WaitUntilAsync(() => provider.Reads >= 2);
    }

    [Fact]
    public async Task DisposalCancelsAndWaitsForAManualCycle()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeQuotaProvider { OnFetch = async (_, ct) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); } };
        var coordinator = new RefreshCoordinator(provider);
        Task refresh = coordinator.RequestRefreshAsync(CancellationToken.None);
        await entered.Task;
        await coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        Assert.True(provider.Disposed);
    }

    [Fact]
    public async Task ChangedAccountWithFailedQuotaCannotRetainThePreviousAccount()
    {
        int cycle = 0;
        var factory = new JsonRpcProcessClientFactory(new ProcessTransportProcessFactory());
        var provider = new CodexQuotaProvider(factory, () => FakeServerHarness.Spec(cycle++ == 0 ? "happy" : "account-other-quota-error"));
        await using var coordinator = new RefreshCoordinator(provider);
        await coordinator.RefreshOnceAsync(CancellationToken.None);
        Assert.NotNull(coordinator.State.LastQuota);
        await coordinator.RefreshOnceAsync(CancellationToken.None);
        Assert.Null(coordinator.State.LastQuota);
    }

    [Fact]
    public async Task AccountNotificationDuringTheCycleRejectsMixedAccountData()
    {
        var factory = new JsonRpcProcessClientFactory(new ProcessTransportProcessFactory());
        await using var provider = new CodexQuotaProvider(factory, FakeServerHarness.Spec("account-updated"));
        QuotaProviderException error = await Assert.ThrowsAsync<QuotaProviderException>(() => provider.FetchAsync(DateOnly.FromDateTime(DateTime.Now), CancellationToken.None));
        Assert.Equal(ProviderFailureKind.AccountChanged, error.Kind);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) { await Task.Delay(10, deadline.Token); }
    }

    private sealed class MutableProvider : IQuotaProvider
    {
        public string Id => "test";
        public Exception? Failure { get; set; }
        public int Reads { get; private set; }
        public Task<ProviderRefreshResult> FetchAsync(DateOnly date, CancellationToken cancellationToken)
        {
            Reads++;
            return Failure is { } failure
            ? Task.FromException<ProviderRefreshResult>(failure)
            : Task.FromResult(new ProviderRefreshResult(Fixtures.MainQuota(20) with { Buckets = [Fixtures.MainQuota(20).Buckets[0] with { Windows = [new QuotaWindow("primary", 300, 20, 80, DateTimeOffset.Now.AddHours(1))] }] }, DailyUsageResult.Available(date, 7, date), "synthetic"));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
