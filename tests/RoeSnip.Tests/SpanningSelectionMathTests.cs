using System.Collections.Generic;
using System.Windows;
using RoeSnip.Capture;
using RoeSnip.Overlay;
using Xunit;

namespace RoeSnip.Tests;

/// <summary>Pure geometry for the cross-monitor ("spanning") selection feature — see
/// SpanningSelectionMath's own doc comment for why this is factored out of OverlayController/
/// OverlayWindow specifically so it's testable with no WPF Window involved. Covers both the original
/// distribute primitive (used by every drag mode: NewSelection, and — as of resize-after-place —
/// SpanningResize/SpanningMove too) and ApplyResize, the resize math those last two now share with
/// the plain single-monitor Resize drag.</summary>
public class SpanningSelectionMathTests
{
    // Two side-by-side monitors, directly adjacent at x=1920 (no gap): a common dual-monitor layout.
    private static readonly RectPhysical MonitorA = new(0, 0, 1920, 1080);
    private static readonly RectPhysical MonitorB = new(1920, 0, 3840, 1080);

    // Two monitors with a real gap between them (x in [1920, 3000] belongs to no monitor) — e.g.
    // non-adjacent displays, or a portrait monitor that doesn't reach as far down as its neighbor.
    private static readonly RectPhysical MonitorAGap = new(0, 0, 1920, 1080);
    private static readonly RectPhysical MonitorCGap = new(3000, 0, 4920, 1080);

    // ---------- ComputeVirtualDesktopBounds ----------

    [Fact]
    public void ComputeVirtualDesktopBounds_UnionsEveryMonitor()
    {
        var bounds = SpanningSelectionMath.ComputeVirtualDesktopBounds(new[] { MonitorA, MonitorB });
        Assert.Equal(new RectPhysical(0, 0, 3840, 1080), bounds);
    }

    [Fact]
    public void ComputeVirtualDesktopBounds_IncludesTheGapBetweenNonAdjacentMonitors()
    {
        var bounds = SpanningSelectionMath.ComputeVirtualDesktopBounds(new[] { MonitorAGap, MonitorCGap });
        Assert.Equal(new RectPhysical(0, 0, 4920, 1080), bounds);
    }

    [Fact]
    public void ComputeVirtualDesktopBounds_EmptyList_ReturnsDefault()
    {
        var bounds = SpanningSelectionMath.ComputeVirtualDesktopBounds(System.Array.Empty<RectPhysical>());
        Assert.Equal(default, bounds);
    }

    // ---------- ClampToVirtualDesktop ----------

    [Fact]
    public void ClampToVirtualDesktop_RectEntirelyInside_IsUnchanged()
    {
        var vdb = new RectPhysical(0, 0, 3840, 1080);
        var r = new RectPhysical(100, 100, 200, 200);
        Assert.Equal(r, SpanningSelectionMath.ClampToVirtualDesktop(r, vdb));
    }

    [Fact]
    public void ClampToVirtualDesktop_RectOverhangingEveryEdge_ClampsToTheBoundingBox()
    {
        var vdb = new RectPhysical(0, 0, 3840, 1080);
        var r = new RectPhysical(-500, -500, 9999, 9999);
        Assert.Equal(vdb, SpanningSelectionMath.ClampToVirtualDesktop(r, vdb));
    }

    // ---------- SlideToBounds ----------
    // The Move-drag clamp: preserves width/height by sliding the ORIGIN back inside bounds, unlike
    // ClampToVirtualDesktop's independent-per-edge clamp (correct for Resize/NewSelection, wrong for
    // Move — see SlideToBounds's own doc comment).

    [Fact]
    public void SlideToBounds_RectEntirelyInside_IsUnchanged()
    {
        var vdb = new RectPhysical(0, 0, 3840, 1080);
        var r = new RectPhysical(100, 100, 900, 700);
        Assert.Equal(r, SpanningSelectionMath.SlideToBounds(r, vdb));
    }

    [Fact]
    public void SlideToBounds_OverhangingLeftEdge_SlidesWithoutShrinking()
    {
        // An 800-wide rect whose Left would be -200 (Right = 600) — sliding it to Left=0 keeps the
        // full 800 width (Right becomes 800), unlike ClampToVirtualDesktop which would independently
        // pull Left to 0 and leave Right at 600, shrinking the rect to 600 wide.
        var vdb = new RectPhysical(0, 0, 3840, 1080);
        var r = new RectPhysical(-200, 100, 600, 500);
        var result = SpanningSelectionMath.SlideToBounds(r, vdb);
        Assert.Equal(new RectPhysical(0, 100, 800, 500), result);
    }

