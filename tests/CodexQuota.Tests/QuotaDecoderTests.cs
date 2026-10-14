using System.Text.Json;
using CodexQuota.Core.Models;
using CodexQuota.Core.Providers;
using CodexQuota.Core.Services;
using CodexQuota.Tests.Support;

namespace CodexQuota.Tests;

public sealed class QuotaDecoderTests
{
    private static QuotaSnapshot Decode(string json, string? plan = null)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return CodexResponseDecoder.DecodeQuota(document.RootElement, DateTimeOffset.UnixEpoch, plan);
    }

    [Fact]
    public void MainQuotaUsesTheMoreConstrainedPeriod()
    {
        QuotaSnapshot snapshot = Decode(
            """
            {"rateLimits":{"limitId":"codex",
              "primary":{"usedPercent":54,"windowDurationMins":300},
              "secondary":{"usedPercent":8,"windowDurationMins":10080}}}
            """,
            "plus");

        Assert.Equal(46, QuotaProjection.GetRemainingPercent(snapshot));
        Assert.Equal(2, QuotaProjection.SelectMainBucket(snapshot)!.Windows.Count);
        Assert.Equal("plus", snapshot.Plan);
    }

    [Fact]
    public void AuxiliaryBucketIsExcludedFromMainRemaining()
    {
        QuotaSnapshot snapshot = Decode(
            """
            {"rateLimits":{"limitId":"codex","primary":{"usedPercent":20,"windowDurationMins":300}},
             "rateLimitsByLimitId":{
               "codex":{"limitId":"codex","primary":{"usedPercent":20,"windowDurationMins":300}},
               "other":{"limitId":"other","primary":{"usedPercent":99,"windowDurationMins":1440}}}}
            """);

        Assert.Equal(80, QuotaProjection.GetRemainingPercent(snapshot));
        Assert.Single(QuotaProjection.GetAuxiliaryBuckets(snapshot));
        Assert.Equal("other", QuotaProjection.GetAuxiliaryBuckets(snapshot)[0].LimitId);
    }

    [Fact]
    public void OnlyOnePeriodStillProducesRemaining()
    {
        QuotaSnapshot snapshot = Decode(
            """
            {"rateLimits":{"limitId":"codex","primary":null,"secondary":{"usedPercent":25,"windowDurationMins":10080}}}
            """);

        Assert.Equal(75, QuotaProjection.GetRemainingPercent(snapshot));
        Assert.Single(QuotaProjection.SelectMainBucket(snapshot)!.Windows);
    }

    [Fact]
    public void MissingPeriodsProduceNoRemainingValue()
    {
        QuotaSnapshot snapshot = Decode(
            """
            {"rateLimits":{"limitId":"codex","primary":null,"secondary":null}}
            """);

        Assert.Null(QuotaProjection.GetRemainingPercent(snapshot));
        Assert.False(QuotaProjection.SelectMainBucket(snapshot)!.ContainsInvalidWindow);
    }

    [Fact]
    public void MissingUsedPercentInvalidatesTheMainBucketInsteadOfOmittingIt()
    {
        QuotaSnapshot snapshot = Decode(
            """
            {"rateLimits":{"limitId":"codex","primary":{"windowDurationMins":300},
              "secondary":{"usedPercent":1,"windowDurationMins":10080}}}
            """);

        Assert.True(QuotaProjection.SelectMainBucket(snapshot)!.ContainsInvalidWindow);
        Assert.Null(QuotaProjection.GetRemainingPercent(snapshot));
    }

    [Theory]
    [InlineData(101)]
    [InlineData(-1)]
    public void OutOfRangePercentInvalidatesTheMainBucket(int usedPercent)
    {
        string json =
            """{"rateLimits":{"limitId":"codex","primary":{"usedPercent":""" +
            usedPercent +
            ""","windowDurationMins":300}}}""";
        QuotaSnapshot snapshot = Decode(json);

        Assert.True(QuotaProjection.SelectMainBucket(snapshot)!.ContainsInvalidWindow);
        Assert.Null(QuotaProjection.GetRemainingPercent(snapshot));
    }

    [Fact]
    public void NullResetAndDurationAreAllowed()
    {
        QuotaSnapshot snapshot = Decode(
            """
            {"rateLimits":{"limitId":"codex","primary":{"usedPercent":10,"windowDurationMins":null,"resetsAt":null}}}
            """);

        QuotaWindow window = QuotaProjection.SelectMainBucket(snapshot)!.Windows[0];
        Assert.Null(window.DurationMinutes);
        Assert.Null(window.ResetsAt);
        Assert.Equal(90, QuotaProjection.GetRemainingPercent(snapshot));
    }

    [Fact]
    public void OutOfRangeResetTimestampBecomesNull()
    {
        QuotaSnapshot snapshot = Decode(
            """
            {"rateLimits":{"limitId":"codex","primary":{"usedPercent":10,"windowDurationMins":300,"resetsAt":99999999999999}}}
            """);

        Assert.Null(QuotaProjection.SelectMainBucket(snapshot)!.Windows[0].ResetsAt);
    }

    [Fact]
    public void LegacySingleBucketWithoutLimitIdIsAccepted()
    {
        QuotaSnapshot snapshot = Decode(
            """
            {"rateLimits":{"primary":{"usedPercent":40,"windowDurationMins":300}}}
            """);

        Assert.Equal(60, QuotaProjection.GetRemainingPercent(snapshot));
    }

    [Fact]
    public void SingleBucketWithForeignLimitIdIsNotSelectedAsMain()
    {
        QuotaSnapshot snapshot = Decode(
            """
            {"rateLimits":{"limitId":"other","primary":{"usedPercent":1,"windowDurationMins":300}}}
            """);

        Assert.Null(QuotaProjection.SelectMainBucket(snapshot));
        Assert.Null(QuotaProjection.GetRemainingPercent(snapshot));
    }

    [Fact]
    public void MapWithoutCodexBucketFallsBackToLegacyCodexBucket()
    {
        QuotaSnapshot snapshot = Decode(
            """
            {"rateLimits":{"limitId":"codex","primary":{"usedPercent":30,"windowDurationMins":300}},
             "rateLimitsByLimitId":{"other":{"limitId":"other","primary":{"usedPercent":1,"windowDurationMins":1440}}}}
            """);

        Assert.Equal(70, QuotaProjection.GetRemainingPercent(snapshot));
        Assert.Equal(2, snapshot.Buckets.Count);
    }

    [Fact]
    public void CreditsAreExposedSeparatelyAndDoNotAffectRemaining()
    {
        QuotaSnapshot snapshot = Decode(
            """
            {"rateLimits":{"limitId":"codex","primary":{"usedPercent":50,"windowDurationMins":300},
              "credits":{"hasCredits":true,"unlimited":false,"balance":"12.5"}}}
            """);

        QuotaBucket bucket = QuotaProjection.SelectMainBucket(snapshot)!;
        Assert.Equal("12.5", bucket.CreditBalance);
        Assert.True(bucket.HasCredits);
        Assert.False(bucket.UnlimitedCredits);
        Assert.Equal(50, QuotaProjection.GetRemainingPercent(snapshot));
    }

    [Theory]
    [InlineData(100, QuotaSeverity.Green)]
    [InlineData(70, QuotaSeverity.Green)]
    [InlineData(67, QuotaSeverity.Green)]
    [InlineData(66, QuotaSeverity.Yellow)]
    [InlineData(50, QuotaSeverity.Yellow)]
    [InlineData(34, QuotaSeverity.Yellow)]
    [InlineData(33, QuotaSeverity.Red)]
    [InlineData(14, QuotaSeverity.Red)]
    [InlineData(0, QuotaSeverity.Red)]
    public void SeverityBoundariesAreCorrect(int remaining, QuotaSeverity expected) =>
        Assert.Equal(expected, QuotaProjection.GetFreshSeverity(remaining));

    [Fact]
    public void UnknownRemainingIsNeutral()
    {
        Assert.Equal(QuotaSeverity.Unknown, QuotaProjection.GetFreshSeverity(null));

        QuotaSnapshot stale = Fixtures.MainQuota(50);
        var state = new RefreshState(
            RefreshStatus.Stale,
            stale,
            DailyUsageResult.Missing(new DateOnly(2026, 10, 11)),
            null,
            null,
            null,
            "stale");

        Assert.Equal(QuotaSeverity.Stale, QuotaProjection.GetDisplaySeverity(state));
    }

    [Fact]
    public void WindowLabelsFallBackWhenDurationIsMissing()
    {
        var withDuration = new QuotaWindow("primary", 300, 10, 90, null);
        var withoutDuration = new QuotaWindow("secondary", null, 10, 90, null);

        Assert.Contains("5 小时", QuotaFormatting.DescribeWindowLabel(withDuration), StringComparison.Ordinal);
        Assert.Equal("次时段", QuotaFormatting.DescribeWindowLabel(withoutDuration));
    }
}
