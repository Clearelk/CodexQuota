using System.Windows;
using System.Windows.Media;
using CodexQuota.Core.Models;
using CodexQuota.Core.Services;
using CodexQuota.Desktop.Services;

namespace CodexQuota.Desktop.ViewModels;

/// <summary>One auxiliary quota bucket row. Auxiliary buckets never affect the ball.</summary>
public sealed class QuotaRowViewModel
{
    public QuotaRowViewModel(string label, QuotaWindow window, Brush fill, Brush track, AppLanguage language = AppLanguage.SimplifiedChinese)
    {
        Label = label;
        RemainingText = AppText.Format(language, "RemainingValue", window.RemainingPercent);
        RemainingPercent = window.RemainingPercent;
        Fill = fill;
        Track = track;
    }

    public string Label { get; }

    public string RemainingText { get; }

    public int RemainingPercent { get; }

    public Brush Fill { get; }

    public Brush Track { get; }
}

/// <summary>
/// One reference-style period card of the main bucket. It shows the actual returned
/// period, the numeric remainder, a matching remaining bar, the used percentage and the
/// local reset time. No severity adjective is ever rendered.
/// </summary>
public sealed class PeriodCardViewModel
{
    public PeriodCardViewModel(string title, QuotaWindow window, Brush accent, Brush valueAccent, Brush track, AppLanguage language = AppLanguage.SimplifiedChinese)
    {
        Title = title;
        PeriodText = QuotaFormatting.DescribeDuration(window.DurationMinutes, language);
        HeaderText = AppText.Format(language, "PeriodHeader", title, PeriodText);
        RemainingText = AppText.Format(language, "RemainingValue", window.RemainingPercent);
        RemainingPercent = window.RemainingPercent;
        UsedText = AppText.Format(language, "UsedValue", window.UsedPercent);
        ResetText = AppText.Format(language, "ResetValue", QuotaFormatting.DescribeResetTime(window.ResetsAt, TimeZoneInfo.Local, language));
        Accent = accent;
        ValueAccent = valueAccent;
        Track = track;
    }

    public string Title { get; }

    public string PeriodText { get; }

    public string HeaderText { get; }

    public string RemainingText { get; }

    public string RemainingValueText => RemainingPercent.ToString(System.Globalization.CultureInfo.InvariantCulture) + "%";

    public int RemainingPercent { get; }

    public string UsedText { get; }

    public string ResetText { get; }

    public Brush Accent { get; }

    public Brush ValueAccent { get; }

    public Brush Track { get; }
}

/// <summary>
/// One observed-consumption row: the period label with its actual returned duration and
/// a neutral estimate as a percentage of that period's full budget, a waiting state or an
/// unavailable state. It never uses the remaining-quota severity colors.
/// </summary>
public sealed class ObservedPeriodViewModel
{
    public ObservedPeriodViewModel(string title, string valueText, bool hasNumber)
    {
        Title = title;
        ValueText = valueText;
        HasNumber = hasNumber;
    }

    public string Title { get; }

    public string ValueText { get; }

    /// <summary>True only when this row shows a numeric observed estimate.</summary>
    public bool HasNumber { get; }
}

/// <summary>
/// Presentation state for the ball and the details panel. It renders normalized
/// refresh state and the device-local daily snapshot only; it never parses JSON, never
/// touches the file system and never triggers a provider or local scan.
/// </summary>
public sealed class WidgetViewModel : ObservableObject
{
    private AppLanguage Language => _settings.Language;
    private string Text(string key, params object?[] arguments) => AppText.Format(Language, key, arguments);

    private RefreshState _state;
    private QuotaWindow? _fiveHourWindow;
    private LocalUsageSnapshot? _localUsage;
    private ObservedUsageSnapshot _observedUsage;
    private bool _isVisible = true;
    private WidgetSettings _settings = WidgetSettings.Default;

