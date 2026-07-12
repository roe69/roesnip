using System.Collections.Generic;
using System.Linq;
using RoeSnip.Capture;
using RoeSnip.Recording;
using Xunit;

namespace RoeSnip.Tests;

/// <summary>Pure geometry tests for the multi-monitor recording phase-1 drag-handoff decision logic
/// (MultiMonitorRecording — see that class's own doc comment). No live WGC/device/window needed —
/// these are exactly the "coordinate conversion, handoff decision logic" pure helpers the workstream
/// extracted specifically to be unit-testable, mirroring RecordingSizeEstimatorTests' own
/// no-live-encoder convention.</summary>
public class MultiMonitorRecordingTests
{
    // Three-monitor layout used throughout, modeled on a real dev rig noted in TESTING.md:
    // DISPLAY3 primary at (0,0)-(2560,1440), DISPLAY1 directly above it at (0,-1440)-(2560,0), and
    // DISPLAY2 to the right of DISPLAY3 at (2560,0)-(4480,1440).
    private static MonitorInfo Display3 => new(
        Index: 0, DeviceName: "\\\\.\\DISPLAY3", HMonitor: 1,
        BoundsPx: new RectPhysical(0, 0, 2560, 1440),
        DpiX: 96, DpiY: 96, AdvancedColorActive: false,
        SdrWhiteNits: 240.0, MaxLuminanceNits: 1000.0, IsPrimary: true);

    private static MonitorInfo Display1 => new(
        Index: 1, DeviceName: "\\\\.\\DISPLAY1", HMonitor: 2,
        BoundsPx: new RectPhysical(0, -1440, 2560, 0),
        DpiX: 96, DpiY: 96, AdvancedColorActive: true,
        SdrWhiteNits: 203.0, MaxLuminanceNits: 800.0, IsPrimary: false);

    private static MonitorInfo Display2 => new(
        Index: 2, DeviceName: "\\\\.\\DISPLAY2", HMonitor: 3,
        BoundsPx: new RectPhysical(2560, 0, 4480, 1440),
        DpiX: 96, DpiY: 96, AdvancedColorActive: false,
        SdrWhiteNits: 240.0, MaxLuminanceNits: 1000.0, IsPrimary: false);

    private static IReadOnlyList<MonitorInfo> ThreeMonitors => new[] { Display3, Display1, Display2 };

    // ---------- UnionBounds ----------

    [Fact]
    public void UnionBounds_ThreeMonitors_IsTheBoundingBoxOfAll()
    {
        var union = MultiMonitorRecording.UnionBounds(ThreeMonitors);
        Assert.Equal(new RectPhysical(0, -1440, 4480, 1440), union);
    }

    [Fact]
    public void UnionBounds_EmptyList_IsDegenerate()
    {
        var union = MultiMonitorRecording.UnionBounds(System.Array.Empty<MonitorInfo>());
        Assert.Equal(default, union);
    }

    [Fact]
    public void UnionBounds_SingleMonitor_IsItsOwnBounds()
    {
        var union = MultiMonitorRecording.UnionBounds(new[] { Display3 });
        Assert.Equal(Display3.BoundsPx, union);
    }

    // ---------- Contains ----------

    [Theory]
    [InlineData(100, 100, 500, 500, true)]   // fully inside
    [InlineData(0, 0, 2560, 1440, true)]     // touching every edge exactly - still contained
    [InlineData(-10, 100, 500, 500, false)]  // pokes past the left edge
    [InlineData(2000, 100, 2600, 500, false)] // pokes past the right edge
    public void Contains_VariousRects_AgainstDisplay3(int l, int t, int r, int b, bool expected)
    {
        var inner = new RectPhysical(l, t, r, b);
        Assert.Equal(expected, MultiMonitorRecording.Contains(Display3.BoundsPx, inner));
    }

    // ---------- FindOwningMonitor ----------

    [Fact]
    public void FindOwningMonitor_FullyOnOneMonitor_ReturnsThatMonitor()
    {
        var region = RectPhysical.FromSize(100, 100, 800, 600); // entirely on DISPLAY3
        var owner = MultiMonitorRecording.FindOwningMonitor(region, ThreeMonitors);
        Assert.Equal(Display3.DeviceName, owner!.DeviceName);
    }

