namespace CodexQuota.Core.Models;

/// <summary>
/// Availability of the observed-consumption estimate for one quota period of the
/// current official account. The estimate is a sampled increase of the provider's own
/// used percentage, never a Token conversion or an official whole-day figure.
/// </summary>
public enum ObservedPeriodState
{
    /// <summary>No usable comparable baseline of this period is available today.</summary>
    Unavailable,

    /// <summary>A baseline exists; a second comparable fresh sample is needed before a delta can be shown.</summary>
    Waiting,

    /// <summary>A numeric estimate is available with no known interruption.</summary>
    Available,

    /// <summary>A numeric estimate exists but a known gap or reset makes it a lower bound.</summary>
    Partial,
}

/// <summary>
/// Observed consumption for one quota period in percentage points of the official
/// window. The value is accumulated from comparable fresh samples of the provider's
/// used percentage, so it can legitimately exceed one window's 100% across observed
/// resets. It is a lower bound of the current account's activity during this run.
/// </summary>
public sealed record ObservedPeriodEstimate(
    string Slot,
    long? DurationMinutes,
    ObservedPeriodState State,
    double ObservedPercentPoints,
    int ComparableSamples,
    DateTimeOffset? FirstObservedAt,
    DateTimeOffset? LastObservedAt,
    bool HasGap);

/// <summary>Overall availability of today's observed-consumption estimate.</summary>
public enum ObservedUsageState
{
    /// <summary>No usable comparable baseline of a main quota period is available today.</summary>
    Unavailable,

    /// <summary>A baseline exists but no period has two comparable fresh samples yet.</summary>
    Waiting,

    /// <summary>At least one period has a numeric estimate and no known gap.</summary>
    Available,

    /// <summary>A numeric estimate exists but a known interruption makes it a lower bound.</summary>
    Partial,
}

/// <summary>
/// Immutable snapshot of today's observed official-quota consumption. It is derived
/// only from already-published fresh official quota snapshots and is deliberately
/// independent of the device-local Token statistic.
/// </summary>
public sealed record ObservedUsageSnapshot(
    DateOnly LocalDate,
    ObservedUsageState State,
    IReadOnlyList<ObservedPeriodEstimate> Periods)
{
    /// <summary>Snapshot before any fresh observation today.</summary>
    public static ObservedUsageSnapshot Initial(DateOnly localDate) =>
        new(localDate, ObservedUsageState.Unavailable, []);
}