    public bool ShowRemaining => _settings.ShowRemaining;
    public bool ShowResetTime => _settings.ShowResetTime;
    public bool ShowTodayUsage => _settings.ShowTodayUsage;
    public bool ShowPeriodCards => HasPeriodCards && (ShowRemaining || ShowResetTime);
    public bool ShowAuxiliaryRows => HasAuxiliaryRows && ShowRemaining;
    public void UpdateSettings(WidgetSettings settings) { _settings = settings; RaiseAll(); }
    private readonly ProviderIconAppearance _iconAppearance;

    public WidgetViewModel(RefreshState? initialState) : this(initialState, ProviderIconAppearance.Codex)
    {
    }

    internal WidgetViewModel(RefreshState? initialState, ProviderIconAppearance iconAppearance)
    {
        _iconAppearance = iconAppearance;
        _state = initialState ?? RefreshState.Initial(DateOnly.FromDateTime(DateTime.Now));
        _fiveHourWindow = QuotaProjection.SelectFiveHourWindow(_state.LastQuota);
        _observedUsage = ObservedUsageSnapshot.Initial(DateOnly.FromDateTime(DateTime.Now));
    }

    public RefreshState State => _state;

    /// <summary>Ball visibility toggled from the tray; the saved position is preserved.</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value)
            {
                return;
            }

