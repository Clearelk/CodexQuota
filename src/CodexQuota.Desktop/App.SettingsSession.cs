using System.ComponentModel;
using System.Globalization;
using System.Windows;
using CodexQuota.Core.Models;
using CodexQuota.Desktop.Services;
using CodexQuota.Desktop.ViewModels;
using CodexQuota.Desktop.Views;

namespace CodexQuota.Desktop;

public partial class App
{
    private bool _applyingBaseline;
    private bool _settingsHoldApplied;
    private bool _leavePromptOpen;
    private bool _settingsSessionActive;
    private bool _userGeometryEdited;
    private long _settingsSession;
    private Task? _leaveTask;
    private SettingsViewModel? _draftEventSource;
    private WidgetSettings _baselineSettings = WidgetSettings.Default;
    private WidgetSettings _baselineEditable = WidgetSettings.Default;
    private WidgetSettings _lastBallDraft = WidgetSettings.Default;
    private Point _baselineOriginPhysical;
    private string _baselineThresholdText = string.Empty;

    private void BeginSettingsBaselineLoad() => _applyingBaseline = true;

    private void AttachSettingsDraftEvents()
    {
        if (ReferenceEquals(_draftEventSource, _settingsViewModel)) { return; }
        if (_draftEventSource is not null) { _draftEventSource.PropertyChanged -= OnSettingsDraftPropertyChanged; }
        _draftEventSource = _settingsViewModel;
        _draftEventSource.PropertyChanged += OnSettingsDraftPropertyChanged;
    }

    private WidgetSettings EditableValues() => _settingsViewModel.ToSettings() with
    {
        BallLeft = null,
        BallTop = null,
        BallPositionScale = null,
    };

