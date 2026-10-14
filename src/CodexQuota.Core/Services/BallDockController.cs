using CodexQuota.Core.Models;

namespace CodexQuota.Core.Services;

/// <summary>Transient on-screen presentation of a docked ball on its existing HWND.</summary>
public enum BallDockPresentation
{
    /// <summary>Ordinary undocked ball.</summary>
    Floating,

    /// <summary>Collapsed to the slim side-edge strip.</summary>
    Collapsed,

    /// <summary>Docked but temporarily expanded to the full inward ball.</summary>
    Revealed,
}

/// <summary>
/// Minimal Core state owner for side-edge docking: canonical side plus the transient
/// collapsed/revealed presentation and the roughly 250 ms leave grace. Hover, reveal and
/// recollapse are presentation-only state; they never write preferences, request reads
/// or scans, and are not a Settings edit. Engagement holds (visible panels, menus,
/// confirmations, active drag) keep a docked ball revealed; the leave callback rechecks
/// current state once instead of polling.
/// </summary>
public sealed class BallDockController : IDisposable
{
    private readonly IDelayScheduler _scheduler;
    private readonly TimeSpan _grace;

    private BallDockSide _side;
    private BallDockPresentation _presentation;
    private bool _overStrip;
    private bool _overRevealed;
    private bool _dragging;
    private bool _hidden;
    private int _holds;
    private IDisposable? _pendingCollapse;
    private bool _disposed;

    public BallDockController(IDelayScheduler scheduler, TimeSpan? leaveGrace = null)
    {
        _scheduler = scheduler;
        _grace = leaveGrace ?? TimeSpan.FromMilliseconds(250);
    }

    /// <summary>Canonical dock side; None while the ball is an ordinary floating ball.</summary>
    public BallDockSide Side => _side;

    /// <summary>Current transient presentation derived from side and engagement state.</summary>
    public BallDockPresentation Presentation => _presentation;

    /// <summary>Raised when the desired presentation changed and the window must re-project.</summary>
    public event Action? PresentationChanged;

    /// <summary>Adopts a canonical dock (from a qualifying release or restored preferences).</summary>
    public void Dock(BallDockSide side)
    {
        if (side is not (BallDockSide.Left or BallDockSide.Right))
        {
            return;
        }

        _side = side;
        CancelCollapse();
        Update();
    }

    /// <summary>Clears the canonical dock, e.g. after dragging the ball back inside.</summary>
    public void Undock()
    {
        if (_side == BallDockSide.None)
        {
            return;
        }

        _side = BallDockSide.None;
        CancelCollapse();
        Update();
    }

    public void PointerEnteredStrip()
    {
        _overStrip = true;
        CancelCollapse();
        Update();
    }

    public void PointerExitedStrip()
    {
        _overStrip = false;
        ScheduleCollapseIfIdle();
    }

    public void PointerEnteredRevealed()
    {
        _overRevealed = true;
        CancelCollapse();
        Update();
    }

    public void PointerExitedRevealed()
    {
        _overRevealed = false;
        ScheduleCollapseIfIdle();
    }

    /// <summary>A press/drag from the strip or revealed ball holds the reveal and blocks recollapse.</summary>
    public void DragStarted()
    {
        _dragging = true;
        CancelCollapse();
        Update();
    }

    public void DragEnded()
    {
        _dragging = false;
        ScheduleCollapseIfIdle();
    }

    /// <summary>
    /// Marks an engagement hold such as a visible Details/Settings panel, an open menu or
    /// an owned confirmation. Holds nest, so every push needs exactly one pop.
    /// </summary>
    public void PushHold()
    {
        _holds++;
        CancelCollapse();
        Update();
    }

    public void PopHold()
    {
        if (_holds > 0)
        {
            _holds--;
        }

        // Reevaluate from current state even though the pointer may not have moved.
        ScheduleCollapseIfIdle();
    }

    /// <summary>Suppresses the reveal while the ball window is hidden; canonical side is retained.</summary>
    public void SetHidden(bool hidden)
    {
        _hidden = hidden;
        if (hidden)
        {
            CancelCollapse();
        }

        Update();
    }

    private void Update()
    {
        if (_disposed) { return; }
        BallDockPresentation desired = _side == BallDockSide.None
            ? BallDockPresentation.Floating
            : !_hidden && (_dragging || _overStrip || _overRevealed || _holds > 0)
                ? BallDockPresentation.Revealed
                : BallDockPresentation.Collapsed;

        if (desired == _presentation)
        {
            return;
        }

        _presentation = desired;
        PresentationChanged?.Invoke();
    }

    private void ScheduleCollapseIfIdle()
    {
        if (_disposed || _side == BallDockSide.None || _dragging || _hidden || _overStrip || _overRevealed || _holds > 0)
        {
            return;
        }

        CancelCollapse();
        _pendingCollapse = _scheduler.Schedule(_grace, () =>
        {
            _pendingCollapse = null;
            // Single recheck of event-derived state at execution; no polling.
            if (_side != BallDockSide.None && !_dragging && !_hidden && !_overStrip && !_overRevealed && _holds <= 0)
            {
                Update();
            }
        });
    }

    private void CancelCollapse()
    {
        _pendingCollapse?.Dispose();
        _pendingCollapse = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelCollapse();
    }
}
