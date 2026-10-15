using CodexQuota.Core.Models;
using CodexQuota.Core.Services;

namespace CodexQuota.Desktop.ViewModels;

/// <summary>Updates option text independently of the stable selected item.</summary>
public abstract class SettingsOption(string label) : ObservableObject
{
    private string _label = label;

    public string Label
    {
        get => _label;
        internal set
        {
            if (_label == value) { return; }
            _label = value;
            Raise();
        }
    }
}

public sealed class ThemeOption(ThemeMode mode, string label) : SettingsOption(label)
{
    public ThemeMode Mode { get; } = mode;
}

public sealed class PlacementOption(BallPlacement placement, string label) : SettingsOption(label)
{
    public BallPlacement Placement { get; } = placement;
}

public sealed class IntervalOption(int seconds, string label) : SettingsOption(label)
{
    public int Seconds { get; } = seconds;
}

public sealed record LanguageOption(AppLanguage Language, string Label);

/// <summary>
/// Settings editing. Every editable value — including the start-at-login choice — is a
/// draft until the caller completes a Save; the registry is only touched by the
/// submitted Save path, and the verified registration (not the checkbox) stays the
/// truth that a failed write must reflect. Persisted settings never contain quota or
/// account data.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly IStartupRegistration _startup;
    private readonly SettingsStore _store;
    private AppLanguage _language;
    private LanguageOption _selectedLanguage = AvailableLanguages[0];
    private AppMessage? _startupCopy;
    private AppMessage? _statusCopy;
    private AppMessage? _cliCopy;

    private static IReadOnlyList<LanguageOption> AvailableLanguages { get; } =
    [new(AppLanguage.SimplifiedChinese, "简体中文"), new(AppLanguage.English, "English")];
    public IReadOnlyList<LanguageOption> LanguageOptions => AvailableLanguages;

    public LanguageOption SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (value is null || value.Language == _selectedLanguage.Language) { return; }
            _selectedLanguage = value;
            Raise();
        }
    }

    private string Text(string key, params object?[] arguments) => AppText.Format(_language, key, arguments);

    private string _executablePath = string.Empty;
    private double _opacityPercent = 95;
    private int _intervalSeconds = WidgetSettings.DefaultIntervalSeconds;
    private ThemeOption _selectedTheme;
    private string _codexCliPath = string.Empty;
    private bool _startAtLogin;
    private bool _startupPending;
    private bool? _startupVerified;
    private string _cliStatusText = string.Empty;
    private string _statusMessage = string.Empty;
    private int _ballSizePercent = 100;
    private int _lowQuotaThreshold = 20;
    private bool _showRemaining = true, _showResetTime = true, _showTodayUsage = true, _notificationsEnabled;
    private PlacementOption _selectedPlacement;
    private BallDockSide _dockSide = BallDockSide.None;
    private readonly IReadOnlyList<IntervalOption> _presetIntervalOptions =
        [new(30, string.Empty), new(60, string.Empty), new(120, string.Empty), new(300, string.Empty), new(600, string.Empty), new(900, string.Empty)];
    private IReadOnlyList<IntervalOption> _intervalOptions;

    public IReadOnlyList<PlacementOption> PlacementOptions { get; } =
        [new(BallPlacement.Custom, string.Empty), new(BallPlacement.TopLeft, string.Empty), new(BallPlacement.TopRight, string.Empty),
            new(BallPlacement.BottomLeft, string.Empty), new(BallPlacement.BottomRight, string.Empty)];

    public IReadOnlyList<IntervalOption> IntervalOptions => _intervalOptions;

    private string DescribeInterval(int seconds) => seconds % 60 == 0
        ? Text("IntervalMinutes", seconds / 60)
        : Text("IntervalSeconds", seconds);

    private void RelabelIntervalOptions()
    {
        foreach (IntervalOption option in IntervalOptions)
        {
            string description = DescribeInterval(option.Seconds);
            option.Label = option.Seconds == WidgetSettings.DefaultIntervalSeconds
                ? Text("IntervalRecommended", description)
                : _presetIntervalOptions.Any(preset => preset.Seconds == option.Seconds)
                    ? description : Text("IntervalCurrent", description);
        }
    }

    private void UpdateIntervalOptions()
    {
        if (_presetIntervalOptions.Any(option => option.Seconds == _intervalSeconds))
        {
            _intervalOptions = _presetIntervalOptions;
        }
        else if (!_intervalOptions.Any(option => option.Seconds == _intervalSeconds))
        {
            _intervalOptions = [.. _presetIntervalOptions, new IntervalOption(_intervalSeconds, string.Empty)];
        }

        RelabelIntervalOptions();
    }

    public IntervalOption SelectedInterval
    {
        get => IntervalOptions.First(option => option.Seconds == _intervalSeconds);
        set { if (value is not null && value.Seconds != _intervalSeconds) { IntervalSeconds = value.Seconds; } }
    }

    public PlacementOption SelectedPlacement
    {
        get => _selectedPlacement;
        set
        {
            if (value is null) { return; }
            PlacementOption option = PlacementOptions.Single(candidate => candidate.Placement == value.Placement);
            if (ReferenceEquals(option, _selectedPlacement)) { return; }
            _selectedPlacement = option;
            Raise();
        }
    }
    public int BallSizePercent { get => _ballSizePercent; set { _ballSizePercent = Math.Clamp(value, 75, 150); Raise(); Raise(nameof(BallSizeCaption)); } }
    public int LowQuotaThreshold { get => _lowQuotaThreshold; set { _lowQuotaThreshold = Math.Clamp(value, 1, 100); Raise(); Raise(nameof(LowQuotaThresholdCaption)); } }
    public string BallSizeCaption => Text(BallSizePercent < 100 ? "SizeSmall" : BallSizePercent > 120 ? "SizeLarge" : "SizeMedium");
    public string LowQuotaThresholdCaption => Text("ThresholdCaption", LowQuotaThreshold);
    public bool ShowRemaining { get => _showRemaining; set { _showRemaining = value; Raise(); } }
    public bool ShowResetTime { get => _showResetTime; set { _showResetTime = value; Raise(); } }
    public bool ShowTodayUsage { get => _showTodayUsage; set { _showTodayUsage = value; Raise(); } }
    public bool NotificationsEnabled { get => _notificationsEnabled; set { _notificationsEnabled = value; Raise(); } }

    public SettingsViewModel(IStartupRegistration startup, SettingsStore store)
    {
        _startup = startup;
        _store = store;
        _selectedTheme = ThemeOptions[0];
        _selectedPlacement = PlacementOptions[0];
        _intervalOptions = _presetIntervalOptions;
        ApplyLanguage(AppLanguage.SimplifiedChinese);
    }

    public IReadOnlyList<ThemeOption> ThemeOptions { get; } =
        [new(ThemeMode.System, string.Empty), new(ThemeMode.Light, string.Empty), new(ThemeMode.Dark, string.Empty)];

    /// <summary>Relabels committed presentation without changing selections, newer drafts or invalid input text.</summary>
    public void ApplyLanguage(AppLanguage language)
    {
        _language = language;
        foreach (ThemeOption option in ThemeOptions) { option.Label = Text("Theme" + option.Mode); }
        foreach (PlacementOption option in PlacementOptions) { option.Label = Text("Position" + option.Placement); }
        RelabelIntervalOptions();
        foreach (string property in new[] { nameof(BallSizeCaption), nameof(LowQuotaThresholdCaption),
            nameof(StartupStatusText), nameof(StatusMessage), nameof(CliStatusText), nameof(SettingsPathText) }) { Raise(property); }
    }

    public double OpacityPercent
    {
        get => _opacityPercent;
        set
        {
            if (Math.Abs(_opacityPercent - value) < 0.01)
            {
                return;
            }

            _opacityPercent = value;
            Raise();
        }
    }

    public int IntervalSeconds
    {
        get => _intervalSeconds;
        set
        {
            if (_intervalSeconds == value)
            {
                return;
            }

            _intervalSeconds = value;
            UpdateIntervalOptions();
            Raise();
            Raise(nameof(IntervalOptions));
            Raise(nameof(SelectedInterval));
        }
    }

    public ThemeOption SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (value is null) { return; }
            ThemeOption option = ThemeOptions.Single(candidate => candidate.Mode == value.Mode);
            if (ReferenceEquals(option, _selectedTheme)) { return; }
            _selectedTheme = option;
            Raise();
        }
    }

    public string CodexCliPath
    {
        get => _codexCliPath;
        set
        {
            if (_codexCliPath == value)
            {
                return;
            }

            _codexCliPath = value;
            Raise();
        }
    }

    /// <summary>
    /// Draft start-at-login choice. Toggling only records intent; the submitted Save
    /// performs the real registry operation and verifies the actual outcome.
    /// </summary>
    public bool StartAtLogin
    {
        get => _startAtLogin;
        set
        {
            if (_startAtLogin == value)
            {
                return;
            }

            _startAtLogin = value;
            _startupPending = _startupVerified is null || _startupVerified != value;
            Raise();
        }
    }

    /// <summary>True when the draft startup choice still differs from the verified registry truth.</summary>
    public bool HasPendingStartupChange => _startupPending;

    /// <summary>Draft side-edge docking; None keeps the ordinary floating ball.</summary>
    public BallDockSide DockSide
    {
        get => _dockSide;
        set
        {
            if (_dockSide == value)
            {
                return;
            }

            _dockSide = value;
            Raise();
        }
    }

    public string StartupStatusText => _startupCopy?.Format(_language) ?? string.Empty;
    public string CliStatusText => _cliCopy?.Format(_language) ?? _cliStatusText;
    public string StatusMessage => _statusCopy?.Format(_language) ?? _statusMessage;
    public string SettingsPathText => Text("SettingsPath", _store.SettingsPath);

    private void SetStatusMessage(AppMessage? message)
    {
        _statusCopy = message;
        _statusMessage = string.Empty;
        Raise(nameof(StatusMessage));
    }

    private void SetStartupMessage(AppMessage message)
    {
        _startupCopy = message;
        SetStatusMessage(message);
        Raise(nameof(StartupStatusText));
    }

    public WidgetSettings ToSettings() => SettingsStore.Normalize(new WidgetSettings(
        WidgetSettings.CurrentVersion,
        OpacityPercent / 100.0,
        SelectedTheme.Mode,
        IntervalSeconds,
        PositionLeft,
        PositionTop,
        CodexCliPath)
    {
        Language = SelectedLanguage.Language,
        Placement = SelectedPlacement.Placement,
        DockSide = DockSide,
        BallSizePercent = BallSizePercent,
        ShowRemaining = ShowRemaining,
        ShowResetTime = ShowResetTime,
        ShowTodayUsage = ShowTodayUsage,
        LowQuotaThreshold = LowQuotaThreshold,
        NotificationsEnabled = NotificationsEnabled,
    });

    /// <summary>Position carried through unchanged; the ball owns the saved values.</summary>
    public double? PositionLeft { get; set; }

    /// <summary>Position carried through unchanged; the ball owns the saved values.</summary>
    public double? PositionTop { get; set; }

    public void LoadFrom(WidgetSettings settings, string executablePath, string cliStatusText, AppMessage? cliMessage = null)
    {
        _executablePath = executablePath;
        LoadDraftValues(settings);
        AppMessage startupStatus;
        try
        {
            _startAtLogin = _startup.IsEnabled();
            _startupVerified = _startAtLogin;
            _startupPending = false;
            startupStatus = new AppMessage(_startAtLogin ? "StartupEnabled" : "StartupDisabled");
        }
        catch (Exception)
        {
            _startupVerified = null;
            _startupPending = false;
            startupStatus = new AppMessage("StartupReadFailure");
        }

        _cliStatusText = cliStatusText;
        _cliCopy = cliMessage;
        _startupCopy = startupStatus;
        _statusCopy = _store.LastWarningMessage ?? (_startupVerified is null ? startupStatus : null);
        _statusMessage = string.Empty;
        RaiseAll();
    }

    private void LoadDraftValues(WidgetSettings settings)
    {
        _selectedLanguage = LanguageOptions.Single(option => option.Language == settings.Language);
        PositionLeft = settings.BallLeft;
        PositionTop = settings.BallTop;

        _opacityPercent = Math.Round(settings.Opacity * 100);
        _intervalSeconds = settings.RefreshIntervalSeconds;
        UpdateIntervalOptions();
        _codexCliPath = settings.CodexCliPath ?? string.Empty;
        _selectedTheme = ThemeOptions.FirstOrDefault(option => option.Mode == settings.Theme) ?? ThemeOptions[0];
        _selectedPlacement = PlacementOptions.First(option => option.Placement == settings.Placement);
        _dockSide = settings.DockSide;
        _ballSizePercent = settings.BallSizePercent;
        _showRemaining = settings.ShowRemaining;
        _showResetTime = settings.ShowResetTime;
        _showTodayUsage = settings.ShowTodayUsage;
        _lowQuotaThreshold = settings.LowQuotaThreshold;
        _notificationsEnabled = settings.NotificationsEnabled;
    }

    public void UpdateCliStatus(string text, AppMessage? message = null)
    {
        _cliStatusText = text;
        _cliCopy = message;
        Raise(nameof(CliStatusText));
    }

    /// <summary>
    /// Resets drafts, including startup intent, while keeping the current ball position.
    /// No registry operation occurs until the submitted Save applies the whole draft.
    /// </summary>
    public void RestoreDefaults()
    {
        LoadDraftValues(WidgetSettings.Default with { BallLeft = PositionLeft, BallTop = PositionTop });
        _startAtLogin = false;
        _startupPending = _startupVerified is null || _startupVerified != false;
        SetStatusMessage(new AppMessage("DefaultsDrafted"));
        RaiseAll();
    }

    /// <summary>Ordinary complete Save: file and (when requested) startup both succeeded.</summary>
    public void MarkSaved()
    {
        SetStatusMessage(new AppMessage("Saved"));
    }

    public void ReportSavedWithPendingEdits() => SetStatusMessage(new AppMessage("SavedPendingEdits"));

    /// <summary>The settings file could not be written; nothing was applied.</summary>
    public void ReportSaveFailure() => SetStatusMessage(new AppMessage("SaveFailure"));

    /// <summary>An invalid uncommitted threshold blocked the submitted Save.</summary>
    public void ReportThresholdBlocked() => SetStatusMessage(new AppMessage("ThresholdBlocked"));

    /// <summary>
    /// JSON succeeded but the requested startup change did not: the file values really
    /// are retained while the registry keeps its verified actual value.
    /// </summary>
    public void ReportStartupPartialFailure(bool actualEnabled)
    {
        _startupVerified = actualEnabled;
        _startupPending = _startAtLogin != actualEnabled;
        SetStartupMessage(new AppMessage(actualEnabled ? "StartupDisableFailure" : "StartupEnableFailure"));
        RaiseAll();
    }

    /// <summary>
    /// Applies the submitted startup intent for real and verifies the registry outcome.
    /// Returns the verified actual enablement, or null when the truth cannot be read.
    /// </summary>
    public bool? ApplyStartupIntent(bool enabled)
    {
        try
        {
            if (enabled)
            {
                if (!string.IsNullOrWhiteSpace(_executablePath))
                {
                    _startup.Enable(StartupCommandLine.Quote(_executablePath));
                }
            }
            else { _startup.Disable(); }
        }
        catch (Exception) { /* Verification below reports the actual outcome. */ }
        bool? verified = ReadVerified();
        _startupVerified = verified;
        _startupPending = verified is null || _startAtLogin != verified;
        if (verified is null)
        {
            SetStartupMessage(new AppMessage("StartupVerifyFailure"));
        }
        else if (verified != enabled)
        {
            SetStartupMessage(new AppMessage(verified.Value ? "StartupDisableFailure" : "StartupEnableFailure"));
        }
        Raise(nameof(HasPendingStartupChange));
        return verified;
    }

    private bool? ReadVerified()
    {
        try
        {
            return _startup.IsEnabled();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
