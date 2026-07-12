using System;
using System.Collections.Generic;
using RoeSnip.Capture;
using RoeSnip.Color;
using RoeSnip.Recording;
using Xunit;

namespace RoeSnip.Tests;

/// <summary>Pure compositor-core tests for multi-monitor recording PHASE 2 (true spanning capture —
/// see SpanningCanvasCompositor's own class doc and PLAN-MULTIMON-RECORDING.md §1/§2/§8). No live
/// WGC/device/thread dependency: every CapturedFrame here is a synthetic, directly-constructed
/// Fp16ScRgb buffer with a known constant color, mirroring MultiMonitorRecordingTests' own
/// no-live-session convention. Fp16 (not Bgra8) frames are used deliberately throughout — Bgra8
/// frames bypass ToneMapOptions entirely (SdrImage.FromCapturedFrame's passthrough branch), which
/// would make it impossible to prove a slot's own tone-map curve was actually applied, i.e. exactly
/// the "assert the seam is reproduced, not accidentally blended" property plan §8 calls out.</summary>
public class SpanningCanvasCompositorTests
{
    // Two-monitor layout: DISPLAY3 (0,0)-(1000,500), DISPLAY2 to its right (1000,0)-(2000,500).
    // Deliberately different SdrWhiteNits so the two monitors' own fixed tone-map curves produce
    // DIFFERENT output for the SAME input scRGB value — the whole point of these tests.
    private static MonitorInfo DisplayA => new(
        Index: 0, DeviceName: "\\\\.\\DISPLAYA", HMonitor: 1,
        BoundsPx: new RectPhysical(0, 0, 1000, 500),
        DpiX: 96, DpiY: 96, AdvancedColorActive: true,
        SdrWhiteNits: 200.0, MaxLuminanceNits: 1000.0, IsPrimary: true);

    private static MonitorInfo DisplayB => new(
        Index: 1, DeviceName: "\\\\.\\DISPLAYB", HMonitor: 2,
        BoundsPx: new RectPhysical(1000, 0, 2000, 500),
        DpiX: 96, DpiY: 96, AdvancedColorActive: true,
        SdrWhiteNits: 400.0, MaxLuminanceNits: 1000.0, IsPrimary: false);

    private static IReadOnlyList<MonitorInfo> TwoMonitors => new[] { DisplayA, DisplayB };

    private static readonly ToneMapOptions FixedOpts = new(Knee: 0.90, PeakOverride: 2.0);

    /// <summary>Builds a solid-color Fp16ScRgb CapturedFrame (R16G16B16A16Float, 8 bytes/pixel,
    /// R,G,B,A half-float order — see CapturedFrame.ReadPixelScRgb's own indexing doc). Values are
    /// scRGB linear (1.0 == 80 nits), matching what WGC actually delivers for HDR content.</summary>
    private static CapturedFrame MakeSolidFp16Frame(int width, int height, MonitorInfo monitor, float r, float g, float b, float a = 1.0f)
    {
        int stride = width * 8;
        var pixels = new byte[stride * height];
        ushort rh = BitConverter.HalfToUInt16Bits((Half)r);
        ushort gh = BitConverter.HalfToUInt16Bits((Half)g);
        ushort bh = BitConverter.HalfToUInt16Bits((Half)b);
        ushort ah = BitConverter.HalfToUInt16Bits((Half)a);
        for (int y = 0; y < height; y++)
        {
            int rowOff = y * stride;
            for (int x = 0; x < width; x++)
            {
                int o = rowOff + x * 8;
                BitConverter.TryWriteBytes(pixels.AsSpan(o, 2), rh);
                BitConverter.TryWriteBytes(pixels.AsSpan(o + 2, 2), gh);
                BitConverter.TryWriteBytes(pixels.AsSpan(o + 4, 2), bh);
                BitConverter.TryWriteBytes(pixels.AsSpan(o + 6, 2), ah);
            }
        }
        return new CapturedFrame(FrameFormat.Fp16ScRgb, width, height, stride, pixels, monitor);
    }

    private static byte[] ReadCanvasPixel(byte[] canvas, int canvasWidth, int x, int y)
    {
        int o = (y * canvasWidth + x) * 4;
        return new[] { canvas[o], canvas[o + 1], canvas[o + 2], canvas[o + 3] };
    }

