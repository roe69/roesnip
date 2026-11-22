using RoeSnip.Core.Capture;
using RoeSnip.Core.Notifications;
using Xunit;

namespace RoeSnip.Core.Tests;

public class ToastFollowerTests
{
    private static readonly RectPhysical Work = RectPhysical.FromSize(0, 0, 2560, 1440);
    private const int W = 360, H = 84;

    private static (ToastFollower F, RectPhysical Rect) PlacedAt(int cx, int cy)
    {
        var (x, y) = ToastPlacement.NextToCursor(cx, cy, W, H, Work, 16);
        var rect = RectPhysical.FromSize(x, y, W, H);
        return (new ToastFollower((cx, cy), rect), rect);
    }

    [Fact]
    public void PointerUnchanged_DoesNotMove()
    {
        var (f, rect) = PlacedAt(1000, 500);
        Assert.Null(f.Step(1000, 500, rect, Work, W, H, 1.0));
    }

    [Fact]
    public void PointerMovesAway_FollowsWithSameOffset()
    {
        var (f, rect) = PlacedAt(1000, 500);
        var next = f.Step(900, 400, rect, Work, W, H, 1.0);
        Assert.Equal((916, 416), next);
        Assert.False(f.Frozen);
    }

    [Fact]
    public void PointerHeadsForToast_HoldsThenFreezes()
    {
        var (f, rect) = PlacedAt(1000, 500); // toast at 1016,516
        Assert.Null(f.Step(1002, 502, rect, Work, W, H, 1.0)); // 2 px closer: hold
        Assert.False(f.Frozen);
        Assert.Null(f.Step(1005, 505, rect, Work, W, H, 1.0)); // 6 px closer: freeze
        Assert.True(f.Frozen);
        Assert.Null(f.Step(600, 300, rect, Work, W, H, 1.0)); // frozen for good
    }

    [Fact]
    public void SlowApproach_NeverRunsAway()
    {
        var (f, rect) = PlacedAt(1000, 500);
        for (int i = 1; i <= 16 && !f.Frozen; i++)
        {
            Assert.Null(f.Step(1000 + i, 500 + i, rect, Work, W, H, 1.0));
        }
        Assert.True(f.Frozen);
    }

    [Fact]
    public void PointerInsideToast_Freezes()
    {
        var (f, rect) = PlacedAt(1000, 500);
        Assert.Null(f.Step(1100, 550, rect, Work, W, H, 1.0));
        Assert.True(f.Frozen);
    }

    [Fact]
    public void ApproachFurtherThanFreezeDistance_HoldsWithoutFreezing()
    {
        // Pinned to the bottom-right corner, the toast sits far from a pointer in the middle.
        var rect = RectPhysical.FromSize(2200, 1356, W, H);
        var f = new ToastFollower((1000, 500), rect);
        Assert.Null(f.Step(1100, 600, rect, Work, W, H, 1.0));
        Assert.False(f.Frozen);
    }

    [Fact]
    public void NearRightEdge_FollowFlipsLeft()
    {
        var (f, rect) = PlacedAt(1000, 500);
        var next = f.Step(2500, 500, rect, Work, W, H, 1.0);
        Assert.Equal((2500 - 16 - W, 516), next);
    }

    [Fact]
    public void MonitorFallbackPlacement_FirstPointerPlacesIt()
    {
        var rect = RectPhysical.FromSize(1100, 1340, W, H);
        var f = new ToastFollower(null, rect);
        Assert.Equal((516, 316), f.Step(500, 300, rect, Work, W, H, 1.0));
    }

    [Fact]
    public void Freeze_StopsFollowing()
    {
        var (f, rect) = PlacedAt(1000, 500);
        f.Freeze();
        Assert.Null(f.Step(500, 200, rect, Work, W, H, 1.0));
    }

    [Fact]
    public void Distance_ZeroInsideAndEuclideanOutside()
    {
        var r = RectPhysical.FromSize(10, 10, 10, 10);
        Assert.Equal(0, ToastFollower.Distance(15, 15, r));
        Assert.Equal(5, ToastFollower.Distance(5, 15, r));
        Assert.Equal(5, ToastFollower.Distance(24, 15, r)); // Right is exclusive: last column is 19
        Assert.Equal(Math.Sqrt(50), ToastFollower.Distance(5, 5, r), 6);
    }
}
