using System.Windows;
using System.Windows.Controls;
using CodexQuota.Core.Models;
using CodexQuota.Core.Services;
using CodexQuota.Desktop.ViewModels;
using CodexQuota.Desktop.Views;

namespace CodexQuota.Desktop;

// Only the isolated --verify App fixture calls this partial.
public partial class App
{
    private async Task VerifySettingsOrderingAsync(Action<string, bool> check)
    {
        WidgetSettings original = _settings;
        Point originalOrigin = _ball!.CanonicalPhysicalOrigin;
        ShowSettings();
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        _settingsViewModel.BallSizePercent = 100;
        _settingsViewModel.OpacityPercent = 60;
        Point submittedOrigin = _ball!.CanonicalPhysicalOrigin;
        await _settingsUpdateGate.WaitAsync();
        Task<SettingsSaveOutcome> submitted;
        try
        {
            submitted = SaveSettingsAsync();
            _settingsViewModel.BallSizePercent = 150;
            _settingsViewModel.OpacityPercent = 80;
            _settingsViewModel.SelectedPlacement = _settingsViewModel.PlacementOptions.First(option => option.Placement == BallPlacement.TopLeft);
        }
        finally { _settingsUpdateGate.Release(); }

        await submitted;
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        WidgetSettings retained = await _settingsStore.LoadAsync();
        check("pending_save_retains_only_immutable_submitted_values", retained.BallSizePercent == 100 && Math.Abs(retained.Opacity - .6) < .001);
        check("pending_save_keeps_newer_native_preview", BallHasPhysicalSize(_ball, 108) && Math.Abs(_ball.Opacity - .8) < .001);
        check("pending_save_keeps_newer_draft_dirty", _settingsViewModel.BallSizePercent == 150 && _settingsViewModel.OpacityPercent == 80 && IsSettingsDirty());
        DiscardDraft();
        CloseSettingsSurface();
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Point restored = _ball.CanonicalPhysicalOrigin;
        check("discard_after_pending_save_uses_submitted_native_baseline", BallHasPhysicalSize(_ball, 72) && Math.Abs(_ball.Opacity - .6) < .001 &&
            Math.Abs(restored.X - submittedOrigin.X) <= 1 && Math.Abs(restored.Y - submittedOrigin.Y) <= 1);

        // A loaded fractional opacity is represented by a whole-percent slider but is
        // neither an edit nor a reason for unrelated interval changes to round the ball.
        WidgetSettings prior = _settings;
        _settings = prior with { Opacity = .954, RefreshIntervalSeconds = 45 };
        _ball.ApplyOpacity(.954);
        ShowSettings();
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        check("fractional_loaded_opacity_is_clean_without_rounding_preview", !IsSettingsDirty() && Math.Abs(_ball.Opacity - .954) < .0001);
        _settingsViewModel.SelectedInterval = _settingsViewModel.IntervalOptions.Single(option => option.Seconds == 60);
        check("save_only_interval_does_not_reapply_ball_preview", Math.Abs(_ball.Opacity - .954) < .0001);
        _settingsViewModel.IntervalSeconds = 45;
        _settingsViewModel.OpacityPercent = 90;
        _settingsViewModel.OpacityPercent = 95;
        check("undo_slider_restores_exact_loaded_opacity_and_clean_state", Math.Abs(_ball.Opacity - .954) < .0001 && !IsSettingsDirty());
        _settingsViewModel.SelectedPlacement = _settingsViewModel.PlacementOptions.Single(option => option.Placement == BallPlacement.BottomRight);
        _settingsViewModel.BallSizePercent = 150;
        GetWindowRect(new System.Windows.Interop.WindowInteropHelper(_ball).Handle, out NativeRect resizedCorner);
        double cornerScale = Services.MonitorService.GetScaleForWindow(_ball);
        var cornerArea = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(_ball).Handle).WorkingArea;
        check("resizing_corner_preview_keeps_configured_edge_margin", WithinOnePhysicalPixel(resizedCorner.Right, cornerArea.Right - MonitorPlacement.EdgeMargin * cornerScale) &&
            WithinOnePhysicalPixel(resizedCorner.Bottom, cornerArea.Bottom - MonitorPlacement.EdgeMargin * cornerScale));
        _settingsViewModel.NotificationsEnabled = true;
        var threshold = (TextBox)_settingsWindow!.FindName("ThresholdInput");
        threshold.Text = "invalid";
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        check("invalid_raw_threshold_is_dirty_and_blocks_shared_save", IsSettingsDirty() && !_settingsWindow.CanSave() && await SaveSettingsAsync() == SettingsSaveOutcome.NotAttempted);
        DiscardDraft();
        CloseSettingsSurface();
        _settings = original;
        await _settingsStore.SaveAsync(original);
        RestoreBallGeometry(original, originalOrigin);
        ShowSettings();
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        CloseSettingsSurface();

