using CodexQuota.Core.Models;

namespace CodexQuota.Core.Services;

/// <summary>
/// Tiny, in-memory owner of the "today's observed consumption" estimate. It is fed only
/// the already-published refresh states from the quota coordinator, tracks one
/// comparable baseline per main-bucket period, and accumulates the provider's own
/// percentage-point increases during the current run and local day. It performs no
/// provider call, file read, persistence or Token conversion.
///
/// Every path is serialized behind one lock so the reducer has a single writer
/// regardless of which thread the coordinator publishes from. The clock is injected so
/// local-day attribution and deduplication are testable.
/// </summary>
public sealed class ObservedUsageEstimator
{
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private State _state;

    public ObservedUsageEstimator(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _state = State.Initial(Today());
    }

    /// <summary>Most recent estimate. Safe to read from any thread.</summary>
    public ObservedUsageSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                if (_state.LocalDate != Today()) { _state = State.Initial(Today()); }
                return _state.ToSnapshot();
            }
        }
    }

    /// <summary>
    /// Folds one published refresh state into the estimate and returns the new snapshot.
    /// </summary>
    public ObservedUsageSnapshot Observe(RefreshState refresh)
    {
        ArgumentNullException.ThrowIfNull(refresh);
        lock (_gate)
        {
            _state = Advance(_state, refresh, Today(), _timeProvider.LocalTimeZone);
            return _state.ToSnapshot();
        }
    }

    private DateOnly Today() => DateOnly.FromDateTime(_timeProvider.GetLocalNow().Date);

    private static State Advance(State state, RefreshState refresh, DateOnly today, TimeZoneInfo zone)
    {
        // A new local day starts a fresh baseline: an interval spanning midnight is never
        // attributed wholly to today.
        if (state.LocalDate != today)
        {
            state = State.Initial(today);
        }

        // Account invalidation, sign-out and unsupported accounts clear the estimate
        // (the coordinator publishes LastQuota = null for those transitions). Ordinary
        // Loading with a retained quota keeps it, and transient failures never add.
        if (refresh.LastQuota is null)
        {
            return State.Initial(today);
        }

        if (refresh.Status == RefreshStatus.Loading && refresh.FailureKind == ProviderFailureKind.None)
        {
            // A normal in-flight read with retained quota is not a failed observation.
            return state;
        }

        if (refresh.Status != RefreshStatus.Fresh || refresh.FailureKind != ProviderFailureKind.None || refresh.LastSuccess is not { } observedAt)
        {
            foreach (Slot slot in state.Slots.Values) { MarkGap(slot); }
            return state;
        }

        DateTimeOffset fetchedAt = refresh.LastQuota.FetchedAt;
        // Completion and snapshot must both belong to today. Re-publishing yesterday's
        // cached Fresh state (or completing a read across midnight) cannot seed today.
        if (DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(observedAt, zone).Date) != today ||
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(fetchedAt, zone).Date) != today)
        {
            return state;
        }

        // Deduplicate repeated state/theme publications and ignore out-of-order timestamps.
        if ((state.LastObservedAt is { } last && observedAt <= last) ||
            (state.LastSnapshotAt is { } lastSnapshot && fetchedAt <= lastSnapshot))
        {
            return state;
        }

        Dictionary<string, QuotaWindow> windows = SelectWindows(refresh.LastQuota);
        state.LastObservedAt = observedAt;
        state.LastSnapshotAt = fetchedAt;

        string? plan = refresh.LastQuota.Plan;
        string provider = refresh.LastQuota.ProviderId;
        if (state.HasIdentity &&
            (!string.Equals(state.Plan, plan, StringComparison.Ordinal) ||
             !string.Equals(state.Provider, provider, StringComparison.Ordinal)))
        {
            // A different provider/plan can change both budgets, including an absent
            // slot. Never relabel old percentage points with the new denominator.
            state.Slots.Clear();
            state.BudgetChanged = true;
        }
        state.HasIdentity = true;
        state.Plan = plan;
        state.Provider = provider;

        // A period absent from a fresh observation is a known gap, never a fabricated value.
        foreach (Slot slot in state.Slots.Values)
        {
            if (!windows.ContainsKey(slot.SlotName))
            {
                MarkGap(slot);
            }
        }

        foreach ((string slotName, QuotaWindow window) in windows)
        {
            if (!state.Slots.TryGetValue(slotName, out Slot? slot))
            {
                slot = new Slot(slotName);
                state.Slots[slotName] = slot;
                slot.HasGap = state.BudgetChanged;
            }

            if (slot.Duration != window.DurationMinutes)
            {
                // A missing or changed duration cannot carry a numeric total into the
                // new budget. Other slots remain independent.
                slot.HasGap |= slot.EverObserved || slot.Duration is not null;
                slot.Total = 0;
                slot.HasComparableTotal = false;
                slot.EverObserved = false;
                slot.FirstObservedAt = null;
                slot.MissingLast = true;
            }

            slot.Duration = window.DurationMinutes;
            if (window.DurationMinutes is not > 0 || window.ResetsAt is not { } reset || reset <= observedAt ||
                string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(plan))
            {
                // Two null metadata values are not proof of a common quota epoch.
                // Keep a previous safe total only when its denominator is unchanged.
                MarkGap(slot);
                slot.LastObservedAt = observedAt;
                continue;
            }

            if (!slot.EverObserved || slot.MissingLast || slot.ResetsAt != window.ResetsAt || window.UsedPercent < slot.UsedPercent)
            {
                // Missing cycles, reset epochs and decreasing counters break continuity.
                // Same-duration safe totals survive as partial; no cross-gap jump is added.
                Rebaseline(slot, window, observedAt, gap: slot.EverObserved);
                continue;
            }

            slot.Total += window.UsedPercent - slot.UsedPercent;
            slot.ComparableSamples += 1;
            if (slot.ComparableSamples >= 2)
            {
                slot.HasComparableTotal = true;
            }

            slot.UsedPercent = window.UsedPercent;
            slot.LastObservedAt = observedAt;
        }

        return state;
    }

    private static Dictionary<string, QuotaWindow> SelectWindows(QuotaSnapshot snapshot)
    {
        var windows = new Dictionary<string, QuotaWindow>(StringComparer.Ordinal);
        QuotaBucket? main = QuotaProjection.SelectMainBucket(snapshot);
        if (main is null || main.ContainsInvalidWindow)
        {
            return windows;
        }

        foreach (QuotaWindow window in main.Windows)
        {
            if (window.Slot is not ("primary" or "secondary")) { continue; }
            if (window.UsedPercent is < 0 or > 100) { windows.Clear(); break; }
            if (!windows.TryAdd(window.Slot, window))
            {
                // Ambiguous duplicate slot: the whole observation is unusable.
                windows.Clear();
                break;
            }
        }

        return windows;
    }

    private static void MarkGap(Slot slot)
    {
        slot.MissingLast = true;
        slot.HasGap = true;
        slot.ComparableSamples = 0;
    }

    private static void Rebaseline(Slot slot, QuotaWindow window, DateTimeOffset observedAt, bool gap)
    {
        slot.UsedPercent = window.UsedPercent;
        slot.Duration = window.DurationMinutes;
        slot.ResetsAt = window.ResetsAt;
        slot.LastObservedAt = observedAt;
        slot.FirstObservedAt ??= observedAt;
        slot.ComparableSamples = 1;
        slot.EverObserved = true;
        slot.MissingLast = false;
        if (gap)
        {
            slot.HasGap = true;
        }
    }

    private sealed class Slot(string slotName)
    {
        public string SlotName { get; } = slotName;

        public bool EverObserved;
        public bool MissingLast;
        public bool HasGap;
        public bool HasComparableTotal;
        public int UsedPercent;
        public long? Duration;
        public DateTimeOffset? ResetsAt;
        public double Total;
        public int ComparableSamples;
        public DateTimeOffset? FirstObservedAt;
        public DateTimeOffset? LastObservedAt;

        public ObservedPeriodEstimate ToEstimate()
        {
            ObservedPeriodState state = !EverObserved || (MissingLast && !HasComparableTotal)
                ? ObservedPeriodState.Unavailable
                : HasComparableTotal
                    ? (HasGap ? ObservedPeriodState.Partial : ObservedPeriodState.Available)
                    : ObservedPeriodState.Waiting;

            return new ObservedPeriodEstimate(
                SlotName,
                Duration,
                state,
                Total,
                ComparableSamples,
                FirstObservedAt,
                LastObservedAt,
                HasGap);
        }
    }

    private sealed class State
    {
        public DateOnly LocalDate;
        public DateTimeOffset? LastObservedAt;
        public DateTimeOffset? LastSnapshotAt;
        public bool HasIdentity;
        public bool BudgetChanged;
        public string? Provider;
        public string? Plan;
        public Dictionary<string, Slot> Slots { get; } = new(StringComparer.Ordinal);

        public static State Initial(DateOnly localDate) => new() { LocalDate = localDate };

        public ObservedUsageSnapshot ToSnapshot()
        {
            var periods = Slots.Values
                .OrderBy(slot => slot.SlotName, PeriodOrder.Instance)
                .Select(slot => slot.ToEstimate())
                .ToList();

            ObservedUsageState overall;
            if (periods.Any(period => period.State is ObservedPeriodState.Available or ObservedPeriodState.Partial))
            {
                overall = periods.Any(period => period.HasGap)
                    ? ObservedUsageState.Partial
                    : ObservedUsageState.Available;
            }
            else if (periods.Any(period => period.State == ObservedPeriodState.Waiting))
            {
                overall = ObservedUsageState.Waiting;
            }
            else
            {
                overall = ObservedUsageState.Unavailable;
            }

            return new ObservedUsageSnapshot(LocalDate, overall, periods);
        }
    }

    /// <summary>Primary first, then secondary, then any other period in ordinal order.</summary>
    private sealed class PeriodOrder : IComparer<string>
    {
        public static readonly PeriodOrder Instance = new();

        public int Compare(string? x, string? y)
        {
            int byRank = Rank(x).CompareTo(Rank(y));
            return byRank != 0 ? byRank : string.CompareOrdinal(x, y);
        }

        private static int Rank(string? slot) => slot switch
        {
            "primary" => 0,
            "secondary" => 1,
            _ => 2,
        };
    }
}
