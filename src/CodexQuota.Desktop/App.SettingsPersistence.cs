using System.Windows;
using CodexQuota.Core.Models;
using CodexQuota.Desktop.Services;
using CodexQuota.Desktop.ViewModels;

namespace CodexQuota.Desktop;

public partial class App
{
    internal enum SettingsSaveOutcome { NotAttempted, Success, FileFailure, StartupPartial }

    private sealed record SaveSubmission(long Session, SettingsViewModel Editor, WidgetSettings Settings,
        WidgetSettings Editable, string Threshold, Point Origin, double Scale, bool Startup, bool RequestStartup);

    private Task<SettingsSaveOutcome>? _pendingSave;
    private SaveSubmission? _pendingSubmission;
    private int _pendingOrdinaryPositionWrites;

    private Task<SettingsSaveOutcome> SaveSettingsAsync()
    {
        if (_shuttingDown) { return Task.FromResult(SettingsSaveOutcome.NotAttempted); }
        if (_settingsWindow is { IsVisible: true } && !_settingsWindow.CanSave())
        {
            _settingsViewModel.ReportThresholdBlocked();
            return Task.FromResult(SettingsSaveOutcome.NotAttempted);
        }

        // Capture every submitted value before the first await, including canonical
        // full-ball geometry rather than the current narrow/revealed HWND projection.
        double scale = _ball is null ? _settings.BallPositionScale ?? 1 : MonitorService.GetScaleForWindow(_ball);
        Point origin = _ball?.CanonicalPhysicalOrigin ?? default;
        WidgetSettings editable = EditableValues();
        WidgetSettings candidate = _settingsViewModel.ToSettings() with
        {
            Opacity = _ball?.Opacity ?? editable.Opacity,
            DockSide = _ball?.DockSide ?? editable.DockSide,
            BallLeft = _ball is null ? _settings.BallLeft : origin.X / scale,
            BallTop = _ball is null ? _settings.BallTop : origin.Y / scale,
            BallPositionScale = scale,
        };
        var submission = new SaveSubmission(_settingsSession, _settingsViewModel, candidate, editable,
            ThresholdSignature(), origin, scale, _settingsViewModel.StartAtLogin, _settingsViewModel.HasPendingStartupChange);
        if (_pendingSave is { IsCompleted: false } && _pendingSubmission == submission) { return _pendingSave; }

        _pendingSubmission = submission;
        _pendingSave = SaveSettingsCoreAsync(submission);
        return _pendingSave;
    }

    /// <summary>
    /// Save-button path: persist, then close straight onto the saved floating ball when
    /// the save fully lands clean. A file failure or startup partial stays open for retry.
    /// </summary>
    private async Task SaveSettingsAndCloseAsync()
    {
        long session = _settingsSession;
        SettingsSaveOutcome outcome = await SaveSettingsAsync();
        if (outcome == SettingsSaveOutcome.Success &&
            !_shuttingDown && _settingsSessionActive && session == _settingsSession && !IsSettingsDirty())
        {
            CloseSettingsSurface();
        }
    }

