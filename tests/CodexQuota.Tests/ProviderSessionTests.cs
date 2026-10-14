using System.Diagnostics;
using System.Globalization;
using CodexQuota.Core.Models;
using CodexQuota.Core.Providers;
using CodexQuota.Core.Services;
using CodexQuota.Core.Transport;
using CodexQuota.Tests.Support;

namespace CodexQuota.Tests;

public sealed class ProviderSessionTests
{
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    private static CodexQuotaProvider CreateProvider(string scenario, string? appendFile = null, TimeSpan? timeout = null)
    {
        var factory = new JsonRpcProcessClientFactory(new ProcessTransportProcessFactory(), timeout);
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        // The optional official daily read is opt-in; these tests cover the legacy path.
        return new CodexQuotaProvider(factory, FakeServerHarness.Spec(scenario, appendFile: appendFile), timeProvider, includeDailyUsage: true);
    }

    [Fact]
    public async Task OneHelperPerFetchAndNoHelperRemainsBetweenCycles()
    {
        string appendFile = Path.Combine(Path.GetTempPath(), $"codexquota-launch-{Guid.NewGuid():N}.txt");

        await using CodexQuotaProvider provider = CreateProvider("happy", appendFile);

        ProviderRefreshResult first = await provider.FetchAsync(Today, CancellationToken.None);
        Assert.Equal(46, QuotaProjection.GetRemainingPercent(first.Quota));
        Assert.Equal(DailyUsageAvailability.Available, first.DailyUsage.Availability);

        ProviderRefreshResult second = await provider.FetchAsync(Today, CancellationToken.None);
        Assert.Equal(46, QuotaProjection.GetRemainingPercent(second.Quota));

        var pids = ReadPids(appendFile);
        Assert.Equal(2, pids.Count);
        foreach (int pid in pids)
        {
            Assert.False(FakeServerHarness.ProcessExists(pid), $"helper {pid} must not remain between cycles");
        }
    }

    [Fact]
    public async Task DailyUnsupportedDoesNotDiscardQuota()
    {
        await using CodexQuotaProvider provider = CreateProvider("usage-method-not-found");

        ProviderRefreshResult result = await provider.FetchAsync(Today, CancellationToken.None);

        Assert.Equal(46, QuotaProjection.GetRemainingPercent(result.Quota));
        Assert.Equal(DailyUsageAvailability.Unsupported, result.DailyUsage.Availability);
        Assert.Null(result.DailyUsage.Tokens);
    }

    [Fact]
    public async Task DailyTokensForTodayAreAvailable()
    {
        await using CodexQuotaProvider provider = CreateProvider("happy");

        ProviderRefreshResult result = await provider.FetchAsync(Today, CancellationToken.None);

        Assert.Equal(DailyUsageAvailability.Available, result.DailyUsage.Availability);
        Assert.Equal(123456, result.DailyUsage.Tokens);
        Assert.Equal(Today, result.DailyUsage.SourceDate);
    }

    [Fact]
    public async Task SignedOutAccountIsReportedAsNotSignedIn()
    {
        await using CodexQuotaProvider provider = CreateProvider("account-null");

        QuotaProviderException error = await Assert.ThrowsAsync<QuotaProviderException>(
            () => provider.FetchAsync(Today, CancellationToken.None));

        Assert.Equal(ProviderFailureKind.NotSignedIn, error.Kind);
    }

    [Fact]
    public async Task ApiKeyAccountIsReportedAsUnsupported()
    {
        await using CodexQuotaProvider provider = CreateProvider("account-apikey");

        QuotaProviderException error = await Assert.ThrowsAsync<QuotaProviderException>(
            () => provider.FetchAsync(Today, CancellationToken.None));

        Assert.Equal(ProviderFailureKind.UnsupportedAccount, error.Kind);
    }

    [Fact]
    public async Task QuotaErrorPropagatesAndTheOwnedHelperExits()
    {
        string appendFile = Path.Combine(Path.GetTempPath(), $"codexquota-launch-{Guid.NewGuid():N}.txt");
        await using CodexQuotaProvider provider = CreateProvider("quota-error", appendFile);

        await Assert.ThrowsAsync<QuotaProviderException>(() => provider.FetchAsync(Today, CancellationToken.None));

        foreach (int pid in ReadPids(appendFile))
        {
            Assert.False(FakeServerHarness.ProcessExists(pid), $"helper {pid} must be disposed after a failure");
        }
    }

    [Fact]
    public async Task CancellationDisposesTheOwnedHelper()
    {
        string appendFile = Path.Combine(Path.GetTempPath(), $"codexquota-launch-{Guid.NewGuid():N}.txt");
        await using CodexQuotaProvider provider = CreateProvider("hang-all", appendFile);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.FetchAsync(Today, cancellation.Token));

        await WaitUntilAsync(() => ReadPids(appendFile).Count > 0, TimeSpan.FromSeconds(5));
        foreach (int pid in ReadPids(appendFile))
        {
            await WaitUntilAsync(() => !FakeServerHarness.ProcessExists(pid), TimeSpan.FromSeconds(10));
            Assert.False(FakeServerHarness.ProcessExists(pid), $"helper {pid} must be disposed after cancellation");
        }
    }

    [Fact]
    public async Task AccountKeyIsAnOpaqueShortFingerprint()
    {
        await using CodexQuotaProvider provider = CreateProvider("happy");

        ProviderRefreshResult result = await provider.FetchAsync(Today, CancellationToken.None);

        Assert.NotNull(result.AccountKey);
        Assert.Equal(16, result.AccountKey!.Length);
        Assert.DoesNotContain("acct-test", result.AccountKey, StringComparison.Ordinal);
    }

    private static List<int> ReadPids(string appendFile)
    {
        if (!File.Exists(appendFile))
        {
            return [];
        }

        var pids = new List<int>();
        foreach (string line in File.ReadAllLines(appendFile))
        {
            if (line.StartsWith("start:", StringComparison.Ordinal) &&
                int.TryParse(line["start:".Length..], CultureInfo.InvariantCulture, out int pid))
            {
                pids.Add(pid);
            }
        }

        return pids;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }
    }
}
