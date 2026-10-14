using CodexQuota.Core.Services;
using CodexQuota.Tests.Support;

namespace CodexQuota.Tests;

public sealed class InteractionControllerTests
{
    [Fact]
    public void OutsideClickUnpinsAndAllowsLaterHoverWithoutAStalePointer()
    {
        (InteractionController controller, ManualDelayScheduler scheduler) = Create();
        controller.PointerEnteredBall();
        controller.BallClicked();
        // An outside click establishes that the pointer is no longer over either surface.
        controller.OutsideClicked();
        controller.PointerEnteredBall();
        scheduler.FireAll();
        Assert.True(controller.IsPanelVisible);
        Assert.False(controller.IsPinned);
    }

    private static (InteractionController Controller, ManualDelayScheduler Scheduler) Create()
    {
        var scheduler = new ManualDelayScheduler();
        return (new InteractionController(scheduler), scheduler);
    }

    [Fact]
    public void HoverDelayOpensAnUnpinnedPreview()
    {
        (InteractionController controller, ManualDelayScheduler scheduler) = Create();

        controller.PointerEnteredBall();

        Assert.False(controller.IsPanelVisible);
        Assert.Equal(TimeSpan.FromMilliseconds(300), scheduler.LastDelay);

        scheduler.FireAll();

        Assert.True(controller.IsPanelVisible);
        Assert.False(controller.IsPinned);
    }

    [Fact]
    public void LeavingTheBallBeforeTheDelayCancelsOpening()
    {
        (InteractionController controller, ManualDelayScheduler scheduler) = Create();

        controller.PointerEnteredBall();
        controller.PointerExitedBall();
        scheduler.FireAll();

        Assert.False(controller.IsPanelVisible);
    }

    [Fact]
    public void PointerCanMoveFromBallToPanelWithoutClosing()
    {
        (InteractionController controller, ManualDelayScheduler scheduler) = Create();
        var shown = 0;
        var hidden = 0;
        controller.PanelShown += () => shown++;
        controller.PanelHidden += () => hidden++;

        controller.PointerEnteredBall();
        scheduler.FireAll();
        Assert.True(controller.IsPanelVisible);

        controller.PointerEnteredPanel();
        controller.PointerExitedBall();
        scheduler.FireAll();

        Assert.True(controller.IsPanelVisible);
        Assert.Equal(0, hidden);
        Assert.Equal(1, shown);

        controller.PointerExitedPanel();
        scheduler.FireAll();

        Assert.False(controller.IsPanelVisible);
        Assert.Equal(1, hidden);
    }

    [Fact]
    public void UnpinnedPreviewClosesAfterTheGracePeriod()
    {
        (InteractionController controller, ManualDelayScheduler scheduler) = Create();

        controller.PointerEnteredBall();
        scheduler.FireAll();
        controller.PointerExitedBall();

        Assert.True(controller.IsPanelVisible);

        scheduler.FireAll();

        Assert.False(controller.IsPanelVisible);
        Assert.Equal(TimeSpan.FromMilliseconds(250), scheduler.LastDelay);
    }

    [Fact]
    public void ClickPinsAndSecondClickCloses()
    {
        (InteractionController controller, ManualDelayScheduler scheduler) = Create();

        controller.BallClicked();
        Assert.True(controller.IsPanelVisible);
        Assert.True(controller.IsPinned);

        // Leaving does not close a pinned panel.
        controller.PointerExitedBall();
        scheduler.FireAll();
        Assert.True(controller.IsPanelVisible);

        controller.BallClicked();
        Assert.False(controller.IsPanelVisible);
        Assert.False(controller.IsPinned);
    }

    [Fact]
    public void ClickWhileAGraceCloseIsPendingKeepsThePreviewOpen()
    {
        (InteractionController controller, ManualDelayScheduler scheduler) = Create();

        controller.PointerEnteredBall();
        scheduler.FireAll();
        controller.PointerExitedBall();
        controller.BallClicked();
        scheduler.FireAll();

        Assert.True(controller.IsPanelVisible);
        Assert.True(controller.IsPinned);
    }

    [Fact]
    public void ExplicitCloseSuppressesReopeningUntilThePointerLeavesTheBall()
    {
        (InteractionController controller, ManualDelayScheduler scheduler) = Create();

        controller.PointerEnteredBall();
        scheduler.FireAll();
        Assert.True(controller.IsPanelVisible);

        controller.CloseExplicitly();
        Assert.False(controller.IsPanelVisible);
        Assert.False(controller.IsPinned);

        // The pointer never left the ball, so hover must not reopen the panel.
        controller.PointerEnteredBall();
        scheduler.FireAll();
        Assert.False(controller.IsPanelVisible);

        controller.PointerExitedBall();
        controller.PointerEnteredBall();
        scheduler.FireAll();

        Assert.True(controller.IsPanelVisible);
    }

    [Fact]
    public void EscapeCloseThenTrayDetailsStillOpensPinned()
    {
        (InteractionController controller, ManualDelayScheduler scheduler) = Create();

        controller.CloseExplicitly();
        controller.ShowPinned();

        Assert.True(controller.IsPanelVisible);
        Assert.True(controller.IsPinned);
        Assert.Equal(0, scheduler.PendingCount);
    }

    [Fact]
    public void DragCancelsThePreviewAndSuppressesTheClick()
    {
        (InteractionController controller, ManualDelayScheduler scheduler) = Create();

        controller.PointerEnteredBall();
        scheduler.FireAll();
        Assert.True(controller.IsPanelVisible);

        controller.DragStarted();
        Assert.False(controller.IsPanelVisible);

        // A click that arrives while dragging must not pin or reopen anything.
        controller.BallClicked();
        Assert.False(controller.IsPanelVisible);
        Assert.False(controller.IsPinned);

        controller.DragCompleted();
    }

    [Fact]
    public void DragCancelsAPendingHoverOpen()
    {
        (InteractionController controller, ManualDelayScheduler scheduler) = Create();

        controller.PointerEnteredBall();
        controller.DragStarted();
        scheduler.FireAll();

        Assert.False(controller.IsPanelVisible);
    }

    [Fact]
    public void HoverEventsAroundDragReleaseCannotReopenThePreview()
    {
        (InteractionController controller, ManualDelayScheduler scheduler) = Create();
        controller.PointerEnteredBall();
        controller.DragStarted();
        // Capture/window motion may deliver another enter, or queue one until release.
        controller.PointerEnteredBall();
        controller.DragCompleted();
        controller.PointerEnteredBall();
        scheduler.FireAll();
        Assert.False(controller.IsPanelVisible);
        Assert.False(controller.IsPinned);

        controller.PointerExitedBall();
        controller.PointerEnteredBall();
        scheduler.FireAll();
        Assert.True(controller.IsPanelVisible);
        Assert.False(controller.IsPinned);
    }

    [Fact]
    public void ShowPinnedIgnoresHoverTimers()
    {
        (InteractionController controller, ManualDelayScheduler scheduler) = Create();

        controller.PointerEnteredBall();
        controller.ShowPinned();
        scheduler.FireAll();

        Assert.True(controller.IsPanelVisible);
        Assert.True(controller.IsPinned);
    }

    [Fact]
    public void DisposeCancelsPendingTimers()
    {
        (InteractionController controller, ManualDelayScheduler scheduler) = Create();

        controller.PointerEnteredBall();
        controller.Dispose();
        scheduler.FireAll();

        Assert.False(controller.IsPanelVisible);
        Assert.Equal(0, scheduler.PendingCount);
    }
}