    [Fact]
    public void SlideToBounds_OverhangingRightAndBottomEdges_SlidesWithoutShrinking()
    {
        var vdb = new RectPhysical(0, 0, 3840, 1080);
        var r = new RectPhysical(3700, 1000, 4200, 1300); // 500 wide, 300 tall
        var result = SpanningSelectionMath.SlideToBounds(r, vdb);
        Assert.Equal(new RectPhysical(3340, 780, 3840, 1080), result);
    }

    [Fact]
    public void SlideToBounds_LargerThanBounds_ShrinksToBoundsSizeAsALastResort()
    {
        var vdb = new RectPhysical(0, 0, 1920, 1080);
        var r = new RectPhysical(-500, -500, 5000, 5000);
        var result = SpanningSelectionMath.SlideToBounds(r, vdb);
        Assert.Equal(vdb, result);
    }

    [Fact]
    public void SlideToBounds_NormalizesAnInvertedRectFirst()
    {
        var vdb = new RectPhysical(0, 0, 3840, 1080);
        var inverted = new RectPhysical(600, 500, -200, 100); // Right < Left, Bottom < Top
        var result = SpanningSelectionMath.SlideToBounds(inverted, vdb);
        Assert.Equal(new RectPhysical(0, 100, 800, 500), result);
    }

    // ---------- Distribute: single-monitor degenerate case ----------

    [Fact]
    public void Distribute_CandidateEntirelyOnOneMonitor_IsNotSpanning()
    {
        var vdb = SpanningSelectionMath.ComputeVirtualDesktopBounds(new[] { MonitorA, MonitorB });
        var candidate = new RectPhysical(100, 100, 500, 500); // entirely within MonitorA
        var result = SpanningSelectionMath.Distribute(candidate, vdb, new[] { MonitorA, MonitorB });

        Assert.False(result.IsSpanning);
        Assert.Single(result.Hits);
        Assert.Equal(0, result.Hits[0].MonitorIndex);
        Assert.Equal(candidate, result.Hits[0].LocalRect); // MonitorA's origin is (0,0) — local == virtual
        Assert.Equal(SelectionEdges.All, result.Hits[0].RealEdges); // nothing clipped it away from itself
    }

    [Fact]
    public void Distribute_CandidateTouchingNoMonitorAtAll_ProducesNoHits()
    {
        var vdb = SpanningSelectionMath.ComputeVirtualDesktopBounds(new[] { MonitorAGap, MonitorCGap });
        // Entirely inside the gap between the two monitors.
        var candidate = new RectPhysical(2200, 100, 2400, 300);
        var result = SpanningSelectionMath.Distribute(candidate, vdb, new[] { MonitorAGap, MonitorCGap });

        Assert.False(result.IsSpanning);
        Assert.Empty(result.Hits);
    }

    // ---------- Distribute: the actual spanning case ----------

    [Fact]
    public void Distribute_CandidateAcrossTwoAdjacentMonitors_SplitsCorrectlyWithRealEdgesAtTheSeam()
    {
        var vdb = SpanningSelectionMath.ComputeVirtualDesktopBounds(new[] { MonitorA, MonitorB });
        // Straddles the seam at x=1920: [1500,2500] x [100,900].
        var candidate = new RectPhysical(1500, 100, 2500, 900);
        var result = SpanningSelectionMath.Distribute(candidate, vdb, new[] { MonitorA, MonitorB });

        Assert.True(result.IsSpanning);
        Assert.Equal(candidate, result.ClampedVirtual); // nothing outside the virtual desktop bounds
        Assert.Equal(2, result.Hits.Count);

        var hitA = result.Hits[0];
        Assert.Equal(0, hitA.MonitorIndex);
        // MonitorA's own local rect: candidate clipped to [0,1920), MonitorA's origin is (0,0).
        Assert.Equal(new RectPhysical(1500, 100, 1920, 900), hitA.LocalRect);
        // Left/Top/Bottom are real edges of the true selection; Right is where MonitorA's own screen
        // cuts it off (the selection continues onto MonitorB) — must NOT offer a Right/corner handle.
        Assert.Equal(SelectionEdges.Left | SelectionEdges.Top | SelectionEdges.Bottom, hitA.RealEdges);

        var hitB = result.Hits[1];
        Assert.Equal(1, hitB.MonitorIndex);
        // MonitorB's own local rect: candidate clipped to [1920,3840), MonitorB's own origin is (1920,0).
        Assert.Equal(new RectPhysical(0, 100, 580, 900), hitB.LocalRect);
        Assert.Equal(SelectionEdges.Top | SelectionEdges.Right | SelectionEdges.Bottom, hitB.RealEdges);
    }

