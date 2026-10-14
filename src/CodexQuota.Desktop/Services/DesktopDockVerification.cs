using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using CodexQuota.Core.Models;
using CodexQuota.Core.Services;
using CodexQuota.Desktop.Services;

namespace CodexQuota.Desktop;

public partial class App
{
    private async Task VerifyBothDockEdgesAsync(string outputDirectory, Action<string, bool> check)
    {
        WidgetSettings original = _settings;
        Point originalOrigin = _ball!.CanonicalPhysicalOrigin;
        var observations = new List<object>();
        UIElement circle = (UIElement)_ball.FindName("Ball");
        UIElement lane = (UIElement)_ball.FindName("DockStrip");
        int releases = 0;
        void CountRelease(double left, double top) => releases++;
        _ball.PositionPersisted += CountRelease;
        try
        {
            foreach (BallDockSide side in new[] { BallDockSide.Left, BallDockSide.Right })
            {
                string prefix = side.ToString().ToLowerInvariant();
                var screen = side == BallDockSide.Left
                    ? System.Windows.Forms.Screen.AllScreens.MinBy(item => item.Bounds.Left)!
                    : System.Windows.Forms.Screen.AllScreens.MaxBy(item => item.Bounds.Right)!;
                System.Drawing.Rectangle area = screen.WorkingArea;
                foreach (int offset in new[] { 2, 0, -2 })
                {
                    Pointer(lane, false); Pointer(circle, false);
                    _controller!.CloseExplicitly();
                    _dockController!.Undock(); _ball.ClearDock(); _ball.ApplySize(100);
                    _ball.ApplyOpacity(.4);
                    MonitorService.MoveDraggedWindow(_ball, new Point(area.Left + 250, area.Top + 150));
                    await Task.Delay(80);
                    GetWindowRect(new WindowInteropHelper(_ball).Handle, out NativeRect normal);
                    double diameter = normal.Right - normal.Left;
                    // Anchor on the relaxed quarter-diameter docking threshold: offset > 0
                    // leaves slightly less than a quarter of the ball beyond the edge
                    // (ordinary release), offset <= 0 is at or beyond a quarter (docks).
                    // For the right side the left-edge target includes the full diameter
                    // so the measured overhang past `area.Right` is `cross - offset`.
                    double cross = diameter / 4;
                    double target = side == BallDockSide.Left
                        ? area.Left - cross + offset
                        : area.Right + cross - diameter - offset;
                    int before = releases;
                    MouseButton(circle, true);
                    MoveDraggedBallTo(_ball, target, area.Top + 150);
                    MouseButton(circle, false);
                    await Task.Delay(160);
                    BallDockSide expected = offset > 0 ? BallDockSide.None : side;
                    check($"native_{prefix}_quarter_threshold_offset{offset}_classifies_actual_release", _ball.DockSide == expected && releases == before + 1);
                    WidgetSettings saved = await _settingsStore.LoadAsync();
                    check($"native_{prefix}_quarter_threshold_offset{offset}_retains_canonical_metadata", saved.DockSide == expected && saved.BallLeft is not null && saved.BallTop is not null);
                    observations.Add(new { side = prefix, offset, actual = ReadGeometry(_ball), canonical = _ball.CanonicalPhysicalOrigin, dockSide = _ball.DockSide.ToString() });
                }

                nint handle = new WindowInteropHelper(_ball).Handle;
                GetWindowRect(handle, out NativeRect strip);
                // Use the inner part of the input lane, beyond the capsule shell.
                double scale = MonitorService.GetScaleForWindow(_ball);
                var lanePoint = new Point(side == BallDockSide.Left ? strip.Left + 15 * scale : strip.Right - 15 * scale,
                    strip.Top + 2 * scale);
                await WaitUntilAsync(() => NativeOwnsPoint(handle, lanePoint), TimeSpan.FromSeconds(2));
                check($"native_{prefix}_collapsed_lane_has_real_hwnd_hit_target_at40opacity", NativeOwnsPoint(handle, lanePoint));
                WidgetSettings beforeHover = await _settingsStore.LoadAsync();
                Pointer(lane, true);
                await Task.Delay(60);
                check($"native_{prefix}_hover_immediately_reveals_full_ball", _ball.CurrentPresentation == BallDockPresentation.Revealed && BallHasPhysicalSize(_ball, 72));
                check($"native_{prefix}_original_corner_lane_stays_hit_testable", ReferenceEquals(_ball.InputHitTest(_ball.PointFromScreen(lanePoint)), lane));
                await WaitUntilAsync(() => NativeOwnsPoint(handle, lanePoint), TimeSpan.FromSeconds(2));
                check($"native_{prefix}_revealed_corner_lane_has_real_hwnd_hit_target_at40opacity", NativeOwnsPoint(handle, lanePoint));
                CaptureSettingsWindow(_ball, System.IO.Path.Combine(outputDirectory, $"actual-dock-{prefix}-revealed.png"));
                Pointer(lane, false);
                await Task.Delay(700);
                check($"native_{prefix}_leave_recollapses_without_retention", _ball.CurrentPresentation == BallDockPresentation.Collapsed && await _settingsStore.LoadAsync() == beforeHover);
                CaptureSettingsWindow(_ball, System.IO.Path.Combine(outputDirectory, $"actual-dock-{prefix}-strip.png"));

                // Isolate the size-only native boundary on a real App window: align the
                // narrow projection's origin with its canonical inward-ball origin, then
                // reveal without any x/y notification. The visible Settings panel must follow once.
                _controller!.ShowPinned();
                ShowSettings();
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                var focusedControl = (System.Windows.Controls.Slider)_settingsWindow!.FindName("OpacitySlider");
                focusedControl.Focus();
                _ball.ApplyDockPresentation(BallDockPresentation.Collapsed);
                MonitorService.MoveDraggedWindow(_ball, _ball.CanonicalPhysicalOrigin);
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                GetWindowRect(handle, out NativeRect beforeReveal);
                int panelMoves = 0;
                HwndSource source = HwndSource.FromHwnd(new WindowInteropHelper(_settingsWindow).Handle)!;
                nint CountMove(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
                {
                    if (message == 0x0047 && lParam != 0 && (System.Runtime.InteropServices.Marshal.PtrToStructure<NativeWindowPosition>(lParam).Flags & 2) == 0) { panelMoves++; }
                    return 0;
                }
                source.AddHook(CountMove);
                _ball.ApplyDockPresentation(BallDockPresentation.Revealed);
                source.RemoveHook(CountMove);
                GetWindowRect(handle, out NativeRect afterReveal);
                int expectedMoves = side == BallDockSide.Left ? 1 : 0;
                observations.Add(new
                {
                    side = prefix,
                    kind = "size-only-follow",
                    beforeReveal = new { left = beforeReveal.Left, top = beforeReveal.Top, right = beforeReveal.Right, bottom = beforeReveal.Bottom },
                    afterReveal = new { left = afterReveal.Left, top = afterReveal.Top, right = afterReveal.Right, bottom = afterReveal.Bottom },
                    panelMoves,
                    expectedMoves
                });
                check($"native_{prefix}_size_only_reveal_follows_without_redundant_native_move", beforeReveal.Left == afterReveal.Left && beforeReveal.Top == afterReveal.Top &&
                    afterReveal.Right - afterReveal.Left > beforeReveal.Right - beforeReveal.Left && panelMoves == expectedMoves && PanelTracksBall(_ball, _settingsWindow));
                check($"native_{prefix}_shape_follow_preserves_settings_focus_and_keeps_details_hidden", focusedControl.IsKeyboardFocusWithin &&
                    !_controller.IsPinned && _details?.IsVisible != true && PanelTracksBall(_ball, _settingsWindow));
                CloseSettingsSurface();
                _controller.CloseExplicitly();
                Pointer(lane, false); Pointer(circle, false);
                await Task.Delay(350);

                MouseButton(lane, true);
                Point pressOrigin = _ball.WindowStartScreen;
                Point canonical = _ball.CanonicalPhysicalOrigin;
                check($"native_{prefix}_strip_press_captures_revealed_origin", Math.Abs(pressOrigin.X - canonical.X) <= 1 && Math.Abs(pressOrigin.Y - canonical.Y) <= 1);
                double desiredX = area.Left + 350;
                MoveDraggedBallTo(_ball, desiredX, area.Top + 200);
                GetWindowRect(handle, out NativeRect dragged);
                check($"native_{prefix}_drag_out_keeps_capture_and_does_not_jump", ReferenceEquals(Mouse.Captured, circle) && Math.Abs(dragged.Left - desiredX) <= 1 && _ball.DockSide == BallDockSide.None);
                MouseButton(circle, false);
                await Task.Delay(160);
                check($"native_{prefix}_drag_out_retains_normal_ball", (await _settingsStore.LoadAsync()).DockSide == BallDockSide.None && BallHasPhysicalSize(_ball, 72));
            }

            // Draft docking, submitted canonical Save while revealed, later corner edit
            // and discard must recover the newest retained dock rather than the opening ball.
            ShowSettings();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            WidgetSettings beforeDraft = await _settingsStore.LoadAsync();
            var right = System.Windows.Forms.Screen.AllScreens.MaxBy(screen => screen.Bounds.Right)!.WorkingArea;
            MouseButton(circle, true);
            MoveDraggedBallTo(_ball, right.Right - 20, right.Top + 200);
            MouseButton(circle, false);
            await Task.Delay(100);
            check("native_settings_dock_is_preview_and_dirty_without_write", _ball.DockSide == BallDockSide.Right && IsSettingsDirty() && await _settingsStore.LoadAsync() == beforeDraft);
            await SaveSettingsAsync();
            WidgetSettings retainedDock = await _settingsStore.LoadAsync();
            check("native_settings_save_uses_canonical_dock_while_revealed", retainedDock.DockSide == BallDockSide.Right && !IsSettingsDirty() &&
                _ball.CurrentPresentation == BallDockPresentation.Revealed);
            _settingsViewModel.SelectedPlacement = _settingsViewModel.PlacementOptions.Single(option => option.Placement == BallPlacement.TopLeft);
            check("native_settings_corner_preview_undocks_without_write", _ball.DockSide == BallDockSide.None && await _settingsStore.LoadAsync() == retainedDock);
            DiscardDraft(); CloseSettingsSurface();
            await Task.Delay(700);
            check("native_settings_discard_restores_latest_saved_dock", _ball.DockSide == BallDockSide.Right && _ball.CurrentPresentation == BallDockPresentation.Collapsed && await _settingsStore.LoadAsync() == retainedDock);
            ShowSettings();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            check("native_settings_transient_reveal_stays_clean", !IsSettingsDirty());
            CloseSettingsSurface();
        }
        finally
        {
            _ball.PositionPersisted -= CountRelease;
            Pointer(lane, false); Pointer(circle, false);
            _controller!.CloseExplicitly();
            CloseSettingsSurface();
            _settings = original;
            await _settingsStore.SaveAsync(original);
            RestoreBallGeometry(original, originalOrigin);
        }
        await WriteJsonAsync(System.IO.Path.Combine(outputDirectory, "dock-native-observations.json"), observations);
    }

    private static bool NativeOwnsPoint(nint handle, Point point) => WindowFromPoint(new System.Drawing.Point((int)Math.Round(point.X), (int)Math.Round(point.Y))) == handle;

    private static void Pointer(UIElement surface, bool enter) => surface.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
    { RoutedEvent = enter ? Mouse.MouseEnterEvent : Mouse.MouseLeaveEvent });
}
