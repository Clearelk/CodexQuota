namespace CodexQuota.Core.Services;

/// <summary>A monitor work area expressed in device-independent pixels.</summary>
public sealed record MonitorWorkArea(string Id, double Left, double Top, double Width, double Height, bool IsPrimary)
{
    public double Right => Left + Width;

    public double Bottom => Top + Height;

    public double CenterX => Left + (Width / 2);

    public double CenterY => Top + (Height / 2);
}

/// <summary>Supplies current monitor work areas in DIPs. Injected so placement is testable.</summary>
public interface IMonitorProvider
{
    IReadOnlyList<MonitorWorkArea> GetWorkAreas();

    /// <summary>Work area containing (or nearest to) a physical-pixel point, used for panel placement.</summary>
    MonitorWorkArea? GetWorkAreaAt(double dipX, double dipY);
}

/// <summary>
/// Pure placement math for the ball and details panel. Windows integration converts
/// monitor geometry to DIPs at one boundary; the clamping rules live here so they can
/// be verified with negative coordinates, removed monitors and oversized panels.
/// </summary>
public static class MonitorPlacement
{
    /// <summary>Initial margin from the bottom/right edges of the primary work area.</summary>
    public const double EdgeMargin = 16;

    /// <summary>Bottom-right placement of the ball on first run.</summary>
    public static (double Left, double Top) GetInitialPosition(MonitorWorkArea primaryWorkArea, double width, double height) =>
        (primaryWorkArea.Right - width - EdgeMargin, primaryWorkArea.Bottom - height - EdgeMargin);

    /// <summary>
    /// Clamps a rectangle into the most relevant work area. If no work area contains
    /// the rectangle (for example the saved monitor was removed), the nearest work
    /// area is used. Oversized rectangles are aligned to the area's left/top edge.
    /// </summary>
    public static (double Left, double Top) Clamp(
        double left,
        double top,
        double width,
        double height,
        IReadOnlyList<MonitorWorkArea> workAreas)
    {
        if (workAreas.Count == 0)
        {
            return (left, top);
        }

        MonitorWorkArea target = SelectTarget(left, top, width, height, workAreas);

        double maxLeft = Math.Max(target.Left, target.Right - width);
        double maxTop = Math.Max(target.Top, target.Bottom - height);

        return (Math.Clamp(left, target.Left, maxLeft), Math.Clamp(top, target.Top, maxTop));
    }

    /// <summary>
    /// Places a panel next to the ball, preferring the left side (the ball lives in the
    /// lower-right corner) and falling back to the right when there is no room.
    /// </summary>
    public static (double Left, double Top) PlacePanelBeside(
        double ballLeft,
        double ballTop,
        double ballSize,
        double panelWidth,
        double panelHeight,
        MonitorWorkArea workArea)
    {
        double gap = 8;
        double left = ballLeft - panelWidth - gap;
        if (left < workArea.Left)
        {
            left = ballLeft + ballSize + gap;
        }

        return Clamp(left, ballTop, panelWidth, panelHeight, [workArea]);
    }

    private static MonitorWorkArea SelectTarget(
        double left,
        double top,
        double width,
        double height,
        IReadOnlyList<MonitorWorkArea> workAreas)
    {
        MonitorWorkArea? best = null;
        double bestIntersection = 0;

        foreach (MonitorWorkArea area in workAreas)
        {
            double intersectionWidth = Math.Min(left + width, area.Right) - Math.Max(left, area.Left);
            double intersectionHeight = Math.Min(top + height, area.Bottom) - Math.Max(top, area.Top);
            double intersection = Math.Max(0, intersectionWidth) * Math.Max(0, intersectionHeight);

            if (intersection > bestIntersection)
            {
                bestIntersection = intersection;
                best = area;
            }
        }

        if (best is not null)
        {
            return best;
        }

        // No overlap: fall back to the nearest work area by center distance.
        double centerX = left + (width / 2);
        double centerY = top + (height / 2);
        MonitorWorkArea nearest = workAreas[0];
        double nearestDistance = double.MaxValue;

        foreach (MonitorWorkArea area in workAreas)
        {
            double dx = centerX - area.CenterX;
            double dy = centerY - area.CenterY;
            double distance = (dx * dx) + (dy * dy);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = area;
            }
        }

        return nearest;
    }
}