    [Fact]
    public void FindOwningMonitor_MostlyStraddledOntoDisplay1_PicksDisplay1()
    {
        // 800x500 region whose top edge is well above the DISPLAY3/DISPLAY1 seam (y=0): only 50px
        // of its 500px height still overlaps DISPLAY3 — DISPLAY1 has the clear majority.
        var region = RectPhysical.FromSize(600, -450, 800, 500);
        var owner = MultiMonitorRecording.FindOwningMonitor(region, ThreeMonitors);
        Assert.Equal(Display1.DeviceName, owner!.DeviceName);
    }

    [Fact]
    public void FindOwningMonitor_JustBarelyStraddled_StillPicksTheMajorityMonitor()
    {
        // Same region, but shifted so only a thin 10px sliver has crossed onto DISPLAY1 - DISPLAY3
        // still holds the overwhelming majority of the area.
        var region = RectPhysical.FromSize(600, -10, 800, 500);
        var owner = MultiMonitorRecording.FindOwningMonitor(region, ThreeMonitors);
        Assert.Equal(Display3.DeviceName, owner!.DeviceName);
    }

    [Fact]
    public void FindOwningMonitor_NoIntersectionWithAnyMonitor_ReturnsNull()
    {
        // Far off in a dead gap past every monitor's bounds.
        var region = RectPhysical.FromSize(10_000, 10_000, 400, 300);
        var owner = MultiMonitorRecording.FindOwningMonitor(region, ThreeMonitors);
        Assert.Null(owner);
    }

    [Fact]
    public void FindOwningMonitor_ExactTieBreaksTowardLowerIndex()
    {
        // A region split exactly in half between DISPLAY3 (Index 0) and DISPLAY2 (Index 2) along
        // their shared vertical seam at x=2560.
        var region = RectPhysical.FromSize(2460, 100, 200, 400); // 100px each side of x=2560
        var owner = MultiMonitorRecording.FindOwningMonitor(region, ThreeMonitors);
        Assert.Equal(Display3.DeviceName, owner!.DeviceName); // Index 0 < Index 2
    }

    [Fact]
    public void FindOwningMonitor_TeleportAcrossTwoMonitors_LandsOnTheFinalOne()
    {
        // A single automation `select` jump (not an incremental drag) straight from DISPLAY3
        // territory to squarely on DISPLAY1, skipping DISPLAY2 entirely — mirrors the E2E test's own
        // one-shot cross-monitor `select` call.
        var region = RectPhysical.FromSize(200, -1200, 800, 600); // squarely on DISPLAY1
        var owner = MultiMonitorRecording.FindOwningMonitor(region, ThreeMonitors);
        Assert.Equal(Display1.DeviceName, owner!.DeviceName);
    }

    // ---------- ClampToMonitor ----------

    [Fact]
    public void ClampToMonitor_AlreadyFitsInside_IsUnchanged()
    {
        var region = RectPhysical.FromSize(100, 100, 800, 600);
        var clamped = MultiMonitorRecording.ClampToMonitor(region, Display3);
        Assert.Equal(region, clamped);
    }

    [Fact]
    public void ClampToMonitor_PokesPastTheRightEdge_SlidesLeftWithoutResizing()
    {
        var region = RectPhysical.FromSize(2400, 100, 800, 600); // right edge at 3200, past 2560
        var clamped = MultiMonitorRecording.ClampToMonitor(region, Display3);
        Assert.Equal(800, clamped.Width);
        Assert.Equal(600, clamped.Height);
        Assert.True(MultiMonitorRecording.Contains(Display3.BoundsPx, clamped));
        Assert.Equal(Display3.BoundsPx.Right - 800, clamped.Left);
        Assert.Equal(100, clamped.Top); // Y untouched — only the offending axis moves
    }