    // ---------- BuildSlots ----------

    [Fact]
    public void BuildSlots_TwoMonitorSpan_ProducesTwoDisjointCanvasSubRects()
    {
        // Selection straddles the seam at x=1000: 300px on DISPLAYA, 500px on DISPLAYB.
        var selection = RectPhysical.FromSize(700, 100, 800, 300);
        var slots = SpanningCanvasCompositor.BuildSlots(selection, TwoMonitors, _ => FixedOpts);

        Assert.Equal(2, slots.Count);

        Assert.Equal(DisplayA.DeviceName, slots[0].Monitor.DeviceName);
        Assert.Equal(new RectPhysical(0, 0, 300, 300), slots[0].CanvasSubRect); // left 300px of the 800-wide canvas
        Assert.Equal(new RectPhysical(700, 100, 1000, 400), slots[0].MonitorRelativeCrop); // relative to A's own origin (0,0)

        Assert.Equal(DisplayB.DeviceName, slots[1].Monitor.DeviceName);
        Assert.Equal(new RectPhysical(300, 0, 800, 300), slots[1].CanvasSubRect); // remaining 500px
        Assert.Equal(new RectPhysical(0, 100, 500, 400), slots[1].MonitorRelativeCrop); // relative to B's own origin (1000,0)
    }

    [Fact]
    public void BuildSlots_SingleMonitorSelection_IsOneSlotCoveringTheWholeCanvas()
    {
        var selection = RectPhysical.FromSize(100, 100, 400, 300); // entirely on DISPLAYA
        var slots = SpanningCanvasCompositor.BuildSlots(selection, TwoMonitors, _ => FixedOpts);

        Assert.Single(slots);
        Assert.Equal(new RectPhysical(0, 0, 400, 300), slots[0].CanvasSubRect); // the whole canvas
    }

    [Fact]
    public void BuildSlots_UsesTheCallersOwnToneMapPerMonitor()
    {
        var selection = RectPhysical.FromSize(700, 100, 800, 300);
        var perMonitorOpts = new Dictionary<string, ToneMapOptions>
        {
            [DisplayA.DeviceName] = new ToneMapOptions(Knee: 0.80, PeakOverride: 3.0),
            [DisplayB.DeviceName] = new ToneMapOptions(Knee: 0.95, PeakOverride: 6.0),
        };
        var slots = SpanningCanvasCompositor.BuildSlots(selection, TwoMonitors, m => perMonitorOpts[m.DeviceName]);

        Assert.Equal(perMonitorOpts[DisplayA.DeviceName], slots[0].ToneMap);
        Assert.Equal(perMonitorOpts[DisplayB.DeviceName], slots[1].ToneMap);
    }

    // ---------- CompositeSlot: the seam is reproduced, not blended ----------

    [Fact]
    public void CompositeSlot_TwoMonitorsDifferentToneMapAndColor_WriteDistinctPixelsIntoTheirOwnSubRects()
    {
        var selection = RectPhysical.FromSize(700, 0, 800, 300); // straddles the seam, A gets 0-300, B gets 300-800
        var toneMapA = new ToneMapOptions(Knee: 0.90, PeakOverride: 2.0);
        var toneMapB = new ToneMapOptions(Knee: 0.90, PeakOverride: 2.0); // same curve params...
        var slots = SpanningCanvasCompositor.BuildSlots(selection, TwoMonitors, m =>
            m.DeviceName == DisplayA.DeviceName ? toneMapA : toneMapB);

        // ...but DisplayA/DisplayB have DIFFERENT SdrWhiteNits (200 vs 400), so identical scRGB
        // input still tone-maps to different SDR output — this is the "seam is correct, not a bug"
        // property from plan §2.
        var frameA = MakeSolidFp16Frame(slots[0].MonitorRelativeCrop.Width, slots[0].MonitorRelativeCrop.Height, DisplayA, 0.5f, 0.5f, 0.5f);
        var frameB = MakeSolidFp16Frame(slots[1].MonitorRelativeCrop.Width, slots[1].MonitorRelativeCrop.Height, DisplayB, 0.5f, 0.5f, 0.5f);

        var expectedA = RoeSnip.Imaging.SdrImage.FromCapturedFrame(frameA, toneMapA);
        var expectedB = RoeSnip.Imaging.SdrImage.FromCapturedFrame(frameB, toneMapB);
        Assert.NotEqual(expectedA.Pixels[0], expectedB.Pixels[0]); // sanity: the two monitors really do differ

        var canvas = new byte[selection.Width * 4 * selection.Height];
        byte[]? scratchA = null, scratchB = null;
        SpanningCanvasCompositor.CompositeSlot(canvas, selection.Width, selection.Height, slots[0], frameA, ref scratchA);
        SpanningCanvasCompositor.CompositeSlot(canvas, selection.Width, selection.Height, slots[1], frameB, ref scratchB);

        // The canvas pixel just left of the seam (x=0, A's own sub-rect) matches A's own tone-map
        // output; just right of it (x=300, B's own sub-rect) matches B's — proving the seam boundary
        // lands exactly where the geometry says, and the two halves are NOT blended together.
        Assert.Equal(ReadCanvasPixel(expectedA.Pixels, expectedA.Width, 0, 0), ReadCanvasPixel(canvas, selection.Width, 0, 0));
        Assert.Equal(ReadCanvasPixel(expectedB.Pixels, expectedB.Width, 0, 0), ReadCanvasPixel(canvas, selection.Width, 300, 0));
    }

