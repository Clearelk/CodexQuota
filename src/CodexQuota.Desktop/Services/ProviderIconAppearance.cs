using System.Windows;
using System.Windows.Media;
using CodexQuota.Core.Models;

namespace CodexQuota.Desktop.Services;

/// <summary>Reserved visual identities only; this list does not register provider integrations.</summary>
internal sealed record ProviderIconAppearance(string Name, string Symbol, string LightAccent, string DarkAccent, string? AccentEnd, string LightSymbol)
{
    public static ProviderIconAppearance Codex { get; } = new("Codex", "C", "#FFCF37", "#FFD64A", "#D98C05", "#E58A0A");
    public static ProviderIconAppearance Claude { get; } = new("Claude", "A", "#E9A079", "#F1B189", "#BB6840", "#D38150");
    public static ProviderIconAppearance Gemini { get; } = new("Gemini", "G", "#2786FF", "#64A6FF", "#8E5ADF", "#1478F5");
    public static ProviderIconAppearance Grok { get; } = new("Grok", "X", "#515A67", "#E1E5EE", null, "#343B46");
    public static IReadOnlyList<ProviderIconAppearance> PreviewStyles { get; } = [Codex, Claude, Gemini, Grok];

    public Color Accent(bool dark) => Parse(dark ? DarkAccent : LightAccent);
    public Color End(bool dark) => AccentEnd is { } end ? Parse(end) : Mix(Accent(dark), Colors.Black, dark ? 0.35 : 0.45);
    public Color SymbolColor(bool dark) => dark ? Accent(true) : Parse(LightSymbol);

    public Brush AccentBrush(bool dark)
    {
        Color accent = Accent(dark);
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        brush.GradientStops.Add(new GradientStop(Mix(accent, Colors.White, 0.28), 0));
        brush.GradientStops.Add(new GradientStop(accent, 0.28));
        brush.GradientStops.Add(new GradientStop(Mix(accent, End(dark), 0.35), 0.55));
        brush.GradientStops.Add(new GradientStop(End(dark), 1));
        brush.Freeze();
        return brush;
    }

    internal static Color Parse(string value) => (Color)ColorConverter.ConvertFromString(value);

    internal static Color Mix(Color color, Color target, double fraction) => Color.FromArgb(color.A,
        (byte)Math.Round(color.R + (target.R - color.R) * fraction),
        (byte)Math.Round(color.G + (target.G - color.G) * fraction),
        (byte)Math.Round(color.B + (target.B - color.B) * fraction));
}

/// <summary>The same normalized presentation input drives WPF and the notification-area icon.</summary>
internal readonly record struct ProviderIconState(ProviderIconAppearance Appearance, int? RemainingPercent, RefreshStatus Status, bool IsDark)
{
    public Brush ArcBrush => Status == RefreshStatus.Fresh ? Appearance.AccentBrush(IsDark) : ResourceBrush("SeverityNeutralBrush");
    public Brush SymbolBrush
    {
        get
        {
            Color color = Appearance.SymbolColor(IsDark);
            var brush = new LinearGradientBrush(ProviderIconAppearance.Mix(color, Colors.White, 0.08), ProviderIconAppearance.Mix(color, Colors.Black, IsDark ? 0.20 : 0.28), new Point(0, 0), new Point(0, 1));
            brush.Freeze();
            return brush;
        }
    }

    public Color SurfaceTop => ProviderIconAppearance.Parse(IsDark ? "#47515F" : "#FFFFFF");
    public Color SurfaceBottom => ProviderIconAppearance.Parse(IsDark ? "#1B222C" : "#E2E9F2");
    public Brush SurfaceBrush => VerticalBrush(SurfaceTop, SurfaceBottom);
    public Brush InnerRimBrush => VerticalBrush(Color.FromArgb(IsDark ? (byte)110 : (byte)190, 255, 255, 255), Color.FromArgb(IsDark ? (byte)150 : (byte)80, 12, 22, 36));
    public Brush TrackBrush => ShadeBrush(ResourceColor("BallRingTrackBrush"), 0.10, 0.05);
    public Brush BadgeBrush => ShadeBrush(ResourceColor(DotKey), 0.18, 0.10);
    public Brush BadgeSeparatorBrush => VerticalBrush(Colors.White, ProviderIconAppearance.Parse("#F3F6FA"));
    public Brush BadgeHighlightBrush
    {
        get
        {
            var brush = new RadialGradientBrush(Color.FromArgb(38, 255, 255, 255), Colors.Transparent);
            brush.Freeze();
            return brush;
        }
    }

    private string DotKey => Status switch
    {
        RefreshStatus.Fresh => "SeverityGreenBrush",
        RefreshStatus.Stale => "SeverityYellowBrush",
        RefreshStatus.Error => "SeverityRedBrush",
        _ => "SeverityNeutralBrush",
    };

    public Brush DotBrush => ResourceBrush(DotKey);

    private static Brush ShadeBrush(Color color, double light, double dark)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        brush.GradientStops.Add(new GradientStop(ProviderIconAppearance.Mix(color, Colors.White, light), 0));
        brush.GradientStops.Add(new GradientStop(color, 0.5));
        brush.GradientStops.Add(new GradientStop(ProviderIconAppearance.Mix(color, Colors.Black, dark), 1));
        brush.Freeze();
        return brush;
    }

    private static Brush VerticalBrush(Color top, Color bottom)
    {
        var brush = new LinearGradientBrush(top, bottom, new Point(0, 0), new Point(0, 1));
        brush.Freeze();
        return brush;
    }

    private static Color ResourceColor(string key) => ResourceBrush(key) is SolidColorBrush solid ? solid.Color : Colors.Gray;

    internal static bool CurrentThemeIsDark => Application.Current?.TryFindResource("IconThemeDark") is true;
    internal static Brush ResourceBrush(string key) => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
}
