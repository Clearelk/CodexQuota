using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CodexQuota.Core.Models;
using CodexQuota.Core.Providers;
using CodexQuota.Core.Services;
using CodexQuota.Desktop.ViewModels;
using CodexQuota.Desktop.Views;
using Microsoft.Win32;

namespace CodexQuota.Desktop;

// Only --verify invokes this partial. It exercises normal App composition with an isolated provider/settings root.
public partial class App
{
    private async Task RunDesktopEventsAsync(string outputDirectory)
    {
        string temporaryRoot = Path.Combine(Path.GetTempPath(), "codexquota-desktop-" + Guid.NewGuid().ToString("N"));
        string localRoot = Path.Combine(Path.GetTempPath(), "codexquota-local-verify-" + Guid.NewGuid().ToString("N"));
        WriteLocalFixture(localRoot);
        var provider = new EventProvider();
        var checks = new List<object>();
        var geometry = new List<object>();
        var settingsOpenings = new List<object>();
        var popupObservations = new List<object>();
        nint priorForeground = GetForegroundWindow();
        void Check(string name, bool passed)
        {
            checks.Add(new { check = name, passed });
            if (_ball is not null && (name.Contains("follow", StringComparison.Ordinal) || name.Contains("clamp", StringComparison.Ordinal) || name.Contains("restore", StringComparison.Ordinal) || name.Contains("scale", StringComparison.Ordinal) || name.Contains("save_failure", StringComparison.Ordinal) || name.Contains("dock", StringComparison.Ordinal) || name.Contains("strip", StringComparison.Ordinal)))
            {
                geometry.Add(new
                {
                    check = name,
                    ball = ReadGeometry(_ball),
                    details = _details is null ? null : ReadGeometry(_details),
                    settings = _settingsWindow is null ? null : ReadGeometry(_settingsWindow),
                    pinned = _controller?.IsPinned,
                });
            }
        }
        try
        {
            await StartNormalAsync(provider, temporaryRoot, localRoot);
            if (_ball is null) { throw new InvalidOperationException("The widget must be stopped before desktop verification."); }
            // Scripted routed pointer events do not represent the user's physical cursor.
            // Isolate the global sink, then reattach it for dedicated outside-click cases.
            _outsideClicks!.ButtonDown -= OnObservedButtonDown;
            _ball.MoveTo(60, 60);
            _ball.ShowActivated = false;
            await Task.Delay(100);

            // The isolated local fixture must reach the view model without blocking the UI thread.
            await WaitUntilAsync(() => _localUsage?.CompletedScans >= 1, TimeSpan.FromSeconds(10));
            Check("local_fixture_scan_published", _widget.HasLocalNumbers);
            Check("local_fixture_tokens_reach_view_model", _widget.LocalTokensText == "4,321 Tokens");
            Check("local_fixture_session_count_reaches_view_model", _widget.LocalSessionsText == "1 个对话");
            int localScansBefore = _localUsage!.CompletedScans;

            // The observed estimate may show a waiting baseline but must never fabricate a
            // numeric delta from a single fresh read, and it stays independent of local Tokens.
            Check("observed_estimate_rows_present_after_fresh_read", _widget.ObservedPeriods.Count >= 1);
            Check("observed_estimate_waits_for_a_second_comparable_sample", _widget.ObservedPendingText.Length > 0 && !_widget.ObservedHasNumber);
            Check("observed_estimate_keeps_local_numbers_independent", _widget.HasLocalNumbers);
            Check("normal_startup_keeps_details_and_settings_lazy", _details is null && _settingsWindow is null);
            _widget.UpdateSettings(_settings);
            _widget.Update(_widget.State);
            await Task.Delay(40);
            Check("content_updates_keep_uncreated_panels_lazy", _details is null && _settingsWindow is null);

            UIElement surface = (UIElement)_ball.FindName("Ball");
            Check("actual_center_hit_targets_ball_through_body_overlay", ReferenceEquals(_ball.InputHitTest(new Point(36, 36)), surface));
            Check("actual_outer_rim_hit_targets_72_dip_ball", ReferenceEquals(_ball.InputHitTest(new Point(36, 2)), surface));
            Check("actual_circle_rim_targets_ball", ReferenceEquals(_ball.InputHitTest(new Point(4, 24)), surface));
            var inlineDot = (FrameworkElement)_ball.FindName("StatusDot");
            Point inlineDotCenter = inlineDot.TransformToAncestor(_ball).Transform(new Point(inlineDot.ActualWidth / 2, inlineDot.ActualHeight / 2));
            Check("actual_inline_health_row_hit_targets_ball", ReferenceEquals(_ball.InputHitTest(inlineDotCenter), surface));
            Check("transparent_square_corner_does_not_target_circle", !ReferenceEquals(_ball.InputHitTest(new Point(8, 8)), surface));
            Check("former_external_badge_region_misses_floating_ball", !ReferenceEquals(_ball.InputHitTest(new Point(70, 60)), surface));
            Check("percentage_widget_uses_actual_ellipse_hit_surface", surface is System.Windows.Shapes.Ellipse && ReferenceEquals(_ball.InputHitTest(new Point(36, 36)), surface));
            void Pointer(UIElement target, bool enter) => target.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
            {
                RoutedEvent = enter ? Mouse.MouseEnterEvent : Mouse.MouseLeaveEvent,
            });
            void Click() { MouseButton(surface, true); MouseButton(surface, false); }

            Pointer(surface, true);
            await Task.Delay(110);
            Check("real_hover_not_open_at_110ms", _details?.IsVisible != true);
            Pointer(surface, false);
            await Task.Delay(300);
            Check("real_hover_leave_cancels_300ms_timer", _details?.IsVisible != true);
            Pointer(surface, true);
            await Task.Delay(410);
            Check("real_hover_opens_after_300ms_unpinned", _details?.IsVisible == true && !_controller!.IsPinned);
            if (_details is null) { throw new InvalidOperationException("Hover wiring did not create the details window."); }
            UIElement panel = (UIElement)_details.FindName("PanelRoot");
            Pointer(surface, false);
            await Task.Delay(80);
            Pointer(panel, true);
            await Task.Delay(300);
            Check("real_ball_to_panel_cancels_leave_timer", _details.IsVisible);
            Pointer(panel, false);
            await Task.Delay(100);
            Check("real_leave_grace_keeps_panel_at_100ms", _details.IsVisible);
            await Task.Delay(240);
            Check("real_leave_closes_after_250ms", !_details.IsVisible);

            MouseButton(surface, false);
            Check("unpaired_mouse_up_does_not_click", !_details.IsVisible);
            _controller!.CloseExplicitly();
            Click();
            Check("real_ball_click_pins", _details.IsVisible && _controller.IsPinned);
            Pointer(surface, false);
            Pointer(panel, false);
            await Task.Delay(330);
            Check("real_pinned_survives_leave", _details.IsVisible);
            Click();
            Check("real_second_click_closes", !_details.IsVisible && !_controller.IsPinned);

            Pointer(surface, true);
            await Task.Delay(410);
            ((Button)_details.FindName("CloseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pointer(surface, true);
            await Task.Delay(410);
            Check("real_title_close_suppresses_hover", !_details.IsVisible);
            Pointer(surface, false);
            Pointer(surface, true);
            await Task.Delay(410);
            Check("real_leave_resets_explicit_close_suppression", _details.IsVisible);
            Click();
            Check("pin_existing_preview_updates_window_activation", _details.ShowActivated && _controller.IsPinned);
            int readsBeforeOutside = provider.Reads;
            int scansBeforeOutside = _localUsage.CompletedScans;
            _outsideClicks!.ButtonDown += OnObservedButtonDown;
            Check("outside_observer_installed_while_details_visible", _outsideClicks?.IsRunning == true);
            _outsideClicks!.ObserveButtonDown(_details.PointToScreen(new Point(24, 24)));
            await Task.Delay(40);
            Check("inside_details_click_keeps_open", _details.IsVisible && _controller.IsPinned);
            _outsideClicks.ObserveButtonDown(new Point(-30000, -30000));
            await Task.Delay(40);
            Check("outside_click_closes_and_unpins", !_details.IsVisible && !_controller.IsPinned);
            Check("outside_observer_removed_on_hide", !_outsideClicks.IsRunning);
            Check("outside_click_never_reads_or_scans", provider.Reads == readsBeforeOutside && _localUsage.CompletedScans == scansBeforeOutside);
            _outsideClicks.ButtonDown -= OnObservedButtonDown;
            _controller.ShowPinned();
            RaiseKey(_details, Key.Escape);
            Check("real_details_escape_closes_and_unpins", !_details.IsVisible && !_controller.IsPinned);
            RaiseKey(_ball, Key.Enter);
            Check("real_ball_enter_opens_pinned", _details.IsVisible && _controller.IsPinned);
            int reflowActivations = 0;
            _details.Activated += (_, _) => reflowActivations++;
            int reflowReads = provider.Reads;
            int reflowScans = _localUsage.CompletedScans;
            RefreshState reflowState = _widget.State;
            double reflowNormalHeight = _details.Height;
            _widget.UpdateSettings(_settings with { ShowRemaining = false, ShowResetTime = false, ShowTodayUsage = false });
            await Task.Delay(50);
            Check("visible_details_hidden_cards_reflow_and_native_follow", _details.Height < reflowNormalHeight - 60 && PanelTracksBall(_ball, _details) && DetailsHintFits(_details) && _controller.IsPinned);
            _widget.UpdateSettings(_settings);
            _widget.Update(reflowState with { LastQuota = null });
            await Task.Delay(50);
            Check("visible_details_quota_update_reflows_without_show_command", _details.Height < reflowNormalHeight && PanelTracksBall(_ball, _details) && DetailsHintFits(_details));
            _widget.Update(reflowState);
            OnLocalUsageUpdated(new LocalUsageSnapshot(DateOnly.FromDateTime(DateTime.Now), int.MaxValue, 4321, LocalUsageCoverage.Partial, "SAMPLE", DateTimeOffset.Now, 1, 1, 0));
            await Task.Delay(50);
            Check("visible_details_local_update_reflows_and_stays_pinned", _details.Height > reflowNormalHeight && PanelTracksBall(_ball, _details) && DetailsHintFits(_details) && _controller.IsPinned);
            _widget.Update(reflowState with { StatusText = string.Concat(Enumerable.Repeat("SAMPLE 长状态文字验证换行和工作区高度上限。", 35)) });
            await Task.Delay(50);
            var reflowScroll = (ScrollViewer)_details.FindName("PanelScroll");
            Check("visible_details_long_status_clamps_and_native_follows", reflowScroll.ScrollableHeight > 0 && PanelWithinWorkArea(_ball, _details) && PanelTracksBall(_ball, _details) && _controller.IsPinned);
            double stableReflowHeight = _details.Height;
            _widget.RefreshTheme();
            _widget.RefreshTheme();
            _widget.RefreshTheme();
            await Task.Delay(50);
            Check("content_reflow_does_not_activate_query_scan_or_repeat_resize", reflowActivations == 0 && provider.Reads == reflowReads && _localUsage.CompletedScans == reflowScans && _details.Height == stableReflowHeight);
            _widget.Update(reflowState);
            OnLocalUsageUpdated(new LocalUsageSnapshot(DateOnly.FromDateTime(DateTime.Now), 1, 4321, LocalUsageCoverage.Available, null, DateTimeOffset.Now, 1, 0, 0));
            await Task.Delay(50);
            RaiseKey(_ball, Key.Escape);
            Check("real_ball_escape_closes", !_details.IsVisible);
            _widget.RefreshTheme();
            await Task.Delay(40);
            Check("hidden_details_content_update_does_not_show_window", !_details.IsVisible && !_controller.IsPinned);

            int readsBefore = provider.Reads;
            int saved = 0;
            _ball.PositionPersisted += (_, _) => saved++;
            Pointer(surface, true);
            Point freeStart = _ball.PointToScreen(new Point());
            MouseButton(surface, true);
            Check("drag_capture_routes_move_and_release_to_ball_handlers", ReferenceEquals(Mouse.Captured, surface));
            _ball.ApplyDragPosition(_ball.DragStartScreen + new Vector(24, 16));
            Pointer(surface, true);
            MouseButton(surface, false);
            Pointer(surface, true);
            await Task.Delay(410);
            Check("real_drag_release_does_not_click_or_preview", !_details.IsVisible && !_controller.IsPinned);
            Check("real_drag_saves_once", saved == 1);
            Point freeEnd = _ball.PointToScreen(new Point());
            Check("real_drag_keeps_arbitrary_position_without_snap", Math.Abs(freeEnd.X - freeStart.X - 24) <= 1 && Math.Abs(freeEnd.Y - freeStart.Y - 16) <= 1);
            WidgetSettings savedPosition = await _settingsStore.LoadAsync();
            Check("real_drag_persists_position_and_dpi_scale", savedPosition.BallLeft is not null && savedPosition.BallTop is not null && savedPosition.BallPositionScale > 0);
            _ball.MoveTo(500, 300);
            _monitorService.RestoreBall(_ball, savedPosition);
            Point restoredPosition = _ball.PointToScreen(new Point());
            Check("real_saved_free_position_restores_in_physical_coordinates", Math.Abs(restoredPosition.X - freeEnd.X) <= 1 && Math.Abs(restoredPosition.Y - freeEnd.Y) <= 1);
            MouseButton(surface, true);
            surface.ReleaseMouseCapture();
            MouseButton(surface, false);
            Check("lost_capture_release_does_not_click_or_save", !_details.IsVisible && saved == 1);
            MouseButton(surface, true);
            // Cross only the top boundary: the left/right exposed edges dock deliberately.
            MoveDraggedBallTo(_ball, _ball.WindowStartScreen.X, -10000);
            geometry.Add(new { check = "top_release_before_settlement", ball = ReadGeometry(_ball), origin = _ball.WindowStartScreen, side = _monitorService.EvaluateDockRelease(_ball).ToString(), monitors = _monitorService.GetDockMonitors() });
            MouseButton(surface, false);
            Point clampedRelease = _ball.PointToScreen(new Point());
            System.Drawing.Rectangle releaseArea = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(_ball).Handle).WorkingArea;
            // The left/right exposed edges now dock deliberately; a top-edge crossing is a
            // noneligible ordinary release and still clamps the full reachable ball.
            Check("offscreen_drag_release_clamps_full_ball_on_noneligible_top_edge", releaseArea.Contains((int)clampedRelease.X, (int)clampedRelease.Y)
                && clampedRelease.X + _ball.ActualWidth * Services.MonitorService.GetScaleForWindow(_ball) <= releaseArea.Right + 1
                && clampedRelease.Y + _ball.ActualHeight * Services.MonitorService.GetScaleForWindow(_ball) <= releaseArea.Bottom + 1
                && _ball.DockSide == BallDockSide.None);
            Check("offscreen_drag_release_saves_exactly_once", saved == 2 && !_details.IsVisible);
            await Task.Delay(100);
            Pointer(surface, false);
            Pointer(surface, true);
            await Task.Delay(410);
            Check("real_hover_resumes_after_leaving_dragged_ball", _details.IsVisible && !_controller.IsPinned);
            _controller.CloseExplicitly();
            Pointer(surface, false);
            Check("hover_click_drag_never_query", provider.Reads == readsBefore);
            Check("interaction_never_triggers_local_scan", _localUsage!.CompletedScans == localScansBefore);

            // Regression: a visible pinned panel must follow the ball through every real
            // native drag step (it used to stay behind), settle at the release clamp and
            // flip sides near the left edge — without any official read, local scan or
            // new panel activation. Hidden panels stay hidden through movement.
            _controller.ShowPinned();
            await Task.Delay(120);
            Check("pinned_panel_visible_before_follow_drag", _details.IsVisible && _controller.IsPinned);
            int activatedDuringFollow = 0;
            _details.Activated += (_, _) => activatedDuringFollow++;
            int movedPanels = 0;
            System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(_details).Handle)!.AddHook(CountPanelMove);
            nint CountPanelMove(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
            {
                if (message == 0x0047 && lParam != 0 && (Marshal.PtrToStructure<NativeWindowPosition>(lParam).Flags & 2) == 0) { movedPanels++; }
                return 0;
            }
            int readsBeforeFollow = provider.Reads;
            int scansBeforeFollow = _localUsage.CompletedScans;
            System.Drawing.Rectangle followArea = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(_ball).Handle).WorkingArea;
            double followScale = Services.MonitorService.GetScaleForWindow(_ball);
            double midLeft = followArea.Left + (followArea.Width * 0.6);
            double midTop = followArea.Top + (followArea.Height * 0.15);

            MouseButton(surface, true);
            int savesBeforeFollow = saved;
            WidgetSettings settingsBeforeFollow = await _settingsStore.LoadAsync();
            int movesBeforeStep = movedPanels;
            MoveDraggedBallTo(_ball, midLeft, midTop);
            Check("pinned_panel_follows_first_drag_step", PanelTracksBall(_ball, _details));
            Check("first_follow_step_places_panel_once_without_origin_jitter", movedPanels == movesBeforeStep + 1);
            Check("panel_stays_pinned_and_visible_during_drag", _details.IsVisible && _controller.IsPinned);
            movesBeforeStep = movedPanels;
            MoveDraggedBallTo(_ball, midLeft + (120 * followScale), midTop + (60 * followScale));
            Check("pinned_panel_follows_second_drag_step", PanelTracksBall(_ball, _details));
            Check("second_follow_step_places_panel_once_without_origin_jitter", movedPanels == movesBeforeStep + 1);
            Check("follow_steps_do_not_save_position", saved == savesBeforeFollow && await _settingsStore.LoadAsync() == settingsBeforeFollow);
            MouseButton(surface, false);
            await Task.Delay(150);
            Check("pinned_panel_follows_release_clamp", PanelTracksBall(_ball, _details));
            Check("follow_drag_release_saves_once", saved == savesBeforeFollow + 1);

            double edgeLeft = followArea.Left + (6 * followScale);
            double edgeTop = followArea.Top + (30 * followScale);
            MouseButton(surface, true);
            MoveDraggedBallTo(_ball, edgeLeft, edgeTop);
            Check("pinned_panel_follows_to_left_edge_on_right_side", PanelTracksBall(_ball, _details) && PanelOnRightOfBall(_ball, _details));
            MouseButton(surface, false);
            await Task.Delay(150);
            Check("pinned_panel_follows_left_edge_release_clamp", PanelTracksBall(_ball, _details));

            MouseButton(surface, true);
            MoveDraggedBallTo(_ball, followArea.Right - (80 * followScale), edgeTop);
            Check("pinned_panel_follows_to_right_edge_on_left_side", PanelTracksBall(_ball, _details) && !PanelOnRightOfBall(_ball, _details));
            MouseButton(surface, false);
            await Task.Delay(100);
            WidgetSettings visibleRestore = await _settingsStore.LoadAsync();
            MouseButton(surface, true);
            MoveDraggedBallTo(_ball, _ball.WindowStartScreen.X, -10000);
            MouseButton(surface, false);
            await Task.Delay(100);
            Check("visible_pinned_panel_settles_at_actual_offscreen_release_clamp", PanelTracksBall(_ball, _details) && _details.IsVisible && _controller.IsPinned);
            _monitorService.RestoreBall(_ball, visibleRestore);
            Check("visible_pinned_panel_follows_native_restore", PanelTracksBall(_ball, _details));
            Check("following_does_not_reactivate_details", activatedDuringFollow == 0);

            Check("panel_follow_never_queries_official", provider.Reads == readsBeforeFollow);
            Check("panel_follow_never_scans_local", _localUsage.CompletedScans == scansBeforeFollow);

            _controller.CloseExplicitly();
            Check("panel_hidden_before_hidden_drag", !_details.IsVisible);
            Point hiddenBase = _ball.PointToScreen(new Point());
            MouseButton(surface, true);
            MoveDraggedBallTo(_ball, hiddenBase.X - (60 * followScale), hiddenBase.Y - (40 * followScale));
            MouseButton(surface, false);
            await Task.Delay(140);
            Check("hidden_panel_stays_hidden_through_drag", !_details.IsVisible);

            WidgetSettings followRestore = await _settingsStore.LoadAsync();
            _ball.MoveTo(500, 300);
            _monitorService.RestoreBall(_ball, followRestore);
            _controller.ShowPinned();
            await Task.Delay(120);
            Check("panel_opens_beside_restored_ball_position", _details.IsVisible && PanelTracksBall(_ball, _details));
            _controller.CloseExplicitly();
            Pointer(surface, false);

            // The header gear must raise the same settings command the tray uses.
            int readsBeforeSettingsOpening = provider.Reads;
            int scansBeforeSettingsOpening = _localUsage.CompletedScans;
            Pointer(surface, true);
            await Task.Delay(410);
            CloseSettingsSurface();
            ((Button)_details!.FindName("SettingsGear")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("real_gear_opens_settings", _settingsWindow?.IsVisible == true);
            await RecordSettingsOpeningAsync("first-gear");
            Button settingsClose = SettingsCloseButton(_settingsWindow!);
            ((UIElement)Keyboard.FocusedElement).MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            Check("settings_keyboard_tab_reaches_accessible_close", settingsClose.IsKeyboardFocused && System.Windows.Automation.AutomationProperties.GetName(settingsClose) == "关闭设置");
            await RecordSettingsOpeningAsync("intentional-keyboard-focus", false);
            settingsClose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("real_settings_x_hides_reusable_window", !_settingsWindow!.IsVisible);
            ((Button)_details.FindName("SettingsGear")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await RecordSettingsOpeningAsync("reopen-after-x");
            RaiseKey(_settingsWindow, Key.Escape);
            Check("real_settings_escape_hides_reusable_window", !_settingsWindow.IsVisible);
            _tray!.Menu.Items[2].PerformClick();
            await RecordSettingsOpeningAsync("tray-after-escape");
            settingsClose.Focus();
            RaiseKey(settingsClose, Key.Enter);
            Check("settings_keyboard_enter_activates_close", !_settingsWindow.IsVisible);
            _tray.Menu.Items[2].PerformClick();
            await RecordSettingsOpeningAsync("tray-after-keyboard-close");
            // Drive WPF's real mouse-over target transition, scoped to this fixture.
            // This exercises the rendered hover trigger without moving/injecting the user's cursor.
            var changeMouseOver = typeof(MouseDevice).GetMethod("ChangeMouseOver", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new NotSupportedException("This WPF runtime does not expose the verification-only mouse-over transition.");
            IInputElement? previousMouseOver = Mouse.DirectlyOver;
            try
            {
                changeMouseOver.Invoke(Mouse.PrimaryDevice, [settingsClose, Environment.TickCount]);
                await RecordSettingsOpeningAsync("intentional-hover", null);
                var hoverRoot = (Border)settingsClose.Template.FindName("Root", settingsClose);
                Check("settings_intentional_hover_keeps_close_feedback", settingsClose.IsMouseOver && !settingsClose.IsKeyboardFocused && hoverRoot.Background is System.Windows.Media.SolidColorBrush hoverBrush && hoverBrush.Color.A > 0);
            }
            finally { changeMouseOver.Invoke(Mouse.PrimaryDevice, [previousMouseOver, Environment.TickCount]); }
            Check("settings_hover_fixture_restores_cached_mouse_target", ReferenceEquals(Mouse.DirectlyOver, previousMouseOver));
            var editingThreshold = (TextBox)_settingsWindow.FindName("ThresholdInput");
            _settingsViewModel.NotificationsEnabled = true;
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            editingThreshold.Focus();
            editingThreshold.Text = "27";
            _details.Activate();
            _settingsWindow.Activate();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Check("settings_ordinary_reactivation_preserves_editing_focus_and_draft", editingThreshold.IsKeyboardFocused && editingThreshold.Text == "27" && _settingsViewModel.LowQuotaThreshold == 27);
            _themeService!.Apply(CodexQuota.Core.Models.ThemeMode.Dark);
            _widget.RefreshTheme();
            _settingsViewModel.LowQuotaThreshold = 28;
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Check("settings_theme_and_data_updates_preserve_editing_focus", editingThreshold.IsKeyboardFocused && editingThreshold.Text == "28");
            _themeService.Apply(CodexQuota.Core.Models.ThemeMode.Light);
            Check("settings_open_focus_theme_and_hover_never_read_or_scan", provider.Reads == readsBeforeSettingsOpening && _localUsage.CompletedScans == scansBeforeSettingsOpening);
            CloseSettingsSurface();
            _controller.CloseExplicitly();
            Pointer(surface, false);
            await Task.Delay(300);

            // A reused hidden details window remembers its last focused element, so a gear
            // that was focused when the panel closed must not come back highlighted on any
            // hidden -> visible path (hover preview, pinned open, gear click).
            _controller.ShowPinned();
            await Task.Delay(120);
            var reuseGear = (Button)_details!.FindName("SettingsGear");
            reuseGear.Focus();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Check("focused_gear_renders_the_selected_look", reuseGear.IsKeyboardFocused && !DetailGearIsNeutral(reuseGear));
            _controller.CloseExplicitly();
            await Task.Delay(300);
            Pointer(surface, true);
            await Task.Delay(410);
            Check("preview_reopen_clears_remembered_gear_focus", _details.IsVisible && !reuseGear.IsKeyboardFocused &&
                ReferenceEquals(Keyboard.FocusedElement, _details.FindName("PanelRoot")) && DetailGearIsNeutral(reuseGear));
            Pointer(surface, false);
            await Task.Delay(300);
            _controller.ShowPinned();
            await Task.Delay(120);
            Check("pinned_open_clears_remembered_gear_focus", !reuseGear.IsKeyboardFocused &&
                ReferenceEquals(Keyboard.FocusedElement, _details.FindName("PanelRoot")) && DetailGearIsNeutral(reuseGear));
            reuseGear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Check("gear_click_leaves_no_selected_gear_behind", !reuseGear.IsKeyboardFocused && DetailGearIsNeutral(reuseGear));
            CloseSettingsSurface();
            _controller.CloseExplicitly();
            await Task.Delay(300);

            _controller.ShowPinned();
            ((Button)_details.FindName("SettingsGear")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("gear_hides_and_unpins_details_while_settings_open", !_controller.IsPinned && !_details.IsVisible && _settingsWindow?.IsVisible == true);
            _outsideClicks!.ButtonDown += OnObservedButtonDown;
            _outsideClicks!.ObserveButtonDown(_settingsWindow!.PointToScreen(new Point(24, 24)));
            await Task.Delay(40);
            Check("settings_click_keeps_details_hidden_and_unpinned", !_controller.IsPinned && !_details.IsVisible);
            _outsideClicks.ButtonDown -= OnObservedButtonDown;
            var refreshCombo = (ComboBox)_settingsWindow.FindName("RefreshCombo");
            refreshCombo.IsDropDownOpen = true;
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var refreshPopup = (System.Windows.Controls.Primitives.Popup)refreshCombo.Template.FindName("PART_Popup", refreshCombo);
            bool popupOwned = false;
            Point expectedPopupPoint = NativePopupCenter(refreshPopup.Child);
            popupObservations.Add(ReadPopupSurface(refreshPopup.Child, "refresh-open"));
            await WaitUntilAsync(() => PopupOwnsNativeCenter(refreshPopup.Child), TimeSpan.FromSeconds(2));
            popupObservations.Add(ReadPopupSurface(refreshPopup.Child, "refresh-after-native-ready-wait"));
            void CapturePopupOwner(Point point, nint target, bool owned)
            {
                // Only the explicitly driven native coordinate belongs to this fixture.
                // Real user clicks may happen while --verify is exercising its windows.
                if (point != expectedPopupPoint) { return; }
                popupOwned = owned;
                GetWindowThreadProcessId(target, out uint targetOwner);
                GetWindowRect(target, out NativeRect targetRect);
                popupObservations.Add(new { point, target = target.ToInt64(), targetOwner, owned, targetRect = new { left = targetRect.Left, top = targetRect.Top, right = targetRect.Right, bottom = targetRect.Bottom }, details = ReadGeometry(_details), settings = ReadGeometry(_settingsWindow), pinned = _controller.IsPinned });
                OnObservedButtonDown(point, target, owned);
            }
            _outsideClicks.ButtonDown += CapturePopupOwner;
            _outsideClicks.ObserveButtonDown(expectedPopupPoint);
            await Task.Delay(40);
            Check("settings_dropdown_owned_click_keeps_details_hidden", popupOwned && !_controller.IsPinned && !_details.IsVisible);
            refreshCombo.IsDropDownOpen = false;
            var ballMenu = ((FrameworkElement)surface).ContextMenu!;
            ballMenu.PlacementTarget = surface;
            ballMenu.IsOpen = true;
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            popupOwned = false;
            expectedPopupPoint = NativePopupCenter(ballMenu);
            popupObservations.Add(ReadPopupSurface(ballMenu, "context-open"));
            await WaitUntilAsync(() => PopupOwnsNativeCenter(ballMenu), TimeSpan.FromSeconds(2));
            popupObservations.Add(ReadPopupSurface(ballMenu, "context-after-native-ready-wait"));
            _outsideClicks.ObserveButtonDown(expectedPopupPoint);
            await Task.Delay(40);
            Check("ball_context_menu_owned_click_keeps_details_hidden_during_settings", popupOwned && !_controller.IsPinned && !_details.IsVisible);
            ballMenu.IsOpen = false;
            _outsideClicks.ButtonDown -= CapturePopupOwner;
            int settingsActivations = 0;
            _settingsWindow!.Activated += (_, _) => settingsActivations++;
            nint foregroundBeforeSettingsFollow = GetForegroundWindow();
            int readsBeforeSettingsFollow = provider.Reads;
            int scansBeforeSettingsFollow = _localUsage.CompletedScans;
            int savesBeforeSettingsFollow = saved;
            refreshCombo.Focus();
            MouseButton(surface, true);
            MoveDraggedBallTo(_ball, midLeft, midTop);
            Check("visible_settings_follows_native_drag_step", PanelTracksBall(_ball, _settingsWindow));
            Check("settings_follow_keeps_details_hidden_and_unpinned", !_details.IsVisible && !_controller.IsPinned);
            Check("settings_follow_keeps_foreground_without_activation", settingsActivations == 0 && GetForegroundWindow() == foregroundBeforeSettingsFollow);
            Check("settings_native_follow_preserves_control_focus", refreshCombo.IsKeyboardFocusWithin);
            Check("settings_follow_never_queries_scans_or_saves_per_step", provider.Reads == readsBeforeSettingsFollow && _localUsage.CompletedScans == scansBeforeSettingsFollow && saved == savesBeforeSettingsFollow);
            MouseButton(surface, false);
            await Task.Delay(100);
            CloseSettingsSurface();
            _controller.CloseExplicitly();

            _tray!.Menu.Items[0].PerformClick();
            Check("real_tray_details_pins", _details.IsVisible && _controller.IsPinned);
            _settings = _settings with { CodexCliPath = @"C:\verification-fixture\codex.exe" };
            _tray.Menu.Items[2].PerformClick();
            Check("real_tray_settings_shows", _settingsWindow?.IsVisible == true);
            WidgetSettings settingsBeforePreview = await _settingsStore.LoadAsync();
            int readsBeforePreview = provider.Reads;
            int scansBeforePreview = _localUsage.CompletedScans;
            // Only the three ball properties preview before Save; theme and Details flags
            // stay retained, nothing is written and no new surface is created.
            _settingsViewModel.OpacityPercent = 60;
            Check("preview_opacity_applies_before_save", Math.Abs(_ball!.Opacity - 0.6) < 0.01);
            _settingsViewModel.BallSizePercent = 125;
            Check("preview_size_applies_before_save", BallHasPhysicalSize(_ball, 90));
            _settingsViewModel.ShowTodayUsage = false;
            _settingsViewModel.SelectedTheme = _settingsViewModel.ThemeOptions[2];
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Check("display_flags_and_theme_stay_retained_before_save", _widget.ShowTodayUsage && !_themeService!.IsDark);
            Check("preview_writes_nothing", await _settingsStore.LoadAsync() == settingsBeforePreview);
            Check("preview_reads_or_scans_nothing", provider.Reads == readsBeforePreview && _localUsage.CompletedScans == scansBeforePreview);
            Check("preview_keeps_details_hidden_and_unpinned", !_details.IsVisible && !_controller.IsPinned);
            ((Button)_settingsWindow!.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            MouseButton(surface, true);
            _ball.ApplyDragPosition(_ball.PointToScreen(Mouse.GetPosition(_ball)) + new Vector(20, 12));
            MouseButton(surface, false);
            await Task.Delay(200);
            WidgetSettings savedSettings = await _settingsStore.LoadAsync();
            Check("real_settings_save_applies_opacity_and_theme", Math.Abs(_ball.Opacity - 0.6) < 0.01 && _themeService!.IsDark && savedSettings.Theme == CodexQuota.Core.Models.ThemeMode.Dark && Math.Abs(savedSettings.Opacity - 0.6) < 0.01);
            Check("ordinary_settings_save_preserves_loaded_legacy_cli_path", savedSettings.CodexCliPath == @"C:\verification-fixture\codex.exe");
            Check("settings_scale_applies_to_actual_ball", BallHasPhysicalSize(_ball, 90) && savedSettings.BallSizePercent == 125);
            Check("settings_today_switch_applies_to_visual_card", _details.FindName("DailyCard") is FrameworkElement dailyCard && dailyCard.Visibility == Visibility.Collapsed);
            Check("settings_save_reflows_details_and_preserves_native_fit", DetailsHintFits(_details) && PanelWithinWorkArea(_ball, _details) && _controller.IsPinned);
            Check("settings_and_details_share_width_and_fit_independently", Math.Abs(_settingsWindow!.ActualWidth - _details.ActualWidth) < 0.1 && _details.ActualHeight < _settingsWindow.ActualHeight && PanelTracksBall(_ball, _details));
            Check("drag_keeps_new_preferences_and_marks_custom", savedSettings.Placement == BallPlacement.Custom && savedSettings.BallSizePercent == 125 && !savedSettings.ShowTodayUsage);
            // The release above happened inside the editing session: draft-only, no write.
            Check("editing_session_drag_writes_nothing", await _settingsStore.LoadAsync() == savedSettings);
            var positionCombo = (ComboBox)_settingsWindow.FindName("PositionCombo");
            foreach (BallPlacement placement in new[] { BallPlacement.TopLeft, BallPlacement.TopRight, BallPlacement.BottomLeft, BallPlacement.BottomRight })
            {
                positionCombo.SelectedItem = _settingsViewModel.PlacementOptions.First(option => option.Placement == placement);
                await SaveSettingsAsync();
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                double scale = Services.MonitorService.GetScaleForWindow(_ball);
                System.Drawing.Rectangle area = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(_ball).Handle).WorkingArea;
                double expectedX = placement is BallPlacement.TopLeft or BallPlacement.BottomLeft ? area.Left + 16 * scale : area.Right - _ball.Width * scale - 16 * scale;
                double expectedY = placement is BallPlacement.TopLeft or BallPlacement.TopRight ? area.Top + 16 * scale : area.Bottom - _ball.Height * scale - 16 * scale;
                Point corner = _ball.PointToScreen(new Point());
                WidgetSettings persisted = await _settingsStore.LoadAsync();
                Check($"position_{placement}_moves_and_persists_actual_corner", Math.Abs(corner.X - expectedX) < 2 && Math.Abs(corner.Y - expectedY) < 2 && persisted.Placement == placement);
                _monitorService.RestoreBall(_ball, persisted);
                _monitorService.ApplyPlacement(_ball, persisted.Placement);
                Check($"position_{placement}_restores_from_saved_preferences", _ball.PointToScreen(new Point()) == corner);
            }
            foreach (int size in new[] { 75, 150 })
            {
                ((Slider)_settingsWindow.FindName("SizeSlider")).Value = size;
                await SaveSettingsAsync();
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                double width = 72 * size / 100d;
                Check($"size_{size}_scales_window_and_input_surface", BallHasPhysicalSize(_ball, width) &&
                    _ball.InputHitTest(new Point(width / 2, width / 2)) is FrameworkElement { Name: "Ball" } &&
                    _ball.InputHitTest(new Point(width - 3 * size / 100d, width / 2)) is FrameworkElement { Name: "Ball" });
            }
            _settingsViewModel.BallSizePercent = 125;
            await SaveSettingsAsync();
            _settingsViewModel.BallSizePercent = 150;
            double widthBeforeFailedSave = _ball.Width;
            using (var lockedSettings = new FileStream(_settingsStore.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await SaveSettingsAsync();
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Check("settings_save_failure_shows_status_and_preserves_runtime", _ball.Width == widthBeforeFailedSave &&
                    ((TextBlock)_settingsWindow.FindName("SettingsStatus")).Text.Contains("保存设置失败", StringComparison.Ordinal));
            }
            _settingsViewModel.BallSizePercent = 125;
            await SaveSettingsAsync();
            var nativeNotifyIcon = (System.Windows.Forms.NotifyIcon)typeof(Services.TrayService).GetField("_notifyIcon", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(_tray)!;
            nativeNotifyIcon.Visible = false;
            WidgetSettings notificationSettings = _settings;
            _settings = _settings with { NotificationsEnabled = true, LowQuotaThreshold = 20 };
            RefreshState notificationState = _widget.State with { Status = RefreshStatus.Fresh, FailureKind = ProviderFailureKind.None };
            QuotaSnapshot notificationQuota = notificationState.LastQuota!;
            QuotaBucket notificationMain = notificationQuota.Buckets[0];
            QuotaWindow fiveHour = notificationMain.Windows[0] with { UsedPercent = 20, RemainingPercent = 80 };
            QuotaWindow week = fiveHour with { Slot = "secondary", DurationMinutes = 10080, UsedPercent = 90, RemainingPercent = 10 };
            RefreshState NotificationSample(QuotaWindow selected, QuotaWindow weekly) => notificationState with
            {
                LastQuota = notificationQuota with { Buckets = [notificationMain with { Windows = [selected, weekly] }] },
            };
            EvaluateQuotaNotification(notificationState with { LastQuota = null });
            nativeNotifyIcon.BalloonTipText = "verification sentinel";
            EvaluateQuotaNotification(NotificationSample(fiveHour, week));
            Check("actual_app_fivehour80_week10_does_not_notify", nativeNotifyIcon.BalloonTipText == "verification sentinel");
            fiveHour = fiveHour with { UsedPercent = 81, RemainingPercent = 19 };
            EvaluateQuotaNotification(NotificationSample(fiveHour, week with { UsedPercent = 99, RemainingPercent = 1 }));
            Check("actual_app_notification_uses_selected19_not_week1", nativeNotifyIcon.BalloonTipTitle == "Codex 额度提醒" && nativeNotifyIcon.BalloonTipText == "5 小时额度剩余 19%，已低于设定的 20%。" && nativeNotifyIcon.BalloonTipIcon == System.Windows.Forms.ToolTipIcon.Warning);
            nativeNotifyIcon.BalloonTipText = "verification repetition sentinel";
            EvaluateQuotaNotification(NotificationSample(fiveHour, week with { ResetsAt = week.ResetsAt!.Value.AddDays(1) }));
            Check("actual_app_weekly_metadata_does_not_repeat_alert", nativeNotifyIcon.BalloonTipText == "verification repetition sentinel");
            _settings = notificationSettings;
            EvaluateQuotaNotification(notificationState with { LastQuota = null });
            nativeNotifyIcon.Visible = true;
            RaiseKey(_settingsWindow!, Key.Escape);
            Check("real_settings_escape_hides_without_exit", _settingsWindow?.IsVisible == false && _ball.IsVisible);

            await VerifySettingsOrderingAsync(Check);

            // ---- Guarded leave flow: one Settings-owned confirmation for unsaved changes ----
            SettingsLeaveDialog? VisiblePrompt() => Application.Current.Windows.OfType<SettingsLeaveDialog>().FirstOrDefault(window => window.IsVisible);
            void InteractWithPrompt(Action<SettingsLeaveDialog> interact) =>
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
                {
                    if (VisiblePrompt() is { } prompt) { interact(prompt); }
                });
            Button PromptButton(SettingsLeaveDialog prompt, string accessibleName) => PromptActionButton(prompt, accessibleName);

            _tray.Menu.Items[2].PerformClick();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Check("clean_reopen_loads_retained_values", _settingsWindow!.IsVisible && _settingsViewModel.BallSizePercent == 125 && !_settingsViewModel.HasPendingStartupChange);
            SettingsCloseButton(_settingsWindow).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (_leaveTask is not null) { await _leaveTask; }
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Check("clean_close_hides_without_prompt", !_settingsWindow.IsVisible && VisiblePrompt() is null);

            _tray.Menu.Items[2].PerformClick();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            _settingsViewModel.BallSizePercent = 100;
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Check("dirty_draft_previews_ball_size", BallHasPhysicalSize(_ball!, 72));
            int promptsDuringModal = -1;
            InteractWithPrompt(_ =>
            {
                promptsDuringModal = Application.Current.Windows.OfType<SettingsLeaveDialog>().Count(window => window.IsVisible);
                SettingsCloseButton(_settingsWindow!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            InteractWithPrompt(prompt => PromptButton(prompt, "继续编辑").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            SettingsCloseButton(_settingsWindow).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (_leaveTask is not null) { await _leaveTask; }
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Check("dirty_close_opens_one_owned_confirmation", promptsDuringModal == 1 && VisiblePrompt() is null && _settingsWindow.IsVisible);
            Check("continue_keeps_draft_preview_and_page", _settingsViewModel.BallSizePercent == 100 && BallHasPhysicalSize(_ball!, 72));

            InteractWithPrompt(prompt => RaiseKey(prompt, Key.Escape));
            SettingsCloseButton(_settingsWindow).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (_leaveTask is not null) { await _leaveTask; }
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Check("prompt_escape_keeps_editing", VisiblePrompt() is null && _settingsWindow.IsVisible && _settingsViewModel.BallSizePercent == 100);

            WidgetSettings baselineBeforeDiscard = await _settingsStore.LoadAsync();
            InteractWithPrompt(prompt => PromptButton(prompt, "不保存离开").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            SettingsCloseButton(_settingsWindow).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (_leaveTask is not null) { await _leaveTask; }
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Check("discard_restores_retained_geometry_and_hides",
                !_settingsWindow.IsVisible && VisiblePrompt() is null && BallHasPhysicalSize(_ball!, 90) && _settingsViewModel.BallSizePercent == 125);
            Check("discard_writes_nothing", await _settingsStore.LoadAsync() == baselineBeforeDiscard);

            _tray.Menu.Items[2].PerformClick();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            _settingsViewModel.BallSizePercent = 110;
            InteractWithPrompt(prompt => PromptButton(prompt, "保存并离开").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            SettingsCloseButton(_settingsWindow).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (_leaveTask is not null) { await _leaveTask; }
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Check("save_and_leave_closes_on_full_success",
                !_settingsWindow.IsVisible && VisiblePrompt() is null && (await _settingsStore.LoadAsync()).BallSizePercent == 110 && BallHasPhysicalSize(_ball!, 110 * 72 / 100d));

            _tray.Menu.Items[2].PerformClick();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            _settingsViewModel.BallSizePercent = 75;
            using (FileStream locked = new FileStream(_settingsStore.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                SettingsSaveOutcome failedOutcome = await SaveSettingsAsync();
                Check("save_failure_keeps_page_and_preview", failedOutcome == SettingsSaveOutcome.FileFailure && _settingsWindow!.IsVisible &&
                    BallHasPhysicalSize(_ball!, 54) && ((TextBlock)_settingsWindow.FindName("SettingsStatus")).Text.Contains("保存设置失败", StringComparison.Ordinal));
            }

            InteractWithPrompt(prompt => PromptButton(prompt, "保存并离开").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            SettingsCloseButton(_settingsWindow).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (_leaveTask is not null) { await _leaveTask; }
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Check("retry_save_and_leave_closes_on_success", !_settingsWindow.IsVisible && (await _settingsStore.LoadAsync()).BallSizePercent == 75);

            // The ordinary Save button itself must close onto the saved ball on full success,
            // and must keep the page with a retryable draft when the file write fails.
            _tray.Menu.Items[2].PerformClick();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            _settingsViewModel.BallSizePercent = 130;
            Button saveButton = SettingsSaveButton(_settingsWindow!);
            saveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => _pendingSave is null || _pendingSave.IsCompleted, TimeSpan.FromSeconds(3));
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Check("ordinary_save_button_closes_onto_the_saved_ball",
                !_settingsWindow!.IsVisible && VisiblePrompt() is null && _ball!.IsVisible && _settingsViewModel.BallSizePercent == 130 &&
                BallHasPhysicalSize(_ball, 130 * 72 / 100d) && (await _settingsStore.LoadAsync()).BallSizePercent == 130);

            _tray.Menu.Items[2].PerformClick();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            _settingsViewModel.BallSizePercent = 95;
            using (FileStream lockedSave = new FileStream(_settingsStore.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                saveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitUntilAsync(() => _pendingSave is null || _pendingSave.IsCompleted, TimeSpan.FromSeconds(3));
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Check("ordinary_save_button_keeps_the_retryable_page_on_file_failure", _settingsWindow.IsVisible &&
                    _settingsViewModel.BallSizePercent == 95 && BallHasPhysicalSize(_ball, 95 * 72 / 100d) &&
                    ((TextBlock)_settingsWindow.FindName("SettingsStatus")).Text.Contains("保存设置失败", StringComparison.Ordinal));
            }

            // Return the retained settings to the saved 130% value and leave the page clean
            // for the following partial-startup case.
            _settingsViewModel.BallSizePercent = 130;
            await SaveSettingsAsync();
            RaiseKey(_settingsWindow, Key.Escape);
            if (_leaveTask is not null) { await _leaveTask; }
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            // JSON success with a denied startup write: explicit partial outcome, page open,
            // file retained, retryable intent, registry untouched.
            _tray.Menu.Items[2].PerformClick();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            SettingsViewModel savedVm = _settingsViewModel;
            var deniedStartup = new DeniedStartup(enabled: false);
            var partialVm = new SettingsViewModel(deniedStartup, _settingsStore);
            BeginSettingsBaselineLoad();
            try { partialVm.LoadFrom(_settings, Environment.ProcessPath!, _cliStatusText); }
            finally { _applyingBaseline = false; }
            partialVm.StartAtLogin = true;
            _settingsViewModel = partialVm;
            _settingsWindow!.DataContext = partialVm;
            SettingsSaveOutcome partialOutcome = await SaveSettingsAsync();
            Check("partial_startup_failure_reports_explicit_partial", partialOutcome == SettingsSaveOutcome.StartupPartial
                && partialVm.HasPendingStartupChange && !deniedStartup.IsEnabled()
                && ((TextBlock)_settingsWindow.FindName("SettingsStatus")).Text.Contains("开机启动失败", StringComparison.Ordinal));
            Check("partial_startup_failure_still_retains_json", await _settingsStore.LoadAsync() == _settings);
            Check("partial_startup_failure_keeps_page_open", _settingsWindow.IsVisible);
            _settingsViewModel = savedVm;
            _settingsWindow.DataContext = savedVm;
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            RaiseKey(_settingsWindow, Key.Escape);
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Check("restored_session_still_clean_closes", !_settingsWindow.IsVisible);

            _controller.CloseExplicitly();
            Pointer(surface, false);

            // ---- Deliberate left/right edge docking on the same HWND ----
            _controller.PointerExitedPanel();
            double dockedDiameter = _ball!.BallSize;
            System.Drawing.Rectangle dockArea = System.Windows.Forms.Screen.AllScreens.OrderBy(screen => screen.WorkingArea.Left).First().WorkingArea;
            Services.MonitorService.MoveDraggedWindow(_ball, new Point(dockArea.Left + 300, dockArea.Top + 200));
            await Task.Delay(100);
            double dockScale = Services.MonitorService.GetScaleForWindow(_ball);
            int readsBeforeDock = provider.Reads;
            int scansBeforeDock = _localUsage.CompletedScans;
            int savesBeforeDock = saved;
            WidgetSettings beforeDock = await _settingsStore.LoadAsync();
            UIElement strip = (UIElement)_ball.FindName("DockStrip");

            MouseButton(surface, true);
            MoveDraggedBallTo(_ball, dockArea.Left - (dockedDiameter * dockScale), dockArea.Top + (200 * dockScale));
            geometry.Add(new { check = "left_release_before_settlement", ball = ReadGeometry(_ball), side = _monitorService.EvaluateDockRelease(_ball).ToString(), session = _settingsSessionActive, controllerSide = _dockController!.Side.ToString() });
            MouseButton(surface, false);
            await Task.Delay(150);
            Check("offscreen_left_release_docks_strip", _ball.DockSide == BallDockSide.Left && _ball.CurrentPresentation == BallDockPresentation.Collapsed);
            GetWindowRect(new System.Windows.Interop.WindowInteropHelper(_ball).Handle, out NativeRect stripRect);
            double stripWidthDip = 16 * (dockedDiameter / 72d);
            double stripHeightDip = 52 * (dockedDiameter / 72d);
            double stripWidthExpected = Math.Round(stripWidthDip * dockScale);
            double stripHeightExpected = Math.Round(stripHeightDip * dockScale);
            Check("strip_native_geometry_is_slim_and_at_edge",
                Math.Abs(stripRect.Left - dockArea.Left) <= 1 &&
                Math.Abs(stripRect.Right - stripRect.Left - stripWidthExpected) <= 1 &&
                Math.Abs(stripRect.Bottom - stripRect.Top - stripHeightExpected) <= 1 &&
                stripRect.Top >= dockArea.Top - 1 && stripRect.Bottom <= dockArea.Bottom + 1);
            Check("strip_hit_surface_targets_strip_lane",
                ReferenceEquals(_ball.InputHitTest(new Point(stripWidthDip / 2, stripHeightDip / 2)), strip));
            WidgetSettings dockedPrefs = await _settingsStore.LoadAsync();
            Check("dock_release_persists_side_and_anchor_once",
                saved == savesBeforeDock + 1 && dockedPrefs.DockSide == BallDockSide.Left && dockedPrefs.Placement == BallPlacement.Custom &&
                dockedPrefs.BallLeft is { } savedLeft && Math.Abs(savedLeft - (dockArea.Left / dockScale)) <= 2);
            Check("dock_transition_writes_no_extra_reads_or_scans", provider.Reads == readsBeforeDock && _localUsage.CompletedScans == scansBeforeDock);

            Pointer(strip, true);
            await Task.Delay(80);
            Check("strip_hover_reveals_inward_full_ball", _ball.CurrentPresentation == BallDockPresentation.Revealed && BallHasPhysicalSize(_ball, dockedDiameter));
            GetWindowRect(new System.Windows.Interop.WindowInteropHelper(_ball).Handle, out NativeRect revealedRect);
            Check("revealed_ball_stays_inside_work_area", Math.Abs(revealedRect.Left - dockArea.Left) <= 1 && revealedRect.Right <= dockArea.Right + 1);
            Pointer(strip, false);
            Pointer(strip, true);
            await Task.Delay(320);
            Check("lane_hysteresis_keeps_reveal_stationary", _ball.CurrentPresentation == BallDockPresentation.Revealed);
            Pointer(strip, false);
            await Task.Delay(700);
            Check("leave_grace_recollapses_strip", _ball.CurrentPresentation == BallDockPresentation.Collapsed && _ball.DockSide == BallDockSide.Left);
            Check("hover_cycle_writes_and_reads_nothing", provider.Reads == readsBeforeDock && _localUsage.CompletedScans == scansBeforeDock && await _settingsStore.LoadAsync() == dockedPrefs);

            _controller.ShowPinned();
            await Task.Delay(120);
            Pointer(strip, true);
            await Task.Delay(120);
            Check("docked_reveal_follows_pinned_details", _details.IsVisible && PanelTracksBall(_ball, _details));
            _controller.CloseExplicitly();
            Pointer(strip, false);
            await Task.Delay(400);

            MouseButton(strip, true);
            Check("strip_press_reveals_before_drag", _ball.CurrentPresentation == BallDockPresentation.Revealed);
            MoveDraggedBallTo(_ball, dockArea.Left + (dockArea.Width * 0.4), dockArea.Top + (300 * dockScale));
            MouseButton(strip, false);
            await Task.Delay(150);
            Check("drag_out_restores_ordinary_ball_and_persists_once",
                _ball.DockSide == BallDockSide.None && _ball.CurrentPresentation == BallDockPresentation.Floating && saved == savesBeforeDock + 2);
            WidgetSettings undockedPrefs = await _settingsStore.LoadAsync();
            Check("drag_out_persists_undocked_custom", undockedPrefs.DockSide == BallDockSide.None && undockedPrefs.Placement == BallPlacement.Custom);

            MouseButton(surface, true);
            MoveDraggedBallTo(_ball, dockArea.Left + (2 * dockScale), dockArea.Top + (300 * dockScale));
            MouseButton(surface, false);
            await Task.Delay(100);
            Check("near_edge_below_threshold_release_stays_ordinary", _ball.DockSide == BallDockSide.None && saved == savesBeforeDock + 3);

            MouseButton(surface, true);
            MoveDraggedBallTo(_ball, dockArea.Left - (dockedDiameter * dockScale), dockArea.Top + (300 * dockScale));
            MouseButton(surface, false);
            await Task.Delay(120);
            Check("deliberate_second_release_redocks", _ball.DockSide == BallDockSide.Left);
            WidgetSettings redockedPrefs = await _settingsStore.LoadAsync();
            Pointer(strip, true);
            MouseButton(strip, true);
            MoveDraggedBallTo(_ball, dockArea.Left + (dockArea.Width * 0.5), dockArea.Top + (400 * dockScale));
            Mouse.Captured?.ReleaseMouseCapture();
            MouseButton(strip, false);
            await Task.Delay(120);
            Check("docked_lost_capture_keeps_moved_ball_without_write",
                _ball.DockSide == BallDockSide.None && _ball.CurrentPresentation == BallDockPresentation.Floating &&
                saved == savesBeforeDock + 4 && await _settingsStore.LoadAsync() == redockedPrefs);

            _ball.MoveTo(500, 300);
            _monitorService.RestoreBall(_ball, redockedPrefs);
            _ball.AdoptDock(redockedPrefs.DockSide);
            _dockController!.Dock(redockedPrefs.DockSide);
            await Task.Delay(120);
            GetWindowRect(new System.Windows.Interop.WindowInteropHelper(_ball).Handle, out NativeRect restoredStrip);
            Check("restart_restores_reachable_left_dock", _ball.DockSide == BallDockSide.Left && _ball.CurrentPresentation == BallDockPresentation.Collapsed &&
                Math.Abs(restoredStrip.Left - dockArea.Left) <= 1 && restoredStrip.Bottom <= dockArea.Bottom + 1);

            Pointer(strip, true);
            MouseButton(strip, true);
            MoveDraggedBallTo(_ball, dockArea.Left + (dockArea.Width * 0.4), dockArea.Top + (300 * dockScale));
            MouseButton(strip, false);
            await Task.Delay(120);
            WidgetSettings cleanupPrefs = await _settingsStore.LoadAsync();
            Check("cleanup_undock_after_restore", _ball.DockSide == BallDockSide.None && saved == savesBeforeDock + 5);

            // Editing session: a dock release updates the draft only; discard restores.
            _tray.Menu.Items[2].PerformClick();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            MouseButton(surface, true);
            MoveDraggedBallTo(_ball, dockArea.Left - (dockedDiameter * dockScale), dockArea.Top + (200 * dockScale));
            MouseButton(surface, false);
            await Task.Delay(120);
            Check("editing_session_dock_release_updates_draft_only",
                _ball.DockSide == BallDockSide.Left && _settingsViewModel.DockSide == BallDockSide.Left && await _settingsStore.LoadAsync() == cleanupPrefs);
            InteractWithPrompt(prompt => PromptButton(prompt, "不保存离开").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            SettingsCloseButton(_settingsWindow).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (_leaveTask is not null) { await _leaveTask; }
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Check("discard_restores_undocked_baseline_after_draft_dock",
                !_settingsWindow.IsVisible && _ball.DockSide == BallDockSide.None && BallHasPhysicalSize(_ball, dockedDiameter) && _settingsViewModel.DockSide == BallDockSide.None);
            _ = beforeDock;
            await VerifyBothDockEdgesAsync(outputDirectory, Check);
            await VerifyStartupAndCloseOrderingAsync(outputDirectory, Check);

            _tray.Menu.Items[3].PerformClick();
            Check("real_tray_hide_hides_ball_and_details", !_ball.IsVisible && !_details.IsVisible);
            _tray.Menu.Items[3].PerformClick();
            Check("real_tray_show_restores_ball", _ball.IsVisible);
            _controller.ShowPinned();
            _details.Close();
            Check("os_details_close_hides_and_clears_pin", !_details.IsVisible && !_controller.IsPinned && _ball.IsVisible);
            _ball.MoveTo(-10000, -10000);
            SendMessage(new System.Windows.Interop.WindowInteropHelper(_ball).Handle, 0x007E, 0, 0);
            await Task.Delay(150);
            System.Drawing.Rectangle workArea = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(_ball).Handle).WorkingArea;
            Point position = _ball.PointToScreen(new Point(0, 0));
            Check("display_message_reclamps_existing_ball", workArea.Contains((int)position.X, (int)position.Y));
            provider.UsedPercent = 58;
            _tray.Menu.Items[1].PerformClick();
            await Task.Delay(200);
            Check("real_tray_refresh_uses_coordinator", provider.Reads == readsBefore + 1);
            Check("second_official_observation_reaches_app_estimate_as_four_percent_of_budget", _widget.ObservedHasNumber && _widget.ObservedPeriods[0].ValueText == "约4%");
            provider.Failure = new QuotaProviderException(ProviderFailureKind.Transport, "synthetic verification interruption");
            await _coordinator!.RefreshOnceAsync(CancellationToken.None);
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Check("failed_official_read_marks_app_estimate_partial_without_adding", _widget.HasObservedNote && _widget.ObservedPeriods[0].ValueText == "约4%（部分）");
            provider.Failure = null;
            provider.UsedPercent = 90;
            await _coordinator.RefreshOnceAsync(CancellationToken.None);
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Check("official_recovery_excludes_cross_gap_change_in_app", _widget.ObservedPeriods[0].ValueText == "约4%（部分）");

            provider.UsedPercent = 54;
            await _coordinator.RefreshOnceAsync(CancellationToken.None);
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            byte[] trayAt46 = CaptureTrayPixels(_tray);
            provider.UsedPercent = 53;
            await _coordinator.RefreshOnceAsync(CancellationToken.None);
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Check("published_46_to_47_updates_ball_and_tray_within_same_severity",
                _widget.BallText == "47%" && _widget.Severity == QuotaSeverity.Yellow && !trayAt46.SequenceEqual(CaptureTrayPixels(_tray)));
            byte[] darkTray = CaptureTrayPixels(_tray);
            int readsBeforeTheme = provider.Reads;
            _themeService!.Apply(CodexQuota.Core.Models.ThemeMode.Light);
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Check("theme_change_repaints_existing_tray_without_quota_read", !darkTray.SequenceEqual(CaptureTrayPixels(_tray)) && provider.Reads == readsBeforeTheme);

            var denied = new DeniedStartup();
            var vm = new SettingsViewModel(denied, new SettingsStore(temporaryRoot));
            vm.LoadFrom(WidgetSettings.Default, Environment.ProcessPath!, "test");
            vm.StartAtLogin = false;
            Check("startup_disable_is_draft_only_before_save", !vm.StartAtLogin && vm.HasPendingStartupChange && denied.IsEnabled());
            Check("failed_startup_delete_reflects_verified_truth_and_retryable_intent", vm.ApplyStartupIntent(false) == true && !vm.StartAtLogin && vm.HasPendingStartupChange);
            vm.RestoreDefaults();
            Check("restore_defaults_is_draft_and_keeps_startup_truth", !vm.StartAtLogin && vm.HasPendingStartupChange && denied.IsEnabled());
            var deniedEnable = new DeniedStartup(false);
            var deniedVm = new SettingsViewModel(deniedEnable, new SettingsStore(temporaryRoot));
            deniedVm.LoadFrom(WidgetSettings.Default, Environment.ProcessPath!, "test");
            _settingsWindow!.DataContext = deniedVm;
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var startupBox = (CheckBox)_settingsWindow.FindName("StartAtLoginBox");
            startupBox.IsChecked = true;
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Check("startup_enable_checkbox_is_draft_only", deniedVm.StartAtLogin && startupBox.IsChecked == true && !deniedEnable.IsEnabled());
            Check("failed_startup_enable_keeps_retryable_choice", deniedVm.ApplyStartupIntent(true) == false && deniedVm.StartAtLogin && deniedVm.HasPendingStartupChange);
            _settingsWindow.DataContext = _settingsViewModel;
            await VerifyStartupRegistrationAsync(Check);

            await _coordinator!.DisposeAsync();
            _coordinator = null;
            if (_localUsage is not null)
            {
                await _localUsage.DisposeAsync();
                _localUsage = null;
            }

            _allowWindowClose = true;
            _tray.Dispose(); _tray = null;
            _controller.Dispose(); _controller = null;
            _dockController?.Dispose(); _dockController = null;
            _settingsWindow?.Close(); _settingsWindow = null;
            _details.Close(); _details = null;
            _ball.Close(); _ball = null;
            _themeService?.Dispose(); _themeService = null;
            _singleInstance?.Dispose(); _singleInstance = null;
            string json = JsonSerializer.Serialize(new
            {
                kind = "real-app-routed-events-and-dispatcher-timers",
                buildModuleId = typeof(App).Module.ModuleVersionId.ToString(),
                note = "Normal App composition, real WPF windows, XAML routed events and DispatcherTimers; synthetic provider and isolated settings. Drag uses the same production coordinate handler. Close hover uses a scoped/restored WPF cached-device mouse-over transition. No physical pointer/keyboard injection or other-app clicks. Not mixed-DPI/hotplug proof.",
                checks,
                settingsOpenings,
                popupObservations,
                nativeGeometry = geometry,
                allPassed = checks.All(item => (bool)item.GetType().GetProperty("passed")!.GetValue(item)!),
            }, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "desktop-events.json"), json);
            if (checks.Any(item => !(bool)item.GetType().GetProperty("passed")!.GetValue(item)!)) { throw new InvalidOperationException("Desktop event verification failed; see the sanitized report."); }

            async Task RecordSettingsOpeningAsync(string name, bool? expectNeutral = true)
            {
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                var window = _settingsWindow!;
                Button close = SettingsCloseButton(window);
                var closeRoot = (Border)close.Template.FindName("Root", close);
                var body = (ScrollViewer)window.FindName("SettingsBody");
                var focused = Keyboard.FocusedElement as FrameworkElement;
                bool neutral = closeRoot.Background is System.Windows.Media.SolidColorBrush brush && brush.Color.A == 0;
                settingsOpenings.Add(new
                {
                    name,
                    closeKeyboardFocused = close.IsKeyboardFocused,
                    closeMouseOver = close.IsMouseOver,
                    focusedType = focused?.GetType().Name,
                    focusedName = focused?.Name,
                    closeBackground = closeRoot.Background.ToString(),
                    neutral,
                    windowFocusable = window.Focusable,
                    height = window.Height,
                    bodyExtent = body.ExtentHeight,
                    bodyViewport = body.ViewportHeight,
                    groups = ((Panel)body.Content).Children.OfType<GroupBox>().Select(group => new { height = group.ActualHeight, desiredHeight = group.DesiredSize.Height }).ToArray(),
                });
                await File.WriteAllTextAsync(Path.Combine(outputDirectory, "settings-opening-observations.json"), JsonSerializer.Serialize(settingsOpenings, new JsonSerializerOptions { WriteIndented = true }));
                CaptureSettingsWindow(window, Path.Combine(outputDirectory, "settings-opening-" + name + ".png"));
                if (expectNeutral == true) { Check("settings_" + name + "_neutral_close_without_hover", !close.IsMouseOver && !close.IsKeyboardFocused && neutral); }
                else if (expectNeutral == false) { Check("settings_intentional_keyboard_focus_keeps_close_feedback", close.IsKeyboardFocused && !neutral); }
            }
        }
        finally
        {
            _outsideClicks?.Dispose();
            _outsideClicks = null;
            if (priorForeground != 0) { SetForegroundWindow(priorForeground); }
            if (Directory.Exists(temporaryRoot)) { Directory.Delete(temporaryRoot, true); }
            if (Directory.Exists(localRoot)) { Directory.Delete(localRoot, true); }
        }
    }

    private static Button SettingsCloseButton(Window window) => (Button)window.FindName("SettingsCloseAction");

    private static Button SettingsSaveButton(Window window) => (Button)window.FindName("SaveButton");

    private static Button PromptActionButton(SettingsLeaveDialog window, string label)
    {
        string key = new[] { "SaveAndLeave", "DiscardAndLeave", "ContinueEditing" }.Single(key =>
            AppText.Template(AppLanguage.SimplifiedChinese, key) == label || AppText.Template(AppLanguage.English, key) == label);
        return (Button)window.FindName(key == "ContinueEditing" ? "ContinueButton" : key + "Button");
    }

    /// <summary>True while the details header gear renders its plain, unselected surface.</summary>
    private static bool DetailGearIsNeutral(Button gear) =>
        !gear.IsMouseOver && gear.Template.FindName("Root", gear) is Border root &&
        root.Background is System.Windows.Media.SolidColorBrush brush && brush.Color.A == 0;

    private static IEnumerable<T> VisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) { yield return match; }
            foreach (T descendant in VisualChildren<T>(child)) { yield return descendant; }
        }
    }

    private static void CaptureSettingsWindow(Window window, string path)
    {
        var content = (FrameworkElement)window.Content;
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>
    /// Writes a synthetic rollout record under an isolated temporary root. It never
    /// touches the real Codex home and the value is clearly a fixture.
    /// </summary>
    private static void WriteLocalFixture(string root)
    {
        string sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(sessions);
        string stamp = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);
        string content =
            "{\"timestamp\":\"" + stamp + "\",\"type\":\"session_meta\",\"payload\":{\"id\":\"verify-session\",\"session_id\":\"verify-session\",\"model_provider\":\"openai\"}}\n" +
            "{\"timestamp\":\"" + stamp + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"total_tokens\":4321},\"last_token_usage\":{\"total_tokens\":4321}}}}\n";
        File.WriteAllText(Path.Combine(sessions, "rollout-verify.jsonl"), content, new UTF8Encoding(false));
    }

    private static async Task WaitUntilAsync(Func<bool?> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition() == true) { return; }
            await Task.Delay(25);
        }
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint hwnd, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeWindowPosition
    {
        public nint Window, InsertAfter;
        public int X, Y, Width, Height;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    private static object ReadGeometry(Window window)
    {
        GetWindowRect(new System.Windows.Interop.WindowInteropHelper(window).Handle, out NativeRect rect);
        return new { left = rect.Left, top = rect.Top, width = rect.Right - rect.Left, height = rect.Bottom - rect.Top, dipWidth = window.Width, dipHeight = window.Height, actualDipWidth = window.ActualWidth, actualDipHeight = window.ActualHeight, scale = Services.MonitorService.GetScaleForWindow(window), visible = window.IsVisible };
    }

    private static bool BallHasPhysicalSize(Window ball, double dipSize)
    {
        GetWindowRect(new System.Windows.Interop.WindowInteropHelper(ball).Handle, out NativeRect rect);
        double scale = Services.MonitorService.GetScaleForWindow(ball);
        // Native dimensions use whole pixels: 90 DIP at 125% is 112.5 pixels.
        // WPF can reconcile Width/ActualWidth to 90.4 DIP after a native move.
        return WithinOnePhysicalPixel(rect.Right - rect.Left, dipSize * scale) &&
            WithinOnePhysicalPixel(rect.Bottom - rect.Top, dipSize * scale) &&
            WithinOnePhysicalPixel(ball.ActualWidth * scale, dipSize * scale) &&
            WithinOnePhysicalPixel(ball.ActualHeight * scale, dipSize * scale);
    }

    private static bool WithinOnePhysicalPixel(double actual, double expected)
    {
        // Keep the one-pixel native bound; allow only a few floating-point ULPs
        // at the measured magnitude for DIP/scale arithmetic, not extra pixel slack.
        double magnitude = Math.Max(1, Math.Max(Math.Abs(actual), Math.Abs(expected)));
        double arithmeticEpsilon = 4 * (Math.BitIncrement(magnitude) - magnitude);
        return Math.Abs(actual - expected) <= 1 + arithmeticEpsilon;
    }

    private static Point NativePopupCenter(System.Windows.Media.Visual popupContent)
    {
        var source = (System.Windows.Interop.HwndSource)PresentationSource.FromVisual(popupContent);
        GetWindowRect(source.Handle, out NativeRect rect);
        return new Point((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2);
    }

    private static object ReadPopupSurface(System.Windows.Media.Visual content, string name)
    {
        var source = (System.Windows.Interop.HwndSource)PresentationSource.FromVisual(content);
        GetWindowRect(source.Handle, out NativeRect rect);
        var element = (FrameworkElement)content;
        Point contentCenter = element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
        nint context = GetThreadDpiAwarenessContext();
        nint ordinaryTarget = WindowFromPoint(new System.Drawing.Point((int)contentCenter.X, (int)contentCenter.Y));
        nint priorContext = SetThreadDpiAwarenessContext(-4);
        nint physicalTarget;
        try { physicalTarget = WindowFromPoint(new System.Drawing.Point((int)contentCenter.X, (int)contentCenter.Y)); }
        finally { SetThreadDpiAwarenessContext(priorContext); }
        return new { name, handle = source.Handle.ToInt64(), nativeRect = new { left = rect.Left, top = rect.Top, right = rect.Right, bottom = rect.Bottom }, contentCenter, contentWidth = element.ActualWidth, contentHeight = element.ActualHeight, visible = IsWindowVisible(source.Handle), enabled = IsWindowEnabled(source.Handle), windowStyle = GetWindowLongPtr(source.Handle, -16).ToInt64(), extendedStyle = GetWindowLongPtr(source.Handle, -20).ToInt64(), threadDpiContext = context.ToInt64(), threadDpiAwareness = GetAwarenessFromDpiAwarenessContext(context), ordinaryTarget = ordinaryTarget.ToInt64(), physicalTarget = physicalTarget.ToInt64(), popup = NativeDwmState(source.Handle), previousInZOrder = NativeDwmState(GetWindow(source.Handle, 3)), foreground = NativeDwmState(GetForegroundWindow()) };
    }

    private static object NativeDwmState(nint hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint owner);
        int result = DwmGetWindowAttribute(hwnd, 14, out int cloaked, sizeof(int));
        GetWindowRect(hwnd, out NativeRect rect);
        return new { handle = hwnd.ToInt64(), owner, visible = IsWindowVisible(hwnd), enabled = IsWindowEnabled(hwnd), cloakedResult = result, cloaked, rect = new { left = rect.Left, top = rect.Top, right = rect.Right, bottom = rect.Bottom } };
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint hwnd, uint command);

    private static bool PopupOwnsNativeCenter(System.Windows.Media.Visual content)
    {
        var source = (System.Windows.Interop.HwndSource)PresentationSource.FromVisual(content);
        Point point = NativePopupCenter(content);
        return WindowFromPoint(new System.Drawing.Point((int)point.X, (int)point.Y)) == source.Handle;
    }

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(System.Drawing.Point point);

    [DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll")]
    private static extern nint GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")]
    private static extern int GetAwarenessFromDpiAwarenessContext(nint context);
    [DllImport("user32.dll")]
    private static extern nint SetThreadDpiAwarenessContext(nint context);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hwnd);

    /// <summary>
    /// Drives the production drag path to an absolute physical position using the geometry
    /// the window captured at press, so the result does not depend on synthetic mouse state.
    /// </summary>
    private static void MoveDraggedBallTo(Views.FloatingBallWindow ball, double left, double top)
    {
        Point current = ball.DragStartScreen + new Vector(left - ball.WindowStartScreen.X, top - ball.WindowStartScreen.Y);
        ball.ApplyDragPosition(current);
    }

    /// <summary>
    /// True when the panel's native rectangle sits directly beside the ball's current
    /// rectangle (left or right side, with the placement gap) and spans it vertically.
    /// </summary>
    private static bool PanelTracksBall(Window ball, Window panel)
    {
        if (!GetWindowRect(new System.Windows.Interop.WindowInteropHelper(ball).Handle, out NativeRect ballRect)) { return false; }
        if (!GetWindowRect(new System.Windows.Interop.WindowInteropHelper(panel).Handle, out NativeRect panelRect)) { return false; }
        double gap = 8;
        bool horizontal = Math.Abs(panelRect.Right + gap - ballRect.Left) <= 3
            || Math.Abs(panelRect.Left - ballRect.Right - gap) <= 3;
        bool vertical = panelRect.Top <= ballRect.Top + 3 && panelRect.Bottom >= ballRect.Bottom - 3;
        return horizontal && vertical;
    }

    private static bool DetailsHintFits(Views.DetailsWindow panel)
    {
        var hint = (FrameworkElement)panel.FindName("DetailsHint");
        var root = (FrameworkElement)panel.Content;
        double gap = root.ActualHeight - hint.TransformToAncestor(root).Transform(new Point(0, hint.ActualHeight)).Y;
        return gap is >= 12 and <= 18;
    }

    private static bool PanelWithinWorkArea(Window ball, Window panel)
    {
        GetWindowRect(new System.Windows.Interop.WindowInteropHelper(panel).Handle, out NativeRect rect);
        var area = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(ball).Handle).WorkingArea;
        return rect.Left >= area.Left - 1 && rect.Right <= area.Right + 1 && rect.Top >= area.Top - 1 && rect.Bottom <= area.Bottom + 1;
    }

    /// <summary>True when the panel was placed on the ball's right side.</summary>
    private static bool PanelOnRightOfBall(Window ball, Window panel)
    {
        if (!GetWindowRect(new System.Windows.Interop.WindowInteropHelper(ball).Handle, out NativeRect ballRect)) { return false; }
        if (!GetWindowRect(new System.Windows.Interop.WindowInteropHelper(panel).Handle, out NativeRect panelRect)) { return false; }
        double gap = 8;
        return Math.Abs(panelRect.Left - ballRect.Right - gap) <= 3;
    }

    private static void MouseButton(UIElement surface, bool down) => surface.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Left)
    {
        RoutedEvent = down ? UIElement.MouseLeftButtonDownEvent : UIElement.MouseLeftButtonUpEvent,
    });

    private static byte[] CaptureTrayPixels(Services.TrayService tray)
    {
        var field = typeof(Services.TrayService).GetField("_notifyIcon", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var notifyIcon = (System.Windows.Forms.NotifyIcon)field.GetValue(tray)!;
        using System.Drawing.Bitmap bitmap = notifyIcon.Icon!.ToBitmap();
        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        return stream.ToArray();
    }

    private static void RaiseKey(UIElement window, Key key) => window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, key) { RoutedEvent = Keyboard.KeyDownEvent });

    private async Task VerifyStartupRegistrationAsync(Action<string, bool> check)
    {
        const string path = @"Software\Microsoft\Windows\CurrentVersion\Run";
        using RegistryKey registry = Registry.CurrentUser.CreateSubKey(path, true);
        string name = _startupService.ValueName;
        bool existed = registry.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase);
        object? previous = existed ? registry.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) : null;
        RegistryValueKind kind = existed ? registry.GetValueKind(name) : RegistryValueKind.String;
        try
        {
            _startupService.Disable();
            check("actual_startup_registration_delete", !_startupService.IsEnabled());
            _settingsViewModel.LoadFrom(WidgetSettings.Default, Environment.ProcessPath!, "verification");
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            check("actual_startup_default_off", !_settingsViewModel.StartAtLogin && !_settingsViewModel.HasPendingStartupChange);
            // Toggling is draft-only: the registry stays untouched until a submitted Save.
            _settingsViewModel.StartAtLogin = true;
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            check("actual_startup_toggle_is_draft_only", _settingsViewModel.StartAtLogin && _settingsViewModel.HasPendingStartupChange && !_startupService.IsEnabled());
            SettingsSaveOutcome enableOutcome = await SaveSettingsAsync();
            check("actual_startup_registration_value_exists", enableOutcome == SettingsSaveOutcome.Success && _startupService.IsEnabled());
            check("actual_startup_registration_exact_quoted_path",
                registry.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string == "\"" + Environment.ProcessPath + "\"");
            _settingsViewModel.RestoreDefaults();
            check("actual_restore_defaults_requests_startup_off", !_settingsViewModel.StartAtLogin && _settingsViewModel.HasPendingStartupChange && _startupService.IsEnabled());
            SettingsSaveOutcome disableOutcome = await SaveSettingsAsync();
            check("actual_restore_defaults_save_disables_startup", disableOutcome == SettingsSaveOutcome.Success && !_startupService.IsEnabled());
            _settingsViewModel.StartAtLogin = true;
            _settingsViewModel.StartAtLogin = false;
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            check("actual_startup_draft_toggles_leave_registry_untouched", !_settingsViewModel.StartAtLogin && registry.GetValue(name) is null);
        }
        finally
        {
            if (existed) { registry.SetValue(name, previous!, kind); }
            else { registry.DeleteValue(name, false); }
        }
        check("actual_startup_prior_value_restored", existed
            ? System.Collections.StructuralComparisons.StructuralEqualityComparer.Equals(previous, registry.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)) && registry.GetValueKind(name) == kind
            : !registry.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase));
    }

    private sealed class EventProvider : IQuotaProvider
    {
        private readonly DateTimeOffset _reset = DateTimeOffset.Now.AddHours(1);
        public string Id => "verification";
        public int Reads { get; private set; }
        public int UsedPercent { get; set; } = 54;
        public QuotaProviderException? Failure { get; set; }
        public Task<ProviderRefreshResult> FetchAsync(DateOnly date, CancellationToken cancellationToken)
        {
            Reads++;
            if (Failure is not null) { throw Failure; }
            var bucket = new QuotaBucket("codex", null, [new QuotaWindow("primary", 300, UsedPercent, 100 - UsedPercent, _reset)], false, null, null, null, null);
            return Task.FromResult(new ProviderRefreshResult(new QuotaSnapshot("codex", "SAMPLE", [bucket], DateTimeOffset.Now), DailyUsageResult.Missing(date)));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DeniedStartup : IStartupRegistration
    {
        private readonly bool _enabled;
        public DeniedStartup(bool enabled = true) => _enabled = enabled;
        public string ValueName => "test";
        public bool IsEnabled() => _enabled;
        public bool Enable(string command) => false;
        public void Disable() { }
    }
}
