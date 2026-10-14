using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CodexQuota.Core.Services;
namespace CodexQuota.Desktop.Services;

/// <summary>Dispatcher-backed hover/close scheduler so timers run on the UI thread.</summary>
public sealed class DispatcherDelayScheduler : IDelayScheduler
{
    private readonly Dispatcher _dispatcher;

    public DispatcherDelayScheduler(Dispatcher? dispatcher = null) =>
        _dispatcher = dispatcher ?? Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

    public IDisposable Schedule(TimeSpan delay, Action callback)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher) { Interval = delay };
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            timer.Stop();
            if (handler is not null)
            {
                timer.Tick -= handler;
            }

            callback();
        };

        timer.Tick += handler;
        timer.Start();
        return new TimerHandle(timer, handler);
    }

    private sealed class TimerHandle : IDisposable
    {
        private readonly DispatcherTimer _timer;
        private readonly EventHandler _handler;
        private bool _disposed;

        public TimerHandle(DispatcherTimer timer, EventHandler handler)
        {
            _timer = timer;
            _handler = handler;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer.Stop();
            _timer.Tick -= _handler;
        }
    }
}

/// <summary>Applies light/dark/system palettes to application resources.</summary>
public sealed class ThemeService : IDisposable
{
    private readonly Application _application;

    public ThemeService(Application application)
    {
        _application = application;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public ThemeMode CurrentMode { get; private set; } = ThemeMode.System;

    public bool IsDark { get; private set; }

    public event Action? ThemeChanged;

    public void Apply(ThemeMode mode)
    {
        CurrentMode = mode;
        ApplyPalette(ShouldUseDark(mode));
    }

    public static bool ShouldUseDark(ThemeMode mode) => mode switch
    {
        ThemeMode.Dark => true,
        ThemeMode.Light => false,
        _ => IsSystemDark(),
    };

    /// <summary>Reads the Windows "app mode" preference; unknown values fall back to light.</summary>
    public static bool IsSystemDark()
    {
        try
        {
            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void ApplyPalette(bool dark)
    {
        IsDark = dark;
        _application.Resources["IconThemeDark"] = dark;
        foreach ((string key, string hex) in Palette(dark))
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            _application.Resources[key] = brush;
        }

        ThemeChanged?.Invoke();
    }

    private static IEnumerable<(string Key, string Hex)> Palette(bool dark)
    {
        if (dark)
        {
            return
            [
                ("WindowSurfaceBrush", "#FF1F1F23"),
                ("WindowBorderBrush", "#FF3A3A40"),
                ("InputSurfaceBrush", "#FF2A2A2F"),
                ("ButtonSurfaceBrush", "#FF2F2F35"),
                ("ButtonHoverBrush", "#FF3A3A42"),
                ("TextPrimaryBrush", "#FFF2F2F4"),
                ("TextSecondaryBrush", "#FFB4B4BC"),
                ("AccentBrush", "#FF4C8DFF"),
                ("AccentForegroundBrush", "#FFFFFFFF"),
                ("FocusBrush", "#FF8AB4FF"),
                ("SeverityGreenBrush", "#FF39CE69"),
                ("SeverityYellowBrush", "#FFFFB81C"),
                ("SeverityBlueBrush", "#FF0A84FF"),
                ("QuotaAmberTextBrush", "#FFFFB81C"),
                ("SeverityRedBrush", "#FFD9443F"),
                ("SeverityNeutralBrush", "#FF6B6B74"),
                ("BallStrokeBrush", "#FF4A4A52"),
                ("ProgressTrackBrush", "#FF3A3A40"),
                ("CardSurfaceBrush", "#FF26262B"),
                ("CardBorderBrush", "#FF3A3A42"),
                ("BallSurfaceBrush", "#FF1F1F23"),
                ("BallBorderBrush", "#FF3A3A40"),
                ("BallRingTrackBrush", "#FF3A3A40"),
                ("DockStripShellBrush", "#FFE1E8E4"),
                ("DockStripBorderBrush", "#FFA8BBB0"),
                ("DockStripTrackBrush", "#FFCAD7D0"),
                ("BadgeAmberSurfaceBrush", "#FF4A3A16"),
                ("BadgeAmberTextBrush", "#FFF0C96B"),
            ];
        }

        return
        [
            ("WindowSurfaceBrush", "#FFFFFFFF"),
            ("WindowBorderBrush", "#FFCCD6E6"),
            ("InputSurfaceBrush", "#FFF6F6F8"),
            ("ButtonSurfaceBrush", "#FFF0F0F3"),
            ("ButtonHoverBrush", "#FFE4E4E9"),
            ("TextPrimaryBrush", "#FF0C1326"),
            ("TextSecondaryBrush", "#FF616A80"),
            ("AccentBrush", "#FF0B75FF"),
            ("AccentForegroundBrush", "#FFFFFFFF"),
            ("FocusBrush", "#FF0B75FF"),
            ("SeverityGreenBrush", "#FF22B34E"),
            ("SeverityYellowBrush", "#FFFFB312"),
            ("SeverityBlueBrush", "#FF007AFF"),
            ("QuotaAmberTextBrush", "#FFDA8D06"),
            ("SeverityRedBrush", "#FFC0392B"),
            ("SeverityNeutralBrush", "#FF8A8A93"),
            ("BallStrokeBrush", "#FFD0D0D6"),
            ("ProgressTrackBrush", "#FFE5EAF2"),
            ("CardSurfaceBrush", "#FFF5F7FB"),
            ("CardBorderBrush", "#FFE8EDF5"),
            ("BallSurfaceBrush", "#FFFFFFFF"),
            ("BallBorderBrush", "#FFCCD6E6"),
            ("BallRingTrackBrush", "#FFE5EAF2"),
            ("DockStripShellBrush", "#FFF0F4F1"),
            ("DockStripBorderBrush", "#FFD3DED7"),
            ("DockStripTrackBrush", "#FFE7ECE9"),
            ("BadgeAmberSurfaceBrush", "#FFFFEDD1"),
            ("BadgeAmberTextBrush", "#FFB86408"),
        ];
    }

    private void OnUserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (CurrentMode == ThemeMode.System)
        {
            Application.Current?.Dispatcher.BeginInvoke(() => ApplyPalette(ShouldUseDark(ThemeMode.System)));
        }
    }

    public void Dispose() => Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
}
