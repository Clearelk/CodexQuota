using CodexQuota.Core.Models;

namespace CodexQuota.Core.Services;

/// <summary>A physical-pixel rectangle in the Windows virtual-screen space.</summary>
public readonly record struct PixelRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;

    public int Bottom => Top + Height;

    public double CenterX => Left + (Width / 2.0);

    public double CenterY => Top + (Height / 2.0);

    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;

    public bool IntersectsVertical(PixelRect other, int tolerance) =>
        Top - tolerance < other.Bottom && other.Top - tolerance < Bottom;
}

/// <summary>
/// One attached display in physical pixels: the full bounds (used for shared-edge
/// adjacency, i.e. free monitor-to-monitor passage) and the work area (used for
/// reachable strip/ball placement, i.e. without covering the taskbar).
/// </summary>
public sealed record DockMonitor(string Id, PixelRect Bounds, PixelRect WorkArea, bool IsPrimary);

/// <summary>
/// Pure left/right edge-docking decisions. A deliberate drag release with a quarter or
/// more of the full ball beyond an *exposed* left/right work-area edge docks the ball as
/// a slim strip at that edge; top/bottom boundaries and shared monitor passages stay
/// ordinary clamp/free-drag boundaries. The original half-diameter rule was deliberately
/// relaxed to a quarter in 2026-10 so a lighter deliberate drag still docks.
/// </summary>
public static class DockGeometry
{
    /// <summary>Native rounding tolerance in physical pixels.</summary>
    public const int PixelTolerance = 1;

    /// <summary>
    /// Fraction of the full-ball diameter that must cross an exposed left/right work-area
    /// edge on drag release to dock; the boundary itself docks. Deliberately relaxed from
    /// the original half-diameter contract in 2026-10 — set back to 0.5 to restore the old
    /// feel. Only the fraction changes here; exposure rules and strip geometry are untouched.
    /// </summary>
    public const double DockCrossFraction = 0.25;

    public static bool CanRestoreDock(PixelRect savedAnchor, BallDockSide side, IReadOnlyList<DockMonitor> monitors)
    {
        if (side is not (BallDockSide.Left or BallDockSide.Right) || savedAnchor.Width <= 0 || savedAnchor.Height <= 0) { return false; }
        DockMonitor? home = monitors.FirstOrDefault(monitor => monitor.Bounds.Contains((int)savedAnchor.CenterX, (int)savedAnchor.CenterY));
        if (home is null || home.WorkArea.Width < savedAnchor.Width || home.WorkArea.Height < savedAnchor.Height) { return false; }
        int top = Math.Clamp(savedAnchor.Top, home.WorkArea.Top, home.WorkArea.Bottom - savedAnchor.Height);
        return IsSideExposed(home, monitors, savedAnchor with { Top = top }, side == BallDockSide.Left);
    }

    /// <summary>
    /// Classifies a drag-release rectangle. Returns the eligible exposed side when at
    /// least <see cref="DockCrossFraction"/> (a quarter) of the diameter lies beyond the
    /// home monitor's left/right work-area edge — the boundary itself docks, within one
    /// physical-pixel tolerance — or <see cref="BallDockSide.None"/> for an ordinary
    /// release. The home monitor is
    /// the work area containing the ball center (nearest by center distance as fallback)
    /// so an extreme offscreen move cannot select an unrelated display.
    /// </summary>
    public static BallDockSide EvaluateRelease(PixelRect ball, IReadOnlyList<DockMonitor> monitors)
    {
        if (monitors.Count == 0 || ball.Width <= 0)
        {
            return BallDockSide.None;
        }

        DockMonitor? home = null;
        long bestArea = -1;
        long bestDistance = long.MaxValue;
        foreach (DockMonitor monitor in monitors)
        {
            long overlapWidth = Math.Min(ball.Right, monitor.WorkArea.Right) - Math.Max(ball.Left, monitor.WorkArea.Left);
            long overlapHeight = Math.Min(ball.Bottom, monitor.WorkArea.Bottom) - Math.Max(ball.Top, monitor.WorkArea.Top);
            long overlap = Math.Max(0, overlapWidth) * Math.Max(0, overlapHeight);
            if (overlap > bestArea)
            {
                bestArea = overlap;
                home = monitor;
            }
        }

        if (bestArea <= 0)
        {
            double centerX = ball.CenterX;
            double centerY = ball.CenterY;
            foreach (DockMonitor monitor in monitors)
            {
                // Distance to the rectangle keeps a top/bottom-only gesture on its
                // horizontal display; display-center distance can choose a shorter neighbor.
                double dx = centerX - Math.Clamp(centerX, monitor.WorkArea.Left, monitor.WorkArea.Right);
                double dy = centerY - Math.Clamp(centerY, monitor.WorkArea.Top, monitor.WorkArea.Bottom);
                long distance = (long)((dx * dx) + (dy * dy));
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    home = monitor;
                }
            }
        }

        if (home is null)
        {
            return BallDockSide.None;
        }

        double cross = ball.Width * DockCrossFraction;
        bool leftCrossed = ball.Left + cross <= home.WorkArea.Left + PixelTolerance;
        bool rightCrossed = ball.Right - cross >= home.WorkArea.Right - PixelTolerance;
        bool leftExposed = leftCrossed && IsSideExposed(home, monitors, ball, toLeft: true);
        bool rightExposed = rightCrossed && IsSideExposed(home, monitors, ball, toLeft: false);

        return (leftExposed, rightExposed) switch
        {
            (true, true) => ball.CenterX <= home.WorkArea.CenterX ? BallDockSide.Left : BallDockSide.Right,
            (true, false) => BallDockSide.Left,
            (false, true) => BallDockSide.Right,
            _ => BallDockSide.None,
        };
    }

    /// <summary>
    /// True when nothing adjoins the home monitor's edge across the ball's vertical span,
    /// so a strip there would be genuinely visible instead of blocking a passage into a
    /// neighboring display. Adjacency uses physical monitor bounds; vertical staggering
    /// keeps partly shared and partly exposed segments independent.
    /// </summary>
    public static bool IsSideExposed(DockMonitor home, IReadOnlyList<DockMonitor> monitors, PixelRect ball, bool toLeft)
    {
        int edge = toLeft ? home.Bounds.Left : home.Bounds.Right;
        foreach (DockMonitor other in monitors)
        {
            if (ReferenceEquals(other, home) || other.Bounds.Width <= 0 || other.Bounds.Height <= 0)
            {
                continue;
            }

            int otherEdge = toLeft ? other.Bounds.Right : other.Bounds.Left;
            bool touches = Math.Abs(otherEdge - edge) <= PixelTolerance ||
                (toLeft ? other.Bounds.Right > edge && other.Bounds.Left < edge : other.Bounds.Left < edge && other.Bounds.Right > edge);
            if (touches && other.Bounds.IntersectsVertical(ball, PixelTolerance))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Clamps the canonical full-ball vertical anchor (DIP top) so both the revealed
    /// full ball and the centered collapsed strip stay reachable inside the work area.
    /// Returns false when the work area cannot host the ball at all.
    /// </summary>
    public static bool ClampDockAnchor(double anchorTop, double diameter, double stripHeight, MonitorWorkArea area, out double clampedTop)
    {
        double halfConstraint = Math.Max(diameter, stripHeight) / 2;
        double center = anchorTop + (diameter / 2);
        double clampedCenter = Math.Clamp(center, area.Top + halfConstraint, Math.Max(area.Top + halfConstraint, area.Bottom - halfConstraint));
        clampedTop = clampedCenter - (diameter / 2);
        return diameter <= area.Height && stripHeight <= area.Height;
    }
}
