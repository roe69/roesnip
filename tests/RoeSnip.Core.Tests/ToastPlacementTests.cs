using RoeSnip.Core.Capture;
using RoeSnip.Core.Notifications;
using Xunit;

namespace RoeSnip.Core.Tests;

public class ToastPlacementTests
{
    // A 3-monitor layout: portrait at 0,0, one at 1440,0, primary at 1440,1440 (top bar 32 px).
    private static readonly RectPhysical Portrait = RectPhysical.FromSize(0, 0, 1440, 2560);
    private static readonly RectPhysical Upper = RectPhysical.FromSize(1440, 0, 2560, 1440);
    private static readonly RectPhysical Primary = RectPhysical.FromSize(1440, 1440, 2560, 1440);
    private static readonly RectPhysical PrimaryWork = RectPhysical.FromSize(1440, 1472, 2560, 1408);

    [Fact]
    public void NextToCursor_RoomAvailable_GoesDownRight()
    {
        var (x, y) = ToastPlacement.NextToCursor(2000, 2000, 360, 84, PrimaryWork, 16);
        Assert.Equal((2016, 2016), (x, y));
    }

    [Fact]
    public void NextToCursor_NearRightEdge_FlipsLeft()
    {
        var (x, y) = ToastPlacement.NextToCursor(3900, 2000, 360, 84, PrimaryWork, 16);
        Assert.Equal(3900 - 16 - 360, x);
        Assert.Equal(2016, y);
    }

    [Fact]
    public void NextToCursor_NearBottomEdge_FlipsUp()
    {
        var (x, y) = ToastPlacement.NextToCursor(2000, 2850, 360, 84, PrimaryWork, 16);
        Assert.Equal(2016, x);
        Assert.Equal(2850 - 16 - 84, y);
    }

    [Fact]
    public void NextToCursor_BottomRightCorner_FlipsBoth()
    {
        var (x, y) = ToastPlacement.NextToCursor(3999, 2879, 360, 84, PrimaryWork, 16);
        Assert.Equal((3999 - 16 - 360, 2879 - 16 - 84), (x, y));
    }

    [Fact]
    public void NextToCursor_FlipWouldLeaveWorkArea_Clamps()
    {
        // Cursor over the top bar (inside the monitor, above the work area): clamp down into it.
        var (x, y) = ToastPlacement.NextToCursor(1450, 1445, 360, 84, PrimaryWork, 16);
        Assert.Equal(1466, x);
        Assert.Equal(PrimaryWork.Top, y);
    }

    [Fact]
    public void NextToCursor_StaysFullyInsideWorkArea()
    {
        for (int cx = PrimaryWork.Left; cx < PrimaryWork.Right; cx += 97)
        {
            for (int cy = Primary.Top; cy < Primary.Bottom; cy += 53)
            {
                var (x, y) = ToastPlacement.NextToCursor(cx, cy, 720, 168, PrimaryWork, 32);
                Assert.InRange(x, PrimaryWork.Left, PrimaryWork.Right - 720);
                Assert.InRange(y, PrimaryWork.Top, PrimaryWork.Bottom - 168);
            }
        }
    }

    [Fact]
    public void NextToCursor_ToastLargerThanWorkArea_PinsTopLeft()
    {
        var small = RectPhysical.FromSize(100, 100, 200, 50);
        Assert.Equal((100, 100), ToastPlacement.NextToCursor(150, 120, 360, 84, small, 16));
    }

    [Fact]
    public void BottomCentre_CentresAboveBottomMargin()
    {
        var (x, y) = ToastPlacement.BottomCentre(360, 84, PrimaryWork, 16);
        Assert.Equal(1440 + (2560 - 360) / 2, x);
        Assert.Equal(2880 - 16 - 84, y);
    }

    [Fact]
    public void MonitorIndexAt_FindsContainingMonitor()
    {
        var monitors = new[] { Portrait, Upper, Primary };
        Assert.Equal(0, ToastPlacement.MonitorIndexAt(monitors, 10, 2500));
        Assert.Equal(1, ToastPlacement.MonitorIndexAt(monitors, 1440, 0));
        Assert.Equal(2, ToastPlacement.MonitorIndexAt(monitors, 3999, 2879));
        Assert.Equal(-1, ToastPlacement.MonitorIndexAt(monitors, 4000, 100));
        Assert.Equal(-1, ToastPlacement.MonitorIndexAt(monitors, 100, 2560));
    }
}
