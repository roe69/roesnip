using System;
using System.Collections.Generic;
using RoeSnip.Capture;
using RoeSnip.Color;
using RoeSnip.Imaging;

namespace RoeSnip.Recording;

/// <summary>Pure, unit-testable CPU-side compositor core for spanning recording — multi-monitor
/// recording PHASE 2 (record a region that spans N monitors simultaneously; see
/// PLAN-MULTIMON-RECORDING.md §1/§2). No live WGC/device/thread dependency: given an already-in-hand
/// <see cref="CapturedFrame"/> (real or synthetic) per intersected monitor, tone-maps each one with
/// THAT monitor's own fixed <see cref="ToneMapOptions"/> and copies the result into that monitor's
/// own axis-aligned sub-rect of a shared canvas buffer — the seam between two monitors' sub-rects is
/// therefore whatever their independently-tone-mapped edges happen to look like. That seam is
/// documented as CORRECT behavior (plan §2), not something this type tries to blend away: it matches
/// what the user's own eyes see looking at the two physical panels, since each monitor's peak/knee
/// is derived from ITS OWN photometrics, never averaged with a neighbor's.
///
/// Lives entirely on RecordingSession's encoder thread (never the WGC callback thread, never the UI
/// thread) — same threading rule <see cref="RegionRecorder"/>'s own tone-map-adjacent work already
/// follows, just generalized from one monitor to N.
///
/// Public (not internal): the "make the testable slice a public class instead of an
/// InternalsVisibleTo edit" convention this codebase already uses for
/// <see cref="MultiMonitorRecording"/>/<see cref="Mp4Encoder"/>'s own pure math (see either class's
/// doc comment) — RoeSnip.Tests has no InternalsVisibleTo access to this assembly.</summary>
public static class SpanningCanvasCompositor
{
    /// <summary>One intersected monitor's fixed contribution to a spanning take. Everything here is
    /// computed once at BeginCapture (or a mid-take topology rebuild — RecordingSession.
    /// RebuildSpanningRecorders) and held fixed for as long as this slot exists; only the PIXELS
    /// behind <see cref="MonitorRelativeCrop"/>/<see cref="CanvasSubRect"/> change frame to frame.
    /// A plain data record, not a class, on purpose: RecordingSession.TrySlideSpanningInPlace
    /// rebuilds one of these per tick-worthy drag via a `with` expression rather than mutating
    /// shared state a compositing thread might be mid-read of.</summary>
    public readonly record struct MonitorSlot(
        MonitorInfo Monitor,
        RectPhysical MonitorRelativeCrop, // RegionRecorder's own crop rect, relative to Monitor's own (0,0) — see RegionRecorder's ctor doc
        RectPhysical CanvasSubRect,       // where this slot's pixels land in the shared canvas — canvas-local, (0,0) is the selection's own top-left
        ToneMapOptions ToneMap);

    /// <summary>Builds one slot per monitor <paramref name="selectionAbs"/> intersects, in
    /// <see cref="MultiMonitorRecording.IntersectingMonitors"/>'s own deterministic (Index-sorted)
    /// order — callers rely on that order staying stable across a rebuild for the same monitor set
    /// (RecordingSession.SameMonitorSet does a positional compare, not a set compare, specifically
    /// because of this guarantee). <paramref name="toneMapFor"/> is the caller's own fixed-tone-map
    /// formula (RecordingSession.ComputeFixedToneMapOpts) — kept as a delegate so this stays a pure
    /// geometry function with no opinion of its own about photometrics (plan §2's "recomputed per
    /// monitor, never blended" is the CALLER's formula to own, not this type's).</summary>
    public static IReadOnlyList<MonitorSlot> BuildSlots(
        RectPhysical selectionAbs, IReadOnlyList<MonitorInfo> monitors, Func<MonitorInfo, ToneMapOptions> toneMapFor)
    {
        var intersecting = MultiMonitorRecording.IntersectingMonitors(selectionAbs, monitors);
        var slots = new List<MonitorSlot>(intersecting.Count);
        foreach (var monitor in intersecting)
        {
            var cropAbs = MultiMonitorRecording.Intersect(selectionAbs, monitor.BoundsPx);
            var monitorRelativeCrop = MultiMonitorRecording.ToMonitorRelative(cropAbs, monitor);
            var canvasSubRect = new RectPhysical(
                cropAbs.Left - selectionAbs.Left, cropAbs.Top - selectionAbs.Top,
                cropAbs.Right - selectionAbs.Left, cropAbs.Bottom - selectionAbs.Top);
            slots.Add(new MonitorSlot(monitor, monitorRelativeCrop, canvasSubRect, toneMapFor(monitor)));
        }
        return slots;
    }

    /// <summary>Tone-maps <paramref name="frame"/> into <paramref name="slot"/>'s own sub-rect of
    /// <paramref name="canvas"/> (BGRA8, tightly packed rows, <paramref name="canvasWidth"/>*4 bytes
    /// each). Passing <c>frame: null</c> is a deliberate no-op — the "reuse the last composited frame
    /// for a monitor that had nothing new this tick" policy (plan §1) lives in the CALLER (a static
    /// monitor legitimately produces no WGC callback at all), not here; this method only ever writes
    /// pixels it was actually handed, never clears/blanks a sub-rect.
    ///
    /// <paramref name="scratch"/> is a persistent, caller-owned per-slot buffer (recording-cadence
    /// callers must never allocate a fresh canvas/slot-sized array per frame — the f7aa9a3 LOH/Gen2
    /// lesson). It is deliberately left null and UNUSED whenever <paramref name="slot"/>'s own
    /// sub-rect happens to be the WHOLE canvas (the N==1 degenerate case — a selection that
    /// intersects only one monitor): the tone-map then writes directly into
    /// <paramref name="canvas"/> with no separate scratch buffer or row-copy at all, making that case
    /// byte-for-byte as cheap as the pre-spanning single-recorder pipeline, not "spanning with N=1
    /// paying a compositor tax".</summary>
    public static void CompositeSlot(
        byte[] canvas, int canvasWidth, int canvasHeight,
        in MonitorSlot slot, CapturedFrame? frame, ref byte[]? scratch)
    {
        if (frame is null)
        {
            return;
        }

        var sub = slot.CanvasSubRect;
        bool wholeCanvas = sub.Left == 0 && sub.Top == 0 && sub.Width == canvasWidth && sub.Height == canvasHeight;
        if (wholeCanvas)
        {
            SdrImage.FromCapturedFrame(frame, slot.ToneMap, canvas);
            return;
        }

        int w = sub.Width, h = sub.Height;
        if (scratch is null || scratch.Length != w * 4 * h)
        {
            scratch = new byte[w * 4 * h];
        }
        var sdr = SdrImage.FromCapturedFrame(frame, slot.ToneMap, scratch);
        CopyIntoCanvas(sdr, canvas, canvasWidth, sub);
    }

    private static void CopyIntoCanvas(SdrImage sdr, byte[] canvas, int canvasWidth, RectPhysical sub)
    {
        int rowBytes = sdr.Width * 4;
        int canvasStride = canvasWidth * 4;
        for (int y = 0; y < sdr.Height; y++)
        {
            int srcOffset = y * sdr.Stride;
            int dstOffset = (sub.Top + y) * canvasStride + sub.Left * 4;
            Buffer.BlockCopy(sdr.Pixels, srcOffset, canvas, dstOffset, rowBytes);
        }
    }
}
