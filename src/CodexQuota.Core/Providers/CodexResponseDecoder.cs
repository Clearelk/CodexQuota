using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexQuota.Core.Models;
using CodexQuota.Core.Services;

namespace CodexQuota.Core.Providers;

/// <summary>
/// The single owner of the official app-server JSON contract. Every consumer of
/// quota or daily data goes through this decoder; validation happens exactly here so
/// no other layer re-parses payload fields.
/// </summary>
public static class CodexResponseDecoder
{
    private const long MinUnixSeconds = -62135596800L;
    private const long MaxUnixSeconds = 253402300799L;

    private static readonly string[] AccountKeyFields =
    [
        "id",
        "accountId",
        "userId",
        "chatgptAccountId",
        "workspaceId",
        "email",
    ];

    /// <summary>
    /// Decodes an <c>account/rateLimits/read</c> result into a normalized snapshot.
    /// <paramref name="result"/> is the JSON-RPC <c>result</c> element.
    /// </summary>
    public static QuotaSnapshot DecodeQuota(JsonElement result, DateTimeOffset fetchedAt, string? plan = null)
    {
        var buckets = new List<QuotaBucket>();

        QuotaBucket? legacy = null;
        if (result.ValueKind == JsonValueKind.Object &&
            result.TryGetProperty("rateLimits", out JsonElement single) &&
            single.ValueKind == JsonValueKind.Object)
        {
            legacy = DecodeBucket(single, mapKey: null);
        }

        bool mapPresent = result.ValueKind == JsonValueKind.Object &&
            result.TryGetProperty("rateLimitsByLimitId", out JsonElement map) &&
            map.ValueKind == JsonValueKind.Object;

        if (mapPresent)
        {
            foreach (JsonProperty entry in result.GetProperty("rateLimitsByLimitId").EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object)
                {
                    buckets.Add(new QuotaBucket(entry.Name, null, [], true, null, null, null, null));
                    continue;
                }

                buckets.Add(DecodeBucket(entry.Value, entry.Name));
            }

            if (legacy is not null && !buckets.Any(IsMainBucket) && IsMainBucket(legacy))
            {
                buckets.Insert(0, legacy with { LimitId = string.IsNullOrEmpty(legacy.LimitId) ? "codex" : legacy.LimitId });
            }
        }
        else if (legacy is not null)
        {
            buckets.Add(legacy);
        }

        string? resolvedPlan = ResolvePlan(buckets, legacy, plan);

