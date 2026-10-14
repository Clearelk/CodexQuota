using System.Windows;
using System.Windows.Media;

namespace CodexQuota.Desktop.Views;

/// <summary>A native inset quota capsule with a truthful bottom-up fill and no animation.</summary>
public sealed class QuotaCapsule : FrameworkElement
{
    public static readonly DependencyProperty FractionProperty = DependencyProperty.Register(
        nameof(Fraction), typeof(double), typeof(QuotaCapsule),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShellBrushProperty = RegisterBrush(nameof(ShellBrush));
    public static readonly DependencyProperty BorderBrushProperty = RegisterBrush(nameof(BorderBrush));
    public static readonly DependencyProperty TrackBrushProperty = RegisterBrush(nameof(TrackBrush));
    public static readonly DependencyProperty FillBrushProperty = RegisterBrush(nameof(FillBrush));

    public double Fraction { get => (double)GetValue(FractionProperty); set => SetValue(FractionProperty, value); }
    public Brush ShellBrush { get => (Brush)GetValue(ShellBrushProperty); set => SetValue(ShellBrushProperty, value); }
    public Brush BorderBrush { get => (Brush)GetValue(BorderBrushProperty); set => SetValue(BorderBrushProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public Brush FillBrush { get => (Brush)GetValue(FillBrushProperty); set => SetValue(FillBrushProperty, value); }

    private static DependencyProperty RegisterBrush(string name) => DependencyProperty.Register(
        name, typeof(Brush), typeof(QuotaCapsule),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (ActualWidth <= 0 || ActualHeight <= 0) { return; }
        double scale = ActualWidth / 14d;
        var shell = new Rect(.5 * scale, .5 * scale, ActualWidth - scale, ActualHeight - scale);
        Brush shellBrush = ShellBrush;
        if (ShellBrush is SolidColorBrush solid)
        {
            var shaded = new LinearGradientBrush(Colors.White, solid.Color, new Point(0, 0), new Point(1, 1));
            shaded.Freeze();
            shellBrush = shaded;
        }
        drawingContext.DrawRoundedRectangle(shellBrush, new Pen(BorderBrush, .6 * scale), shell, shell.Width / 2, shell.Width / 2);

        var track = new Rect(3 * scale, 4 * scale, ActualWidth - 6 * scale, ActualHeight - 8 * scale);
        if (track.Width <= 0 || track.Height <= 0) { return; }
        double radius = track.Width / 2;
        drawingContext.DrawRoundedRectangle(TrackBrush, null, track, radius, radius);
        double fraction = double.IsFinite(Fraction) ? Math.Clamp(Fraction, 0, 1) : 0;
        if (fraction <= 0) { return; }

        // Draw caps in the actual short segment's coordinates, without stretching a full
        // capsule or inventing a minimum amount. The inset clip preserves the frame at 100%.
        double height = track.Height * fraction;
        var fill = new Rect(track.Left, track.Bottom - height, track.Width, height);
        double fillRadius = Math.Min(radius, height / 2);
        drawingContext.PushClip(new RectangleGeometry(track, radius, radius));
        drawingContext.DrawRoundedRectangle(FillBrush, null, fill, fillRadius, fillRadius);
        drawingContext.Pop();
    }
}