    [Fact]
    public void Distribute_CandidateAcrossAGap_OnlyProducesHitsForMonitorsItActuallyTouches()
    {
        var vdb = SpanningSelectionMath.ComputeVirtualDesktopBounds(new[] { MonitorAGap, MonitorCGap });
        // Spans from inside MonitorA, across the [1920,3000) gap, into MonitorC.
        var candidate = new RectPhysical(1500, 100, 3500, 900);
        var result = SpanningSelectionMath.Distribute(candidate, vdb, new[] { MonitorAGap, MonitorCGap });

        Assert.True(result.IsSpanning);
        Assert.Equal(2, result.Hits.Count);
        Assert.Equal(new RectPhysical(1500, 100, 1920, 900), result.Hits[0].LocalRect);
        Assert.Equal(new RectPhysical(0, 100, 500, 900), result.Hits[1].LocalRect); // 3500-3000=500
        // Neither monitor's facing edge is real — the true selection's middle third is the gap,
        // which belongs to no monitor at all, so there is nothing there to grab a handle on.
        Assert.Equal(SelectionEdges.None, result.Hits[0].RealEdges & SelectionEdges.Right);
        Assert.Equal(SelectionEdges.None, result.Hits[1].RealEdges & SelectionEdges.Left);
    }

    [Fact]
    public void Distribute_CandidateClampedByVirtualDesktopBounds_ClampedEdgeCountsAsReal()
    {
        var vdb = SpanningSelectionMath.ComputeVirtualDesktopBounds(new[] { MonitorA, MonitorB });
        // Right edge (9999) is WAY past MonitorB's own 3840 edge, which is also the virtual desktop's
        // own outer edge — after clamping, that edge legitimately IS the true selection's own edge
        // (there's no monitor beyond it to clip it further), so MonitorB should report Right as real.
        var candidate = new RectPhysical(1500, 100, 9999, 900);
        var result = SpanningSelectionMath.Distribute(candidate, vdb, new[] { MonitorA, MonitorB });

        Assert.Equal(new RectPhysical(1500, 100, 3840, 900), result.ClampedVirtual);
        var hitB = Assert.Single(result.Hits, h => h.MonitorIndex == 1);
        Assert.True((hitB.RealEdges & SelectionEdges.Right) != 0);
    }

    [Fact]
    public void Distribute_NormalizesAnInvertedCandidateBeforeDistributing()
    {
        var vdb = SpanningSelectionMath.ComputeVirtualDesktopBounds(new[] { MonitorA, MonitorB });
        // Right < Left, Bottom < Top — as a drag anchored at the bottom-right and dragged up-left
        // would produce before normalization.
        var inverted = new RectPhysical(2500, 900, 1500, 100);
        var result = SpanningSelectionMath.Distribute(inverted, vdb, new[] { MonitorA, MonitorB });

        Assert.True(result.IsSpanning);
        Assert.Equal(new RectPhysical(1500, 100, 2500, 900), result.ClampedVirtual);
    }

    // ---------- ApplyResize ----------

    private static readonly RectPhysical ResizeStart = new(100, 100, 300, 300);

    [Fact]
    public void ApplyResize_Right_MovesOnlyTheRightEdge()
    {
        var result = SpanningSelectionMath.ApplyResize(ResizeStart, SelectionHandle.Right, new Point(250, 999));
        Assert.Equal(new RectPhysical(100, 100, 250, 300), result);
    }

    [Fact]
    public void ApplyResize_TopLeft_MovesBothEdgesItOwns()
    {
        var result = SpanningSelectionMath.ApplyResize(ResizeStart, SelectionHandle.TopLeft, new Point(50, 60));
        Assert.Equal(new RectPhysical(50, 60, 300, 300), result);
    }

    [Fact]
    public void ApplyResize_DraggedPastTheOppositeCorner_NormalizesInsteadOfStayingInverted()
    {
        // Dragging TopLeft's handle down-right past the opposite (BottomRight) corner would produce
        // Left=500 > Right=300 and Top=500 > Bottom=300 before normalization — the rect must still
        // come out as a valid, non-negative-size rect afterwards.
        var result = SpanningSelectionMath.ApplyResize(ResizeStart, SelectionHandle.TopLeft, new Point(500, 500));
        Assert.Equal(new RectPhysical(300, 300, 500, 500), result);
    }

    [Fact]
    public void ApplyResize_Body_LeavesTheRectUnchanged()
    {
        // Body isn't a real resize handle (OverlayWindow never calls ApplyResize for a Body/None hit
        // — this just documents that the switch's default (no case) is a safe no-op if it ever were).
        var result = SpanningSelectionMath.ApplyResize(ResizeStart, SelectionHandle.Body, new Point(999, 999));
        Assert.Equal(ResizeStart, result);
    }
}
