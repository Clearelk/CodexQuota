using System.Windows;
using System.Windows.Media;

namespace CodexQuota.Desktop.Views;

/// <summary>
/// A small vector progress ring: a neutral track plus a rounded remaining arc drawn in
/// device-independent units. It is theme-driven through bound brushes, has no animation,
/// and allocates no bitmap assets. Zero draws no arc and one hundred draws a full circle.
/// </summary>
public sealed class ProgressRing : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(double),
        typeof(ProgressRing),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HasValueProperty = DependencyProperty.Register(
        nameof(HasValue),
        typeof(bool),
        typeof(ProgressRing),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush),
        typeof(Brush),
        typeof(ProgressRing),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ArcBrushProperty = DependencyProperty.Register(
        nameof(ArcBrush),
        typeof(Brush),
        typeof(ProgressRing),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RingThicknessProperty = DependencyProperty.Register(
        nameof(RingThickness),
        typeof(double),
        typeof(ProgressRing),
        new FrameworkPropertyMetadata(5d, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Remaining percentage in the range 0-100.</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>When false only the neutral track is drawn, so unknown data never shows an arc.</summary>
    public bool HasValue
    {
        get => (bool)GetValue(HasValueProperty);
        set => SetValue(HasValueProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush ArcBrush
    {
        get => (Brush)GetValue(ArcBrushProperty);
        set => SetValue(ArcBrushProperty, value);
    }

    public double RingThickness
    {
        get => (double)GetValue(RingThicknessProperty);
        set => SetValue(RingThicknessProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        double size = Math.Min(ActualWidth, ActualHeight);
        double thickness = Math.Max(1d, RingThickness);
        double radius = (size - thickness) / 2d;
        if (radius <= 0)
        {
            return;
        }

        var center = new Point(ActualWidth / 2d, ActualHeight / 2d);
        var trackPen = new Pen(MapBrushToCircle(TrackBrush), thickness);
        if (trackPen.CanFreeze)
        {
            trackPen.Freeze();
        }

        drawingContext.DrawEllipse(null, trackPen, center, radius, radius);

        if (!HasValue)
        {
            return;
        }

        double percent = Math.Clamp(Value, 0d, 100d);
        if (percent <= 0d)
        {
            return;
        }

        var arcPen = new Pen(MapBrushToCircle(ArcBrush), thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (arcPen.CanFreeze)
        {
            arcPen.Freeze();
        }

        if (percent >= 100d)
        {
            drawingContext.DrawEllipse(null, arcPen, center, radius, radius);
            return;
        }

        const double startAngle = -90d;
        double sweep = 360d * percent / 100d;
        Point start = PointOnCircle(center, radius, startAngle);
        Point end = PointOnCircle(center, radius, startAngle + sweep);

        var figure = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
        figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0d, sweep > 180d, SweepDirection.Clockwise, true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        drawingContext.DrawGeometry(null, arcPen, geometry);
    }

    private static Point PointOnCircle(Point center, double radius, double angleDegrees)
    {
        double radians = angleDegrees * Math.PI / 180d;
        return new Point(center.X + (radius * Math.Cos(radians)), center.Y + (radius * Math.Sin(radians)));
    }

    private Brush MapBrushToCircle(Brush brush)
    {
        if (brush is not LinearGradientBrush { MappingMode: BrushMappingMode.RelativeToBoundingBox } gradient)
        {
            return brush;
        }

        // Bind the gradient to the full circle, never to a partial arc's changing bounds.
        LinearGradientBrush mapped = gradient.CloneCurrentValue();
        mapped.MappingMode = BrushMappingMode.Absolute;
        mapped.StartPoint = new Point(gradient.StartPoint.X * ActualWidth, gradient.StartPoint.Y * ActualHeight);
        mapped.EndPoint = new Point(gradient.EndPoint.X * ActualWidth, gradient.EndPoint.Y * ActualHeight);
        mapped.Freeze();
        return mapped;
    }
}
