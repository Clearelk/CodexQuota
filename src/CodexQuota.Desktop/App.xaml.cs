using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using CodexQuota.Core.LocalUsage;
using CodexQuota.Core.Models;
using CodexQuota.Core.Providers;
using CodexQuota.Core.Services;
using CodexQuota.Core.Transport;
using CodexQuota.Desktop.Services;
using CodexQuota.Desktop.ViewModels;
using CodexQuota.Desktop.Views;

namespace CodexQuota.Desktop;

/// <summary>
/// Composition root. Owns the provider, refresh coordinator, tray, windows and
/// Windows integration, enforces a single instance per user session, and shuts down
/// explicitly so no widget-owned helper process survives exit.
/// </summary>
public partial class App : Application
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _settingsUpdateGate = new(1, 1);

    private SingleInstanceService? _singleInstance;
    private SettingsStore _settingsStore = null!;
    private WidgetSettings _settings = WidgetSettings.Default;
    private CodexExecutableResolver _resolver = null!;
    private MonitorService _monitorService = null!;
    private IStartupRegistration _startupService = null!;
    private ThemeService? _themeService;
    private LocalizationService? _localization;
    private RefreshCoordinator? _coordinator;
    private LocalUsageCoordinator? _localUsage;
    private ObservedUsageEstimator? _observedUsage;
    private InteractionController? _controller;
    private BallDockController? _dockController;
    private OutsideClickObserver? _outsideClicks;
    private readonly LowQuotaAlertTracker _quotaAlerts = new();
    private WidgetViewModel _widget = null!;
    private SettingsViewModel _settingsViewModel = null!;
    private FloatingBallWindow? _ball;
    private DetailsWindow? _details;
    private bool _detailsDockHold;
    private bool _ballFollowSuppressed;
    private bool _detailsReflowPending;
    private SettingsWindow? _settingsWindow;
    private TrayService? _tray;
    private string _cliStatusText = string.Empty;
    private AppMessage? _cliStatusMessage;
    private bool _allowWindowClose;
    private bool _shuttingDown;
    private string? _exitVerificationPath;
    private bool _exitVerificationDuringRefresh;
    private DateTimeOffset? _lastLocalScanTrigger;
    private DateOnly _lastLocalScanDate;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // A static 56-DIP widget does not benefit from a resident Direct3D device/driver stack.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        try
        {
            if (TryGetOption(e.Args, "--verify", out string? verifyDirectory))
            {
                await RunVerificationAsync(verifyDirectory!);
                Shutdown();
                return;
            }

            if (TryGetOption(e.Args, "--verify-live", out string? liveOutput))
            {
                await RunLiveVerificationAsync(liveOutput!);
                Shutdown();
                return;
            }

            if (TryGetOption(e.Args, "--verify-local", out string? localOutput))
            {
                await RunLocalUsageVerificationAsync(localOutput!);
                Shutdown();
                return;
            }

            await StartNormalAsync();
            if (TryGetOption(e.Args, "--verify-exit", out string? exitOutput))
            {
                _exitVerificationPath = exitOutput;
                await Task.Delay(100);
                _exitVerificationDuringRefresh = _coordinator?.State is { Status: RefreshStatus.Loading, LastAttempt: not null };
                _tray?.Menu.Items[5].PerformClick();
            }
            if (TryGetOption(e.Args, "--resource-probe", out string? probeOutput))
            {
                await Task.Delay(6000);
                using Process process = Process.GetCurrentProcess();
                var memory = GC.GetGCMemoryInfo();
                await WriteJsonAsync(probeOutput!, new
                {
                    buildModuleId = typeof(App).Module.ModuleVersionId.ToString(),
                    workingSet = process.WorkingSet64,
                    privateBytes = process.PrivateMemorySize64,
                    heapBytes = GC.GetTotalMemory(false),
                    allocatedBytes = GC.GetTotalAllocatedBytes(),
                    heapCommittedBytes = memory.TotalCommittedBytes,
                    renderMode = System.Windows.Media.RenderOptions.ProcessRenderMode.ToString(),
                    serverGc = System.Runtime.GCSettings.IsServerGC,
                    detailsCreated = _details is not null,
                    settingsCreated = _settingsWindow is not null,
                    modules = process.Modules.Cast<ProcessModule>().Select(module => new { name = module.ModuleName, bytes = module.ModuleMemorySize }).ToArray(),
                });
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("startup failed: " + exception.GetType().Name);
            _shuttingDown = true;
            await ShutdownCoreAsync(1);
        }
    }

    private async Task StartNormalAsync(IQuotaProvider? verificationProvider = null, string? settingsRoot = null, string? localUsageRoot = null)
    {
        _singleInstance = new SingleInstanceService();
        if (!_singleInstance.TryAcquire())
        {
            // Another instance already owns the ball for this user session.
            Shutdown();
            return;
        }

        _settingsStore = new SettingsStore(settingsRoot);
        _settings = SettingsStore.Normalize(await _settingsStore.LoadAsync());
        _localization = new LocalizationService(this);
        _localization.Apply(_settings.Language);
        _resolver = new CodexExecutableResolver();
        _monitorService = new MonitorService();
        _startupService = new StartupService();

        _widget = new WidgetViewModel(null);
        _widget.PropertyChanged += (_, _) => QueueDetailsReflow();
        _widget.UpdateSettings(_settings);
        _settingsViewModel = new SettingsViewModel(_startupService, _settingsStore);
        _settingsViewModel.ApplyLanguage(_settings.Language);

        _themeService = new ThemeService(this);
        _themeService.ThemeChanged += OnThemeChanged;
        _themeService.Apply(_settings.Theme);

        var clientFactory = new JsonRpcProcessClientFactory(new ProcessTransportProcessFactory());
        _coordinator = new RefreshCoordinator(
            verificationProvider ?? new CodexQuotaProvider(clientFactory, () => _resolver.Resolve(_settings.CodexCliPath), TimeProvider.System),
            TimeProvider.System,
            _settings.RefreshInterval);
        _coordinator.StateChanged += OnCoordinatorStateChanged;
        _widget.Update(_coordinator.State);

        // The observed-consumption estimate is derived only from coordinator state; it
        // never performs its own provider call, file read or persistence.
        _observedUsage = new ObservedUsageEstimator(TimeProvider.System);
        _widget.UpdateObservedUsage(_observedUsage.Current);

        // Device-local daily statistics run on their own background coordinator so they
        // never block the UI thread and keep working when the official read fails.
        _localUsage = new LocalUsageCoordinator(
            new LocalUsageScanner(localUsageRoot ?? LocalUsagePaths.ResolveCodexHome(), TimeProvider.System));
        _localUsage.Updated += OnLocalUsageUpdated;

        _controller = new InteractionController(new DispatcherDelayScheduler(Dispatcher));
        _controller.PanelShown += ShowDetailsPanel;
        _controller.PanelHidden += HideDetailsPanel;
        _dockController = new BallDockController(new DispatcherDelayScheduler(Dispatcher));
        _dockController.PresentationChanged += OnDockPresentationChanged;
        _outsideClicks = new OutsideClickObserver(Dispatcher);
        _outsideClicks.ButtonDown += OnObservedButtonDown;

        CreateBall();
        CreateTray();
        RefreshCliStatus();

        _localUsage.RequestScan();
        await _coordinator.StartAsync();
    }

    private void CreateBall()
    {
        _ball = new FloatingBallWindow { DataContext = _widget };
        _ball.AttachController(_controller!);
        _ball.AttachDockController(_dockController!);
        AttachDockMenuHolds();
        _ball.ApplyOpacity(_settings.Opacity);
        _ball.ApplySize(_settings.BallSizePercent);
        _ball.PositionPersisted += OnBallPositionPersisted;
        _ball.DragInterrupted += OnBallDragInterrupted;
        _ball.UserDragged += () => { if (_settingsSessionActive) { RecordDraftGeometry(); } };
        _ball.BallMoved += OnBallMoved;
        // Shape-only dock transitions emit no native position change; follow explicitly.
        _ball.DockPresentationChanged += OnBallMoved;
        _ball.RefreshRequested += RequestRefresh;
        _ball.SettingsRequested += ShowSettings;
        _ball.ExitRequested += RequestShutdown;
        _ball.DisplayEnvironmentChanged += () =>
        {
            if (_ball is null || _shuttingDown) { return; }
            // Environment changes re-project a valid dock from its canonical anchor
            // instead of running the ordinary full-ball clamp through the strip.
            if (_ball.DockSide != BallDockSide.None)
            {
                if (_ball.DockHomeAvailable && _monitorService.IsDockSideEligible(_ball, _ball.DockSide))
                { _ball.ApplyDockPresentation(_ball.CurrentPresentation); }
                else
                {
                    _dockController?.Undock(); _ball.ClearDock(); _monitorService.ClampWindow(_ball);
                    if (_settingsSessionActive)
                    {
                        _settingsViewModel.DockSide = BallDockSide.None;
                        if (!_userGeometryEdited)
                        {
                            _baselineEditable = _baselineEditable with { DockSide = BallDockSide.None };
                            _baselineSettings = _baselineSettings with { DockSide = BallDockSide.None };
                        }
                    }
                }
            }
            else
            {
                _monitorService.ClampWindow(_ball);
            }

            OnBallMoved();
        };
        _ball.Closing += (_, args) =>
        {
            if (!_allowWindowClose) { args.Cancel = true; RequestShutdown(); }
        };
        ApplyInitialPlacement();
        _ball.Show();
        _monitorService.RestoreBall(_ball, _settings);
        _monitorService.ApplyPlacement(_ball, _settings.Placement);
        RestoreSavedDock();
    }

    /// <summary>
    /// Restores a valid saved side dock after ordinary placement; an unavailable or
    /// shared edge falls back to a reachable ordinary ball without silently docking
    /// somewhere else or rewriting the file.
    /// </summary>
    private void RestoreSavedDock()
    {
        if (_ball is null || _settings.DockSide == BallDockSide.None)
        {
            return;
        }

        double savedScale = _settings.BallPositionScale ?? MonitorService.GetScaleForWindow(_ball);
        int diameter = (int)Math.Round(_ball.BallSize * savedScale);
        var savedAnchor = new PixelRect((int)Math.Round((_settings.BallLeft ?? 0) * savedScale),
            (int)Math.Round((_settings.BallTop ?? 0) * savedScale), diameter, diameter);
        if (DockGeometry.CanRestoreDock(savedAnchor, _settings.DockSide, _monitorService.GetDockMonitors()) && _monitorService.IsDockSideEligible(_ball, _settings.DockSide))
        {
            _ball.AdoptDock(_settings.DockSide);
            _dockController?.Dock(_settings.DockSide);
            OnBallMoved();
        }
        else
        {
            // In-memory fallback only: the next explicit Save records the reachable state.
            _settings = _settings with { DockSide = BallDockSide.None };
            _monitorService.ClampWindow(_ball);
        }
    }

    /// <summary>
    /// Open context menus hold a docked reveal so the grace cannot collapse the ball
    /// underneath the user's popup. The menu instance is shared by the ball and strip.
    /// </summary>
    private void AttachDockMenuHolds()
    {
        if ((_ball?.FindName("Ball") as FrameworkElement)?.ContextMenu is not { } menu)
        {
            return;
        }

        menu.Opened += (_, _) => _dockController?.PushHold();
        menu.Closed += (_, _) => _dockController?.PopHold();
    }

    /// <summary>
    /// Applies the dock controller's desired presentation to the ball window and follows
    /// already-visible panels once; shape-only transitions emit no native position change.
    /// </summary>
    private void OnDockPresentationChanged()
    {
        if (_shuttingDown || _ball is null || _dockController is null)
        {
            return;
        }

        _ball.ApplyDockPresentation(_dockController.Presentation);
    }

    private void CreateTray()
    {
        _tray = new TrayService(
            showDetails: () => _controller!.ShowPinned(),
            refresh: RequestRefresh,
            showSettings: ShowSettings,
            toggleVisibility: ToggleBallVisibility,
            exit: RequestShutdown,
            language: _settings.Language);
        _tray.SetBallVisible(_widget.IsVisible);
        _tray.UpdateIcon(_widget.IconState);
    }

    private void ApplyInitialPlacement()
    {
        IReadOnlyList<MonitorWorkArea> areas = _monitorService.GetWorkAreas();
        MonitorWorkArea? primary = areas.FirstOrDefault(area => area.IsPrimary) ?? areas.FirstOrDefault();

        if (primary is null)
        {
            _ball!.MoveTo(_settings.BallLeft ?? 120, _settings.BallTop ?? 120);
            return;
        }

        (double left, double top) = _settings.BallLeft is { } savedLeft && _settings.BallTop is { } savedTop
            ? MonitorPlacement.Clamp(savedLeft, savedTop, _ball!.Width, _ball.Height, areas)
            : MonitorPlacement.GetInitialPosition(primary, _ball!.Width, _ball.Height);

        _ball.MoveTo(left, top);
    }

    /// <summary>
    /// Repositions already-visible attached windows beside the ball after each native
    /// drag move. Hidden windows are never created or shown, and the path performs no
    /// settings write, provider read or local scan.
    /// </summary>
    private void OnBallMoved()
    {
        if (_shuttingDown || _ball is null || _ballFollowSuppressed)
        {
            return;
        }

        if (_details?.IsVisible == true)
        {
            _monitorService.SizePanel(_ball, _details);
            _monitorService.FollowPanel(_ball, _details);
        }

        if (_settingsWindow?.IsVisible == true)
        {
            _monitorService.SizePanel(_ball, _settingsWindow);
            _monitorService.FollowPanel(_ball, _settingsWindow);
        }
    }

    private void ShowDetailsPanel()
    {
        if (_settingsSessionActive || _shuttingDown) { return; }
        _details ??= CreateDetailsWindow();

        bool pinned = _controller?.IsPinned ?? false;
        if (_ball is not null) { _monitorService.SizePanel(_ball, _details); }
        _details.ShowActivated = pinned;

        // A reused hidden Window remembers its last focused element, so every path that
        // takes the panel from hidden to visible resets the neutral panel surface below.
        // Otherwise a previously clicked header gear re-highlights as if selected.
        bool opening = !_details.IsVisible;
        if (opening)
        {
            _details.Show();
        }
        else if (pinned)
        {
            _details.Activate();
        }

        PositionDetailsPanel();
        _outsideClicks?.Start();
        if (!_detailsDockHold)
        {
            _detailsDockHold = true;
            _dockController?.PushHold();
        }

        if (pinned)
        {
            _details.Activate();
            _details.FocusOpeningSurface();
        }
        else if (opening)
        {
            // Hover preview and tray details must not steal keyboard focus either: the
            // panel is not activated here, so only its own remembered element is reset.
            _details.FocusOpeningSurface();
        }
    }

    private void QueueDetailsReflow()
    {
        if (_shuttingDown || _details?.IsVisible != true || _detailsReflowPending) { return; }
        _detailsReflowPending = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            _detailsReflowPending = false;
            if (_shuttingDown || _ball is null || _details?.IsVisible != true) { return; }
            _monitorService.SizePanel(_ball, _details);
            _monitorService.FollowPanel(_ball, _details);
        });
    }

    private DetailsWindow CreateDetailsWindow()
    {
        var window = new DetailsWindow { DataContext = _widget, Owner = null };

        window.PointerEntered += () => _controller?.PointerEnteredPanel();
        window.PointerExited += () => _controller?.PointerExitedPanel();
        window.CloseRequested += () => _controller?.CloseExplicitly();
        window.SettingsRequested += ShowSettings;
        window.Closing += (_, args) =>
        {
            if (_allowWindowClose)
            {
                return;
            }

            args.Cancel = true;
            _controller?.CloseExplicitly();
            window.Hide();
        };

        return window;
    }

    private void HideDetailsPanel()
    {
        _outsideClicks?.Stop();
        _details?.Hide();
        if (_detailsDockHold) { _dockController?.PopHold(); _detailsDockHold = false; }
    }

    private void OnObservedButtonDown(Point point, nint target, bool owned)
    {
        if (_details?.IsVisible != true || _shuttingDown) { return; }
        static bool Hits(Window? window, Point screenPoint) => window?.IsVisible == true &&
            window.InputHitTest(window.PointFromScreen(screenPoint)) is not null;
        if (Hits(_ball, point) || Hits(_details, point) || Hits(_settingsWindow, point)) { return; }
        // Keep native owned menus/dropdowns/dialogs usable, even after their popup
        // disappears before the dispatcher processes this observed click.
        if (owned && target != 0 && new Window?[] { _ball, _details, _settingsWindow }
            .All(window => window is null || new System.Windows.Interop.WindowInteropHelper(window).Handle != target)) { return; }
        _controller?.OutsideClicked();
    }

    private void PositionDetailsPanel()
    {
        if (_details is null || _ball is null)
        {
            return;
        }

        _monitorService.SizePanel(_ball, _details);
        _monitorService.PlacePanel(_ball, _details);
    }

    private void ShowSettings()
    {
        bool opening = _settingsWindow is null || !_settingsWindow.IsVisible;
        // Retained values are loaded only on a genuine hidden-to-visible opening, so an
        // already-visible activation never discards the draft, validation text or focus.
        if (opening)
        {
            _controller?.SetSettingsOpen(true);
            BeginSettingsBaselineLoad();
            try
            {
                _settingsViewModel.LoadFrom(_settings, Environment.ProcessPath ?? string.Empty, _cliStatusText, _cliStatusMessage);
                if (_pendingOrdinaryPositionWrites > 0 && _ball is not null)
                {
                    // The completed ordinary gesture is already the visible geometry;
                    // its queued write must not make a newly opened editor use a stale
                    // corner preset, including if Save is submitted before IO completes.
                    Point origin = _ball.CanonicalPhysicalOrigin;
                    double scale = MonitorService.GetScaleForWindow(_ball);
                    _settingsViewModel.PositionLeft = origin.X / scale;
                    _settingsViewModel.PositionTop = origin.Y / scale;
                    _settingsViewModel.SelectedPlacement = _settingsViewModel.PlacementOptions.Single(option => option.Placement == BallPlacement.Custom);
                    _settingsViewModel.DockSide = _ball.DockSide;
                }
            }
            finally { _applyingBaseline = false; }
        }

        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow { DataContext = _settingsViewModel };
            _settingsWindow.SaveRequested += () => _ = SaveSettingsAndCloseAsync();
            _settingsWindow.CloseRequested += HandleSettingsLeaveRequested;
            _settingsWindow.Closing += (_, args) =>
            {
                if (_allowWindowClose)
                {
                    return;
                }

                args.Cancel = true;
                HandleSettingsLeaveRequested();
            };
            AttachSettingsDraftEvents();
        }

        // A reused hidden Window remembers the last action's keyboard focus.
        // Start a new opening at a neutral surface; ordinary activation keeps editing focus.
        if (_ball is not null) { _monitorService.SizePanel(_ball, _settingsWindow); }
        _settingsWindow.Show();
        PositionSettingsWindow();
        _settingsWindow.Activate();
        if (opening)
        {
            _settingsWindow.ResetOpeningSurface();
            BeginSettingsSession();
        }

        if (!_settingsHoldApplied)
        {
            _dockController?.PushHold();
            _settingsHoldApplied = true;
        }
    }

    private void PositionSettingsWindow()
    {
        if (_settingsWindow is null || _ball is null)
        {
            return;
        }

        _monitorService.PlacePanel(_ball, _settingsWindow);
    }

    private void RefreshCliStatus()
    {
        _cliStatusMessage = _resolver.TryResolve(_settings.CodexCliPath, out CodexLaunchSpec? spec, out QuotaProviderException? failure)
            ? new AppMessage("CliFound", spec!.DisplayPath)
            : failure?.PresentationMessage ?? new AppMessage("CliMissing");
        _cliStatusText = _cliStatusMessage.Format(_settings.Language);
    }

    private void ApplyCommittedLanguage()
    {
        _localization?.Apply(_settings.Language);
        _settingsViewModel.ApplyLanguage(_settings.Language);
        _cliStatusText = _cliStatusMessage?.Format(_settings.Language) ?? _cliStatusText;
        _settingsViewModel.UpdateCliStatus(_cliStatusText, _cliStatusMessage);
        _settingsWindow?.RefreshLocalizedValidation();
        _tray?.ApplyLanguage(_settings.Language);
    }

    private void RequestRefresh()
    {
        if (_coordinator is null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _coordinator.RequestRefreshAsync(_lifetime.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception)
            {
                // The coordinator publishes a safe failure state instead of throwing.
            }
        });
    }

    private void ToggleBallVisibility()
    {
        if (_ball is null || _widget is null)
        {
            return;
        }

        _widget.IsVisible = !_widget.IsVisible;

        if (_widget.IsVisible)
        {
            _dockController?.SetHidden(false);
            _ball.Show();
        }
        else
        {
            _controller?.CloseExplicitly();
            _ball.Hide();
            _dockController?.SetHidden(true);
        }

        _tray?.SetBallVisible(_widget.IsVisible);
    }

    private void OnCoordinatorStateChanged(RefreshState state)
    {
        RequestLocalScanIfDue(state);
        ObservedUsageSnapshot observed = _observedUsage?.Observe(state)
            ?? ObservedUsageSnapshot.Initial(DateOnly.FromDateTime(DateTime.Now));
        Dispatcher.BeginInvoke(() =>
        {
            _widget.Update(state);
            _widget.UpdateObservedUsage(observed);
            _tray?.UpdateIcon(_widget.IconState);
            EvaluateQuotaNotification(state);
        });
    }

    /// <summary>
    /// Requests a background local scan only when a new official cycle completed or the
    /// local calendar day changed. Panel and ball interactions never reach this method.
    /// </summary>
    private void RequestLocalScanIfDue(RefreshState state)
    {
        if (_localUsage is null)
        {
            return;
        }

        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        bool dateChanged = today != _lastLocalScanDate;
        bool attemptChanged = state.LastAttempt != _lastLocalScanTrigger;
        if (!dateChanged && !attemptChanged)
        {
            return;
        }

        _lastLocalScanDate = today;
        _lastLocalScanTrigger = state.LastAttempt;
        _localUsage.RequestScan();
    }

    private void OnLocalUsageUpdated(LocalUsageSnapshot snapshot)
    {
        if (_shuttingDown)
        {
            return;
        }

        Dispatcher.BeginInvoke(() => _widget?.UpdateLocalUsage(snapshot));
    }

    private void EvaluateQuotaNotification(RefreshState state)
    {
        if (_quotaAlerts.ShouldNotify(_settings, state) && QuotaProjection.SelectFiveHourWindow(state.LastQuota) is { } selected)
        {
            _tray?.ShowLowQuotaNotification(selected.RemainingPercent, _settings.LowQuotaThreshold);
        }
    }

    private void OnThemeChanged()
    {
        _widget?.RefreshTheme();
        if (_widget is not null) { _tray?.UpdateIcon(_widget.IconState); }
    }

    private void RequestShutdown()
    {
        if (_shuttingDown)
        {
            return;
        }

        _shuttingDown = true;
        _ = ShutdownCoreAsync();
    }

    private async Task ShutdownCoreAsync(int exitCode = 0)
    {
        _allowWindowClose = true;
        _lifetime.Cancel();
        await _settingsUpdateGate.WaitAsync();
        _settingsUpdateGate.Release();

        if (_coordinator is not null)
        {
            await _coordinator.DisposeAsync();
            _coordinator = null;
        }

        if (_localUsage is not null)
        {
            await _localUsage.DisposeAsync();
            _localUsage = null;
        }

        _tray?.Dispose();
        _tray = null;

        _controller?.Dispose();
        _dockController?.Dispose();
        _dockController = null;
        _outsideClicks?.Dispose();
        _outsideClicks = null;
        _themeService?.Dispose();
        _themeService = null;

        _details?.Close();
        _settingsWindow?.Close();
        _ball?.Close();
        _details = null;
        _settingsWindow = null;
        _ball = null;

        _singleInstance?.Dispose();
        _singleInstance = null;

        if (_exitVerificationPath is { } output)
        {
            await WriteJsonAsync(output, new { buildModuleId = typeof(App).Module.ModuleVersionId.ToString(), trayExitInvoked = true, refreshWasInProgress = _exitVerificationDuringRefresh, coordinatorDisposed = _coordinator is null, windowsClosed = _ball is null && _details is null && _settingsWindow is null, trayDisposed = _tray is null });
        }
        Shutdown(exitCode);
    }

    private async Task RunVerificationAsync(string outputDirectory)
    {
        _localization = new LocalizationService(this);
        _localization.Apply(AppLanguage.SimplifiedChinese);
        _settingsStore = new SettingsStore();
        _settings = SettingsStore.Normalize(await _settingsStore.LoadAsync());
        _resolver = new CodexExecutableResolver();
        _monitorService = new MonitorService();
        _startupService = new StartupService();
        _themeService = new ThemeService(this);
        _themeService.Apply(CodexQuota.Core.Models.ThemeMode.Light);

        _widget = new WidgetViewModel(null);
        _settingsViewModel = new SettingsViewModel(_startupService, _settingsStore);

        var harness = new VerificationHarness(_widget, _settingsViewModel, Environment.ProcessPath ?? string.Empty, _themeService);
        await harness.RunAsync(outputDirectory);

        _themeService.Dispose();
        await RunDesktopEventsAsync(outputDirectory);
    }

    private async Task RunLiveVerificationAsync(string outputPath)
    {
        _settingsStore = new SettingsStore();
        _settings = SettingsStore.Normalize(await _settingsStore.LoadAsync());
        _resolver = new CodexExecutableResolver();

        string? cliPath = null;
        string failureMessage = string.Empty;
        if (_resolver.TryResolve(_settings.CodexCliPath, out CodexLaunchSpec? spec, out QuotaProviderException? resolutionFailure))
        {
            cliPath = spec!.DisplayPath;
        }
        else
        {
            failureMessage = resolutionFailure?.Message ?? "未找到官方 Codex 客户端。";
        }

        var report = new Dictionary<string, object?>
        {
            ["buildModuleId"] = typeof(App).Module.ModuleVersionId.ToString(),
            ["cliResolved"] = cliPath is not null,
            ["sanitizedFailure"] = failureMessage,
            ["elapsedMs"] = null,
            ["accountType"] = null,
            ["plan"] = null,
            ["windows"] = Array.Empty<object>(),
            ["dailyAvailability"] = null,
            ["dailyTokensPresent"] = false,
            ["dailySourceMatchesLocalDate"] = false,
            ["helperProcessCountBefore"] = CountCodexProcesses(),
        };

        if (cliPath is null)
        {
            await WriteJsonAsync(outputPath, report);
            throw new InvalidOperationException("Official CLI resolution failed.");
        }

        var clientFactory = new JsonRpcProcessClientFactory(new ProcessTransportProcessFactory());
        await using var provider = new CodexQuotaProvider(clientFactory, () => _resolver.Resolve(_settings.CodexCliPath), TimeProvider.System);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            DateOnly today = DateOnly.FromDateTime(DateTime.Now);
            ProviderRefreshResult result = await provider.FetchAsync(today, CancellationToken.None);
            stopwatch.Stop();

            QuotaBucket? main = QuotaProjection.SelectMainBucket(result.Quota);
            report["elapsedMs"] = stopwatch.ElapsedMilliseconds;
            report["accountType"] = "chatgpt";
            report["plan"] = result.Quota.Plan;
            report["windows"] = main?.Windows.Select(window => new
            {
                slot = window.Slot,
                durationMinutes = window.DurationMinutes,
                usedPercent = window.UsedPercent,
                remainingPercent = window.RemainingPercent,
                hasResetTime = window.ResetsAt is not null,
            }).ToArray() ?? [];
            report["ballRemainingPercent"] = QuotaProjection.GetRemainingPercent(result.Quota);
            report["dailyAvailability"] = result.DailyUsage.Availability.ToString();
            report["dailyTokensPresent"] = result.DailyUsage.Tokens is not null;
            report["dailySourceDate"] = result.DailyUsage.SourceDate?.ToString("yyyy-MM-dd");
            report["dailySourceMatchesLocalDate"] = result.DailyUsage.SourceDate == today;
            report["mainContainsInvalidWindow"] = main?.ContainsInvalidWindow ?? false;
        }
        catch (QuotaProviderException exception)
        {
            stopwatch.Stop();
            report["elapsedMs"] = stopwatch.ElapsedMilliseconds;
            report["sanitizedFailure"] = exception.Kind + ": " + exception.Message;
        }

        report["helperProcessCountAfter"] = CountCodexProcesses();
        await WriteJsonAsync(outputPath, report);
        if (report["accountType"] is null) { throw new InvalidOperationException("Official read verification failed."); }
    }

    /// <summary>
    /// Sanitized verification of the real device-local source. It emits only aggregate
    /// numbers, status and byte counts: never a session identifier, a path, a prompt, a
    /// response, or a raw record line.
    /// </summary>
    private static async Task RunLocalUsageVerificationAsync(string outputPath)
    {
        string root = LocalUsagePaths.ResolveCodexHome();
        bool configured = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CODEX_HOME"));
        var stopwatch = Stopwatch.StartNew();
        LocalUsageSnapshot snapshot = await new LocalUsageScanner(root, TimeProvider.System).ScanAsync(CancellationToken.None);
        stopwatch.Stop();

        await WriteJsonAsync(outputPath, new
        {
            kind = "sanitized-local-usage",
            buildModuleId = typeof(App).Module.ModuleVersionId.ToString(),
            rootSource = configured ? "CODEX_HOME" : "user .codex",
            rootExists = Directory.Exists(root),
            date = snapshot.Date.ToString("yyyy-MM-dd"),
            localDateMatchesToday = snapshot.Date == DateOnly.FromDateTime(DateTime.Now),
            sessionCount = snapshot.SessionCount,
            totalTokens = snapshot.TotalTokens,
            coverage = snapshot.Coverage.ToString(),
            coverageNote = snapshot.CoverageNote,
            filesInspected = snapshot.FilesInspected,
            filesSkipped = snapshot.FilesSkipped,
            bytesRead = snapshot.BytesRead,
            elapsedMs = stopwatch.ElapsedMilliseconds,
            note = "Aggregate numbers only. No session identifiers, file paths, prompts, responses or raw records are emitted or persisted. Decoding projects allowed fields and keeps only bounded transient unfinished frames.",
        });
    }

    private static int CountCodexProcesses()
    {
        try
        {
            return Process.GetProcessesByName("codex").Length;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static async Task WriteJsonAsync(string path, object report)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
            Encoding.UTF8);

        Console.WriteLine("live verification written to " + path);
    }

    private static bool TryGetOption(string[] args, string name, out string? value)
    {
        value = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                value = args[i + 1];
                return true;
            }
        }

        return false;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!_shuttingDown)
        {
            _allowWindowClose = true;
            _lifetime.Cancel();
            _tray?.Dispose();
            _controller?.Dispose();
            _themeService?.Dispose();
            _singleInstance?.Dispose();
            try
            {
                // Best-effort cleanup of the owned background scanner at process exit.
                _localUsage?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
                // The process is exiting; nothing further can be repaired here.
            }
        }

        _lifetime.Dispose();
        base.OnExit(e);
    }
}
