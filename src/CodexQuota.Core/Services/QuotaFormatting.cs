using System.Globalization;
using CodexQuota.Core.Models;

namespace CodexQuota.Core.Services;

/// <summary>
/// Shared, culture-explicit presentation strings for normalized quota values. Kept
/// in the core so the ball, the details panel and tests describe a window the same
/// way instead of each re-deriving labels.
/// </summary>
public static class QuotaFormatting
{
    /// <summary>Human-readable period label derived from the returned duration.</summary>
    public static string DescribeDuration(long? durationMinutes, AppLanguage language = AppLanguage.SimplifiedChinese)
    {
        if (durationMinutes is not { } minutes || minutes <= 0)
        {
            return AppText.Format(language, "UnknownPeriod");
        }

        if (minutes % (24 * 60) == 0)
        {
            return AppText.Count(language, minutes / (24 * 60), "DayOne", "Days");
        }

        if (minutes % 60 == 0)
        {
            return AppText.Count(language, minutes / 60, "HourOne", "Hours");
        }

        return AppText.Count(language, minutes, "MinuteOne", "Minutes");
    }

    /// <summary>
    /// Compact period label for the small floating ball, e.g. 300 minutes -> <c>5h</c>.
    /// Returns an empty string when the duration is unknown so the ball renders nothing.
    /// </summary>
    public static string DescribeCompactDuration(long? durationMinutes)
    {
        if (durationMinutes is not { } minutes || minutes <= 0)
        {
            return string.Empty;
        }

        if (minutes % (24 * 60) == 0)
        {
            return (minutes / (24 * 60)).ToString(CultureInfo.InvariantCulture) + "d";
        }

        if (minutes % 60 == 0)
        {
            return (minutes / 60).ToString(CultureInfo.InvariantCulture) + "h";
        }

        return minutes.ToString(CultureInfo.InvariantCulture) + "m";
    }

    /// <summary>Period label with a generic primary/secondary fallback when duration is absent.</summary>
    public static string DescribeWindowLabel(QuotaWindow window, AppLanguage language = AppLanguage.SimplifiedChinese)
    {
        string fallback = window.Slot switch
        {
            "primary" => AppText.Format(language, "PrimaryPeriod"),
            "secondary" => AppText.Format(language, "SecondaryPeriod"),
            _ => window.Slot,
        };

        string duration = DescribeDuration(window.DurationMinutes, language);
        return window.DurationMinutes is not > 0 ? fallback : $"{fallback} · {duration}";
    }

    /// <summary>Formats a reset time in the viewer's local timezone.</summary>
    public static string DescribeResetTime(DateTimeOffset? resetsAt, TimeZoneInfo localTimeZone, AppLanguage language = AppLanguage.SimplifiedChinese)
    {
        if (resetsAt is not { } value)
        {
            return AppText.Format(language, "NotProvided");
        }

        DateTimeOffset local = TimeZoneInfo.ConvertTime(value, localTimeZone);
        return local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>Formats a fetched/scheduled timestamp in the viewer's local timezone.</summary>
    public static string DescribeTimestamp(DateTimeOffset? timestamp, TimeZoneInfo localTimeZone)
    {
        if (timestamp is not { } value)
        {
            return "—";
        }

        return TimeZoneInfo.ConvertTime(value, localTimeZone)
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>Formats the official daily Token count with thousands separators.</summary>
    public static string DescribeTokens(long tokens) =>
        tokens.ToString("N0", CultureInfo.InvariantCulture);
}
