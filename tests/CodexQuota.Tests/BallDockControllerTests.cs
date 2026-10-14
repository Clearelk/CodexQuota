using CodexQuota.Core.Models;
using CodexQuota.Core.Services;
using CodexQuota.Tests.Support;

namespace CodexQuota.Tests;

public sealed class BallDockControllerTests
{
    [Fact]
    public void UndockedControllerStaysFloatingThroughPointerEvents()
    {
        var controller = Create(out var scheduler);

        controller.PointerEnteredStrip();
        controller.PointerEnteredRevealed();
        scheduler.FireAll();

        Assert.Equal(BallDockPresentation.Floating, controller.Presentation);
        Assert.Equal(BallDockSide.None, controller.Side);
    }

    [Fact]
    public void DockingCollapsesAndHoverRevealsImmediately()
    {
        var controller = Create(out _);
        int changes = 0;
        controller.PresentationChanged += () => changes++;

        controller.Dock(BallDockSide.Left);
        Assert.Equal(BallDockPresentation.Collapsed, controller.Presentation);

        controller.PointerEnteredStrip();
        Assert.Equal(BallDockPresentation.Revealed, controller.Presentation);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void LeavingBothSurfacesRecollapsesAfterTheGrace()
    {
        var controller = Create(out var scheduler);
        controller.Dock(BallDockSide.Right);
        controller.PointerEnteredStrip();

        controller.PointerExitedStrip();
        Assert.Equal(BallDockPresentation.Revealed, controller.Presentation);

        scheduler.FireAll();
        Assert.Equal(BallDockPresentation.Collapsed, controller.Presentation);
        Assert.Equal(TimeSpan.FromMilliseconds(250), scheduler.LastDelay);
    }

    [Fact]
    public void RevealedHoverAcrossTheOldStripLaneDoesNotFlicker()
    {
        // Pointer moves from the ball surface onto the still-active strip lane: the leave
        // must not schedule a collapse while the lane reports it is still engaged.
        var controller = Create(out var scheduler);
        controller.Dock(BallDockSide.Left);
        controller.PointerEnteredStrip();

        controller.PointerExitedStrip();
        controller.PointerEnteredRevealed();
        scheduler.FireAll();
        Assert.Equal(BallDockPresentation.Revealed, controller.Presentation);

        controller.PointerExitedRevealed();
        controller.PointerEnteredStrip();
        scheduler.FireAll();
        Assert.Equal(BallDockPresentation.Revealed, controller.Presentation);
    }

    [Fact]
    public void ActiveDragHoldsTheRevealAndCancelsThePendingCollapse()
    {
        var controller = Create(out var scheduler);
        controller.Dock(BallDockSide.Left);
        controller.PointerEnteredRevealed();

        controller.PointerExitedRevealed();
        controller.DragStarted();
        scheduler.FireAll();
        Assert.Equal(BallDockPresentation.Revealed, controller.Presentation);

        controller.DragEnded();
        scheduler.FireAll();
        Assert.Equal(BallDockPresentation.Collapsed, controller.Presentation);
    }

    [Fact]
    public void EngagementHoldsKeepTheRevealAndReevaluateOnRelease()
    {
        var controller = Create(out var scheduler);
        controller.Dock(BallDockSide.Left);
        controller.PointerEnteredRevealed();
        controller.PushHold();

        controller.PointerExitedRevealed();
        scheduler.FireAll();
        Assert.Equal(BallDockPresentation.Revealed, controller.Presentation);

        controller.PopHold();
        scheduler.FireAll();
        Assert.Equal(BallDockPresentation.Collapsed, controller.Presentation);
    }

    [Fact]
    public void StaleCollapseCallbackCannotCollapseANewerReveal()
    {
        var controller = Create(out var scheduler);
        controller.Dock(BallDockSide.Left);
        controller.PointerEnteredStrip();
        controller.PointerExitedStrip();

        // The pointer returns before the grace elapses; the queued callback must be inert.
        controller.PointerEnteredStrip();
        scheduler.FireAll();
        Assert.Equal(BallDockPresentation.Revealed, controller.Presentation);
    }

    [Fact]
    public void HiddenBallKeepsCanonicalSideButSuppressesTheReveal()
    {
        var controller = Create(out var scheduler);
        controller.Dock(BallDockSide.Left);
        controller.SetHidden(true);
        Assert.Equal(BallDockPresentation.Collapsed, controller.Presentation);

        controller.PointerEnteredStrip();
        Assert.Equal(BallDockPresentation.Collapsed, controller.Presentation);

        controller.SetHidden(false);
        Assert.Equal(BallDockPresentation.Revealed, controller.Presentation);
    }

    [Fact]
    public void UndockClearsSideAndPresentationAndCancelsPendingWork()
    {
        var controller = Create(out var scheduler);
        controller.Dock(BallDockSide.Right);
        controller.PointerEnteredStrip();
        controller.PointerExitedStrip();
        controller.Undock();
        scheduler.FireAll();

        Assert.Equal(BallDockSide.None, controller.Side);
        Assert.Equal(BallDockPresentation.Floating, controller.Presentation);
        Assert.Equal(0, scheduler.PendingCount);
    }

    [Fact]
    public void DisposeCancelsPendingCollapse()
    {
        var controller = Create(out var scheduler);
        controller.Dock(BallDockSide.Left);
        controller.PointerEnteredStrip();
        controller.PointerExitedStrip();
        controller.Dispose();
        scheduler.FireAll();

        Assert.Equal(0, scheduler.PendingCount);
        Assert.Equal(BallDockPresentation.Revealed, controller.Presentation);
    }

    [Fact]
    public void DockingToInvalidSideIsIgnored()
    {
        var controller = Create(out _);

        controller.Dock(BallDockSide.None);

        Assert.Equal(BallDockSide.None, controller.Side);
        Assert.Equal(BallDockPresentation.Floating, controller.Presentation);
    }

    [Fact]
    public void FinishingDragWaitsForLeaveGraceRatherThanCollapsingUnderRelease()
    {
        var controller = Create(out var scheduler);
        controller.Dock(BallDockSide.Right);
        controller.DragStarted();
        controller.DragEnded();

        Assert.Equal(BallDockPresentation.Revealed, controller.Presentation);
        scheduler.FireAll();
        Assert.Equal(BallDockPresentation.Collapsed, controller.Presentation);
    }

    [Fact]
    public void SurfaceLeaveAfterDisposeCannotCreateAnotherTimer()
    {
        var controller = Create(out var scheduler);
        controller.Dock(BallDockSide.Left);
        controller.PointerEnteredStrip();
        controller.Dispose();
        controller.PointerExitedStrip();
        Assert.Equal(0, scheduler.PendingCount);
    }

    private static (BallDockController Controller, ManualDelayScheduler Scheduler) Create()
    {
        var scheduler = new ManualDelayScheduler();
        return (new BallDockController(scheduler), scheduler);
    }

    private static BallDockController Create(out ManualDelayScheduler scheduler)
    {
        scheduler = new ManualDelayScheduler();
        return new BallDockController(scheduler);
    }
}
