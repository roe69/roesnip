using System;
using System.Diagnostics;

namespace RoeSnip.Core.Recording.Gif;

/// <summary>Off-by-default per-stage wall-time accumulators for <see cref="RoeSnip.Core.Recording.GifEncoder"/>'s
/// frame pipeline, used only to answer "where does the CPU actually go" for the GIF software-encode
/// CPU workstream (see GifSizeBenchmarkTests' own class doc for the benchmark content these numbers
/// were gathered against). <see cref="Enabled"/> defaults to false: every call site guards its
/// <see cref="Stopwatch.GetTimestamp"/> pair with a check of this flag first, so a normal recording
/// take (Enabled never set) pays a single false branch per stage per frame — not a real Stopwatch
/// call — keeping this at zero measurable cost on the hot path the rest of this file's LOH/Gen2
/// discipline exists to protect. That "per frame" framing does NOT extend to the LutFillTicks/
/// LutLookupCount drill-down guards inside <see cref="GifNearestColorLut.Lookup"/>: those run PER
/// PIXEL (every ClassifyAndPaint lookup, not once per frame), so a Max@50fps 640x420 frame
/// evaluates them hundreds of thousands of times rather than once. A/B wall-time measurement with
/// those two per-pixel checks removed found no measurable Release-build cost (medians 6.85s vs
/// 6.74s, within noise, since the branch predicts trivially once Enabled settles to false), but
/// Debug builds pay real overhead from it (median 35.5s with the guards vs roughly 29s without,
/// about 18%) — profile Debug-configuration numbers with that in mind rather than assuming this
/// class is free everywhere.
///
/// Buckets deliberately mirror the four named in the workstream's own instrumentation brief (bbox
/// scan, ClassifyAndPaint, octree/LUT, LZW), plus two more the real per-frame pipeline actually has
/// that aren't optional to measure honestly: CollectPaintedPixels (the tolerance-only pre-pass that
/// gathers the dense "newly painted" pixel set the palette step consumes — see GifEncoder.WriteFrame's
/// own doc comment for why it's a separate pass from ClassifyAndPaint) and PackAndHeader (the box-index
/// packing copy plus the direct per-byte GCE/Image-Descriptor/LCT stream writes) — every stage
/// GifEncoder.WriteFrame actually performs, none silently folded into "other".
///
/// [ThreadStatic]: matches GifEncoder's own "encoder thread only" discipline for AddFrame/WriteFrame
/// (a single recording take's frames are already serialized on one thread) while ALSO keeping this
/// safe to use from xunit's test process, where many unrelated tests construct and drive their own
/// GifEncoder instances on their own threads IN PARALLEL by default. A plain (non-thread-local)
/// static here would let one test's measurement window silently accumulate ticks from every other
/// concurrently-running test's encoder activity too (any of them reaching a
/// <see cref="Enabled"/>-guarded call site while it happens to be true) — observed directly during
/// this workstream's own development as an "instrumented total is ~2x wall time" failure the moment
/// the profiling test ran alongside the rest of the suite instead of in isolation. Each field below
/// is therefore a genuinely separate storage location per OS thread; <see cref="Enabled"/> itself
/// stays a plain (non-thread-local) static — a stray true-during-someone-else's-measurement-window
/// only costs an unused thread-local accumulator a few extra Stopwatch calls, never cross-thread data
/// corruption, so there's no need to pay a ThreadStatic slot for it too.</summary>
public static class GifEncoderStageTimings
{
    public static bool Enabled;

    [ThreadStatic] public static long BboxScanTicks;
    [ThreadStatic] public static long CollectPaintedTicks;
    [ThreadStatic] public static long PaletteTicks;
    [ThreadStatic] public static long ClassifyAndPaintTicks;
    [ThreadStatic] public static long PackAndHeaderTicks;
    [ThreadStatic] public static long LzwTicks;

    // Drill-down INSIDE the ClassifyAndPaint bucket (not part of GifEncoder.WriteFrame's own five
    // top-level stages, so not summed into TotalTicks below): GifNearestColorLut.FillBucket's
    // brute-force nearest-palette-entry scan, called lazily from every ClassifyAndPaint's
    // lut.Lookup on a stamp miss. Added specifically to test the "is the LUT's cache-miss fill the
    // real cost inside ClassifyAndPaint, on content whose palette invalidates almost every frame"
    // hypothesis the first measurement pass raised — see the workstream's own commit history for
    // what this number showed.
    [ThreadStatic] public static long LutFillTicks;
    [ThreadStatic] public static long LutFillCount;
    [ThreadStatic] public static long LutLookupCount;

    /// <summary>Zeroes the CALLING THREAD's own accumulators without touching <see cref="Enabled"/>
    /// — call before a fresh measurement pass so an earlier pass's numbers (on this thread; see the
    /// class doc's [ThreadStatic] note) can never leak into this one's totals.</summary>
    public static void Reset()
    {
        BboxScanTicks = 0;
        CollectPaintedTicks = 0;
        PaletteTicks = 0;
        ClassifyAndPaintTicks = 0;
        PackAndHeaderTicks = 0;
        LzwTicks = 0;
        LutFillTicks = 0;
        LutFillCount = 0;
        LutLookupCount = 0;
    }

    /// <summary>Sum of every bucket, in <see cref="Stopwatch"/> ticks — the denominator a caller
    /// divides each bucket by to get a percentage. Deliberately the SUM of the instrumented stages,
    /// not an independently measured wall-clock total: the gap between this and a real wall-clock
    /// total around the whole AddFrame call is exactly the un-instrumented glue (bookkeeping
    /// branches, the keyframe-interval check, method call overhead itself) that the workstream's own
    /// brief did not ask to be its own bucket.</summary>
    public static long TotalTicks =>
        BboxScanTicks + CollectPaintedTicks + PaletteTicks + ClassifyAndPaintTicks + PackAndHeaderTicks + LzwTicks;
}
