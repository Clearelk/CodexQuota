using System.Text.Json;
using CodexQuota.Core.Models;
using CodexQuota.Core.Providers;

namespace CodexQuota.Tests;

public sealed class DailyUsageTests
{
    private static readonly DateOnly Today = new(2026, 10, 11);

    private static DailyUsageResult Decode(string json, DateOnly? date = null)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return CodexResponseDecoder.DecodeDailyUsage(document.RootElement, date ?? Today);
    }

    [Fact]
    public void ExactDateMatchReturnsTokensAndSourceDate()
    {
        DailyUsageResult result = Decode(
            """
            {"summary":{"lifetimeTokens":1},"dailyUsageBuckets":[
              {"startDate":"2026-10-10","tokens":5},
              {"startDate":"2026-10-11","tokens":123456}]}
            """);

        Assert.Equal(DailyUsageAvailability.Available, result.Availability);
        Assert.Equal(123456, result.Tokens);
        Assert.Equal(Today, result.SourceDate);
    }

    [Fact]
    public void ZeroTokensIsAvailableNotMissing()
    {
        DailyUsageResult result = Decode(
            """
            {"dailyUsageBuckets":[{"startDate":"2026-10-11","tokens":0}]}
            """);

        Assert.Equal(DailyUsageAvailability.Available, result.Availability);
        Assert.Equal(0, result.Tokens);
    }

    [Fact]
    public void MissingBucketListIsMissing()
    {
        DailyUsageResult result = Decode("""{"summary":{"lifetimeTokens":1}}""");

        Assert.Equal(DailyUsageAvailability.Missing, result.Availability);
        Assert.Null(result.Tokens);
    }

    [Fact]
    public void NoMatchingDateIsMissing()
    {
        DailyUsageResult result = Decode(
            """
            {"dailyUsageBuckets":[{"startDate":"2026-10-09","tokens":5}]}
            """);

        Assert.Equal(DailyUsageAvailability.Missing, result.Availability);
    }

    [Fact]
    public void NegativeTokensAreMissing()
    {
        DailyUsageResult result = Decode(
            """
            {"dailyUsageBuckets":[{"startDate":"2026-10-11","tokens":-5}]}
            """);

        Assert.Equal(DailyUsageAvailability.Missing, result.Availability);
        Assert.Null(result.Tokens);
    }

    [Fact]
    public void OverflowingTokensAreMissing()
    {
        DailyUsageResult result = Decode(
            """
            {"dailyUsageBuckets":[{"startDate":"2026-10-11","tokens":99999999999999999999}]}
            """);

        Assert.Equal(DailyUsageAvailability.Missing, result.Availability);
        Assert.Null(result.Tokens);
    }

    [Fact]
    public void MalformedDateNeverMatchesToday()
    {
        DailyUsageResult result = Decode(
            """
            {"dailyUsageBuckets":[{"startDate":"11/10/2026","tokens":42}]}
            """);

        Assert.Equal(DailyUsageAvailability.Missing, result.Availability);
    }

    [Fact]
    public void UnsupportedFactoryKeepsRequestedDate()
    {
        DailyUsageResult result = DailyUsageResult.Unsupported(Today, "不支持");

        Assert.Equal(Today, result.RequestedDate);
        Assert.Null(result.Tokens);
        Assert.Equal(DailyUsageAvailability.Unsupported, result.Availability);
    }
}