        // Exercise a closed/reopened lifecycle while an explicit submitted Save is queued.
        // Completion must not recreate the old session or lose the latest retained origin.
        ShowSettings();
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        _settingsViewModel.OpacityPercent = 50;
        await _settingsUpdateGate.WaitAsync();
        Task<SettingsSaveOutcome> late = SaveSettingsAsync();
        CloseSettingsSurface();
        ShowSettings();
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        _settingsViewModel.BallSizePercent = 150;
        _settingsUpdateGate.Release();
        await late;
        check("late_save_does_not_overwrite_reopened_session_preview", _settingsWindow!.IsVisible && _settingsViewModel.BallSizePercent == 150 && BallHasPhysicalSize(_ball, 108));
        DiscardDraft(); CloseSettingsSurface();
        check("reopened_session_discard_uses_latest_explicit_save_baseline", Math.Abs(_ball.Opacity - .5) < .0001 && _settings.BallSizePercent == _settingsViewModel.BallSizePercent);
        _settings = original;
        await _settingsStore.SaveAsync(original);
        RestoreBallGeometry(original, originalOrigin);
        await VerifyOrdinaryReleaseOpeningOrderingAsync(check);
    }

    private async Task VerifyOrdinaryReleaseOpeningOrderingAsync(Action<string, bool> check)
    {
        WidgetSettings original = _settings;
        Point originalOrigin = _ball!.CanonicalPhysicalOrigin;
        UIElement circle = (UIElement)_ball.FindName("Ball");
        UIElement lane = (UIElement)_ball.FindName("DockStrip");
        try
        {
            foreach (int scenario in new[] { 0, 1, 2 })
            {
                bool editAfterOpening = scenario == 1;
                Task<SettingsSaveOutcome>? earlySave = null;
                CloseSettingsSurface();
                _controller!.CloseExplicitly();
                Pointer(lane, false); Pointer(circle, false);
                _settings = original with { Placement = BallPlacement.TopLeft, DockSide = BallDockSide.None };
                await _settingsStore.SaveAsync(_settings);
                RestoreBallGeometry(_settings, originalOrigin);
                _monitorService.ApplyPlacement(_ball, BallPlacement.TopLeft);
                var right = System.Windows.Forms.Screen.AllScreens.MaxBy(screen => screen.Bounds.Right)!.WorkingArea;
                await _settingsUpdateGate.WaitAsync();
                Point retainedOrigin;
                try
                {
                    // The real ordinary release settles before its write can acquire the
                    // gate. Opening Settings must not turn that release into a draft.
                    MouseButton(circle, true);
                    MoveDraggedBallTo(_ball, right.Right - 20, right.Top + 200);
                    MouseButton(circle, false);
                    retainedOrigin = _ball.CanonicalPhysicalOrigin;
                    ShowSettings();
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    if (editAfterOpening)
                    {
                        _settingsViewModel.OpacityPercent = 40;
                        _settingsViewModel.BallSizePercent = 150;
                        _settingsViewModel.ShowTodayUsage = !original.ShowTodayUsage;
                        _settingsViewModel.SelectedPlacement = _settingsViewModel.PlacementOptions.Single(option => option.Placement == BallPlacement.BottomLeft);
                    }
                    else if (scenario == 2) { earlySave = SaveSettingsAsync(); }
                }
                finally { _settingsUpdateGate.Release(); }

                // Acquire behind the queued production release to observe its completed
                // file/runtime promotion without substituting a guessed timer delay.
                await _settingsUpdateGate.WaitAsync();
                _settingsUpdateGate.Release();
                if (earlySave is not null)
                {
                    check("ordinary_queued_release_opening_can_submit_save_before_release_io_completes", await earlySave == SettingsSaveOutcome.Success);
                }
                WidgetSettings retained = await _settingsStore.LoadAsync();
                check("ordinary_queued_release_opening_retains_canonical_dock", retained.Placement == BallPlacement.Custom &&
                    retained.DockSide == BallDockSide.Right && retained == _settings);
                if (!editAfterOpening)
                {
                    check("ordinary_queued_release_opening_updates_unedited_placement_cleanly", !IsSettingsDirty() &&
                        _settingsViewModel.SelectedPlacement.Placement == BallPlacement.Custom && _settingsViewModel.DockSide == BallDockSide.Right);
                    await SaveSettingsAsync();
                    WidgetSettings saved = await _settingsStore.LoadAsync();
                    check("ordinary_queued_release_opening_then_save_keeps_file_runtime_and_dock_equal", saved == _settings &&
                        saved.Placement == BallPlacement.Custom && saved.DockSide == BallDockSide.Right && _ball.DockSide == BallDockSide.Right);
                }
                else
                {
                    check("ordinary_queued_release_completion_preserves_newer_editor_and_native_preview", IsSettingsDirty() &&
                        _settingsViewModel.SelectedPlacement.Placement == BallPlacement.BottomLeft && _settingsViewModel.DockSide == BallDockSide.None &&
                        _settingsViewModel.ShowTodayUsage != original.ShowTodayUsage && _settingsViewModel.BallSizePercent == 150 &&
                        BallHasPhysicalSize(_ball, 108) && Math.Abs(_ball.Opacity - .4) < .0001 && _ball.DockSide == BallDockSide.None);
                    DiscardDraft();
                    Point restored = _ball.CanonicalPhysicalOrigin;
                    check("ordinary_queued_release_opening_then_discard_uses_latest_retained_dock", _ball.DockSide == BallDockSide.Right &&
                        BallHasPhysicalSize(_ball, 72 * original.BallSizePercent / 100d) && Math.Abs(_ball.Opacity - original.Opacity) < .0001 &&
                        Math.Abs(restored.X - retainedOrigin.X) <= 1 && Math.Abs(restored.Y - retainedOrigin.Y) <= 1 && await _settingsStore.LoadAsync() == retained);
                }
            }
        }
        finally
        {
            CloseSettingsSurface();
            _controller!.CloseExplicitly();
            Pointer(lane, false); Pointer(circle, false);
            _settings = original;
            await _settingsStore.SaveAsync(original);
            RestoreBallGeometry(original, originalOrigin);
        }
    }

    private async Task VerifyStartupAndCloseOrderingAsync(string outputDirectory, Action<string, bool> check)
    {
        WidgetSettings original = _settings;
        Point origin = _ball!.CanonicalPhysicalOrigin;
        SettingsViewModel originalEditor = _settingsViewModel;
        var startup = new VerificationStartup { ReadFails = true };
        _settingsViewModel = new SettingsViewModel(startup, _settingsStore);
        AttachSettingsDraftEvents();
        _settingsWindow!.DataContext = _settingsViewModel;
        ShowSettings();
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        check("unreadable_startup_on_open_has_visible_fixed_footer", ((TextBlock)_settingsWindow.FindName("SettingsStatus")).Text.Contains("无法读取", StringComparison.Ordinal));
        startup.ReadFails = false;
        CloseSettingsSurface(); ShowSettings();
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        try
        {
            _settingsViewModel.StartAtLogin = true;
            _settingsViewModel.BallSizePercent = 125;
            using (var locked = new System.IO.FileStream(_settingsStore.SettingsPath, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read))
            {
                check("submitted_json_failure_makes_zero_startup_mutations", await SaveSettingsAsync() == SettingsSaveOutcome.FileFailure && startup.Writes == 0 &&
                    _settingsWindow.IsVisible && _settingsViewModel.HasPendingStartupChange);
            }

            startup.AllowEnable = false;
            _controller!.ShowPinned();
            await ChooseLeaveAsync("保存并离开", observe: true);
            check("partial_save_and_leave_keeps_page_footer_and_retryable_choice", _settingsWindow.IsVisible && _settingsViewModel.StartAtLogin &&
                _settingsViewModel.HasPendingStartupChange && _settingsViewModel.StatusMessage.Contains("开机启动失败", StringComparison.Ordinal) &&
                (await _settingsStore.LoadAsync()).BallSizePercent == 125 && !startup.Enabled);
            startup.AllowEnable = true;
            check("startup_retry_without_retoggle_succeeds", await SaveSettingsAsync() == SettingsSaveOutcome.Success && startup.Enabled && !_settingsViewModel.HasPendingStartupChange);
            _settingsViewModel.StartAtLogin = false;
            startup.ReadFails = true;
            check("startup_unknown_truth_is_explicit_partial_footer", await SaveSettingsAsync() == SettingsSaveOutcome.StartupPartial &&
                _settingsWindow.IsVisible && _settingsViewModel.StatusMessage.Contains("无法验证", StringComparison.Ordinal) && _settingsViewModel.HasPendingStartupChange);
            startup.ReadFails = false;
            check("startup_unknown_truth_can_be_retried", await SaveSettingsAsync() == SettingsSaveOutcome.Success && !startup.Enabled);

            _settingsViewModel.BallSizePercent = 100;
            await _settingsUpdateGate.WaitAsync();
            Task<SettingsSaveOutcome> pending;
            try
            {
                pending = SaveSettingsAsync();
                HandleSettingsLeaveRequested();
                Task? firstLeave = _leaveTask;
                HandleSettingsLeaveRequested();
                check("repeated_close_during_pending_save_reuses_one_leave_request", ReferenceEquals(firstLeave, _leaveTask));
            }
            finally { _settingsUpdateGate.Release(); }
            await pending;
            if (_leaveTask is not null) { await _leaveTask; }
            check("pending_save_same_draft_close_awaits_and_leaves_cleanly", !_settingsWindow.IsVisible &&
                (await _settingsStore.LoadAsync()).BallSizePercent == 100 && !Application.Current.Windows.OfType<SettingsLeaveDialog>().Any(window => window.IsVisible));

            ShowSettings();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            _settingsViewModel.OpacityPercent = 60;
            await _settingsUpdateGate.WaitAsync();
            pending = SaveSettingsAsync();
            _settingsViewModel.BallSizePercent = 150;
            QueuePrompt("继续编辑", observe: true);
            HandleSettingsLeaveRequested();
            _settingsUpdateGate.Release();
            await pending;
            if (_leaveTask is not null) { await _leaveTask; }
            check("pending_save_newer_edits_close_keeps_explicitly_continued_preview", _settingsWindow.IsVisible &&
                _settingsViewModel.BallSizePercent == 150 && BallHasPhysicalSize(_ball, 108) && IsSettingsDirty());
            await ChooseLeaveAsync("不保存离开", observe: false);
            check("discard_after_pending_close_uses_new_retained_values", !_settingsWindow.IsVisible && BallHasPhysicalSize(_ball, 72) && Math.Abs(_ball.Opacity - .6) < .0001);
        }
        finally
        {
            CloseSettingsSurface();
            _settingsViewModel = originalEditor;
            AttachSettingsDraftEvents();
            _settingsWindow.DataContext = originalEditor;
            _settings = original;
            await _settingsStore.SaveAsync(original);
            RestoreBallGeometry(original, origin);
            _controller!.CloseExplicitly();
        }

        async Task ChooseLeaveAsync(string label, bool observe)
        {
            QueuePrompt(label, observe);
            HandleSettingsLeaveRequested();
            if (_leaveTask is not null) { await _leaveTask; }
        }

        void QueuePrompt(string label, bool observe)
        {
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
            timer.Tick += async (_, _) =>
            {
                if (Application.Current.Windows.OfType<SettingsLeaveDialog>().FirstOrDefault(window => window.IsVisible) is not { } prompt) { return; }
                timer.Stop();
                if (observe)
                {
                    prompt.UpdateLayout();
                    nint handle = new System.Windows.Interop.WindowInteropHelper(prompt).Handle;
                    GetWindowRect(handle, out NativeRect rect);
                    Point center = new(rect.Left + (rect.Right - rect.Left) / 2, rect.Top + 30);
                    await WaitUntilAsync(() => NativeOwnsPoint(handle, center), TimeSpan.FromSeconds(2));
                    check("owned_confirmation_has_real_native_hit_target", NativeOwnsPoint(handle, center) && ReferenceEquals(prompt.Owner, _settingsWindow));
                    OnObservedButtonDown(center, handle, owned: true);
                    check("owned_confirmation_native_click_keeps_details_hidden_and_unpinned", !_controller!.IsPinned && _details?.IsVisible != true);
                    CaptureSettingsWindow(prompt, System.IO.Path.Combine(outputDirectory, "actual-unsaved-confirmation.png"));
                }
                PromptActionButton(prompt, label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            timer.Start();
        }
    }

    private sealed class VerificationStartup : IStartupRegistration
    {
        public bool Enabled { get; private set; }
        public bool AllowEnable { get; set; } = true;
        public bool ReadFails { get; set; }
        public int Writes { get; private set; }
        public string ValueName => "isolated-verification-only";
        public bool IsEnabled() => ReadFails ? throw new InvalidOperationException("Synthetic denied read") : Enabled;
        public bool Enable(string command) { Writes++; if (AllowEnable) { Enabled = true; } return Enabled; }
        public void Disable() { Writes++; Enabled = false; }
    }
}
