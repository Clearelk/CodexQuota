namespace CodexQuota.Core.Models;

/// <summary>
/// Persisted widget preferences (schema version 1). Position is optional and stored
/// in device-independent units together with its captured DPI scale so it
/// can be clamped safely after a display change. Start-at-login is intentionally
/// absent: the actual registry registration is the single source of truth.
/// </summary>
public sealed record WidgetSettings(
    int Version,
    double Opacity,
    ThemeMode Theme,
    int RefreshIntervalSeconds,
    double? BallLeft,
    double? BallTop,
    string? CodexCliPath)
{
    public AppLanguage Language { get; init; } = AppLanguage.SimplifiedChinese;
    /// <summary>DPI scale used for saved DIP coordinates; their product recovers physical virtual-screen position.</summary>
    public double? BallPositionScale { get; init; }
    public BallPlacement Placement { get; init; } = BallPlacement.Custom;

    /// <summary>
    /// Optional deliberate side-edge docking. BallLeft/BallTop keep their meaning as the
    /// reachable canonical normal-ball anchor; the collapsed strip is never persisted as
    /// position. Old JSON without this field normalizes to None.
    /// </summary>
    public BallDockSide DockSide { get; init; } = BallDockSide.None;
    public int BallSizePercent { get; init; } = 100;
    public bool ShowRemaining { get; init; } = true;
    public bool ShowResetTime { get; init; } = true;
    public bool ShowTodayUsage { get; init; } = true;
    public int LowQuotaThreshold { get; init; } = 20;
    public bool NotificationsEnabled { get; init; }
    public const int CurrentVersion = 1;
    public const double MinOpacity = 0.4;
    public const double MaxOpacity = 1.0;
    public const int MinIntervalSeconds = 30;
    public const int MaxIntervalSeconds = 900;
    public const int DefaultIntervalSeconds = 120;

    public static WidgetSettings Default { get; } = new(
        CurrentVersion,
        Opacity: 0.95,
        Theme: ThemeMode.System,
        RefreshIntervalSeconds: DefaultIntervalSeconds,
        BallLeft: null,
        BallTop: null,
        CodexCliPath: null);

    public TimeSpan RefreshInterval => TimeSpan.FromSeconds(RefreshIntervalSeconds);
}
