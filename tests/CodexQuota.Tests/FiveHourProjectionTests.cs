using System.Text.Json;
using CodexQuota.Core.Models;
using CodexQuota.Core.Providers;
using CodexQuota.Core.Services;

namespace CodexQuota.Tests;

public sealed class FiveHourProjectionTests
{
    private static QuotaSnapshot Decode(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return CodexResponseDecoder.DecodeQuota(document.RootElement, DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public void LowerWeeklyRemainderDoesNotChangeFiveHourSelectionOrLimitingIngestion()
    {
        QuotaSnapshot snapshot = Decode("""
            {"rateLimits":{"primary":{"usedPercent":20,"windowDurationMins":300},
                           "secondary":{"usedPercent":90,"windowDurationMins":10080}}}
            """);
        Assert.Equal(80, QuotaProjection.SelectFiveHourWindow(snapshot)!.RemainingPercent);
        Assert.Equal(10, QuotaProjection.GetRemainingPercent(snapshot));
    }

    [Theory]
    [InlineData(300, 300, "primary", 80)]
    [InlineData(1440, 300, "secondary", 10)]
    [InlineData(null, 300, "secondary", 10)]
    [InlineData(300, 10080, "primary", 80)]
    [InlineData(1440, 10080, null, null)]
    [InlineData(null, null, null, null)]
    [InlineData(0, 10080, null, null)]
    [InlineData(-1, 10080, null, null)]
    public void RequiresActualDurationAndPrefersMatchingPrimary(int? primaryDuration, int? secondaryDuration, string? slot, int? remaining)
    {
        var primary = new QuotaWindow("primary", primaryDuration, 20, 80, null);
        var secondary = new QuotaWindow("secondary", secondaryDuration, 90, 10, null);
        var main = new QuotaBucket("codex", null, [secondary, primary], false, null, null, null, null);
        var snapshot = new QuotaSnapshot("codex", "plus", [main], DateTimeOffset.UnixEpoch);
        QuotaWindow? selected = QuotaProjection.SelectFiveHourWindow(snapshot);
        Assert.Equal(slot, selected?.Slot);
        Assert.Equal(remaining, selected?.RemainingPercent);
    }

    [Theory]
    [InlineData("""{"rateLimits":{"secondary":{"usedPercent":1,"windowDurationMins":10080}}}""")]
    [InlineData("""{"rateLimits":{"primary":{"usedPercent":1}}}""")]
    [InlineData("""{"rateLimits":{"limitId":"other","primary":{"usedPercent":1,"windowDurationMins":300}}}""")]
    [InlineData("""{"rateLimitsByLimitId":{"codex":null},"rateLimits":{"primary":{"usedPercent":1,"windowDurationMins":300}}}""")]
    [InlineData("""{"rateLimits":{"primary":{"usedPercent":1,"windowDurationMins":300},"secondary":{"usedPercent":101}}}""")]
    [InlineData("""{"rateLimitsByLimitId":{"codex":{"secondary":{"usedPercent":1,"windowDurationMins":10080}},"extra":{"primary":{"usedPercent":1,"windowDurationMins":300}}}}""")]
    public void MissingInvalidOrAuxiliaryDataNeverProvidesFiveHourFallback(string json) =>
        Assert.Null(QuotaProjection.SelectFiveHourWindow(Decode(json)));

    [Fact]
    public void SecondaryRetainsOwnResetAndAmbiguousSlotsAreRejected()
    {
        var secondary = new QuotaWindow("secondary", 300, 81, 19, DateTimeOffset.UnixEpoch.AddHours(5));
        var main = new QuotaBucket("codex", null, [secondary], false, null, null, null, null);
        var snapshot = new QuotaSnapshot("codex", "plus", [main], DateTimeOffset.UnixEpoch);
        Assert.Same(secondary, QuotaProjection.SelectFiveHourWindow(snapshot));
        Assert.Null(QuotaProjection.SelectFiveHourWindow(snapshot with { Buckets = [main with { Windows = [secondary, secondary with { RemainingPercent = 1 }] }] }));
        Assert.Null(QuotaProjection.SelectFiveHourWindow(null));
    }
}