    private string ThresholdSignature()
    {
        string raw = _settingsWindow?.RawThresholdText ?? string.Empty;
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.CurrentCulture, out int value) && value is >= 1 and <= 100
            ? value.ToString(CultureInfo.InvariantCulture) : raw;
    }

    private void BeginSettingsSession()
    {
        _settingsSession++;
        _settingsSessionActive = true;
        _userGeometryEdited = false;
        _settingsWindow?.ResetThresholdToDraft();
        _lastBallDraft = EditableValues();
        CaptureSettingsBaseline(_settings with { DockSide = _ball?.DockSide ?? _settings.DockSide },
            _ball?.CanonicalPhysicalOrigin ?? default, _ball is null ? 1 : MonitorService.GetScaleForWindow(_ball),
            EditableValues(), ThresholdSignature());
    }

    private void CaptureSettingsBaseline(WidgetSettings baseline, Point origin, double scale, WidgetSettings editable, string threshold)
    {
        _baselineSettings = baseline;
        _baselineOriginPhysical = origin;
        _baselineEditable = editable;
        _baselineThresholdText = threshold;
    }

    private void AdvanceSettingsPositionBaseline(WidgetSettings retained, Point origin, double scale)
    {
        if (!_settingsSessionActive) { return; }
        WidgetSettings draft = EditableValues();
        bool placementUnedited = !_userGeometryEdited && draft.Placement == _baselineEditable.Placement &&
            draft.DockSide == _baselineEditable.DockSide;
        // An ordinary release can finish after Settings opened. Promote its retained
        // geometry, but carry only still-unedited placement metadata into that editor.
        // New appearance/field/geometry edits remain draft and keep their native preview.
        _baselineSettings = retained;
        _baselineOriginPhysical = origin;
        _baselineEditable = _baselineEditable with { Placement = retained.Placement, DockSide = retained.DockSide };
        if (!placementUnedited) { return; }
        _applyingBaseline = true;
        try
        {
            _settingsViewModel.PositionLeft = origin.X / scale;
            _settingsViewModel.PositionTop = origin.Y / scale;
            _settingsViewModel.DockSide = retained.DockSide;
            _settingsViewModel.SelectedPlacement = _settingsViewModel.PlacementOptions.Single(option => option.Placement == retained.Placement);
            _lastBallDraft = EditableValues();
        }
        finally { _applyingBaseline = false; }
    }

    private void OnSettingsDraftPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_shuttingDown || _applyingBaseline || !_settingsSessionActive || _ball is null) { return; }
        if (e.PropertyName is not (nameof(SettingsViewModel.OpacityPercent) or nameof(SettingsViewModel.BallSizePercent) or
            nameof(SettingsViewModel.SelectedPlacement) or "")) { return; }
        WidgetSettings next = EditableValues();
        WidgetSettings prior = _lastBallDraft;
        _lastBallDraft = next;
        if (next.Opacity != prior.Opacity)
        {
            // Undoing the displayed rounded value restores the exact loaded opacity.
            _ball.ApplyOpacity(next.Opacity == _baselineEditable.Opacity ? _baselineSettings.Opacity : next.Opacity);
        }
        bool resized = next.BallSizePercent != prior.BallSizePercent;
        bool placed = next.Placement != prior.Placement || next.DockSide != prior.DockSide;
        if (resized) { _ball.ApplySize(next.BallSizePercent); }
        if (placed && next.Placement != BallPlacement.Custom)
        {
            _dockController?.Undock();
            _ball.ClearDock();
            _settingsViewModel.DockSide = Core.Models.BallDockSide.None;
            _monitorService.ApplyPlacement(_ball, next.Placement);
            _userGeometryEdited = true;
        }
        else if (resized)
        {
            if (_ball.DockSide != BallDockSide.None) { _ball.ApplyDockPresentation(_ball.CurrentPresentation); }
            else if (next.Placement != BallPlacement.Custom) { _monitorService.ApplyPlacement(_ball, next.Placement); }
            else { _monitorService.ClampWindow(_ball); }
        }
        if (resized || placed) { OnBallMoved(); }
    }

    private void RecordDraftGeometry()
    {
        if (_ball is null) { return; }
        _userGeometryEdited = true;
        _applyingBaseline = true;
        try
        {
            double scale = MonitorService.GetScaleForWindow(_ball);
            Point origin = _ball.CanonicalPhysicalOrigin;
            _settingsViewModel.DockSide = _ball.DockSide;
            _settingsViewModel.PositionLeft = origin.X / scale;
            _settingsViewModel.PositionTop = origin.Y / scale;
            _settingsViewModel.SelectedPlacement = _settingsViewModel.PlacementOptions.First(option => option.Placement == BallPlacement.Custom);
            _lastBallDraft = EditableValues();
        }
        finally { _applyingBaseline = false; }
    }

    private bool IsSettingsDirty()
    {
        if (!_settingsSessionActive || _settingsWindow is null) { return false; }
        if (EditableValues() != _baselineEditable || _settingsViewModel.HasPendingStartupChange || ThresholdSignature() != _baselineThresholdText)
        { return true; }
        Point origin = _ball?.CanonicalPhysicalOrigin ?? _baselineOriginPhysical;
        return _userGeometryEdited && (Math.Abs(origin.X - _baselineOriginPhysical.X) > 1 ||
            Math.Abs(origin.Y - _baselineOriginPhysical.Y) > 1 || _ball?.DockSide != _baselineSettings.DockSide);
    }

    private void HandleSettingsLeaveRequested()
    {
        if (_leavePromptOpen || _leaveTask is { IsCompleted: false } || _shuttingDown || !_settingsSessionActive) { return; }
        _leaveTask = LeaveSettingsAsync();
    }

    private async Task LeaveSettingsAsync()
    {
        long session = _settingsSession;
        SettingsSaveOutcome? pendingOutcome = null;
        while (_pendingSave is { IsCompleted: false } pending)
        {
            pendingOutcome = await pending;
        }
        if (_shuttingDown || !_settingsSessionActive || session != _settingsSession || _settingsWindow is null) { return; }
        if (!IsSettingsDirty())
        {
            if (pendingOutcome is null or SettingsSaveOutcome.Success) { CloseSettingsSurface(); }
            return;
        }
        _leavePromptOpen = true;
        _dockController?.PushHold();
        SettingsLeaveChoice choice;
        try { choice = SettingsLeaveDialog.ShowFor(_settingsWindow); }
        finally { _dockController?.PopHold(); _leavePromptOpen = false; }
        if (_shuttingDown || !_settingsSessionActive || session != _settingsSession) { return; }
        if (choice == SettingsLeaveChoice.DiscardAndLeave)
        {
            DiscardDraft();
            CloseSettingsSurface();
        }
        else if (choice == SettingsLeaveChoice.SaveAndLeave)
        {
            SettingsSaveOutcome outcome = await SaveSettingsAsync();
            if (outcome == SettingsSaveOutcome.Success && _settingsSessionActive && session == _settingsSession && !IsSettingsDirty())
            { CloseSettingsSurface(); }
        }
    }

    private void CloseSettingsSurface()
    {
        _settingsSessionActive = false;
        _settingsWindow?.Hide();
        _controller?.SetSettingsOpen(false);
        if (_settingsHoldApplied) { _dockController?.PopHold(); _settingsHoldApplied = false; }
    }

    private void DiscardDraft()
    {
        BeginSettingsBaselineLoad();
        try
        {
            _settingsViewModel.LoadFrom(_baselineSettings, Environment.ProcessPath ?? string.Empty, _cliStatusText, _cliStatusMessage);
            _settingsWindow?.ResetThresholdToDraft();
            _lastBallDraft = EditableValues();
        }
        finally { _applyingBaseline = false; }
        _userGeometryEdited = false;
        RestoreBallGeometry(_baselineSettings, _baselineOriginPhysical);
    }

    private void RestoreBallGeometry(WidgetSettings settings, Point physicalOrigin)
    {
        if (_ball is null) { return; }
        bool previous = _ballFollowSuppressed;
        _ballFollowSuppressed = true;
        try
        {
            _dockController?.Undock();
            _ball.ClearDock();
            _ball.ApplyOpacity(settings.Opacity);
            _ball.ApplySize(settings.BallSizePercent);
            // Always use the captured actual physical origin, including for corner presets.
            // Re-evaluating a preset on a different preview monitor would restore the wrong screen.
            MonitorService.MoveDraggedWindow(_ball, physicalOrigin);
            _monitorService.ClampWindow(_ball);
            if (settings.DockSide != BallDockSide.None && _monitorService.IsDockSideEligible(_ball, settings.DockSide))
            {
                _ball.AdoptDock(settings.DockSide);
                _dockController?.Dock(settings.DockSide);
                _ball.ApplyDockPresentation(_dockController?.Presentation ?? Core.Services.BallDockPresentation.Collapsed);
            }
        }
        finally { _ballFollowSuppressed = previous; }
        if (!previous) { OnBallMoved(); }

    }

    private void OnBallDragInterrupted()
    {
        if (_shuttingDown || _ball is null) { return; }
        _dockController?.Undock();
        _ball.ClearDock();
        _monitorService.ClampWindow(_ball);
        if (_settingsSessionActive) { RecordDraftGeometry(); }
    }
}
