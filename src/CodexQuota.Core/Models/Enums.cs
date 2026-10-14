namespace CodexQuota.Core.Models;

/// <summary>
/// Visual severity derived from the normalized remaining quota percentage.
/// Fresh data uses three battery-style tiers in increasing order: Red below 34,
/// Yellow 34-66 and Green from 67. Blue remains defined for theme accents but the
/// fresh quota policy no longer assigns it. Unknown and Stale are intentionally
/// neutral so that absent or out-of-date data is never presented as fresh available
/// quota.
/// </summary>
public enum QuotaSeverity
{
    Unknown,
    Stale,
    Red,
    Yellow,
    Blue,
    Green,
}

/// <summary>User-selectable appearance theme.</summary>
public enum ThemeMode
{
    System,
    Light,
    Dark,
}

/// <summary>Stable persisted language choice; Chinese remains the schema-1 fallback.</summary>
public enum AppLanguage { SimplifiedChinese, English }

/// <summary>Custom saved position or a work-area corner preset.</summary>
public enum BallPlacement { Custom, TopLeft, TopRight, BottomLeft, BottomRight }

/// <summary>
/// Optional collapsed side-edge docking of the floating ball. None is the default for
/// old, unknown or incomplete preferences; only Left/Right work-area edges are eligible.
/// </summary>
public enum BallDockSide { None, Left, Right }

/// <summary>Health of the most recent refresh cycle.</summary>
public enum RefreshStatus
{
    Loading,
    Fresh,
    Stale,
    SignedOut,
    Unsupported,
    Error,
}

/// <summary>Availability of the optional official daily Token statistic.</summary>
public enum DailyUsageAvailability
{
    Available,
    Missing,
    Unsupported,
    Failed,
}

/// <summary>Stable, non-sensitive classification of a provider failure.</summary>
public enum ProviderFailureKind
{
    None,
    CliNotFound,
    NodeNotFound,
    NotSignedIn,
    UnsupportedAccount,
    UnsupportedMethod,
    InvalidData,
    Transport,
    Timeout,
    ProcessExited,
    Canceled,
    AccountChanged,
    Unknown,
}