    [Fact]
    public void CompositeSlot_NullFrame_LeavesTheSubRectUntouched()
    {
        var selection = RectPhysical.FromSize(0, 0, 400, 300);
        var slots = SpanningCanvasCompositor.BuildSlots(selection, new[] { DisplayA }, _ => FixedOpts);
        var canvas = new byte[selection.Width * 4 * selection.Height];
        canvas[0] = 0x42; // sentinel — a real composite would overwrite byte 0 (B channel of pixel 0,0)
        byte[]? scratch = null;

        SpanningCanvasCompositor.CompositeSlot(canvas, selection.Width, selection.Height, slots[0], frame: null, ref scratch);

        Assert.Equal(0x42, canvas[0]); // "reuse the last composited frame" policy — no-op on null, plan §1
        Assert.Null(scratch); // never allocated for a tick that composited nothing
    }

    [Fact]
    public void CompositeSlot_WholeCanvasSlot_WritesDirectlyIntoCanvasWithoutAllocatingScratch()
    {
        // N==1 degenerate case (BuildSlots_SingleMonitorSelection... above) — the canvas-copy step
        // must be skipped entirely, matching the pre-spanning single-recorder pipeline's own cost.
        var selection = RectPhysical.FromSize(0, 0, 200, 150);
        var slots = SpanningCanvasCompositor.BuildSlots(selection, new[] { DisplayA }, _ => FixedOpts);
        var frame = MakeSolidFp16Frame(200, 150, DisplayA, 0.25f, 0.5f, 0.75f);
        var expected = RoeSnip.Imaging.SdrImage.FromCapturedFrame(frame, FixedOpts);

        var canvas = new byte[selection.Width * 4 * selection.Height];
        byte[]? scratch = null;
        SpanningCanvasCompositor.CompositeSlot(canvas, selection.Width, selection.Height, slots[0], frame, ref scratch);

        Assert.Null(scratch); // no per-slot scratch buffer ever allocated for the whole-canvas case
        Assert.Equal(expected.Pixels, canvas); // tone-mapped straight into the canvas
    }

    [Fact]
    public void CompositeSlot_PartialSubRect_ReusesScratchBufferAcrossCalls()
    {
        var selection = RectPhysical.FromSize(700, 0, 800, 300);
        var slots = SpanningCanvasCompositor.BuildSlots(selection, TwoMonitors, _ => FixedOpts);
        var canvas = new byte[selection.Width * 4 * selection.Height];
        byte[]? scratch = null;

        var frame1 = MakeSolidFp16Frame(slots[0].MonitorRelativeCrop.Width, slots[0].MonitorRelativeCrop.Height, DisplayA, 0.1f, 0.1f, 0.1f);
        SpanningCanvasCompositor.CompositeSlot(canvas, selection.Width, selection.Height, slots[0], frame1, ref scratch);
        Assert.NotNull(scratch);
        var firstScratchRef = scratch;

        var frame2 = MakeSolidFp16Frame(slots[0].MonitorRelativeCrop.Width, slots[0].MonitorRelativeCrop.Height, DisplayA, 0.9f, 0.9f, 0.9f);
        SpanningCanvasCompositor.CompositeSlot(canvas, selection.Width, selection.Height, slots[0], frame2, ref scratch);

        Assert.Same(firstScratchRef, scratch); // same-sized slot on the next tick — no reallocation (LOH/Gen2 discipline)
    }

