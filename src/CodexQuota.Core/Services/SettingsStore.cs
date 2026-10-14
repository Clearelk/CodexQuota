using System.Text.Json;
using System.Text.Json.Serialization;
using CodexQuota.Core.Models;

namespace CodexQuota.Core.Services;

/// <summary>
/// Loads and saves validated widget settings under
/// <c>%LOCALAPPDATA%/CodexQuotaWidget/settings.json</c>. Writes go through a sibling
/// temporary file and an atomic replace so a crash cannot leave a half-written file.
/// Malformed input falls back to defaults with a safe, user-visible explanation and
/// never crashes startup. No quota, account or credential data is persisted here.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _directory;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public SettingsStore(string? rootDirectory = null)
    {
        _directory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexQuotaWidget");
        SettingsPath = Path.Combine(_directory, "settings.json");
    }

    /// <summary>Full path of the settings file.</summary>
    public string SettingsPath { get; }

    /// <summary>Safe explanation of the most recent load problem, if any.</summary>
    public string? LastWarning => LastWarningMessage?.Format();
    public AppMessage? LastWarningMessage { get; private set; }

    public async Task<WidgetSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        LastWarningMessage = null;

        if (!File.Exists(SettingsPath))
        {
            return WidgetSettings.Default;
        }

        try
        {
            await using FileStream stream = new(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            SettingsDto? dto = await JsonSerializer.DeserializeAsync<SettingsDto>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);

            if (dto is null)
            {
                LastWarningMessage = new AppMessage("SettingsEmpty");
                return WidgetSettings.Default;
            }

            if (dto.Version is { } version && version != WidgetSettings.CurrentVersion)
            {
                LastWarningMessage = new AppMessage("SettingsVersion", version);
                return WidgetSettings.Default;
            }

            return Normalize(FromDto(dto));
        }
        catch (JsonException)
        {
            LastWarningMessage = new AppMessage("SettingsInvalid");
            return WidgetSettings.Default;
        }
        catch (IOException)
        {
            LastWarningMessage = new AppMessage("SettingsReadFailure");
            return WidgetSettings.Default;
        }
        catch (UnauthorizedAccessException)
        {
            LastWarningMessage = new AppMessage("SettingsDenied");
            return WidgetSettings.Default;
        }
    }

    public async Task SaveAsync(WidgetSettings settings, CancellationToken cancellationToken = default)
    {
        WidgetSettings normalized = Normalize(settings);
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string temporaryPath = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(_directory);
            await using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, ToDto(normalized), SerializerOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            for (int attempt = 0; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    File.Move(temporaryPath, SettingsPath, overwrite: true);
                    break;
                }
                catch (Exception exception) when (attempt < 3 && IsWindowsReplaceContention(exception))
                {
                    // Brief readers without FILE_SHARE_DELETE can block replacement,
                    // including during rapid saves. Keep the same complete temp file;
                    // retry only this boundary, with at most 175 ms of cancellable delay.
                    await Task.Delay(25 << attempt, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch (IOException) { }
            _saveGate.Release();
        }
    }

    /// <summary>
    /// Clamps and validates settings to the supported schema. Non-finite numbers,
    /// out-of-range values and unknown themes fall back to safe defaults.
    /// </summary>
    public static WidgetSettings Normalize(WidgetSettings settings)
    {
        double opacity = IsFinite(settings.Opacity)
            ? Math.Clamp(settings.Opacity, WidgetSettings.MinOpacity, WidgetSettings.MaxOpacity)
            : WidgetSettings.Default.Opacity;

        int interval = settings.RefreshIntervalSeconds is >= WidgetSettings.MinIntervalSeconds and <= WidgetSettings.MaxIntervalSeconds
            ? settings.RefreshIntervalSeconds
            : Math.Clamp(settings.RefreshIntervalSeconds, WidgetSettings.MinIntervalSeconds, WidgetSettings.MaxIntervalSeconds);

        ThemeMode theme = Enum.IsDefined(settings.Theme) ? settings.Theme : WidgetSettings.Default.Theme;

        double? left = IsFinite(settings.BallLeft) ? settings.BallLeft : null;
        double? top = IsFinite(settings.BallTop) ? settings.BallTop : null;
        if ((left is null) != (top is null))
        {
            left = null;
            top = null;
        }

        string? cliPath = string.IsNullOrWhiteSpace(settings.CodexCliPath) ? null : settings.CodexCliPath.Trim();

        return new WidgetSettings(
            WidgetSettings.CurrentVersion,
            opacity,
            theme,
            interval,
            left,
            top,
            cliPath)
        {
            BallPositionScale = left is not null && settings.BallPositionScale is > 0 and <= 8 && IsFinite(settings.BallPositionScale) ? settings.BallPositionScale : null,
            Language = Enum.IsDefined(settings.Language) ? settings.Language : AppLanguage.SimplifiedChinese,
            Placement = Enum.IsDefined(settings.Placement) ? settings.Placement : BallPlacement.Custom,
            DockSide = Enum.IsDefined(settings.DockSide) && left is not null && top is not null && settings.Placement == BallPlacement.Custom
                ? settings.DockSide : BallDockSide.None,
            BallSizePercent = Math.Clamp(settings.BallSizePercent, 75, 150),
            ShowRemaining = settings.ShowRemaining,
            ShowResetTime = settings.ShowResetTime,
            ShowTodayUsage = settings.ShowTodayUsage,
            LowQuotaThreshold = Math.Clamp(settings.LowQuotaThreshold, 1, 100),
            NotificationsEnabled = settings.NotificationsEnabled,
        };
    }

    private static bool IsFinite(double? value) =>
        value is { } number && !double.IsNaN(number) && !double.IsInfinity(number);

    private static bool IsWindowsReplaceContention(Exception exception) =>
        OperatingSystem.IsWindows() && exception is IOException or UnauthorizedAccessException &&
        (exception.HResult & 0xFFFF) is 5 or 32 or 33;

    private static WidgetSettings FromDto(SettingsDto dto) => new(
        dto.Version ?? WidgetSettings.CurrentVersion,
        dto.Opacity ?? WidgetSettings.Default.Opacity,
        ParseTheme(dto.Theme),
        dto.RefreshIntervalSeconds ?? WidgetSettings.DefaultIntervalSeconds,
        dto.BallLeft,
        dto.BallTop,
        dto.CodexCliPath)
    {
        Language = ParseLanguage(dto.Language),
        BallPositionScale = dto.BallPositionScale,
        Placement = Enum.TryParse(dto.Placement, true, out BallPlacement placement) && Enum.IsDefined(placement) ? placement : BallPlacement.Custom,
        DockSide = Enum.TryParse(dto.DockSide, true, out BallDockSide side) && Enum.IsDefined(side) ? side : BallDockSide.None,
        BallSizePercent = dto.BallSizePercent ?? 100,
        ShowRemaining = dto.ShowRemaining ?? true,
        ShowResetTime = dto.ShowResetTime ?? true,
        ShowTodayUsage = dto.ShowTodayUsage ?? true,
        LowQuotaThreshold = dto.LowQuotaThreshold ?? 20,
        NotificationsEnabled = dto.NotificationsEnabled ?? false,
    };

    private static ThemeMode ParseTheme(string? value) =>
        Enum.TryParse(value, ignoreCase: true, out ThemeMode theme) && Enum.IsDefined(theme)
            ? theme
            : WidgetSettings.Default.Theme;

    // An unsupported field value must not invalidate unrelated schema-1 preferences.
    private static AppLanguage ParseLanguage(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.String } text &&
        string.Equals(text.GetString(), nameof(AppLanguage.English), StringComparison.OrdinalIgnoreCase)
            ? AppLanguage.English : AppLanguage.SimplifiedChinese;

    private static SettingsDto ToDto(WidgetSettings settings) => new()
    {
        Version = settings.Version,
        Opacity = settings.Opacity,
        Theme = settings.Theme.ToString(),
        Language = JsonSerializer.SerializeToElement(settings.Language.ToString()),
        RefreshIntervalSeconds = settings.RefreshIntervalSeconds,
        BallLeft = settings.BallLeft,
        BallTop = settings.BallTop,
        CodexCliPath = settings.CodexCliPath,
        BallPositionScale = settings.BallPositionScale,
        Placement = settings.Placement.ToString(),
        DockSide = settings.DockSide == BallDockSide.None ? null : settings.DockSide.ToString(),
        BallSizePercent = settings.BallSizePercent,
        ShowRemaining = settings.ShowRemaining,
        ShowResetTime = settings.ShowResetTime,
        ShowTodayUsage = settings.ShowTodayUsage,
        LowQuotaThreshold = settings.LowQuotaThreshold,
        NotificationsEnabled = settings.NotificationsEnabled,
    };

    private sealed class SettingsDto
    {
        public int? Version { get; set; }

        public double? Opacity { get; set; }

        public string? Theme { get; set; }
        public JsonElement? Language { get; set; }

        public int? RefreshIntervalSeconds { get; set; }

        public double? BallLeft { get; set; }

        public double? BallTop { get; set; }

        public string? CodexCliPath { get; set; }
        public double? BallPositionScale { get; set; }
        public string? Placement { get; set; }
        public string? DockSide { get; set; }
        public int? BallSizePercent { get; set; }
        public bool? ShowRemaining { get; set; }
        public bool? ShowResetTime { get; set; }
        public bool? ShowTodayUsage { get; set; }
        public int? LowQuotaThreshold { get; set; }
        public bool? NotificationsEnabled { get; set; }
    }
}
