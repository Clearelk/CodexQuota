using CodexQuota.Core.Services;

namespace CodexQuota.Tests;

public sealed class PlacementTests
{
    private static readonly MonitorWorkArea Primary = new("primary", 0, 0, 1920, 1040, IsPrimary: true);
    private static readonly MonitorWorkArea LeftMonitor = new("left", -1920, 0, 1920, 1040, IsPrimary: false);
    private static readonly MonitorWorkArea RightMonitor = new("right", 1920, 0, 2560, 1400, IsPrimary: false);

    [Fact]
    public void InitialPositionIsTheLowerRightCornerWithMargin()
    {
        (double left, double top) = MonitorPlacement.GetInitialPosition(Primary, 56, 56);

        Assert.Equal(1920 - 56 - 16, left);
        Assert.Equal(1040 - 56 - 16, top);
    }

    [Fact]
    public void ClampPullsAnOffscreenPositionBackIntoTheWorkArea()
    {
        (double left, double top) = MonitorPlacement.Clamp(5000, 5000, 56, 56, [Primary]);

        Assert.Equal(1920 - 56, left);
        Assert.Equal(1040 - 56, top);
    }

    [Fact]
    public void NegativeCoordinatesAreClampedIntoTheLeftMonitor()
    {
        (double left, double top) = MonitorPlacement.Clamp(-1000, 200, 360, 400, [Primary, LeftMonitor]);

        Assert.Equal(-1000, left);
        Assert.Equal(200, top);
    }

    [Fact]
    public void RemovedMonitorFallsBackToTheNearestRemainingWorkArea()
    {
        // Saved on a monitor that is no longer attached.
        (double left, double top) = MonitorPlacement.Clamp(2600, 300, 56, 56, [Primary, LeftMonitor]);

        // Nearest by center distance is the primary monitor.
        Assert.InRange(left, Primary.Left, Primary.Right - 56);
        Assert.InRange(top, Primary.Top, Primary.Bottom - 56);
    }

    [Fact]
    public void OversizedPanelIsAlignedToTheWorkAreaEdge()
    {
        (double left, double top) = MonitorPlacement.Clamp(400, 400, 4000, 3000, [Primary]);

        Assert.Equal(Primary.Left, left);
        Assert.Equal(Primary.Top, top);
    }

    [Fact]
    public void MultipleMonitorsKeepTheBallOnTheMonitorItIntersectsMost()
    {
        (double left, double top) = MonitorPlacement.Clamp(2000, 100, 56, 56, [Primary, RightMonitor]);

        Assert.Equal(2000, left);
        Assert.Equal(100, top);
    }

    [Fact]
    public void NoWorkAreasLeavesThePositionUnchanged()
    {
        (double left, double top) = MonitorPlacement.Clamp(123, 456, 56, 56, []);

        Assert.Equal(123, left);
        Assert.Equal(456, top);
    }

    [Fact]
    public void PanelIsPlacedToTheLeftOfTheBallWhenThereIsRoom()
    {
        (double left, double top) = MonitorPlacement.PlacePanelBeside(1800, 900, 56, 360, 400, Primary);

        Assert.Equal(1800 - 360 - 8, left);
        Assert.True(top <= Primary.Bottom - 400);
    }

    [Fact]
    public void PanelFallsBackToTheRightWhenTheLeftSideHasNoRoom()
    {
        (double left, double top) = MonitorPlacement.PlacePanelBeside(20, 400, 56, 360, 400, Primary);

        Assert.Equal(20 + 56 + 8, left);
        Assert.Equal(400, top);
    }

    [Fact]
    public void PanelIsClampedVerticallyIntoTheWorkArea()
    {
        (double left, double top) = MonitorPlacement.PlacePanelBeside(1800, 1200, 56, 360, 400, Primary);

        Assert.Equal(1040 - 400, top);
        Assert.Equal(1800 - 360 - 8, left);
    }

    [Theory]
    [InlineData(300, 400, 300, 400)]
    [InlineData(-1000, 200, -1000, 200)]
    [InlineData(1900, 1000, 1848, 968)]
    public void LargerBallPreservesFreePointsAndClampsItsFullSize(double x, double y, double expectedX, double expectedY)
    {
        (double left, double top) = MonitorPlacement.Clamp(x, y, 72, 72, [Primary, LeftMonitor]);
        Assert.Equal(expectedX, left);
        Assert.Equal(expectedY, top);
    }
}