    [Fact]
    public void ClampToMonitor_PokesPastTheTopEdgeAfterAHandoff_SlidesDownWithoutResizing()
    {
        // Simulates landing on DISPLAY1 (negative Y bounds) with a region whose top would sit above
        // DISPLAY1's own top edge.
        var region = RectPhysical.FromSize(600, -1500, 800, 500); // top at -1500, past DISPLAY1's -1440
        var clamped = MultiMonitorRecording.ClampToMonitor(region, Display1);
        Assert.Equal(800, clamped.Width);
        Assert.Equal(500, clamped.Height);
        Assert.True(MultiMonitorRecording.Contains(Display1.BoundsPx, clamped));
        Assert.Equal(Display1.BoundsPx.Top, clamped.Top);
    }

    [Fact]
    public void ClampToMonitor_RegionWiderThanMonitor_IsCenteredNotClampedNegative()
    {
        // A single-monitor-sized region (Display3's own full width) landing on the narrower
        // DISPLAY1... use a pathological case: region wider than the monitor entirely.
        var tinyMonitor = Display1 with { BoundsPx = new RectPhysical(0, -1440, 640, -1040) }; // 640x400
        var region = RectPhysical.FromSize(0, -1440, 800, 300); // wider than the 640-wide monitor
        var clamped = MultiMonitorRecording.ClampToMonitor(region, tinyMonitor);
        Assert.Equal(800, clamped.Width); // never resized
        // Centered: left = monitorLeft - (800-640)/2 = 0 - 80 = -80
        Assert.Equal(-80, clamped.Left);
    }

    // ---------- ToMonitorRelative ----------

    [Fact]
    public void ToMonitorRelative_Display3_IsUnchanged_SinceItsOriginIsZero()
    {
        var abs = RectPhysical.FromSize(100, 200, 800, 600);
        var rel = MultiMonitorRecording.ToMonitorRelative(abs, Display3);
        Assert.Equal(abs, rel);
    }

    [Fact]
    public void ToMonitorRelative_Display1_SubtractsItsNegativeOrigin()
    {
        // DISPLAY1's own top-left is (0,-1440) - an absolute rect at y=-1200 is 240px down from that.
        var abs = RectPhysical.FromSize(100, -1200, 800, 600);
        var rel = MultiMonitorRecording.ToMonitorRelative(abs, Display1);
        Assert.Equal(new RectPhysical(100, 240, 900, 840), rel);
    }

    [Fact]
    public void ToMonitorRelative_Display2_SubtractsItsPositiveXOrigin()
    {
        var abs = RectPhysical.FromSize(2660, 100, 800, 600); // 100px onto DISPLAY2 from its left edge
        var rel = MultiMonitorRecording.ToMonitorRelative(abs, Display2);
        Assert.Equal(100, rel.Left);
        Assert.Equal(100, rel.Top);
        Assert.Equal(800, rel.Width);
        Assert.Equal(600, rel.Height);
    }

    [Fact]
    public void ToMonitorRelative_RoundTripsWithHandoffClamp()
    {
        // Exercises the exact call sequence HandoffToMonitor makes: clamp an absolute rect onto a
        // destination monitor, then convert to that monitor's own relative space for RegionRecorder
        // — the result must itself be Contains()-valid against the monitor's own (0,0)-sized bounds.
        var desired = RectPhysical.FromSize(2500, 100, 800, 600); // straddling DISPLAY3/DISPLAY2 seam
        var clamped = MultiMonitorRecording.ClampToMonitor(desired, Display2);
        var relative = MultiMonitorRecording.ToMonitorRelative(clamped, Display2);
        var monitorLocalBounds = new RectPhysical(0, 0, Display2.BoundsPx.Width, Display2.BoundsPx.Height);
        Assert.True(MultiMonitorRecording.Contains(monitorLocalBounds, relative));
    }

    // ---------- Intersect (multi-monitor recording phase 2 - spanning capture geometry) ----------

    [Fact]
    public void Intersect_OverlappingRects_IsTheSharedRegion()
    {
        var a = RectPhysical.FromSize(2400, 100, 800, 600); // 2400-3200, 100-700
        var b = Display3.BoundsPx; // 0-2560, 0-1440
        var result = MultiMonitorRecording.Intersect(a, b);
        Assert.Equal(new RectPhysical(2400, 100, 2560, 700), result);
    }

