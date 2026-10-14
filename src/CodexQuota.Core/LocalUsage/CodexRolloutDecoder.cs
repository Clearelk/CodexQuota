using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace CodexQuota.Core.LocalUsage;

public enum LocalRolloutKind { Ignored, SessionMeta, TokenCount, Invalid }

/// <summary>Allowed-field projection only. Session identity is an in-memory hash, never a raw identifier.</summary>
public readonly record struct LocalRolloutRecord(
    LocalRolloutKind Kind, DateTimeOffset? Timestamp, string? SessionId, string? Provider,
    long? CumulativeTotal, long? LastTotal, bool InvalidUsage = false);

/// <summary>
/// Reads bounded UTF-8 frames without constructing a JSON document. Unknown values are
/// traversed/skipped by Utf8JsonReader, never materialized as strings or retained in the
/// projection. Reading the envelope first also supports payload-before-type ordering.
/// </summary>
public static class CodexRolloutDecoder
{
    public static LocalRolloutRecord Decode(ReadOnlySpan<byte> utf8Line)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8Line);
            if (!reader.Read()) { return default; }
            if (reader.TokenType != JsonTokenType.StartObject) { return Invalid(); }
            LocalRolloutKind kind = LocalRolloutKind.Ignored;
            bool hasType = false;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) { return Invalid(); }
                bool type = reader.ValueTextEquals("type"u8);
                if (!reader.Read()) { return Invalid(); }
                if (type)
                {
                    if (hasType) { return Invalid(); }
                    hasType = true;
                    kind = reader.TokenType == JsonTokenType.String && reader.ValueTextEquals("session_meta"u8)
                        ? LocalRolloutKind.SessionMeta
                        : reader.TokenType == JsonTokenType.String && reader.ValueTextEquals("event_msg"u8)
                            ? LocalRolloutKind.TokenCount : LocalRolloutKind.Ignored;
                }
                reader.Skip();
            }
            if (reader.TokenType != JsonTokenType.EndObject || reader.Read()) { return Invalid(); }
            if (kind == LocalRolloutKind.Ignored) { return default; }

            var projection = new Projection();
            reader = new Utf8JsonReader(utf8Line);
            reader.Read();
            ReadObject(ref reader, projection, kind, FieldScope.Envelope);
            if (projection.Invalid) { return Invalid(); }
            if (kind == LocalRolloutKind.TokenCount && !projection.IsTokenCount) { return default; }
            if (projection.Last is { } last && projection.Cumulative is { } total && last > total)
            {
                projection.Last = null;
                projection.InvalidUsage = true;
            }
            return new LocalRolloutRecord(kind, projection.Timestamp, projection.Identity,
                projection.Provider, projection.Cumulative, projection.Last, projection.InvalidUsage);
        }
        catch (JsonException) { return Invalid(); }
    }

    private static LocalRolloutRecord Invalid() => new(LocalRolloutKind.Invalid, null, null, null, null, null);

    private enum FieldScope { Envelope, Payload, Info, Cumulative, Last }

    private static void ReadObject(ref Utf8JsonReader reader, Projection projection, LocalRolloutKind kind, FieldScope scope)
    {
        if (reader.TokenType != JsonTokenType.StartObject) { reader.Skip(); return; }
        int seen = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            int field = GetField(ref reader, scope, kind);
            if (!reader.Read()) { projection.Invalid = true; return; }
            if (field != 0 && (seen & (1 << field)) != 0) { projection.Invalid = true; }
            seen |= 1 << field;
            switch (field)
            {
                case 1:
                    string? stamp = SmallString(ref reader, 128);
                    bool hasOffset = stamp is { Length: >= 6 } && (stamp.EndsWith('Z') || stamp[^6] is '+' or '-');
                    projection.Timestamp = hasOffset && DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset value) ? value : null;
                    break;
                case 2: ReadObject(ref reader, projection, kind, FieldScope.Payload); break;
                case 3: projection.Identity = IdentityHash(ref reader) ?? projection.Identity; break;
                case 4: projection.LegacyIdentity = IdentityHash(ref reader); break;
                case 5:
                    projection.Provider = reader.TokenType == JsonTokenType.String
                        ? (reader.ValueTextEquals("openai"u8) ? "openai" : "other") : null;
                    break;
                case 6: projection.IsTokenCount = reader.TokenType == JsonTokenType.String && reader.ValueTextEquals("token_count"u8); break;
                case 7: ReadObject(ref reader, projection, kind, FieldScope.Info); break;
                case 8: ReadObject(ref reader, projection, kind, FieldScope.Cumulative); break;
                case 9: ReadObject(ref reader, projection, kind, FieldScope.Last); break;
                case 10:
                    long? counter = reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out long count) && count >= 0 ? count : null;
                    if (reader.TokenType != JsonTokenType.Null && counter is null) { projection.InvalidUsage = true; }
                    if (scope == FieldScope.Cumulative) { projection.Cumulative = counter; }
                    else { projection.Last = counter; }
                    break;
            }
            reader.Skip();
        }
        if (reader.TokenType != JsonTokenType.EndObject) { projection.Invalid = true; }
        projection.Identity ??= projection.LegacyIdentity;
    }

    private static int GetField(ref Utf8JsonReader reader, FieldScope scope, LocalRolloutKind kind) => scope switch
    {
        FieldScope.Envelope when reader.ValueTextEquals("timestamp"u8) && kind == LocalRolloutKind.TokenCount => 1,
        FieldScope.Envelope when reader.ValueTextEquals("payload"u8) => 2,
        FieldScope.Payload when kind == LocalRolloutKind.SessionMeta && reader.ValueTextEquals("session_id"u8) => 3,
        FieldScope.Payload when kind == LocalRolloutKind.SessionMeta && reader.ValueTextEquals("id"u8) => 4,
        FieldScope.Payload when kind == LocalRolloutKind.SessionMeta && reader.ValueTextEquals("model_provider"u8) => 5,
        FieldScope.Payload when kind == LocalRolloutKind.TokenCount && reader.ValueTextEquals("type"u8) => 6,
        FieldScope.Payload when kind == LocalRolloutKind.TokenCount && reader.ValueTextEquals("info"u8) => 7,
        FieldScope.Info when reader.ValueTextEquals("total_token_usage"u8) => 8,
        FieldScope.Info when reader.ValueTextEquals("last_token_usage"u8) => 9,
        FieldScope.Cumulative or FieldScope.Last when reader.ValueTextEquals("total_tokens"u8) => 10,
        _ => 0,
    };

    private static string? SmallString(ref Utf8JsonReader reader, int limit) =>
        reader.TokenType == JsonTokenType.String && reader.ValueSpan.Length <= limit ? reader.GetString() : null;

    private static string? IdentityHash(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.String || reader.ValueSpan.Length is 0 or > 512) { return null; }
        Span<byte> identity = stackalloc byte[512];
        int count = reader.CopyString(identity);
        string hash = Convert.ToHexString(SHA256.HashData(identity[..count]));
        CryptographicOperations.ZeroMemory(identity);
        return hash;
    }

    private sealed class Projection
    {
        public DateTimeOffset? Timestamp;
        public string? Identity;
        public string? LegacyIdentity;
        public string? Provider;
        public long? Cumulative;
        public long? Last;
        public bool IsTokenCount;
        public bool Invalid;
        public bool InvalidUsage;
    }
}
