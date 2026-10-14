using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using CodexQuota.Core.Models;
using CodexQuota.Core.Services;

namespace CodexQuota.Desktop.Views;

/// <summary>
/// The 72-DIP transparent, topmost ball. Interaction state is owned by the shared
/// <see cref="InteractionController"/>; dragging moves the window and persists its
/// position without ever opening the panel or starting an official read. A deliberate
/// quarter-offscreen release docks the same HWND into a slim side strip; hover reveals it
/// inward and dragging it out restores the ordinary ball.
/// </summary>
public partial class FloatingBallWindow : Window
{
    private const double StripVisibleWidthDip = 14;
    private const double StripHitWidthDip = 16;
    private const double StripHeightDip = 52;

    private InteractionController? _controller;
    private BallDockController? _dock;
    private Point _dragStartScreen;
    private Point _windowStartScreen;
    private double _dragScale = 1.0;
    private bool _dragging;
    private bool _pressActive;
    private Point? _lastNativePosition;
    private double _ballDip = 72;
    private double _dockAnchorPhysicalY;
    private BallDockPresentation _presentation = BallDockPresentation.Floating;
    private UIElement? _dragSurface;
    private bool _changingCapture;
    private bool _pressDockHold;
    private bool _projecting;
    private string? _dockMonitorId;

    public FloatingBallWindow()
    {
        InitializeComponent();
        DockStrip.ContextMenu = Ball.ContextMenu;
    }

    /// <summary>Raised after a drag completes so the caller can persist the position.</summary>
    public event Action<double, double>? PositionPersisted;
    public event Action? DisplayEnvironmentChanged;

    /// <summary>
    /// Raised at the native position-change boundary, including drag steps, restore and
    /// clamp moves. Native SetWindowPos is not reliably reflected by WPF LocationChanged.
    /// </summary>
    public event Action? BallMoved;

    /// <summary>Raised when a drag loses mouse capture; the moved geometry is never persisted.</summary>
    public event Action? DragInterrupted;
    public event Action? UserDragged;

