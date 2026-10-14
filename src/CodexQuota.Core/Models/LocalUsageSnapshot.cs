using CodexQuota.Core.Services;

namespace CodexQuota.Core.Models;

/// <summary>
/// Availability of the device-local daily activity statistic.
/// <see cref="Unavailable"/> means the local records could not be inspected at all and
/// must never be rendered as a verified zero. <see cref="Partial"/> means usable numbers
/// exist but coverage is known to be incomplete.
/// </summary>
public enum LocalUsageCoverage
{
    Unavailable,
    Partial,
    Available,
}

/// <summary>
/// Immutable device-local daily activity for one local calendar date. It is deliberately
/// independent of the official account quota: it describes this computer's local Codex
/// records, is never converted into a quota percentage, and keeps "no data" honestly
/// different from a verified zero.
/// </summary>
public sealed record LocalUsageSnapshot(
    DateOnly Date,
    int SessionCount,
    long TotalTokens,
    LocalUsageCoverage Coverage,
    string? CoverageNote,
    DateTimeOffset ObservedAt,
    int FilesInspected,
    int FilesSkipped,
    long BytesRead)
{
    public AppMessage? CoverageMessage { get; init; }
    /// <summary>True when usable numbers were produced (complete or partial).</summary>
    public bool HasNumbers => Coverage != LocalUsageCoverage.Unavailable;

    /// <summary>Snapshot used before the first successful inspection.</summary>
    public static LocalUsageSnapshot Pending(DateOnly date, DateTimeOffset observedAt) =>
        new(date, 0, 0, LocalUsageCoverage.Unavailable, null, observedAt, 0, 0, 0);

    /// <summary>Snapshot for a root that could not be found or read at all.</summary>
    public static LocalUsageSnapshot Unavailable(DateOnly date, DateTimeOffset observedAt, string note) =>
        new(date, 0, 0, LocalUsageCoverage.Unavailable, note, observedAt, 0, 0, 0);

    public static LocalUsageSnapshot Unavailable(DateOnly date, DateTimeOffset observedAt, AppMessage message) =>
        Unavailable(date, observedAt, message.Format()) with { CoverageMessage = message };
}