        return new QuotaSnapshot("codex", resolvedPlan, buckets, fetchedAt);
    }

    /// <summary>
    /// Decodes an <c>account/usage/read</c> result into the optional daily statistic
    /// for <paramref name="requestedDate"/>. The service-provided calendar key is
    /// matched verbatim and never reinterpreted as a timestamp.
    /// </summary>
    public static DailyUsageResult DecodeDailyUsage(JsonElement result, DateOnly requestedDate)
    {
        if (result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("dailyUsageBuckets", out JsonElement buckets) ||
            buckets.ValueKind != JsonValueKind.Array)
        {
            return DailyUsageResult.Missing(requestedDate, new AppMessage("OfficialDailyMissing"));
        }

        foreach (JsonElement bucket in buckets.EnumerateArray())
        {
            if (bucket.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? startDate = GetString(bucket, "startDate");
            if (!DateOnly.TryParseExact(startDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date) ||
                date != requestedDate)
            {
                continue;
            }

            long? tokens = TryGetInt64(bucket, "tokens");
            if (tokens is not { } value || value < 0)
            {
                return DailyUsageResult.Missing(requestedDate, new AppMessage("OfficialDailyInvalid"));
            }

            return DailyUsageResult.Available(requestedDate, value, date);
        }

        return DailyUsageResult.Missing(requestedDate, new AppMessage("OfficialDailyDateMissing"));
    }

    /// <summary>
    /// Returns the account mode (<c>chatgpt</c>, <c>apiKey</c>, <c>amazonBedrock</c>)
    /// or null when no account is signed in.
    /// </summary>
    public static string? DecodeAccountType(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("account", out JsonElement account) ||
            account.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return GetString(account, "type");
    }

    public static string? DecodeAccountPlan(JsonElement result) =>
        result.ValueKind == JsonValueKind.Object && result.TryGetProperty("account", out JsonElement account) && account.ValueKind == JsonValueKind.Object
            ? GetString(account, "planType") : null;

    /// <summary>
    /// Produces a short, non-reversible fingerprint of the signed-in account for
    /// in-memory cache rejection only. Returns null when no stable identifier is
    /// exposed; the raw value is never persisted, logged, or displayed.
    /// </summary>
    public static string? DecodeAccountKey(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("account", out JsonElement account) ||
            account.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (string field in AccountKeyFields)
        {
            string? value = GetString(account, field);
            if (!string.IsNullOrEmpty(value))
            {
                return Fingerprint(value);
            }
        }

        return null;
    }

    private static string Fingerprint(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash, 0, 8);
    }

    private static bool IsMainBucket(QuotaBucket bucket) =>
        string.Equals(bucket.LimitId, "codex", StringComparison.OrdinalIgnoreCase) ||
        string.IsNullOrEmpty(bucket.LimitId);

    private static string? ResolvePlan(List<QuotaBucket> buckets, QuotaBucket? legacy, string? fallback)
    {
        foreach (QuotaBucket bucket in buckets)
        {
            if (IsMainBucket(bucket) && bucket.PlanType is { Length: > 0 } plan)
            {
                return plan;
            }
        }

        if (legacy?.PlanType is { Length: > 0 } legacyPlan)
        {
            return legacyPlan;
        }

        return fallback;
    }

    private static QuotaBucket DecodeBucket(JsonElement bucket, string? mapKey)
    {
        string? limitId = mapKey ?? GetString(bucket, "limitId");
        string? name = GetString(bucket, "limitName");

        bool invalid = false;
        var windows = new List<QuotaWindow>(2);
        AddWindow(bucket, "primary", "primary", windows, ref invalid);
        AddWindow(bucket, "secondary", "secondary", windows, ref invalid);

        string? balance = null;
        bool? hasCredits = null;
        bool? unlimited = null;
        if (bucket.TryGetProperty("credits", out JsonElement credits) && credits.ValueKind == JsonValueKind.Object)
        {
            balance = GetString(credits, "balance");
            hasCredits = TryGetBool(credits, "hasCredits");
            unlimited = TryGetBool(credits, "unlimited");
        }

        string? reached = GetString(bucket, "rateLimitReachedType");
        string? planType = GetString(bucket, "planType");

        return new QuotaBucket(
            limitId,
            name,
            windows,
            invalid,
            balance,
            hasCredits,
            unlimited,
            reached)
        {
            PlanType = planType,
        };
    }

    private static void AddWindow(
        JsonElement bucket,
        string property,
        string slot,
        List<QuotaWindow> into,
        ref bool invalid)
    {
        if (!bucket.TryGetProperty(property, out JsonElement window) || window.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (window.ValueKind != JsonValueKind.Object)
        {
            invalid = true;
            return;
        }

        if (!window.TryGetProperty("usedPercent", out JsonElement usedElement) ||
            usedElement.ValueKind != JsonValueKind.Number ||
            !usedElement.TryGetInt32(out int used) ||
            used is < 0 or > 100)
        {
            invalid = true;
            return;
        }

        long? duration = TryGetInt64(window, "windowDurationMins");
        if (duration is <= 0)
        {
            duration = null;
        }

        DateTimeOffset? resetsAt = TryGetUnixSeconds(window, "resetsAt");
        into.Add(new QuotaWindow(slot, duration, used, 100 - used, resetsAt));
    }

    private static string? GetString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement element))
        {
            return null;
        }

        return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }

    private static bool? TryGetBool(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement element))
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static long? TryGetInt64(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement element) || element.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return element.TryGetInt64(out long value) ? value : null;
    }

    private static DateTimeOffset? TryGetUnixSeconds(JsonElement parent, string name)
    {
        long? raw = TryGetInt64(parent, name);
        if (raw is not { } seconds || seconds < MinUnixSeconds || seconds > MaxUnixSeconds)
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeSeconds(seconds);
    }
}
