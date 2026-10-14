namespace CodexQuota.Core.Services;

/// <summary>
/// Schedules a one-shot callback. The desktop supplies a dispatcher-backed
/// implementation; tests supply a manually fired implementation so hover/close
/// timing can be verified without waiting on real time.
/// </summary>
public interface IDelayScheduler
{
    /// <summary>Schedules <paramref name="callback"/>; dispose the handle to cancel it.</summary>
    IDisposable Schedule(TimeSpan delay, Action callback);
}

/// <summary>
/// Owns the ball's hover/click/drag interaction state so timers cannot be orphaned
/// when a window closes or the app exits. Hover only reveals cached presentation
/// state and never triggers a provider query.
/// </summary>
public sealed class InteractionController : IDisposable
{
    private readonly IDelayScheduler _scheduler;
    private readonly TimeSpan _hoverDelay;
    private readonly TimeSpan _closeGrace;

    private IDisposable? _pendingOpen;
    private IDisposable? _pendingClose;
    private bool _overBall;
    private bool _overPanel;
    private bool _suppressed;
    private bool _settingsOpen;
    private bool _dragging;
    private bool _disposed;

    public InteractionController(IDelayScheduler scheduler, TimeSpan? hoverDelay = null, TimeSpan? closeGrace = null)
    {
        _scheduler = scheduler;
        _hoverDelay = hoverDelay ?? TimeSpan.FromMilliseconds(300);
        _closeGrace = closeGrace ?? TimeSpan.FromMilliseconds(250);
    }

    /// <summary>Whether the details panel should currently be on screen.</summary>
    public bool IsPanelVisible { get; private set; }

    /// <summary>Whether the panel is pinned open.</summary>
    public bool IsPinned { get; private set; }

    /// <summary>Raised when the panel becomes visible.</summary>
    public event Action? PanelShown;

    /// <summary>Raised when the panel is hidden.</summary>
    public event Action? PanelHidden;

    public void PointerEnteredBall()
    {
        _overBall = true;
        CancelClose();

        if (_settingsOpen || _suppressed || _dragging || IsPanelVisible)
        {
            return;
        }

        CancelOpen();
        _pendingOpen = _scheduler.Schedule(_hoverDelay, () =>
        {
            _pendingOpen = null;
            if (_overBall && !_settingsOpen && !_suppressed && !_dragging)
            {
                Show();
            }
        });
    }

    public void PointerExitedBall()
    {
        _overBall = false;
        CancelOpen();
        _suppressed = false;

        if (!_overPanel && !IsPinned)
        {
            ScheduleClose();
        }
    }

    public void PointerEnteredPanel()
    {
        _overPanel = true;
        CancelClose();
    }

    public void PointerExitedPanel()
    {
        _overPanel = false;

        if (!_overBall && !IsPinned)
        {
            ScheduleClose();
        }
    }

    /// <summary>A left click that is not a drag: pins an open preview or closes a pinned panel.</summary>
    public void BallClicked()
    {
        if (_settingsOpen || _dragging)
        {
            return;
        }

        CancelOpen();
        CancelClose();
        _suppressed = false;

        if (IsPinned)
        {
            IsPinned = false;
            Hide();
            return;
        }

        IsPinned = true;
        Show();
    }

    /// <summary>Called once the pointer moves past the drag threshold.</summary>
    public void DragStarted()
    {
        _dragging = true;
        _suppressed = true;
        CancelOpen();

        if (!IsPinned)
        {
            Hide();
        }
    }

    /// <summary>After a drag, hover resumes only after the pointer leaves the ball.</summary>
    public void DragCompleted()
    {
        _dragging = false;
        _suppressed = true;
        CancelOpen();
    }

    /// <summary>Keyboard or tray activation: show a pinned panel.</summary>
    public void ShowPinned()
    {
        if (_settingsOpen) { return; }
        _suppressed = false;
        CancelOpen();
        CancelClose();
        IsPinned = true;
        Show();
    }

    /// <summary>Escape/title close: hide, unpin, and suppress hover reopening until the pointer leaves the ball.</summary>
    public void CloseExplicitly()
    {
        CancelOpen();
        CancelClose();
        IsPinned = false;
        Hide();

        // Only suppress while the pointer is still over the ball; otherwise there is
        // nothing to suppress and hover must keep working.
        _suppressed = _overBall;
    }

    /// <summary>An outside click hides/unpins and reconciles pointer state before later hover.</summary>
    public void OutsideClicked()
    {
        _overBall = false;
        _overPanel = false;
        CloseExplicitly();
    }

    private void ScheduleClose()
    {
        CancelClose();
        if (_settingsOpen) { return; }
        _pendingClose = _scheduler.Schedule(_closeGrace, () =>
        {
            _pendingClose = null;
            if (!_overBall && !_overPanel && !IsPinned)
            {
                Hide();
            }
        });
    }

    private void Show()
    {
        if (_settingsOpen || _disposed) { return; }
        if (IsPanelVisible)
        {
            PanelShown?.Invoke();
            return;
        }

        IsPanelVisible = true;
        PanelShown?.Invoke();
    }

    private void Hide()
    {
        if (!IsPanelVisible)
        {
            return;
        }

        IsPanelVisible = false;
        PanelHidden?.Invoke();
    }

    /// <summary>Restricts Details for the whole Settings session, including owned dialogs and failed saves.</summary>
    public void SetSettingsOpen(bool open)
    {
        if (_settingsOpen == open) { return; }
        _settingsOpen = open;
        _overPanel = false;
        CloseExplicitly();
    }

    private void CancelOpen()
    {
        _pendingOpen?.Dispose();
        _pendingOpen = null;
    }

    private void CancelClose()
    {
        _pendingClose?.Dispose();
        _pendingClose = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelOpen();
        CancelClose();
    }
}
