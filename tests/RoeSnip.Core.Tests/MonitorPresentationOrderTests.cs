using RoeSnip.Core.Capture;
using RoeSnip.Core.Overlay;
using Xunit;

namespace RoeSnip.Core.Tests;

/// <summary>Pure monitor-set/ordering helpers backing the Windows flash dimmer (item 18) — ported
/// from the WPF app's FlashDimmer.Matches/OrderCursorMonitorFirst semantics. No window/display
/// needed: these operate purely on MonitorInfo + an explicit cursor position.</summary>
public class MonitorPresentationOrderTests
{
    private static MonitorInfo Monitor(string name, int left, int top, int width, int height, int index = 0) =>
        new(
            Index: index, DeviceName: name, BackendKey: "test",
            BoundsPx: RectPhysical.FromSize(left, top, width, height),
            DpiX: 96, DpiY: 96, Scale: 1.0, AdvancedColorActive: false,
            SdrWhiteNits: 80.0, MaxLuminanceNits: 80.0, IsPrimary: index == 0);

    // ---------- SetsMatch ----------

    [Fact]
    public void SetsMatch_IdenticalSets_SameOrder_ReturnsTrue()
    {
        var a = new[] { Monitor("A", 0, 0, 1920, 1080), Monitor("B", 1920, 0, 1920, 1080) };
        var b = new[] { Monitor("A", 0, 0, 1920, 1080), Monitor("B", 1920, 0, 1920, 1080) };
        Assert.True(MonitorPresentationOrder.SetsMatch(a, b));
    }

    [Fact]
    public void SetsMatch_IdenticalSets_DifferentOrder_ReturnsTrue()
    {
        // Order-independence is the whole point (see the class doc comment) — a caller's own
        // enumeration order must never force a rebuild.
        var have = new[] { Monitor("B", 1920, 0, 1920, 1080), Monitor("A", 0, 0, 1920, 1080) };
        var want = new[] { Monitor("A", 0, 0, 1920, 1080), Monitor("B", 1920, 0, 1920, 1080) };
        Assert.True(MonitorPresentationOrder.SetsMatch(have, want));
    }

    [Fact]
    public void SetsMatch_DifferentCount_ReturnsFalse()
    {
        var have = new[] { Monitor("A", 0, 0, 1920, 1080) };
        var want = new[] { Monitor("A", 0, 0, 1920, 1080), Monitor("B", 1920, 0, 1920, 1080) };
        Assert.False(MonitorPresentationOrder.SetsMatch(have, want));
    }

    [Fact]
    public void SetsMatch_SameCountDifferentDeviceName_ReturnsFalse()
    {
        var have = new[] { Monitor("A", 0, 0, 1920, 1080) };
        var want = new[] { Monitor("C", 0, 0, 1920, 1080) };
        Assert.False(MonitorPresentationOrder.SetsMatch(have, want));
    }

    [Fact]
    public void SetsMatch_SameNameDifferentBounds_ReturnsFalse()
    {
        // A resolution/DPI change (same device, moved/resized bounds) must count as a real change —
        // the flash windows are sized to the OLD bounds and would dim the wrong area otherwise.
        var have = new[] { Monitor("A", 0, 0, 1920, 1080) };
        var want = new[] { Monitor("A", 0, 0, 2560, 1440) };
        Assert.False(MonitorPresentationOrder.SetsMatch(have, want));
    }

    [Fact]
    public void SetsMatch_DeviceNameIsCaseInsensitive()
    {
        var have = new[] { Monitor(@"\\.\DISPLAY1", 0, 0, 1920, 1080) };
        var want = new[] { Monitor(@"\\.\display1", 0, 0, 1920, 1080) };
        Assert.True(MonitorPresentationOrder.SetsMatch(have, want));
    }

    [Fact]
    public void SetsMatch_BothEmpty_ReturnsTrue()
    {
        Assert.True(MonitorPresentationOrder.SetsMatch(System.Array.Empty<MonitorInfo>(), System.Array.Empty<MonitorInfo>()));
    }

    // ---------- OrderCursorMonitorFirst ----------

    [Fact]
    public void OrderCursorMonitorFirst_SingleMonitor_ReturnsUnchanged()
    {
        var monitors = new[] { Monitor("A", 0, 0, 1920, 1080) };
        var ordered = MonitorPresentationOrder.OrderCursorMonitorFirst(monitors, cursorX: 500, cursorY: 500);
        Assert.Same(monitors, ordered);
    }

    [Fact]
    public void OrderCursorMonitorFirst_CursorOnSecondMonitor_MovesItFirst()
    {
        var a = Monitor("A", 0, 0, 1920, 1080);
        var b = Monitor("B", 1920, 0, 1920, 1080);
        var ordered = MonitorPresentationOrder.OrderCursorMonitorFirst(new[] { a, b }, cursorX: 2500, cursorY: 300);
        Assert.Equal(new[] { b, a }, ordered);
    }

    [Fact]
    public void OrderCursorMonitorFirst_CursorAlreadyOnFirstMonitor_ReturnsUnchanged()
    {
        var a = Monitor("A", 0, 0, 1920, 1080);
        var b = Monitor("B", 1920, 0, 1920, 1080);
        var monitors = new[] { a, b };
        var ordered = MonitorPresentationOrder.OrderCursorMonitorFirst(monitors, cursorX: 500, cursorY: 300);
        Assert.Same(monitors, ordered);
    }

    [Fact]
    public void OrderCursorMonitorFirst_CursorOverNoMonitor_ReturnsUnchanged()
    {
        // A gap between non-adjacent monitors (e.g. a portrait secondary) — no monitor's bounds
        // contain the cursor.
        var a = Monitor("A", 0, 0, 1920, 1080);
        var b = Monitor("B", 3000, 0, 1920, 1080);
        var monitors = new[] { a, b };
        var ordered = MonitorPresentationOrder.OrderCursorMonitorFirst(monitors, cursorX: 2500, cursorY: 300);
        Assert.Same(monitors, ordered);
    }

    [Fact]
    public void OrderCursorMonitorFirst_ThreeMonitors_CursorOnThird_MovesItFirstKeepsRestOrder()
    {
        var a = Monitor("A", 0, 0, 1920, 1080);
        var b = Monitor("B", 1920, 0, 1920, 1080);
        var c = Monitor("C", 3840, 0, 1920, 1080);
        var ordered = MonitorPresentationOrder.OrderCursorMonitorFirst(new[] { a, b, c }, cursorX: 4000, cursorY: 300);
        Assert.Equal(new[] { c, a, b }, ordered);
    }

    [Fact]
    public void OrderCursorMonitorFirst_CursorOnBoundaryIsExclusiveOnRightBottom()
    {
        // BoundsPx.Right/Bottom are exclusive (matches RectPhysical's Width/Height = Right - Left
        // convention) — a cursor exactly at monitor A's right edge belongs to B, not A.
        var a = Monitor("A", 0, 0, 1920, 1080);
        var b = Monitor("B", 1920, 0, 1920, 1080);
        var ordered = MonitorPresentationOrder.OrderCursorMonitorFirst(new[] { a, b }, cursorX: 1920, cursorY: 0);
        Assert.Equal(new[] { b, a }, ordered);
    }
}
