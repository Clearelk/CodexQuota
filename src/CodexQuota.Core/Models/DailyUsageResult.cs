using CodexQuota.Core.Services;

namespace CodexQuota.Core.Models;

/// <summary>
/// Result of the optional official daily Token read. A returned bucket containing
/// zero Tokens is a valid Available value and is deliberately distinct from
/// Missing. SourceDate is the exact service-provided calendar date; its timezone
/// is undocumented, so it is surfaced verbatim rather than converted.
/// </summary>
public sealed record DailyUsageResult(
    DateOnly RequestedDate,
    DailyUsageAvailability Availability,
    long? Tokens,
    DateOnly? SourceDate,
    string? Explanation)
{
    public AppMessage? ExplanationMessage { get; init; }
    public static DailyUsageResult Available(DateOnly requestedDate, long tokens, DateOnly sourceDate) =>
        new(requestedDate, DailyUsageAvailability.Available, tokens, sourceDate, null);

    public static DailyUsageResult Missing(DateOnly requestedDate, string? explanation = null) =>
        new(requestedDate, DailyUsageAvailability.Missing, null, null, explanation);

    public static DailyUsageResult Missing(DateOnly requestedDate, AppMessage message) =>
        Missing(requestedDate, message.Format()) with { ExplanationMessage = message };

    public static DailyUsageResult Unsupported(DateOnly requestedDate, string? explanation = null) =>
        new(requestedDate, DailyUsageAvailability.Unsupported, null, null, explanation);

    public static DailyUsageResult Unsupported(DateOnly requestedDate, AppMessage message) =>
        Unsupported(requestedDate, message.Format()) with { ExplanationMessage = message };

    public static DailyUsageResult Failed(DateOnly requestedDate, string? explanation = null) =>
        new(requestedDate, DailyUsageAvailability.Failed, null, null, explanation);
}
