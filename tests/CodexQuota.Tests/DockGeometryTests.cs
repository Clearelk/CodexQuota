using CodexQuota.Core.Models;
using CodexQuota.Core.Services;

namespace CodexQuota.Tests;

public sealed class DockGeometryTests
{
    private static readonly PixelRect Ball72 = new(0, 0, 72, 72);

    private static readonly DockMonitor Primary = new(
        "primary", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), IsPrimary: true);

    private static readonly DockMonitor StaggeredLeft = new(
        "left", new PixelRect(-1920, -200, 1920, 1080), new PixelRect(-1920, -200, 1920, 1040), IsPrimary: false);

    private static readonly DockMonitor StaggeredRight = new(
        "right", new PixelRect(1920, 200, 1920, 880), new PixelRect(1920, 200, 1920, 840), IsPrimary: false);

    [Fact]
    public void QuarterBeyondTheLeftWorkAreaEdgeDocksLeft()
    {
        // A quarter of the 72-DIP ball outside the work area: left edge at -18 with the
        // work area starting at 0. The 2026-10 contract relaxed this from half (=-36).
        var ball = new PixelRect(-18, 100, 72, 72);

        Assert.Equal(BallDockSide.Left, DockGeometry.EvaluateRelease(ball, [Primary]));
    }

    [Fact]
    public void ExactlyQuarterBeyondTheEdgeDocks()
    {
        // The boundary itself docks (exactly 18 px of the 72-DIP ball), and the native
        // one-physical-pixel rounding tolerance keeps the boundary inclusive.
        Assert.Equal(BallDockSide.Left, DockGeometry.EvaluateRelease(new PixelRect(-18, 100, 72, 72), [Primary]));
        Assert.Equal(BallDockSide.Left, DockGeometry.EvaluateRelease(new PixelRect(-18 - DockGeometry.PixelTolerance, 100, 72, 72), [Primary]));
    }

    [Fact]
    public void BetweenQuarterAndHalfNowDocks()
    {
        // 28 px offscreen is 3/8 of the diameter: below the old half threshold (36) yet
        // at or above the relaxed quarter threshold (18), proving the change is real.
        var ball = new PixelRect(-28, 100, 72, 72);

        Assert.Equal(BallDockSide.Left, DockGeometry.EvaluateRelease(ball, [Primary]));
    }

    [Fact]
    public void LessThanQuarterBeyondTheEdgeStaysAnOrdinaryBall()
    {
        var ball = new PixelRect(-14, 100, 72, 72);

        Assert.Equal(BallDockSide.None, DockGeometry.EvaluateRelease(ball, [Primary]));
    }

    [Fact]
    public void NearTheEdgeButFullyInsideNeverDocks()
    {
        var ball = new PixelRect(2, 100, 72, 72);

        Assert.Equal(BallDockSide.None, DockGeometry.EvaluateRelease(ball, [Primary]));
    }

    [Fact]
    public void QuarterBeyondTheRightWorkAreaEdgeDocksRight()
    {
        // Right mirror of the left quarter case: exactly 18 px beyond the right edge
        // (the ball's left edge at Right-54 puts its right edge at Right+18).
        var ball = new PixelRect(Primary.WorkArea.Right - 54, 100, 72, 72);

        Assert.Equal(BallDockSide.Right, DockGeometry.EvaluateRelease(ball, [Primary]));
    }

    [Fact]
    public void ExactlyQuarterBeyondTheRightEdgeDocks()
    {
        // Right mirror of the exact quarter boundary and its one-pixel tolerance.
        Assert.Equal(BallDockSide.Right,
            DockGeometry.EvaluateRelease(new PixelRect(Primary.WorkArea.Right - 54, 100, 72, 72), [Primary]));
        Assert.Equal(BallDockSide.Right,
            DockGeometry.EvaluateRelease(new PixelRect(Primary.WorkArea.Right - 54 - DockGeometry.PixelTolerance, 100, 72, 72), [Primary]));
    }

    [Fact]
    public void LessThanQuarterBeyondTheRightEdgeStaysAnOrdinaryBall()
    {
        var ball = new PixelRect(Primary.WorkArea.Right - 56, 100, 72, 72);

        Assert.Equal(BallDockSide.None, DockGeometry.EvaluateRelease(ball, [Primary]));
    }

    [Fact]
    public void TopAndBottomBoundariesNeverDock()
    {
        var aboveTop = new PixelRect(500, Primary.WorkArea.Top - 40, 72, 72);
        var belowBottom = new PixelRect(500, Primary.WorkArea.Bottom - 32, 72, 72);

        Assert.Equal(BallDockSide.None, DockGeometry.EvaluateRelease(aboveTop, [Primary]));
        Assert.Equal(BallDockSide.None, DockGeometry.EvaluateRelease(belowBottom, [Primary]));
    }

    [Fact]
    public void SharedMonitorPassageIsNotEligible()
    {
        // Primary's right edge adjoins StaggeredRight's left edge across the ball's span.
        var ball = new PixelRect(Primary.WorkArea.Right - 36, 300, 72, 72);

        Assert.Equal(BallDockSide.None, DockGeometry.EvaluateRelease(ball, [Primary, StaggeredRight]));
    }

    [Fact]
    public void SharedEdgeOutsideTheBallSpanStaysEligible()
    {
        // Same adjoining monitors, but the ball sits above the neighbor's top bound.
        var ball = new PixelRect(Primary.WorkArea.Right - 36, 40, 72, 72);

        Assert.Equal(BallDockSide.Right, DockGeometry.EvaluateRelease(ball, [Primary, StaggeredRight]));
    }

    [Fact]
    public void StaggeredMonitorsExposeOnlyNonOverlappingSegments()
    {
        // The left monitor is vertically staggered: the passage exists only across the
        // overlapping span; below it the primary's left edge is genuinely exposed.
        var overlapping = new PixelRect(-36, 300, 72, 72);
        var outsideOverlap = new PixelRect(-36, 950, 72, 72);

        Assert.Equal(BallDockSide.None, DockGeometry.EvaluateRelease(overlapping, [StaggeredLeft, Primary]));
        Assert.Equal(BallDockSide.Left, DockGeometry.EvaluateRelease(outsideOverlap, [StaggeredLeft, Primary]));
    }

    [Fact]
    public void NegativeOriginsAndFractionalScalesKeepTheThreshold()
    {
        var monitor = new DockMonitor("left", new PixelRect(-2560, -400, 2560, 1440), new PixelRect(-2560, -400, 2560, 1396), false);
        var ball = new PixelRect(monitor.WorkArea.Left - 36, -100, 72, 72);

        Assert.Equal(BallDockSide.Left, DockGeometry.EvaluateRelease(ball, [monitor]));
    }

    [Fact]
    public void ExtremeOffscreenReleaseUsesTheNearestWorkAreaNotAnUnrelatedMonitor()
    {
        var farRight = new DockMonitor("far", new PixelRect(10000, 0, 1920, 1080), new PixelRect(10000, 0, 1920, 1040), false);
        var ball = new PixelRect(-20000, 500, 72, 72);

        Assert.Equal(BallDockSide.Left, DockGeometry.EvaluateRelease(ball, [Primary, farRight]));
    }

    [Fact]
    public void EmptyMonitorListNeverDocks()
    {
        Assert.Equal(BallDockSide.None, DockGeometry.EvaluateRelease(Ball72, []));
    }

    [Fact]
    public void FullyOffscreenReleaseSelectsNearestMonitorRegardlessOfEnumerationOrder()
    {
        var farRight = new DockMonitor("far", new PixelRect(10000, 0, 1920, 1080), new PixelRect(10000, 0, 1920, 1040), false);
        var ball = new PixelRect(2000, 500, 72, 72);

        Assert.Equal(BallDockSide.Right, DockGeometry.EvaluateRelease(ball, [farRight, Primary]));
        Assert.Equal(BallDockSide.Right, DockGeometry.EvaluateRelease(ball, [Primary, farRight]));
    }

    [Fact]
    public void ExtremeTopReleaseKeepsItsHorizontalMonitorRatherThanCloserDisplayCenter()
    {
        var shorterLeft = new DockMonitor("left", new PixelRect(-1552, 0, 1552, 1080), new PixelRect(-1552, 0, 1552, 1020), false);
        Assert.Equal(BallDockSide.None, DockGeometry.EvaluateRelease(new PixelRect(114, -10000, 108, 108), [new DockMonitor("wide", new PixelRect(0, 0, 2560, 1600), new PixelRect(0, 0, 2560, 1528), true), shorterLeft]));
    }

    [Fact]
    public void SavedDockRestoresOnlyOnAStillPresentExposedMonitor()
    {
        Assert.True(DockGeometry.CanRestoreDock(new PixelRect(0, 100, 72, 72), BallDockSide.Left, [Primary]));
        Assert.False(DockGeometry.CanRestoreDock(new PixelRect(-1900, 100, 72, 72), BallDockSide.Left, [Primary]));
        Assert.False(DockGeometry.CanRestoreDock(new PixelRect(0, 300, 72, 72), BallDockSide.Left, [Primary, StaggeredLeft]));
        Assert.True(DockGeometry.CanRestoreDock(new PixelRect(0, 950, 72, 72), BallDockSide.Left, [Primary, StaggeredLeft]));
    }

    private static readonly MonitorWorkArea PrimaryArea = new("primary", 0, 0, 1920, 1040, IsPrimary: true);

    [Fact]
    public void DockAnchorStaysReachableForStripAndRevealedBall()
    {
        double diameter = 72;
        double stripHeight = 48;

        bool fits = DockGeometry.ClampDockAnchor(1000, diameter, stripHeight, PrimaryArea, out double top);

        Assert.True(fits);
        Assert.InRange(top, PrimaryArea.Top, PrimaryArea.Bottom - diameter);
        Assert.InRange(top + (diameter / 2) - (stripHeight / 2), PrimaryArea.Top, PrimaryArea.Bottom - stripHeight);
    }

    [Fact]
    public void OversizedBallFallsBackToTheTopOfTheWorkArea()
    {
        bool fits = DockGeometry.ClampDockAnchor(900, 2000, 48, PrimaryArea, out double top);

        Assert.False(fits);
        Assert.Equal(PrimaryArea.Top, top);
    }
}
