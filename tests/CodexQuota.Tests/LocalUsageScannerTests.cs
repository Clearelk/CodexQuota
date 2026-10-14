using System.Globalization;
using System.Text;
using CodexQuota.Core.LocalUsage;
using CodexQuota.Core.Models;
using CodexQuota.Tests.Support;

namespace CodexQuota.Tests;

/// <summary>
/// Deterministic coverage for the device-local daily statistics contract. Every fixture
/// is synthetic: no real session content, prompt, response or account identifier appears
/// here, and the scanner is pointed at an isolated temporary root.
/// </summary>
public sealed class LocalUsageScannerTests : IDisposable
{
    private static readonly DateTimeOffset ScanNow = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 11);
    private static readonly DateTime TodayWriteUtc = new(2026, 10, 11, 1, 0, 0, DateTimeKind.Utc);

    private const string TodayTimestamp = "2026-10-11T10:00:00Z";
    private const string TodayLaterTimestamp = "2026-10-11T11:00:00Z";
    private const string YesterdayTimestamp = "2026-10-10T10:00:00Z";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "codexquota-local-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private LocalUsageScanner CreateScanner() => new(_root, new FakeTimeProvider(ScanNow));

    [Fact]
    public async Task CurrentDayCumulativeIncrementsAreSummed()
    {
        WriteRollout("sessions/2026/10/11/rollout-a.jsonl", Meta("session-a"), Token(TodayTimestamp, 100, 100), Token(TodayLaterTimestamp, 250, 150));

        LocalUsageSnapshot snapshot = await CreateScanner().ScanAsync(CancellationToken.None);

        Assert.Equal(LocalUsageCoverage.Available, snapshot.Coverage);
        Assert.Equal(1, snapshot.SessionCount);
        Assert.Equal(250, snapshot.TotalTokens);
        Assert.Equal(Today, snapshot.Date);
    }

    [Fact]
    public async Task RepeatedCumulativeTotalsCountOnlyOnce()
    {
        WriteRollout("sessions/rollout-a.jsonl", Meta("session-a"), Token(TodayTimestamp, 100, 100), Token(TodayLaterTimestamp, 100, 0), Token("2026-10-11T11:30:00Z", 100, 0));

        LocalUsageSnapshot snapshot = await CreateScanner().ScanAsync(CancellationToken.None);

        Assert.Equal(100, snapshot.TotalTokens);
        Assert.Equal(LocalUsageCoverage.Available, snapshot.Coverage);
    }

    [Fact]
    public async Task EventsFromAnotherDayAreNotAttributedToToday()
    {
        WriteRollout("sessions/rollout-a.jsonl", Meta("session-a"), Token(YesterdayTimestamp, 900, 900), Token("2026-10-10T11:00:00Z", 1200, 300));

        LocalUsageSnapshot snapshot = await CreateScanner().ScanAsync(CancellationToken.None);

        Assert.Equal(0, snapshot.SessionCount);
        Assert.Equal(0, snapshot.TotalTokens);
        Assert.Equal(LocalUsageCoverage.Available, snapshot.Coverage);
    }

    [Fact]
    public async Task ResumedSessionContributesOnlyTodaysIncrement()
    {
        // The file starts yesterday, so the retained baseline lets today's increment be
        // measured exactly instead of being guessed from the last-turn value.
        WriteRollout(
            "sessions/2026/10/10/rollout-resumed.jsonl",
            Meta("session-resumed"),
            Token(YesterdayTimestamp, 1000, 1000),
            Token("2026-10-10T11:00:00Z", 1500, 500),
            Token(TodayTimestamp, 1750, 250));

        LocalUsageSnapshot snapshot = await CreateScanner().ScanAsync(CancellationToken.None);

        Assert.Equal(1, snapshot.SessionCount);
        Assert.Equal(250, snapshot.TotalTokens);
        Assert.Equal(LocalUsageCoverage.Available, snapshot.Coverage);
    }

    [Fact]
    public async Task DuplicateSessionFilesAreCountedOnceAndSkipped()
    {
        WriteRollout("sessions/rollout-a.jsonl", Meta("session-a"), Token(TodayTimestamp, 100, 100), Token(TodayLaterTimestamp, 300, 200));
        WriteRollout(
            "archived_sessions/rollout-a-copy.jsonl",
            Meta("session-a"),
            Token(TodayTimestamp, 100, 100),
            Token(TodayLaterTimestamp, 300, 200));

        LocalUsageSnapshot snapshot = await CreateScanner().ScanAsync(CancellationToken.None);

        Assert.Equal(1, snapshot.SessionCount);
        Assert.Equal(300, snapshot.TotalTokens);
        Assert.Equal(1, snapshot.FilesSkipped);
    }

    [Fact]
    public async Task SeparateSessionsAreCountedIndividually()
    {
        WriteRollout("sessions/rollout-a.jsonl", Meta("session-a"), Token(TodayTimestamp, 100, 100));
        WriteRollout("sessions/rollout-b.jsonl", Meta("session-b"), Token(TodayTimestamp, 50, 50));

        LocalUsageSnapshot snapshot = await CreateScanner().ScanAsync(CancellationToken.None);

        Assert.Equal(2, snapshot.SessionCount);
        Assert.Equal(150, snapshot.TotalTokens);
    }

    [Fact]
    public async Task PartialAppendedLineIsRetriedWithoutDoubleCounting()
    {
        string path = Path.Combine(_root, "sessions", "rollout-a.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string first = Meta("session-a") + "\n" + Token(TodayTimestamp, 100, 100) + "\n" + "{\"timestamp\":\"2026-10-11T11:00:00Z\",\"type\":\"event_msg\",\"pay";
        await File.WriteAllTextAsync(path, first, new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(path, TodayWriteUtc);

        LocalUsageScanner scanner = CreateScanner();
        LocalUsageSnapshot incomplete = await scanner.ScanAsync(CancellationToken.None);
        Assert.Equal(100, incomplete.TotalTokens);

        // Finish the truncated record and append a further turn.
        await File.AppendAllTextAsync(path, "load\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"total_tokens\":250},\"last_token_usage\":{\"total_tokens\":150}}}}\n", new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(path, TodayWriteUtc.AddMinutes(5));

        LocalUsageSnapshot complete = await scanner.ScanAsync(CancellationToken.None);
        Assert.Equal(250, complete.TotalTokens);
        Assert.Equal(1, complete.SessionCount);
    }

    [Fact]
    public async Task UnchangedFileIsNotReadAgain()
    {
        WriteRollout("sessions/rollout-a.jsonl", Meta("session-a"), Token(TodayTimestamp, 100, 100), Token(TodayLaterTimestamp, 250, 150));

        LocalUsageScanner scanner = CreateScanner();
        LocalUsageSnapshot first = await scanner.ScanAsync(CancellationToken.None);
        Assert.True(first.BytesRead > 0);

        LocalUsageSnapshot second = await scanner.ScanAsync(CancellationToken.None);
        Assert.Equal(0, second.BytesRead);
        Assert.Equal(first.TotalTokens, second.TotalTokens);
    }

    [Fact]
    public async Task AppendedTurnsAreReadIncrementally()
    {
        string path = Path.Combine(_root, "sessions", "rollout-a.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, Meta("session-a") + "\n" + Token(TodayTimestamp, 100, 100) + "\n", new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(path, TodayWriteUtc);

        LocalUsageScanner scanner = CreateScanner();
        Assert.Equal(100, (await scanner.ScanAsync(CancellationToken.None)).TotalTokens);

        await File.AppendAllTextAsync(path, Token(TodayLaterTimestamp, 400, 300) + "\n", new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(path, TodayWriteUtc.AddMinutes(1));

        LocalUsageSnapshot appended = await scanner.ScanAsync(CancellationToken.None);
        Assert.Equal(400, appended.TotalTokens);
        Assert.True(appended.BytesRead < new FileInfo(path).Length);
    }

    [Fact]
    public async Task CounterResetRebaselinesAndMarksCoveragePartial()
    {
        WriteRollout("sessions/rollout-a.jsonl", Meta("session-a"), Token(TodayTimestamp, 1000, 1000), Token(TodayLaterTimestamp, 40, 40));

        LocalUsageSnapshot snapshot = await CreateScanner().ScanAsync(CancellationToken.None);

        Assert.Equal(LocalUsageCoverage.Partial, snapshot.Coverage);
        Assert.NotNull(snapshot.CoverageNote);
        // 1000 from the first observation plus the defensible post-reset last-turn value.
        Assert.Equal(1040, snapshot.TotalTokens);
    }

    [Fact]
    public async Task NullUsageValuesAreNotCountedAsZeroUsageSessions()
    {
        WriteRollout(
            "sessions/rollout-a.jsonl",
            Meta("session-a"),
            Token(TodayTimestamp, null, null),
            "{\"timestamp\":\"2026-10-11T11:00:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\"}}");

        LocalUsageSnapshot snapshot = await CreateScanner().ScanAsync(CancellationToken.None);

        Assert.Equal(0, snapshot.SessionCount);
        Assert.Equal(0, snapshot.TotalTokens);
        Assert.Equal(LocalUsageCoverage.Partial, snapshot.Coverage);
    }

    [Fact]
    public async Task MalformedAndUnrelatedLinesAreIgnored()
    {
        WriteRollout(
            "sessions/rollout-a.jsonl",
            Meta("session-a"),
            "not json at all",
            "{\"timestamp\":\"2026-10-11T09:00:00Z\",\"type\":\"response_item\",\"payload\":{\"content\":\"token_count\"}}",
            Token(TodayTimestamp, 100, 100));

        LocalUsageSnapshot snapshot = await CreateScanner().ScanAsync(CancellationToken.None);

        Assert.Equal(100, snapshot.TotalTokens);
        Assert.Equal(1, snapshot.SessionCount);
    }

    [Fact]
    public async Task OverflowingCountersAreRejectedInsteadOfWrapping()
    {
        WriteRollout(
            "sessions/rollout-a.jsonl",
            Meta("session-a"),
            "{\"timestamp\":\"" + TodayTimestamp + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"total_tokens\":99999999999999999999},\"last_token_usage\":{\"total_tokens\":null}}}}");

        LocalUsageSnapshot snapshot = await CreateScanner().ScanAsync(CancellationToken.None);

        Assert.Equal(0, snapshot.TotalTokens);
        Assert.Equal(LocalUsageCoverage.Partial, snapshot.Coverage);
    }

    [Fact]
    public async Task NegativeCountersAreRejected()
    {
        WriteRollout("sessions/rollout-a.jsonl", Meta("session-a"), Token(TodayTimestamp, -5, -5));

        LocalUsageSnapshot snapshot = await CreateScanner().ScanAsync(CancellationToken.None);

        Assert.Equal(0, snapshot.TotalTokens);
    }

    [Fact]
    public async Task TruncatedFileIsRebuiltWithoutDuplicatingOldTokens()
    {
        string path = Path.Combine(_root, "sessions", "rollout-a.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, Meta("session-a") + "\n" + Token(TodayTimestamp, 100, 100) + "\n" + Token(TodayLaterTimestamp, 900, 800) + "\n", new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(path, TodayWriteUtc);

        LocalUsageScanner scanner = CreateScanner();
        Assert.Equal(900, (await scanner.ScanAsync(CancellationToken.None)).TotalTokens);

        await File.WriteAllTextAsync(path, Meta("session-a") + "\n" + Token(TodayLaterTimestamp, 40, 40) + "\n", new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(path, TodayWriteUtc.AddMinutes(1));

        LocalUsageSnapshot rebuilt = await scanner.ScanAsync(CancellationToken.None);
        Assert.Equal(40, rebuilt.TotalTokens);
    }

    [Fact]
    public async Task NonOpenAiProviderRecordsAreExcluded()
    {
        WriteRollout("sessions/rollout-a.jsonl", Meta("session-a", "azure"), Token(TodayTimestamp, 100, 100));

        LocalUsageSnapshot snapshot = await CreateScanner().ScanAsync(CancellationToken.None);

        Assert.Equal(0, snapshot.SessionCount);
        Assert.Equal(0, snapshot.TotalTokens);
        Assert.Equal(1, snapshot.FilesSkipped);
    }

    [Fact]
    public async Task FilesNotModifiedTodayAreNotScanned()
    {
        WriteRollout(
            "sessions/2026/10/09/rollout-old.jsonl",
            [Meta("session-old"), Token(TodayTimestamp, 100, 100)],
            new DateTime(2026, 10, 9, 1, 0, 0, DateTimeKind.Utc));

        LocalUsageSnapshot snapshot = await CreateScanner().ScanAsync(CancellationToken.None);

        Assert.Equal(0, snapshot.SessionCount);
        Assert.Equal(LocalUsageCoverage.Available, snapshot.Coverage);
        Assert.Equal(0, snapshot.FilesInspected);
    }

    [Fact]
    public async Task EmptyInspectedSourceIsAVerifiedZeroNotUnavailable()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sessions"));

        LocalUsageSnapshot snapshot = await CreateScanner().ScanAsync(CancellationToken.None);

        Assert.Equal(LocalUsageCoverage.Available, snapshot.Coverage);
        Assert.Equal(0, snapshot.SessionCount);
        Assert.Equal(0, snapshot.TotalTokens);
        Assert.True(snapshot.HasNumbers);
        Assert.False(string.IsNullOrEmpty(snapshot.CoverageNote));
    }

    [Fact]
    public async Task MissingSourceRootsAreUnavailableRatherThanZero()
    {
        Directory.CreateDirectory(_root);

        LocalUsageSnapshot snapshot = await CreateScanner().ScanAsync(CancellationToken.None);

        Assert.Equal(LocalUsageCoverage.Unavailable, snapshot.Coverage);
        Assert.False(snapshot.HasNumbers);
        Assert.NotNull(snapshot.CoverageNote);
    }

    [Fact]
    public async Task BudgetLimitReportsPartialCoverage()
    {
        for (int i = 0; i < 8; i++)
        {
            WriteRollout($"sessions/rollout-{i}.jsonl", Meta($"session-{i}"), Token(TodayTimestamp, 100, 100));
        }

        var scanner = new LocalUsageScanner(
            _root,
            new FakeTimeProvider(ScanNow),
            new LocalUsageScanOptions { MaxFilesDiscovered = 3 });

        LocalUsageSnapshot snapshot = await scanner.ScanAsync(CancellationToken.None);

        Assert.Equal(LocalUsageCoverage.Partial, snapshot.Coverage);
        Assert.True(snapshot.SessionCount < 8);
    }

    [Fact]
    public async Task CancellationIsPropagated()
    {
        WriteRollout("sessions/rollout-a.jsonl", Meta("session-a"), Token(TodayTimestamp, 100, 100));

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateScanner().ScanAsync(cancellation.Token));
    }

    [Fact]
    public async Task MidnightRolloverDropsYesterdaysAggregateAndKeepsTodaysBaseline()
    {
        WriteRollout("sessions/rollout-a.jsonl", Meta("session-a"), Token(YesterdayTimestamp, 1000, 1000), Token(TodayTimestamp, 1200, 200));

        var clock = new FakeTimeProvider(ScanNow);
        var scanner = new LocalUsageScanner(_root, clock);
        LocalUsageSnapshot today = await scanner.ScanAsync(CancellationToken.None);
        Assert.Equal(200, today.TotalTokens);

        clock.Advance(TimeSpan.FromDays(1));
        File.SetLastWriteTimeUtc(Path.Combine(_root, "sessions", "rollout-a.jsonl"), TodayWriteUtc.AddDays(1));
        await File.AppendAllTextAsync(
            Path.Combine(_root, "sessions", "rollout-a.jsonl"),
            Token("2026-10-12T09:00:00Z", 1500, 300) + "\n",
            new UTF8Encoding(false));

        LocalUsageSnapshot tomorrow = await scanner.ScanAsync(CancellationToken.None);

        Assert.Equal(new DateOnly(2026, 10, 12), tomorrow.Date);
        // Only the post-midnight increment is attributed to the new day.
        Assert.Equal(300, tomorrow.TotalTokens);
    }

    [Fact]
    public async Task CoordinatorCoalescesRequestsAndPublishesSnapshots()
    {
        WriteRollout("sessions/rollout-a.jsonl", Meta("session-a"), Token(TodayTimestamp, 100, 100));

        await using var coordinator = new LocalUsageCoordinator(CreateScanner());
        var published = new TaskCompletionSource<LocalUsageSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.Updated += snapshot => published.TrySetResult(snapshot);

        coordinator.RequestScan();
        coordinator.RequestScan();

        LocalUsageSnapshot snapshot = await published.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(100, snapshot.TotalTokens);
        await Task.Delay(200);
        Assert.True(coordinator.CompletedScans >= 1);
        Assert.NotNull(coordinator.Snapshot);
    }

    [Fact]
    public async Task CoordinatorDisposalStopsFurtherWork()
    {
        WriteRollout("sessions/rollout-a.jsonl", Meta("session-a"), Token(TodayTimestamp, 100, 100));

        var coordinator = new LocalUsageCoordinator(CreateScanner());
        coordinator.RequestScan();
        await coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        int completed = coordinator.CompletedScans;
        coordinator.RequestScan();
        await Task.Delay(200);
        Assert.Equal(completed, coordinator.CompletedScans);
    }

    [Fact]
    public async Task LaterDuplicateContinuationCannotReportCompleteUndercount()
    {
        WriteRollout("sessions/a.jsonl", Meta("same"), Token(TodayTimestamp, 100, 100));
        WriteRollout("archived_sessions/b.jsonl", Meta("same"), Token(TodayTimestamp, 100, 100), Token(TodayLaterTimestamp, 300, 200));
        LocalUsageSnapshot result = await CreateScanner().ScanAsync();
        Assert.Equal(1, result.SessionCount);
        Assert.Equal(300, result.TotalTokens);
        Assert.Equal(LocalUsageCoverage.Partial, result.Coverage);
    }

    [Fact]
    public async Task NullCumulativeLastUsageIsNotRepeatedOrAddedAgainToTheDelta()
    {
        WriteRollout("sessions/a.jsonl", Meta("same"), Token(TodayTimestamp, 100, 100),
            Token(TodayLaterTimestamp, null, 50), Token(TodayLaterTimestamp, null, 50),
            Token("2026-10-11T11:30:00Z", 150, 50));
        LocalUsageSnapshot result = await CreateScanner().ScanAsync();
        Assert.Equal(150, result.TotalTokens);
        Assert.Equal(LocalUsageCoverage.Partial, result.Coverage);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 100)]
    public async Task LastUsageCannotExceedItsCumulativeCounter(bool reset, long expected)
    {
        var lines = new List<string> { Meta("same") };
        if (reset) { lines.Add(Token(TodayTimestamp, 100, 100)); }
        lines.Add(Token(TodayLaterTimestamp, 10, 500));
        WriteRollout("sessions/a.jsonl", lines.ToArray());
        LocalUsageSnapshot result = await CreateScanner().ScanAsync();
        Assert.Equal(expected, result.TotalTokens);
        Assert.Equal(LocalUsageCoverage.Partial, result.Coverage);
    }

    [Fact]
    public async Task CompleteOversizedLineIsDroppedEvenInsideOneReadBuffer()
    {
        WriteRollout("sessions/a.jsonl", Meta("same"), Token(TodayTimestamp, 100, 100).Replace("\"info\":", "\"padding\":\"" + new string('x', 700) + "\",\"info\":"));
        var scanner = new LocalUsageScanner(_root, new FakeTimeProvider(ScanNow),
            new LocalUsageScanOptions { MaxLineBytes = 512, ReadBufferBytes = 4096 });
        LocalUsageSnapshot result = await scanner.ScanAsync();
        Assert.Equal(0, result.TotalTokens);
        Assert.Equal(LocalUsageCoverage.Partial, result.Coverage);
    }

    [Fact]
    public async Task OversizedTailSuffixIsIgnoredUntilItsNewline()
    {
        WriteRollout("sessions/a.jsonl", Meta("same"));
        string path = Path.Combine(_root, "sessions/a.jsonl");
        File.AppendAllText(path, new string('x', 800));
        var scanner = new LocalUsageScanner(_root, new FakeTimeProvider(ScanNow),
            new LocalUsageScanOptions { MaxLineBytes = 512, ReadBufferBytes = 400 });
        await scanner.ScanAsync();
        File.AppendAllText(path, Token(TodayTimestamp, 999, 999) + "\n" + Token(TodayLaterTimestamp, 20, 20) + "\n");
        LocalUsageSnapshot result = await scanner.ScanAsync();
        Assert.Equal(20, result.TotalTokens);
        Assert.Equal(LocalUsageCoverage.Partial, result.Coverage);
    }

    [Fact]
    public async Task UnfinishedTailRemainsPartialUntilCompleted()
    {
        WriteRollout("sessions/a.jsonl", Meta("same"), Token(TodayTimestamp, 100, 100));
        string path = Path.Combine(_root, "sessions/a.jsonl");
        File.AppendAllText(path, "{\"type\":\"event_msg\",\"payload\":");
        LocalUsageSnapshot result = await CreateScanner().ScanAsync();
        Assert.Equal(100, result.TotalTokens);
        Assert.Equal(LocalUsageCoverage.Partial, result.Coverage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacementAtSamePathRebuildsIdentityAndReportsLostCoverage(bool larger)
    {
        WriteRollout("sessions/a.jsonl", Meta("old-id", "azure"), Token(TodayTimestamp, 100, 100));
        LocalUsageScanner scanner = CreateScanner();
        await scanner.ScanAsync();
        WriteRollout("sessions/a.jsonl", Meta("new-id"), Token(TodayTimestamp, 20, 20), larger ? new string(' ', 1000) : "");
        File.SetLastWriteTimeUtc(Path.Combine(_root, "sessions/a.jsonl"), TodayWriteUtc.AddMinutes(1));
        LocalUsageSnapshot result = await scanner.ScanAsync();
        Assert.Equal(20, result.TotalTokens);
        Assert.Equal(1, result.SessionCount);
        Assert.Equal(LocalUsageCoverage.Partial, result.Coverage);
    }

    [Fact]
    public async Task RemovingDuplicateOwnerDoesNotLeaveStaleClaims()
    {
        WriteRollout("sessions/a.jsonl", Meta("same"), Token(TodayTimestamp, 100, 100));
        WriteRollout("archived_sessions/b.jsonl", Meta("same"), Token(TodayTimestamp, 100, 100));
        LocalUsageScanner scanner = CreateScanner();
        await scanner.ScanAsync();
        File.Delete(Path.Combine(_root, "sessions/a.jsonl"));
        LocalUsageSnapshot result = await scanner.ScanAsync();
        Assert.Equal(100, result.TotalTokens);
        Assert.Equal(1, result.SessionCount);
    }

    [Fact]
    public async Task DiscoveryBudgetEventuallyVisitsLaterFilesAndPreservesEarlierTotals()
    {
        for (int i = 0; i < 8; i++) { WriteRollout($"sessions/{i}.jsonl", Meta($"id-{i}"), Token(TodayTimestamp, 10, 10)); }
        var scanner = new LocalUsageScanner(_root, new FakeTimeProvider(ScanNow), new LocalUsageScanOptions { MaxFilesDiscovered = 3 });
        LocalUsageSnapshot result = await scanner.ScanAsync();
        Assert.Equal(LocalUsageCoverage.Partial, result.Coverage);
        for (int i = 0; i < 5; i++) { result = await scanner.ScanAsync(); }
        Assert.Equal(8, result.SessionCount);
        Assert.Equal(80, result.TotalTokens);
    }

    [Fact]
    public async Task ReadDenialCannotBecomeCompleteZeroAndCanRecover()
    {
        WriteRollout("sessions/a.jsonl", Meta("same"), Token(TodayTimestamp, 100, 100));
        LocalUsageScanner scanner = CreateScanner();
        using (var locked = new FileStream(Path.Combine(_root, "sessions/a.jsonl"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            LocalUsageSnapshot denied = await scanner.ScanAsync();
            Assert.Equal(LocalUsageCoverage.Unavailable, denied.Coverage);
            Assert.False(denied.HasNumbers);
        }
        LocalUsageSnapshot recovered = await scanner.ScanAsync();
        Assert.Equal(100, recovered.TotalTokens);
        Assert.Equal(LocalUsageCoverage.Available, recovered.Coverage);
    }

    [Fact]
    public async Task RolloverReplaysAlreadyReadNextDayEvents()
    {
        WriteRollout("sessions/a.jsonl", Meta("same"), Token(TodayTimestamp, 100, 100), Token("2026-10-12T01:00:00Z", 150, 50));
        var clock = new FakeTimeProvider(ScanNow);
        var scanner = new LocalUsageScanner(_root, clock);
        await scanner.ScanAsync();
        clock.Advance(TimeSpan.FromDays(1));
        File.SetLastWriteTimeUtc(Path.Combine(_root, "sessions/a.jsonl"), TodayWriteUtc.AddDays(1));
        LocalUsageSnapshot result = await scanner.ScanAsync();
        Assert.Equal(50, result.TotalTokens);
    }

    [Fact]
    public async Task MissingProviderOrIdentityMakesCoveragePartial()
    {
        WriteRollout("sessions/a.jsonl", Token(TodayTimestamp, 100, 100));
        LocalUsageSnapshot result = await CreateScanner().ScanAsync();
        Assert.Equal(100, result.TotalTokens);
        Assert.Equal(LocalUsageCoverage.Partial, result.Coverage);
    }

    [Fact]
    public async Task ByteBudgetMakesProgressAcrossFilesWithoutLosingEarlierContributions()
    {
        WriteRollout("sessions/a.jsonl", new[] { Meta("large") }.Concat(Enumerable.Range(1, 20)
            .Select(i => Token($"2026-10-11T10:{i:00}:00Z", i * 10, 10))).ToArray());
        WriteRollout("sessions/b.jsonl", Meta("small"), Token(TodayTimestamp, 40, 40));
        var scanner = new LocalUsageScanner(_root, new FakeTimeProvider(ScanNow),
            new LocalUsageScanOptions { MaxBytesPerCycle = 512, ReadBufferBytes = 128 });
        LocalUsageSnapshot result = await scanner.ScanAsync();
        Assert.True(result.BytesRead <= 512);
        Assert.Equal(LocalUsageCoverage.Partial, result.Coverage);
        result = await scanner.ScanAsync();
        Assert.True(result.SessionCount >= 1);
        for (int i = 0; i < 30 && result.Coverage != LocalUsageCoverage.Available; i++) { result = await scanner.ScanAsync(); }
        Assert.Equal(240, result.TotalTokens);
        Assert.Equal(2, result.SessionCount);
        Assert.Equal(LocalUsageCoverage.Available, result.Coverage);
    }

    [Fact]
    public async Task RetainedFileCapNeverClaimsCompleteTotals()
    {
        for (int i = 0; i < 3; i++) { WriteRollout($"sessions/{i}.jsonl", Meta($"id-{i}"), Token(TodayTimestamp, 10, 10)); }
        var scanner = new LocalUsageScanner(_root, new FakeTimeProvider(ScanNow), new LocalUsageScanOptions { MaxTrackedFiles = 2 });
        for (int i = 0; i < 3; i++)
        {
            LocalUsageSnapshot result = await scanner.ScanAsync();
            Assert.Equal(20, result.TotalTokens);
            Assert.Equal(LocalUsageCoverage.Partial, result.Coverage);
        }
    }

    [Fact]
    public async Task OutOfOrderEventsCannotMoveBaselineBackAndInflateNextDelta()
    {
        WriteRollout("sessions/a.jsonl", Meta("same"), Token(TodayTimestamp, 100, 100),
            Token(TodayLaterTimestamp, 250, 150), Token("2026-10-11T09:00:00Z", 150, 50),
            Token("2026-10-11T11:30:00Z", 300, 50));
        LocalUsageSnapshot result = await CreateScanner().ScanAsync();
        Assert.Equal(300, result.TotalTokens);
        Assert.Equal(LocalUsageCoverage.Partial, result.Coverage);
    }

    [Fact]
    public async Task LocalTimezoneDeterminesTheDateInsteadOfTheUtcFolderDate()
    {
        WriteRollout("sessions/2026/10/10/a.jsonl", Meta("same"), Token("2026-10-10T23:00:00Z", 100, 100));
        var scanner = new LocalUsageScanner(_root, new ZonedClock(ScanNow));
        LocalUsageSnapshot result = await scanner.ScanAsync();
        Assert.Equal(Today, result.Date);
        Assert.Equal(100, result.TotalTokens);
        Assert.Equal(LocalUsageCoverage.Available, result.Coverage);
    }

    [Fact]
    public void AllowedProjectionSkipsInstructionFieldsAndHashesIdentityRegardlessOfFieldOrder()
    {
        byte[] line = Encoding.UTF8.GetBytes("{\"payload\":{\"base_instructions\":{\"text\":\"synthetic private text\"},\"id\":\"fixture-id\",\"model_provider\":\"openai\"},\"type\":\"session_meta\"}");
        LocalRolloutRecord record = CodexRolloutDecoder.Decode(line);
        Assert.Equal(LocalRolloutKind.SessionMeta, record.Kind);
        Assert.Equal(64, record.SessionId!.Length);
        Assert.DoesNotContain("fixture-id", record.ToString());
        Assert.DoesNotContain("synthetic private text", record.ToString());
    }

    [Fact]
    public void DuplicateAllowedFieldsAreAmbiguousRatherThanLastWins()
    {
        LocalRolloutRecord record = CodexRolloutDecoder.Decode(Encoding.UTF8.GetBytes(
            Token(TodayTimestamp, 100, 100).Replace("\"total_tokens\":100", "\"total_tokens\":100,\"total_tokens\":200")));
        Assert.Equal(LocalRolloutKind.Invalid, record.Kind);
    }

    [Fact]
    public async Task TimestampWithoutAnOffsetCannotBeAssignedUsingTheMachinesImplicitTimezone()
    {
        WriteRollout("sessions/a.jsonl", Meta("same"), Token("2026-10-11T10:00:00", 100, 100));
        LocalUsageSnapshot result = await CreateScanner().ScanAsync();
        Assert.Equal(0, result.TotalTokens);
        Assert.Equal(LocalUsageCoverage.Partial, result.Coverage);
    }

    private sealed class ZonedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("fixture-east", TimeSpan.FromHours(8), "fixture-east", "fixture-east");
    }

    private void WriteRollout(string relativePath, params string[] lines) =>
        WriteRollout(relativePath, lines, null);

    private void WriteRollout(string relativePath, string[] lines, DateTime? lastWriteUtc)
    {
        string path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join('\n', lines) + "\n", new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(path, lastWriteUtc ?? TodayWriteUtc);
    }

    private static string Meta(string sessionId, string provider = "openai") =>
        string.Format(
            CultureInfo.InvariantCulture,
            "{{\"timestamp\":\"2026-10-10T08:00:00Z\",\"type\":\"session_meta\",\"payload\":{{\"id\":\"{0}\",\"session_id\":\"{0}\",\"model_provider\":\"{1}\"}}}}",
            sessionId,
            provider);

    private static string Token(string timestamp, long? cumulative, long? last) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "{{\"timestamp\":\"{0}\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"token_count\",\"info\":{{\"total_token_usage\":{{\"total_tokens\":{1}}},\"last_token_usage\":{{\"total_tokens\":{2}}}}}}}}}",
            timestamp,
            cumulative?.ToString(CultureInfo.InvariantCulture) ?? "null",
            last?.ToString(CultureInfo.InvariantCulture) ?? "null");
}