    private async Task<SettingsSaveOutcome> SaveSettingsCoreAsync(SaveSubmission submission)
    {
        await _settingsUpdateGate.WaitAsync();
        try
        {
            if (_shuttingDown) { return SettingsSaveOutcome.NotAttempted; }
            try { await _settingsStore.SaveAsync(submission.Settings); }
            catch (Exception)
            {
                if (IsCurrentEditor(submission)) { submission.Editor.ReportSaveFailure(); }
                return SettingsSaveOutcome.FileFailure;
            }

            // JSON succeeded even if startup subsequently fails. Apply committed-only
            // effects, and advance the rollback baseline to this submitted geometry.
            WidgetSettings previous = _settings;
            _settings = submission.Settings;
            ApplyCommittedLanguage();
            _themeService?.Apply(_settings.Theme);
            _widget.UpdateSettings(_settings);
            if (previous.RefreshIntervalSeconds != _settings.RefreshIntervalSeconds) { _coordinator?.UpdateInterval(_settings.RefreshInterval); }
            if (previous.CodexCliPath != _settings.CodexCliPath) { RefreshCliStatus(); }
            if (IsCurrentEditor(submission))
            {
                submission.Editor.UpdateCliStatus(_cliStatusText, _cliStatusMessage);
                CaptureSettingsBaseline(submission.Settings, submission.Origin, submission.Scale, submission.Editable, submission.Threshold);
            }
            else if (_settingsSessionActive)
            {
                // A newer session keeps its current draft, but Discard must still use
                // the most recent explicit Save that actually reached disk.
                WidgetSettings comparable = submission.Editable with { Opacity = Math.Round(submission.Settings.Opacity * 100) / 100d };
                CaptureSettingsBaseline(submission.Settings, submission.Origin, submission.Scale, comparable,
                    submission.Settings.LowQuotaThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            else if (!_settingsSessionActive)
            {
                RestoreBallGeometry(submission.Settings, submission.Origin);
            }

            bool? actual = submission.RequestStartup ? submission.Editor.ApplyStartupIntent(submission.Startup) : submission.Startup;
            if (previous.NotificationsEnabled != _settings.NotificationsEnabled || previous.LowQuotaThreshold != _settings.LowQuotaThreshold)
            { EvaluateQuotaNotification(_widget.State); }
            if (actual is null || actual != submission.Startup) { return SettingsSaveOutcome.StartupPartial; }
            if (IsCurrentEditor(submission))
            {
                if (!IsSettingsDirty()) { submission.Editor.MarkSaved(); }
                else { submission.Editor.ReportSavedWithPendingEdits(); }
            }

            return SettingsSaveOutcome.Success;
        }
        finally { _settingsUpdateGate.Release(); }
    }

    private bool IsCurrentEditor(SaveSubmission submission) => _settingsSessionActive &&
        _settingsSession == submission.Session && ReferenceEquals(_settingsViewModel, submission.Editor);

    private async void OnBallPositionPersisted(double left, double top)
    {
        if (_shuttingDown || _ball is null) { return; }
        // Settle and snapshot this release synchronously. Later gate/IO waits must not
        // reclassify a new session or capture a newer hover/drag representation.
        SettleBallRelease();
        Point origin = _ball.CanonicalPhysicalOrigin;
        double scale = MonitorService.GetScaleForWindow(_ball);
        BallDockSide side = _ball.DockSide;
        if (_settingsSessionActive)
        {
            RecordDraftGeometry();
            return;
        }

        _pendingOrdinaryPositionWrites++;
        await _settingsUpdateGate.WaitAsync();
        try
        {
            if (_shuttingDown) { return; }
            WidgetSettings candidate = _settings with
            {
                BallLeft = origin.X / scale,
                BallTop = origin.Y / scale,
                BallPositionScale = scale,
                Placement = BallPlacement.Custom,
                DockSide = side,
            };
            try
            {
                await _settingsStore.SaveAsync(candidate);
                _settings = candidate;
                AdvanceSettingsPositionBaseline(candidate, origin, scale);
            }
            catch (Exception) { /* A failed release write never breaks dragging. */ }
        }
        finally { _pendingOrdinaryPositionWrites--; _settingsUpdateGate.Release(); }
    }

    private void SettleBallRelease()
    {
        if (_ball is null) { return; }
        bool previous = _ballFollowSuppressed;
        _ballFollowSuppressed = true;
        try
        {
            BallDockSide side = _monitorService.EvaluateDockRelease(_ball);
            if (side == BallDockSide.None)
            {
                _dockController?.Undock();
                _ball.ClearDock();
                _monitorService.ClampWindow(_ball);
            }
            else
            {
                _ball.AdoptDock(side);
                _dockController?.Dock(side);
                _ball.ApplyDockPresentation(_dockController?.Presentation ?? Core.Services.BallDockPresentation.Collapsed);
            }
        }
        finally { _ballFollowSuppressed = previous; }
        if (!previous) { OnBallMoved(); }

    }
}