    // ---------- Benchmark sanity (plan §5/§8) ----------

    /// <summary>Turns plan §5's qualitative "2 monitors is roughly the second monitor's own
    /// readback + tone-map dispatch overhead, not a 2x multiplier" claim into an actual gate, per
    /// §8's own "mandate a benchmark before shipping phase 2" instruction. Measures ONLY the
    /// compositor's own CPU cost (tone-map + row-copy for both slots, at the GIF format's 50fps
    /// ceiling's own per-tick budget of 20ms) — GPU readback and encode are deliberately out of
    /// scope here (they're covered by RegionRecorder's own existing readback path and
    /// Mp4Encoder/GifEncoder's own benchmarks respectively; this type has no live dependency on
    /// either, see the class's own top-of-file doc note). A generous multiple of the 20ms design
    /// budget (not 20ms itself) is asserted to keep this a meaningful regression gate without
    /// making CI flaky on a loaded/virtualized runner — the design number is documented here for a
    /// human to compare against, not enforced to the millisecond.</summary>
    [Fact]
    public void CompositeTick_TwoMonitorsAt1920x1080Canvas_StaysWellUnderTheGifFpsCeilingBudget()
    {
        const int canvasWidth = 1920, canvasHeight = 1080; // a plausible real dual-monitor recording size
        var selection = RectPhysical.FromSize(0, 0, canvasWidth, canvasHeight);
        var left = DisplayA with { BoundsPx = new RectPhysical(0, 0, canvasWidth / 2, canvasHeight) };
        var right = DisplayB with { BoundsPx = new RectPhysical(canvasWidth / 2, 0, canvasWidth, canvasHeight) };
        var monitors = new[] { left, right };
        var slots = SpanningCanvasCompositor.BuildSlots(selection, monitors, _ => FixedOpts);

        var frameLeft = MakeSolidFp16Frame(slots[0].MonitorRelativeCrop.Width, slots[0].MonitorRelativeCrop.Height, left, 0.4f, 0.3f, 0.6f);
        var frameRight = MakeSolidFp16Frame(slots[1].MonitorRelativeCrop.Width, slots[1].MonitorRelativeCrop.Height, right, 0.6f, 0.5f, 0.2f);

        var canvas = new byte[canvasWidth * 4 * canvasHeight];
        byte[]? scratchLeft = null, scratchRight = null;

        // One warm-up tick — JIT the hot path and prime ToneMapper's per-scale LUT cache (built
        // lazily on first use, see ToneMapper.GetLut's own doc comment) before timing starts, so the
        // measurement reflects steady-state per-tick cost, not one-time setup.
        SpanningCanvasCompositor.CompositeSlot(canvas, canvasWidth, canvasHeight, slots[0], frameLeft, ref scratchLeft);
        SpanningCanvasCompositor.CompositeSlot(canvas, canvasWidth, canvasHeight, slots[1], frameRight, ref scratchRight);

        const int ticks = 50; // one second's worth at the GIF format's 50fps ceiling
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < ticks; i++)
        {
            SpanningCanvasCompositor.CompositeSlot(canvas, canvasWidth, canvasHeight, slots[0], frameLeft, ref scratchLeft);
            SpanningCanvasCompositor.CompositeSlot(canvas, canvasWidth, canvasHeight, slots[1], frameRight, ref scratchRight);
        }
        sw.Stop();

        double avgMsPerTick = sw.Elapsed.TotalMilliseconds / ticks;
        // Design budget (plan §5): 20ms/tick. Gated here at 5x that (100ms) to absorb CI noise while
        // still catching a real regression; if this ever needs raising, the plan's own §5 budget is
        // the number to re-check against on real hardware, not this test's slack.
        Assert.True(avgMsPerTick < 100.0, $"spanning composite tick averaged {avgMsPerTick:F2} ms (budget: 20ms design target, 100ms CI gate)");
    }
}
