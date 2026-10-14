using System.Runtime.InteropServices;
using CodexQuota.Core.Services;
using CodexQuota.Core.Models;
using System.Windows;
using System.Windows.Interop;
using CodexQuota.Desktop.Views;

namespace CodexQuota.Desktop.Services;

/// <summary>
/// Converts Windows monitor geometry to device-independent pixels at one boundary.
/// Per-monitor DPI is read with <c>GetDpiForMonitor</c>; if that is unavailable the
/// primary scale is used. Placement math itself lives in Core and is unit tested.
/// </summary>
public sealed class MonitorService : IMonitorProvider
{
    private const uint MonitorDefaultToNearest = 2;
    private const int MdtEffectiveDpi = 0;

    public void SizePanel(Window ball, Window panel)
    {
        double scale = GetScaleForWindow(ball);
        System.Drawing.Rectangle area = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(ball).Handle).WorkingArea;
        SizePanelToWorkArea(panel, area.Height / scale);
    }

    /// <summary>Content stays in DIP; only native placement converts to monitor pixels.</summary>
    internal static void SizePanelToWorkArea(Window panel, double workAreaHeight)
    {
        double width = (double)panel.FindResource("PanelWidth");
        double preferred = panel is DetailsWindow details ? details.GetPreferredHeight(width) : (double)panel.FindResource("SettingsPanelHeight");
        double height = Math.Min(preferred, Math.Min(panel.MaxHeight, Math.Max(1, workAreaHeight - 24)));
        if (Math.Abs(panel.Width - width) > 0.1 || double.IsNaN(panel.Width)) { panel.Width = width; }
        if (Math.Abs(panel.Height - height) > 0.1 || double.IsNaN(panel.Height)) { panel.Height = height; }
        panel.UpdateLayout();
    }

    public void ApplyPlacement(Window ball, BallPlacement placement)
    {
        if (placement == BallPlacement.Custom) { ClampWindow(ball); return; }
        double scale = GetScaleForWindow(ball);
        System.Drawing.Rectangle area = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(ball).Handle).WorkingArea;
        double margin = MonitorPlacement.EdgeMargin * scale;
        double x = placement is BallPlacement.TopLeft or BallPlacement.BottomLeft ? area.Left + margin : area.Right - ball.Width * scale - margin;
        double y = placement is BallPlacement.TopLeft or BallPlacement.TopRight ? area.Top + margin : area.Bottom - ball.Height * scale - margin;
        MovePhysical(ball, x, y);
        ClampWindow(ball);
    }

    // Runtime positioning uses physical virtual-screen coordinates, avoiding a fictitious
    // global DIP plane made by dividing different monitor origins by different scales.
    public void RestoreBall(Window ball, WidgetSettings settings)
    {
        double scale = GetScaleForWindow(ball);
        System.Drawing.Rectangle primary = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
        double left = settings.BallLeft is { } savedLeft ? savedLeft * (settings.BallPositionScale ?? scale) : primary.Right - ((ball.Width + 16) * scale);
        double top = settings.BallTop is { } savedTop ? savedTop * (settings.BallPositionScale ?? scale) : primary.Bottom - ((ball.Height + 16) * scale);
        MovePhysical(ball, left, top);
        ClampWindow(ball);
    }

    public void ClampWindow(Window window)
    {
        if (!GetWindowRect(new WindowInteropHelper(window).Handle, out NativeRect rect)) { return; }
        var areas = System.Windows.Forms.Screen.AllScreens.Select(screen =>
        {
            System.Drawing.Rectangle area = screen.WorkingArea;
            return new MonitorWorkArea(screen.DeviceName, area.Left, area.Top, area.Width, area.Height, screen.Primary);
        }).ToArray();
        (double left, double top) = MonitorPlacement.Clamp(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, areas);
        MovePhysical(window, left, top);
    }

    public void PlacePanel(Window ball, Window panel)
    {
        if (!TryGetWindow(ball, out NativeRect rect)) { return; }
        double scale = GetScaleForWindow(ball);
        // Content measures in DIP; apply the ball monitor's scale at the single final
        // native placement. Moving to the ball origin first visibly jitters open panels.
        panel.UpdateLayout();
        PlacePanelAt(panel, rect, scale);
    }

    /// <summary>
    /// Repositions an already-visible panel beside the ball after a ball move. It reuses
    /// the ball's current physical rectangle and the panel's existing measured size, and
    /// writes the final position with a single native move (no intermediate move to the
    /// ball origin), so repeated drag steps cannot visibly flicker the panel.
    /// </summary>
    public void FollowPanel(Window ball, Window panel)
    {
        if (!TryGetWindow(ball, out NativeRect rect)) { return; }
        PlacePanelAt(panel, rect, GetScaleForWindow(ball));
    }

    private static void PlacePanelAt(Window panel, NativeRect ballRect, double scale)
    {
        if (panel.ActualWidth <= 0 || panel.ActualHeight <= 0) { return; }
        // Prefer the ball's own monitor so the panel never lands on a different screen.
        System.Drawing.Rectangle bounds = System.Windows.Forms.Screen
            .FromPoint(new System.Drawing.Point(ballRect.Left + ((ballRect.Right - ballRect.Left) / 2), ballRect.Top))
            .WorkingArea;
        var area = new MonitorWorkArea("current", bounds.Left, bounds.Top, bounds.Width, bounds.Height, false);
        double width = panel.ActualWidth * scale;
        double height = panel.ActualHeight * scale;
        (double left, double top) = MonitorPlacement.PlacePanelBeside(
            ballRect.Left,
            ballRect.Top,
            ballRect.Right - ballRect.Left,
            width,
            height,
            area);
        MovePhysical(panel, left, top);
    }

    private static bool TryGetWindow(Window window, out NativeRect rect) =>
        GetWindowRect(new WindowInteropHelper(window).Handle, out rect);

    private static void MovePhysical(Window window, double left, double top)
    {
        nint handle = new WindowInteropHelper(window).Handle;
        int x = (int)Math.Round(left);
        int y = (int)Math.Round(top);
        if (GetWindowRect(handle, out NativeRect rect) && rect.Left == x && rect.Top == y) { return; }
        SetWindowPos(handle, 0, x, y, 0, 0, 0x0015); // NOSIZE|NOZORDER|NOACTIVATE
    }

    internal static void MoveDraggedWindow(Window window, Point physicalPosition) => MovePhysical(window, physicalPosition.X, physicalPosition.Y);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);

    public IReadOnlyList<MonitorWorkArea> GetWorkAreas()
    {
        var areas = new List<MonitorWorkArea>();
        foreach (System.Windows.Forms.Screen screen in System.Windows.Forms.Screen.AllScreens)
        {
            System.Drawing.Rectangle bounds = screen.WorkingArea;
            double scale = ScaleFor(bounds);
            areas.Add(new MonitorWorkArea(
                screen.DeviceName,
                bounds.Left / scale,
                bounds.Top / scale,
                bounds.Width / scale,
                bounds.Height / scale,
                screen.Primary));
        }

        return areas;
    }

    /// <summary>
    /// Physical-pixel view of every attached display: full bounds for shared-edge
    /// adjacency and work areas for reachable placement. Pure docking decisions live in
    /// Core's <see cref="DockGeometry"/>; this boundary only supplies real hardware data.
    /// </summary>
    public IReadOnlyList<DockMonitor> GetDockMonitors()
    {
        var monitors = new List<DockMonitor>();
        foreach (System.Windows.Forms.Screen screen in System.Windows.Forms.Screen.AllScreens)
        {
            System.Drawing.Rectangle bounds = screen.Bounds;
            System.Drawing.Rectangle work = screen.WorkingArea;
            monitors.Add(new DockMonitor(
                screen.DeviceName,
                new PixelRect(bounds.Left, bounds.Top, bounds.Width, bounds.Height),
                new PixelRect(work.Left, work.Top, work.Width, work.Height),
                screen.Primary));
        }

        return monitors;
    }

    /// <summary>
    /// Classifies a drag-release rectangle in physical pixels against the exposed
    /// left/right work-area edges. Must run before the ordinary full-ball clamp so the
    /// deliberate quarter-offscreen gesture is not destroyed by reachability clamping.
    /// </summary>
    public BallDockSide EvaluateDockRelease(Window ball)
    {
        if (!GetWindowRectPhysical(ball, out PixelRect rect))
        {
            return BallDockSide.None;
        }

        return DockGeometry.EvaluateRelease(rect, GetDockMonitors());
    }

    /// <summary>
    /// True when the ball's current physical position still exposes the saved dock side
    /// at its vertical span. Used on startup restore so a removed or shared edge falls
    /// back to a reachable ordinary ball instead of an invisible strip.
    /// </summary>
    public bool IsDockSideEligible(Window ball, BallDockSide side)
    {
        if (side is not (BallDockSide.Left or BallDockSide.Right) ||
            !GetWindowRectPhysical(ball, out PixelRect rect))
        {
            return false;
        }

        if (ball is FloatingBallWindow floating && floating.DockSide != BallDockSide.None)
        {
            Point origin = floating.CanonicalPhysicalOrigin;
            int diameter = (int)Math.Round(floating.BallSize * GetScaleForWindow(ball));
            rect = new PixelRect((int)Math.Round(origin.X), (int)Math.Round(origin.Y), diameter, diameter);
        }

        nint handle = new WindowInteropHelper(ball).Handle;
        string deviceName = System.Windows.Forms.Screen.FromHandle(handle).DeviceName;
        IReadOnlyList<DockMonitor> monitors = GetDockMonitors();
        DockMonitor? home = (ball is FloatingBallWindow { DockSide: not BallDockSide.None }
            ? monitors.FirstOrDefault(monitor => monitor.WorkArea.Contains((int)rect.CenterX, (int)rect.CenterY))
            : monitors.FirstOrDefault(monitor => string.Equals(monitor.Id, deviceName, StringComparison.Ordinal)))
            ?? monitors.FirstOrDefault(monitor => monitor.WorkArea.Contains(rect.Left + (rect.Width / 2), rect.Top + (rect.Height / 2)));
        return home is not null && DockGeometry.IsSideExposed(home, monitors, rect, toLeft: side == BallDockSide.Left);
    }

    private static bool GetWindowRectPhysical(Window window, out PixelRect rect)
    {
        nint handle = new WindowInteropHelper(window).Handle;
        if (handle == 0 || !GetWindowRect(handle, out NativeRect native))
        {
            rect = default;
            return false;
        }

        rect = new PixelRect(native.Left, native.Top, native.Right - native.Left, native.Bottom - native.Top);
        return true;
    }


    public MonitorWorkArea? GetWorkAreaAt(double dipX, double dipY)
    {
        IReadOnlyList<MonitorWorkArea> areas = GetWorkAreas();
        if (areas.Count == 0)
        {
            return null;
        }

        foreach (MonitorWorkArea area in areas)
        {
            if (dipX >= area.Left && dipX <= area.Right && dipY >= area.Top && dipY <= area.Bottom)
            {
                return area;
            }
        }

        MonitorWorkArea nearest = areas[0];
        double nearestDistance = double.MaxValue;
        foreach (MonitorWorkArea area in areas)
        {
            double dx = dipX - area.CenterX;
            double dy = dipY - area.CenterY;
            double distance = (dx * dx) + (dy * dy);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = area;
            }
        }

        return nearest;
    }

    /// <summary>Current scale of the window's monitor, used to convert DIP positions to pixels.</summary>
    public static double GetScaleForWindow(System.Windows.Window window)
    {
        try
        {
            uint dpi = GetDpiForWindow(new WindowInteropHelper(window).Handle);
            if (dpi > 0) { return dpi / 96d; }
            System.Windows.Point point = window.PointToScreen(new System.Windows.Point(0, 0));
            return ScaleFor(new System.Drawing.Rectangle((int)point.X, (int)point.Y, 1, 1));
        }
        catch (Exception)
        {
            return 1.0;
        }
    }

    private static double ScaleFor(System.Drawing.Rectangle bounds)
    {
        try
        {
            var point = new NativePoint
            {
                X = bounds.Left + (bounds.Width / 2),
                Y = bounds.Top + (bounds.Height / 2),
            };

            nint monitor = MonitorFromPoint(point, MonitorDefaultToNearest);
            if (monitor != 0 && GetDpiForMonitor(monitor, MdtEffectiveDpi, out uint dpiX, out _) == 0 && dpiX > 0)
            {
                return dpiX / 96.0;
            }
        }
        catch (Exception)
        {
            // Fall through to the neutral scale.
        }

        return 1.0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);
}