    [Fact]
    public void Intersect_NoOverlap_IsDegenerateNotInverted()
    {
        var a = RectPhysical.FromSize(10_000, 10_000, 400, 300); // far off in a dead gap
        var result = MultiMonitorRecording.Intersect(a, Display3.BoundsPx);
        Assert.Equal(0, result.Width);
        Assert.Equal(0, result.Height);
        Assert.True(result.Right >= result.Left); // never inverted, even with no real overlap
        Assert.True(result.Bottom >= result.Top);
    }

    [Fact]
    public void Intersect_TouchingEdgeExactly_IsZeroWidthAtTheSeam()
    {
        // A rect that starts exactly where DISPLAY3 ends (x=2560) touches but doesn't overlap.
        var a = RectPhysical.FromSize(2560, 100, 400, 300);
        var result = MultiMonitorRecording.Intersect(a, Display3.BoundsPx);
        Assert.Equal(0, result.Width);
    }

    [Fact]
    public void Intersect_IsSymmetric()
    {
        var a = RectPhysical.FromSize(2400, -200, 800, 600);
        Assert.Equal(MultiMonitorRecording.Intersect(a, Display1.BoundsPx), MultiMonitorRecording.Intersect(Display1.BoundsPx, a));
    }

    // ---------- IntersectingMonitors ----------

    [Fact]
    public void IntersectingMonitors_FullyOnOneMonitor_ReturnsJustThatOne()
    {
        var region = RectPhysical.FromSize(100, 100, 800, 600); // entirely on DISPLAY3
        var result = MultiMonitorRecording.IntersectingMonitors(region, ThreeMonitors);
        Assert.Single(result);
        Assert.Equal(Display3.DeviceName, result[0].DeviceName);
    }

    [Fact]
    public void IntersectingMonitors_StraddlingTwoMonitors_ReturnsBothIndexOrdered()
    {
        // Straddles the DISPLAY3/DISPLAY2 vertical seam at x=2560.
        var region = RectPhysical.FromSize(2400, 100, 400, 300); // 2400-2800
        var result = MultiMonitorRecording.IntersectingMonitors(region, ThreeMonitors);
        Assert.Equal(2, result.Count);
        Assert.Equal(Display3.DeviceName, result[0].DeviceName); // Index 0
        Assert.Equal(Display2.DeviceName, result[1].DeviceName); // Index 2
    }

    [Fact]
    public void IntersectingMonitors_SpanningAllThree_ReturnsAllThreeIndexOrdered()
    {
        // A huge selection covering the whole union bounds touches all three monitors at once.
        var region = MultiMonitorRecording.UnionBounds(ThreeMonitors);
        var result = MultiMonitorRecording.IntersectingMonitors(region, ThreeMonitors);
        Assert.Equal(3, result.Count);
        Assert.Equal(new[] { "\\\\.\\DISPLAY3", "\\\\.\\DISPLAY1", "\\\\.\\DISPLAY2" }, result.Select(m => m.DeviceName));
    }

    [Fact]
    public void IntersectingMonitors_DeadGap_ReturnsEmpty()
    {
        var region = RectPhysical.FromSize(10_000, 10_000, 400, 300);
        var result = MultiMonitorRecording.IntersectingMonitors(region, ThreeMonitors);
        Assert.Empty(result);
    }

    [Fact]
    public void IntersectingMonitors_TouchingEdgeExactly_DoesNotCount()
    {
        // Same "touching but not overlapping" boundary Contains() treats as inside, but
        // IntersectingMonitors (built on a POSITIVE-area test) must not count a zero-width sliver.
        var region = RectPhysical.FromSize(2560, 100, 400, 300); // starts exactly at DISPLAY3's right edge
        var result = MultiMonitorRecording.IntersectingMonitors(region, ThreeMonitors);
        Assert.Single(result);
        Assert.Equal(Display2.DeviceName, result[0].DeviceName); // only the monitor it's genuinely ON
    }
}