            _isVisible = value;
            Raise();
        }
    }

    public QuotaSeverity Severity => _state.Status == RefreshStatus.Fresh
        ? QuotaProjection.GetFreshSeverity(_fiveHourWindow?.RemainingPercent)
        : (_fiveHourWindow is null ? QuotaSeverity.Unknown : QuotaSeverity.Stale);

    public string BallText
    {
        get
        {
            int? remaining = _fiveHourWindow?.RemainingPercent;
            return remaining is { } value && _state.Status is RefreshStatus.Fresh or RefreshStatus.Stale
                ? value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "%"
                : "--";
        }
    }

    /// <summary>True when a real remaining percentage can be drawn as an arc.</summary>
    public bool BallHasValue =>
        _state.Status is RefreshStatus.Fresh or RefreshStatus.Stale &&
        _fiveHourWindow is not null;

    public double BallRemainingPercent => _fiveHourWindow?.RemainingPercent ?? 0;

    /// <summary>Remaining ratio in 0..1 used to size the docked strip fill; 0 when unknown.</summary>
    public double BallRemainingFraction => BallHasValue ? Math.Clamp(BallRemainingPercent, 0, 100) / 100d : 0d;

    /// <summary>Compact label of the selected real five-hour main period.</summary>
    public string BallPeriodText =>
        QuotaFormatting.DescribeCompactDuration(_fiveHourWindow?.DurationMinutes);

    public bool BallPeriodVisible => BallPeriodText.Length > 0;

    internal ProviderIconState IconState => new(_iconAppearance, BallHasValue ? (int)BallRemainingPercent : null, _state.Status, ProviderIconState.CurrentThemeIsDark);

    public string BallSymbol => _iconAppearance.Symbol;

    public Brush BallSymbolBrush => IconState.SymbolBrush;

    public Brush BallRingBrush => RemainingValueBrush(Severity);

    public Brush BallRingTrackBrush => ThemeBrush("BallRingTrackBrush");

    /// <summary>
    /// Remaining-quota color shared by the docked fill, floating ring and remaining number.
    /// Quota and theme updates re-resolve the same state-dependent brush for every surface.
    /// </summary>
    public Brush DockStripFillBrush => BallRingBrush;

    public Brush BallSurfaceBrush => ThemeBrush("BallSurfaceBrush");

    public Brush BallInnerRimBrush => IconState.InnerRimBrush;

    public Brush BallBadgeBrush => IconState.BadgeBrush;

    public Brush BallBadgeSeparatorBrush => IconState.BadgeSeparatorBrush;

    public Brush BallBadgeHighlightBrush => IconState.BadgeHighlightBrush;

    public Brush BallBorderBrush => ThemeBrush("BallBorderBrush");

    public Brush BallTextBrush => RemainingValueBrush(Severity);

    public Brush BallMutedTextBrush => ThemeBrush("TextSecondaryBrush");

    /// <summary>
    /// Inline dot next to the period label: while fresh it shares the remaining-quota
    /// tier color with the ring; stale, error and other non-fresh states keep the
    /// connection-health color so connectivity problems stay visible.
    /// </summary>
    public Brush BallDotBrush => _state.Status == RefreshStatus.Fresh ? BallRingBrush : ConnectionDotBrush;

    public string TooltipText
    {
        get
        {
            int? remaining = _fiveHourWindow?.RemainingPercent;
            string period = BallPeriodVisible ? Text("BallPeriod", BallPeriodText) : string.Empty;
            string headline = (remaining, _state.Status) switch
            {
                ({ } value, RefreshStatus.Fresh) => Text("BallFresh", value, period),
                ({ } value, RefreshStatus.Stale) => Text("BallStale", value, period),
                _ => Text("BallUnavailable"),
            };

            return Text("BallTooltip", headline);
        }
    }

    public string PlanText
    {
        get
        {
            string? plan = _state.LastQuota?.Plan;
            return string.IsNullOrEmpty(plan) ? Text("PlanUnknown") : Text("Plan", plan);
        }
    }

    public IReadOnlyList<PeriodCardViewModel> PeriodCards
    {
        get
        {
            var cards = new List<PeriodCardViewModel>();
            foreach (QuotaWindow window in QuotaProjection.SelectDetailsWindows(_state.LastQuota))
            {
                QuotaSeverity severity = WindowSeverity(window.RemainingPercent);
                cards.Add(new PeriodCardViewModel(
                    window.DurationMinutes == 10080 ? Text("WeeklyQuota") : DescribeCardTitle(window.Slot),
                    window,
                    ThemeBrush(BrushKey(severity)),
                    RemainingValueBrush(severity),
                    ThemeBrush("ProgressTrackBrush"), Language));
            }
            return cards;
        }
    }

    public bool HasPeriodCards => PeriodCards.Count > 0;

    public string NoPeriodsText => _state.LastQuota is null
        ? Text("NoQuota")
        : Text("NoPeriods");

    public IReadOnlyList<QuotaRowViewModel> AuxiliaryRows
    {
        get
        {
            IReadOnlyList<QuotaBucket> buckets = QuotaProjection.GetAuxiliaryBuckets(_state.LastQuota);
            var rows = new List<QuotaRowViewModel>();
            foreach (QuotaBucket bucket in buckets)
            {
                foreach (QuotaWindow window in bucket.Windows)
                {
                    if (window.DurationMinutes != 300) { continue; }
                    rows.Add(new QuotaRowViewModel(
                        bucket.Name ?? bucket.LimitId ?? Text("OtherBucket"),
                        window,
                        ThemeBrush(BrushKey(WindowSeverity(window.RemainingPercent))),
                        ThemeBrush("ProgressTrackBrush"), Language));
                }
            }

            return rows;
        }
    }

    public bool HasAuxiliaryRows => AuxiliaryRows.Count > 0;

    public string StatusText => _state.Status switch
    {
        RefreshStatus.Fresh => Text("ConnectionFresh"),
        RefreshStatus.Loading => Text("ConnectionLoading"),
        RefreshStatus.Stale => Text("ConnectionStale"),
        RefreshStatus.SignedOut => Text("ConnectionSignedOut"),
        RefreshStatus.Unsupported => Text("ConnectionUnsupported"),
        _ => Text("ConnectionError"),
    };

    public string StatusDetailText => _state.StatusMessage?.FormatCompatible(Language, _state.StatusText) ?? _state.StatusText ?? string.Empty;

    public bool HasStatusDetail => !string.IsNullOrEmpty(_state.StatusText);

    public Brush ConnectionDotBrush => IconState.DotBrush;

    public string LastSuccessText => Text("LastSuccess", DescribeStatusTime(_state.LastSuccess));

    public string NextFetchText => Text("NextFetch", QuotaFormatting.DescribeTimestamp(_state.NextScheduledFetch, TimeZoneInfo.Local));

    public string LastAttemptText => Text("LastAttempt", DescribeStatusTime(_state.LastAttempt));

    public string HintText => Text("DetailsHint");

    public string LocalTitleText => Text("LocalTitle");

    public string LocalBadgeText => Text("LocalBadge");

    public string LocalInfoText => Text("LocalInfo");

    public bool HasLocalNumbers => _localUsage?.HasNumbers == true && _localUsage.Date == DateOnly.FromDateTime(DateTime.Now);

    /// <summary>Explicit statement used when the local source could not be inspected at all.</summary>
    public string LocalPendingText => _localUsage switch
    {
        null => Text("LocalReading"),
        { Date: var date } when date != DateOnly.FromDateTime(DateTime.Now) => Text("LocalTodayReading"),
        { CoverageNote: { Length: > 0 } note } => _localUsage.CoverageMessage?.FormatCompatible(Language, note) ?? note,
        _ => Text("LocalUnavailable"),
    };

    public string LocalSessionsNumber => HasLocalNumbers
        ? _localUsage!.SessionCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : "—";

    public string LocalSessionsText => Text(HasLocalNumbers && _localUsage!.SessionCount == 1 ? "ConversationOne" : "Conversations", LocalSessionsNumber);

    public string LocalSessionsSuffix => Text(HasLocalNumbers && _localUsage!.SessionCount == 1 ? "ConversationSuffixOne" : "ConversationSuffix");

    public string DailyInfoText => LocalInfoText + "\n" + (HasLocalNumbers ? LocalCoverageText : LocalPendingText)
        + "\n\n" + ObservedInfoText + "\n" + ObservedPendingText + ObservedNoteText;

    public string DailyNoteText => !HasLocalNumbers
        ? (_localUsage is null || _localUsage.Date != DateOnly.FromDateTime(DateTime.Now)
            ? Text("DailyCounting")
            : Text("DailyUnavailable"))
        : (_localUsage?.Coverage == LocalUsageCoverage.Partial ? Text("DailyPartial") : string.Empty);

    public bool HasDailyNote => DailyNoteText.Length > 0;

    public string LocalTokensText => _localUsage is { } snapshot
        ? QuotaFormatting.DescribeTokens(snapshot.TotalTokens) + " Tokens"
        : "—";

    public string LocalCoverageText => _localUsage switch
    {
        null => string.Empty,
        { Coverage: LocalUsageCoverage.Unavailable } => string.Empty,
        { Coverage: LocalUsageCoverage.Partial, CoverageNote: { Length: > 0 } note } => Text("LocalPartial", _localUsage.CoverageMessage?.FormatCompatible(Language, note) ?? note),
        { Coverage: LocalUsageCoverage.Partial } => Text("LocalPartial", Text("LocalIncomplete")),
        _ => string.Empty,
    };

    public bool HasLocalCoverageNote => !string.IsNullOrEmpty(LocalCoverageText);

    public string ObservedTitleText => Text("ObservedTitle");

    private bool ObservedIsToday => _observedUsage.LocalDate == DateOnly.FromDateTime(DateTime.Now);

    private ObservedPeriodEstimate? TodayPrimaryEstimate => ObservedIsToday
        ? _observedUsage.Periods.FirstOrDefault(period => string.Equals(period.Slot, "primary", StringComparison.Ordinal))
        : null;

    public string PrimaryConsumptionText => TodayPrimaryEstimate is { } period
        ? period.State switch
        {
            ObservedPeriodState.Waiting => Text("ObservedWaiting"),
            ObservedPeriodState.Unavailable => Text("ObservedUnavailable"),
            _ => DescribeObservedValue(period),
        }
        : Text("ObservedUnavailable");

    /// <summary>
    /// Per-period observed consumption, independent of the local Token card. Values are
    /// neutral and never re-apply the remaining-quota color policy.
    /// </summary>
    public IReadOnlyList<ObservedPeriodViewModel> ObservedPeriods
    {
        get
        {
            if (!ObservedIsToday) { return []; }
            var rows = new List<ObservedPeriodViewModel>(_observedUsage.Periods.Count);
            if (TodayPrimaryEstimate is { } period)
            {
                string title = DescribeCardTitle(period.Slot);
                if (period.DurationMinutes is { } minutes && minutes > 0)
                {
                    title = Text("PeriodHeader", title, QuotaFormatting.DescribeDuration(minutes, Language));
                }

                rows.Add(new ObservedPeriodViewModel(title, DescribeObservedValue(period), period.State is ObservedPeriodState.Available or ObservedPeriodState.Partial));
            }

            return rows;
        }
    }

    public bool HasObservedPeriods => ObservedPeriods.Count > 0;

    /// <summary>True only when at least one period shows a numeric estimate.</summary>
    public bool ObservedHasNumber => TodayPrimaryEstimate?.State is ObservedPeriodState.Available or ObservedPeriodState.Partial;

    public string ObservedPendingText => !ObservedIsToday ? Text("ObservedTodayWaiting") : TodayPrimaryEstimate?.State switch
    {
        ObservedPeriodState.Unavailable => Text("ObservedMetadata"),
        ObservedPeriodState.Waiting => Text("ObservedSecond"),
        null => Text("ObservedFirst"),
        _ => string.Empty,
    };

    public bool HasObservedPending => ObservedPendingText.Length > 0;

    public string ObservedNoteText => TodayPrimaryEstimate?.HasGap == true
        ? Text("ObservedGap")
        : string.Empty;

    public bool HasObservedNote => ObservedNoteText.Length > 0;

    public string ObservedInfoText => Text("ObservedInfo");

    private string DescribeObservedValue(ObservedPeriodEstimate period) => period.State switch
    {
        ObservedPeriodState.Available => Text("ObservedValue", period.ObservedPercentPoints.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)),
        ObservedPeriodState.Partial => Text("ObservedPartialValue", period.ObservedPercentPoints.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)),
        ObservedPeriodState.Waiting => Text("ObservedSecondShort"),
        _ => Text("ObservedPeriodMissing"),
    };

    private static string DescribeStatusTime(DateTimeOffset? timestamp) => timestamp is { } value
        ? TimeZoneInfo.ConvertTime(value, TimeZoneInfo.Local).ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
        : "—";

    public void Update(RefreshState state)
    {
        _state = state;
        _fiveHourWindow = QuotaProjection.SelectFiveHourWindow(state.LastQuota);
        RaiseAll();
    }

    /// <summary>Applies a new device-local daily snapshot. Called on the UI thread only.</summary>
    public void UpdateLocalUsage(LocalUsageSnapshot snapshot)
    {
        _localUsage = snapshot;
        RaiseAll();
    }

    /// <summary>Applies a new observed-consumption estimate. Called on the UI thread only.</summary>
    public void UpdateObservedUsage(ObservedUsageSnapshot snapshot)
    {
        _observedUsage = snapshot;
        RaiseAll();
    }

    /// <summary>Re-resolves theme-dependent brushes after a theme switch.</summary>
    public void RefreshTheme() => RaiseAll();

    private QuotaSeverity WindowSeverity(int remainingPercent) =>
        _state.Status == RefreshStatus.Fresh
            ? QuotaProjection.GetFreshSeverity(remainingPercent)
            : (_state.LastQuota is null ? QuotaSeverity.Unknown : QuotaSeverity.Stale);

    private string DescribeCardTitle(string slot) => slot switch
    {
        "primary" => Text("PrimaryQuota"),
        "secondary" => Text("SecondaryPeriod"),
        _ => slot,
    };

    private static string BrushKey(QuotaSeverity severity) => severity switch
    {
        QuotaSeverity.Green => "SeverityGreenBrush",
        QuotaSeverity.Blue => "SeverityBlueBrush",
        QuotaSeverity.Yellow => "SeverityYellowBrush",
        QuotaSeverity.Red => "SeverityRedBrush",
        _ => "SeverityNeutralBrush",
    };

    // The yellow tier keeps its darker text color for readability on the light card
    // surfaces; every other fresh tier uses its severity brush.
    private static Brush RemainingValueBrush(QuotaSeverity severity) => ThemeBrush(
        severity == QuotaSeverity.Yellow ? "QuotaAmberTextBrush" : BrushKey(severity));

    private static Brush ThemeBrush(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
}