    /// <summary>Raised after a strip/full-ball presentation change; size-only transitions need an explicit panel follow.</summary>
    public event Action? DockPresentationChanged;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(OnWindowMessage);
    }

    private nint OnWindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0047 && lParam != 0) // WM_WINDOWPOSCHANGED
        {
            NativeWindowPosition position = Marshal.PtrToStructure<NativeWindowPosition>(lParam);
            if ((position.Flags & 0x0002) == 0) // !SWP_NOMOVE
            {
                var point = new Point(position.X, position.Y);
                if (_lastNativePosition != point)
                {
                    _lastNativePosition = point;
                    if (!_projecting) { BallMoved?.Invoke(); }
                }
            }
        }
        if (message is 0x007E or 0x02E0 or 0x001A) // display, DPI, work-area/taskbar changes
        {
            Dispatcher.BeginInvoke(() => DisplayEnvironmentChanged?.Invoke());
        }
        return 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeWindowPosition
    {
        public nint Window, InsertAfter;
        public int X, Y, Width, Height;
        public uint Flags;
    }

    public void AttachController(InteractionController controller) => _controller = controller;

    public void AttachDockController(BallDockController controller) => _dock = controller;

    public void ApplyOpacity(double opacity) => Opacity = Math.Clamp(opacity, 0.4, 1.0);

    public void ApplySize(int percent)
    {
        _ballDip = 72 * Math.Clamp(percent, 75, 150) / 100.0;
        if (DockSide == BallDockSide.None)
        {
            Width = Height = _ballDip;
        }

        UpdateLayout();
    }

    public void MoveTo(double left, double top)
    {
        Left = left;
        Top = top;
    }

    /// <summary>Exposed for verification: the ball's actual on-screen size in DIPs.</summary>
    public double BallSize => _ballDip;

    /// <summary>Exposed for verification: the screen point captured at press, used to drive a deterministic drag.</summary>
    internal Point DragStartScreen => _dragStartScreen;

    /// <summary>Exposed for verification: the window origin captured at press.</summary>
    internal Point WindowStartScreen => _windowStartScreen;

    /// <summary>Exposed for verification: the current strip/full-ball presentation.</summary>
    internal BallDockPresentation CurrentPresentation => DockSide == BallDockSide.None ? BallDockPresentation.Floating : _presentation;

    /// <summary>Canonical dock side; None while the ball is an ordinary floating ball.</summary>
    public BallDockSide DockSide { get; private set; }
    internal bool DockHomeAvailable => DockSide == BallDockSide.None || System.Windows.Forms.Screen.AllScreens.Any(screen => screen.DeviceName == _dockMonitorId);

    /// <summary>Canonical vertical anchor of the full ball, in physical pixels.</summary>
    internal double DockAnchorPhysicalY => _dockAnchorPhysicalY;

    /// <summary>
    /// Canonical physical origin of the reachable full ball: the revealed inward position
    /// while docked, otherwise the current window origin. Never a temporary strip rect.
    /// </summary>
    internal Point CanonicalPhysicalOrigin
    {
        get
        {
            Point fallback = PointToScreen(new Point(0, 0));
            if (DockSide == BallDockSide.None || !GetPhysicalWorkArea(out NativeRect area))
            {
                return fallback;
            }

            double scale = Services.MonitorService.GetScaleForWindow(this);
            double ballWPhys = Math.Max(1, Math.Round(_ballDip * scale));
            double top = Math.Clamp(_dockAnchorPhysicalY - (ballWPhys / 2.0), area.Top, Math.Max(area.Top, area.Bottom - ballWPhys));
            double left = DockSide == BallDockSide.Left ? area.Left : area.Right - ballWPhys;
            return new Point(left, top);
        }
    }

    /// <summary>
    /// Adopts a canonical dock from a qualifying release or restored preferences. The
    /// anchor is the current full-ball vertical center; the strip is projected from it.
    /// </summary>
    public void AdoptDock(BallDockSide side)
    {
        if (side is not (BallDockSide.Left or BallDockSide.Right))
        {
            return;
        }

        _dockMonitorId = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle).DeviceName;
        DockSide = side;
        if (GetPhysicalRect(out NativeRect rect))
        {
            _dockAnchorPhysicalY = rect.Top + (rect.Bottom - rect.Top) / 2.0;
        }
        if (GetPhysicalWorkArea(out NativeRect area))
        {
            double diameter = Math.Round(_ballDip * Services.MonitorService.GetScaleForWindow(this));
            _dockAnchorPhysicalY = Math.Clamp(_dockAnchorPhysicalY, area.Top + diameter / 2, Math.Max(area.Top + diameter / 2, area.Bottom - diameter / 2));
        }

        ApplyDockPresentation(BallDockPresentation.Collapsed);
    }

    /// <summary>Clears the canonical dock; the ball keeps its current inward geometry.</summary>
    public void ClearDock()
    {
        if (DockSide == BallDockSide.None)
        {
            return;
        }

        Point normalOrigin = CanonicalPhysicalOrigin;
        DockSide = BallDockSide.None;
        _dockMonitorId = null;
        _presentation = BallDockPresentation.Floating;
        ApplyFloatingGeometry();
        Services.MonitorService.MoveDraggedWindow(this, normalOrigin);
        DockPresentationChanged?.Invoke();
    }

    /// <summary>Projects the desired transient presentation for the canonical dock.</summary>
    public void ApplyDockPresentation(BallDockPresentation presentation)
    {
        if (DockSide == BallDockSide.None)
        {
            presentation = BallDockPresentation.Floating;
        }

        _presentation = presentation;
        _projecting = true;
        try
        {
            switch (presentation)
            {
                case BallDockPresentation.Collapsed: ProjectStrip(); break;
                case BallDockPresentation.Revealed: ProjectRevealedBall(); break;
                default: ApplyFloatingGeometry(); break;
            }
        }
        finally { _projecting = false; }

        DockPresentationChanged?.Invoke();
    }

    private void ApplyFloatingGeometry()
    {
        BallHost.Visibility = Visibility.Visible;
        DockStrip.Visibility = Visibility.Collapsed;
        if (Math.Abs(Width - _ballDip) > 0.01 || Math.Abs(Height - _ballDip) > 0.01)
        {
            Width = Height = _ballDip;
        }

        UpdateLayout();
    }

    private void ProjectStrip()
    {
        if (!GetPhysicalWorkArea(out NativeRect area))
        {
            ApplyFloatingGeometry();
            return;
        }

        double scale = Services.MonitorService.GetScaleForWindow(this);
        double stripWDip = StripHitWidthDip * (_ballDip / 72d);
        double stripHDip = StripHeightDip * (_ballDip / 72d);
        double accentWDip = StripVisibleWidthDip * (_ballDip / 72d);
        double stripWPhys = Math.Max(1, Math.Round(stripWDip * scale));
        double stripHPhys = Math.Max(1, Math.Round(stripHDip * scale));
        double top = Math.Clamp(_dockAnchorPhysicalY - (stripHPhys / 2.0), area.Top, Math.Max(area.Top, area.Bottom - stripHPhys));
        double left = DockSide == BallDockSide.Left ? area.Left : area.Right - stripWPhys;

        BallHost.Visibility = Visibility.Collapsed;
        DockStrip.Visibility = Visibility.Visible;
        DockStripAccent.Visibility = Visibility.Visible;
        DockStrip.HorizontalAlignment = HorizontalAlignment.Stretch;
        DockStrip.VerticalAlignment = VerticalAlignment.Stretch;
        DockStrip.Margin = new Thickness(0);
        DockStrip.Width = double.NaN;
        DockStrip.Height = double.NaN;
        DockStripAccent.Width = accentWDip;
        DockStripAccent.HorizontalAlignment = DockSide == BallDockSide.Left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        Width = stripWPhys / scale;
        Height = stripHPhys / scale;
        UpdateLayout();
        SetWindowRect(left, top, stripWPhys, stripHPhys);
    }

    private void ProjectRevealedBall()
    {
        if (!GetPhysicalWorkArea(out NativeRect area))
        {
            ApplyFloatingGeometry();
            return;
        }

        double scale = Services.MonitorService.GetScaleForWindow(this);
        double ballWPhys = Math.Max(1, Math.Round(_ballDip * scale));
        double stripHDip = StripHeightDip * (_ballDip / 72d);
        double stripWDip = StripHitWidthDip * (_ballDip / 72d);
        double stripHPhys = Math.Max(1, Math.Round(stripHDip * scale));
        double top = Math.Clamp(_dockAnchorPhysicalY - (ballWPhys / 2.0), area.Top, Math.Max(area.Top, area.Bottom - ballWPhys));
        double left = DockSide == BallDockSide.Left ? area.Left : area.Right - ballWPhys;
        double stripTopPhys = Math.Clamp(_dockAnchorPhysicalY - (stripHPhys / 2.0), area.Top, Math.Max(area.Top, area.Bottom - stripHPhys));
        double laneOffsetDip = Math.Max(0, (stripTopPhys - top) / scale);

        BallHost.Visibility = Visibility.Visible;
        DockStrip.Visibility = Visibility.Visible;
        DockStripAccent.Visibility = Visibility.Hidden;
        DockStrip.HorizontalAlignment = DockSide == BallDockSide.Left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        DockStrip.VerticalAlignment = VerticalAlignment.Top;
        DockStrip.Margin = new Thickness(0, laneOffsetDip, 0, 0);
        DockStrip.Width = stripWDip;
        DockStrip.Height = stripHDip;
        DockStripAccent.Width = StripVisibleWidthDip * (_ballDip / 72d);
        DockStripAccent.HorizontalAlignment = DockSide == BallDockSide.Left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        Width = _ballDip;
        Height = _ballDip;
        UpdateLayout();
        SetWindowRect(left, top, ballWPhys, ballWPhys);
    }

    private bool GetPhysicalRect(out NativeRect rect) =>
        GetWindowRect(new WindowInteropHelper(this).Handle, out rect);

    private bool GetPhysicalWorkArea(out NativeRect area)
    {
        if (!GetPhysicalRect(out NativeRect _))
        {
            area = default;
            return false;
        }

        var screen = DockSide == BallDockSide.None ? System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle)
            : System.Windows.Forms.Screen.AllScreens.FirstOrDefault(screen => screen.DeviceName == _dockMonitorId);
        if (screen is null) { area = default; return false; }
        System.Drawing.Rectangle workArea = screen.WorkingArea;
        area = new NativeRect { Left = workArea.Left, Top = workArea.Top, Right = workArea.Right, Bottom = workArea.Bottom };
        return true;
    }

    private void SetWindowRect(double left, double top, double widthPhysical, double heightPhysical)
    {
        nint handle = new WindowInteropHelper(this).Handle;
        int x = (int)Math.Round(left);
        int y = (int)Math.Round(top);
        int w = (int)Math.Round(widthPhysical);
        int h = (int)Math.Round(heightPhysical);
        if (GetWindowRect(handle, out NativeRect current) &&
            current.Left == x && current.Top == y && current.Right - current.Left == w && current.Bottom - current.Top == h)
        {
            return;
        }

        SetWindowPos(handle, 0, x, y, w, h, 0x0014); // NOZORDER|NOACTIVATE
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    private void OnBallMouseEnter(object sender, MouseEventArgs e)
    {
        _dock?.PointerEnteredRevealed();
        _controller?.PointerEnteredBall();
    }

    private void OnBallMouseLeave(object sender, MouseEventArgs e)
    {
        _dock?.PointerExitedRevealed();
        _controller?.PointerExitedBall();
    }

    private void OnDockStripMouseEnter(object sender, MouseEventArgs e)
    {
        _dock?.PointerEnteredStrip();
        // The lane is part of the ball's hover surface so an existing preview survives
        // the ball-to-lane transition without scheduling a close.
        _controller?.PointerEnteredBall();
    }

    private void OnDockStripMouseLeave(object sender, MouseEventArgs e)
    {
        _dock?.PointerExitedStrip();
        _controller?.PointerExitedBall();
    }

    private void OnBallMouseDown(object sender, MouseButtonEventArgs e) => BeginPress(e, Ball);

    private void OnDockStripMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        BeginPress(e, DockStrip);
        // A press on the collapsed strip immediately uses the ordinary full-ball surface;
        // the strip lane stays at the edge so the captured press point keeps its offset.
        if (DockSide != BallDockSide.None && _presentation == BallDockPresentation.Collapsed)
        {
            ApplyDockPresentation(BallDockPresentation.Revealed);
            _windowStartScreen = PointToScreen(new Point(0, 0));
        }
    }

    private void BeginPress(MouseButtonEventArgs e, UIElement surface)
    {
        if (e.ChangedButton != MouseButton.Left) { return; }
        Point press = PointToScreen(e.GetPosition(this));
        _dock?.PushHold();
        _pressDockHold = true;
        if (DockSide != BallDockSide.None) { ApplyDockPresentation(BallDockPresentation.Revealed); }
        _pressActive = true;
        _dragging = false;
        _dragSurface = surface;
        _dragStartScreen = press;
        _windowStartScreen = PointToScreen(new Point(0, 0));
        _dragScale = Services.MonitorService.GetScaleForWindow(this);
        // Captured move/up events must route through the active hit surface's XAML
        // handlers. Capturing the Window instead bypasses this sibling event surface.
        surface.CaptureMouse();
        e.Handled = true;
    }

    private void OnBallMouseMove(object sender, MouseEventArgs e) => ContinueDrag(e);

    private void OnDockStripMouseMove(object sender, MouseEventArgs e) => ContinueDrag(e);

    private void ContinueDrag(MouseEventArgs e)
    {
        if (!_pressActive || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        Point current = PointToScreen(e.GetPosition(this));
        ApplyDragPosition(current);
    }

    internal void ApplyDragPosition(Point current)
    {
        if (!_pressActive) { return; }
        double deltaX = current.X - _dragStartScreen.X;
        double deltaY = current.Y - _dragStartScreen.Y;

        if (!_dragging &&
            (Math.Abs(deltaX / _dragScale) > SystemParameters.MinimumHorizontalDragDistance ||
             Math.Abs(deltaY / _dragScale) > SystemParameters.MinimumVerticalDragDistance))
        {
            _dragging = true;
            // Switch capture before removing the old strip hit lane, preserving the
            // original physical press and the revealed full-ball origin without a jump.
            if (DockStrip.IsMouseCaptured)
            {
                _changingCapture = true;
                try { Ball.CaptureMouse(); _dragSurface = Ball; }
                finally { _changingCapture = false; }
            }
            _dock?.Undock();
            ClearDock();
            _controller?.DragStarted();
            _dock?.DragStarted();
        }

        if (_dragging)
        {
            Services.MonitorService.MoveDraggedWindow(this, _windowStartScreen + new Vector(deltaX, deltaY));
            UserDragged?.Invoke();
        }
    }

    private void OnBallMouseUp(object sender, MouseButtonEventArgs e) => CompletePress(e);

    private void OnDockStripMouseUp(object sender, MouseButtonEventArgs e) => CompletePress(e);

    private void CompletePress(MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || !_pressActive)
        {
            return;
        }

        _pressActive = false;
        bool dragged = _dragging;
        _dragging = false;
        _dragSurface = null;
        if (Ball.IsMouseCaptured)
        {
            Ball.ReleaseMouseCapture();
        }

        if (DockStrip.IsMouseCaptured)
        {
            DockStrip.ReleaseMouseCapture();
        }
        ReleasePressHold();

        if (dragged)
        {
            _controller?.DragCompleted();
            _dock?.DragEnded();
            PositionPersisted?.Invoke(Left, Top);
            return;
        }

        _controller?.BallClicked();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        if (_changingCapture) { base.OnLostMouseCapture(e); return; }
        if (_pressActive)
        {
            bool moved = _dragging;
            _pressActive = false;
            _dragging = false;
            _dragSurface = null;
            _controller?.DragCompleted();
            _dock?.DragEnded();
            ReleasePressHold();
            // Lost capture is not a release commit: the moved geometry stays where the
            // user left it (the caller clamps reachability) and nothing is persisted.
            if (moved) { DragInterrupted?.Invoke(); }
        }
        base.OnLostMouseCapture(e);
    }

    private void ReleasePressHold()
    {
        if (!_pressDockHold) { return; }
        _pressDockHold = false;
        _dock?.PopHold();
    }

    private void OnBallMouseRightUp(object sender, MouseButtonEventArgs e) => OpenContextMenuFor(Ball, e);

    private void OnDockStripMouseRightUp(object sender, MouseButtonEventArgs e) => OpenContextMenuFor(DockStrip, e);

    private void OpenContextMenuFor(FrameworkElement target, MouseButtonEventArgs e)
    {
        if (target.ContextMenu is { } menu)
        {
            menu.PlacementTarget = target;
            menu.IsOpen = true;
            e.Handled = true;
        }
    }

    private void OnBallKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
            case Key.Space:
                _controller?.ShowPinned();
                e.Handled = true;
                break;
            case Key.Escape:
                _controller?.CloseExplicitly();
                e.Handled = true;
                break;
        }
    }

    private void OnMenuDetails(object sender, RoutedEventArgs e) => _controller?.ShowPinned();

    private void OnMenuRefresh(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke();

    private void OnMenuSettings(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();

    private void OnMenuExit(object sender, RoutedEventArgs e) => ExitRequested?.Invoke();

    /// <summary>Raised by the ball's context menu.</summary>
    public event Action? RefreshRequested;

    /// <summary>Raised by the ball's context menu.</summary>
    public event Action? SettingsRequested;

    /// <summary>Raised by the ball's context menu.</summary>
    public event Action? ExitRequested;
}
