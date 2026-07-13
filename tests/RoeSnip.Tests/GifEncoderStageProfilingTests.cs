using System;
using System.IO;
using RoeSnip.Imaging;
using RoeSnip.Recording;
using RoeSnip.Core.Recording.Gif;
using Xunit;
using Xunit.Abstractions;

namespace RoeSnip.Tests;

/// <summary>DIAGNOSTIC, not a correctness or regression gate: measures the wall-time split across
/// GifEncoder's per-frame pipeline stages (<see cref="GifEncoderStageTimings"/>) on the same kind of
/// full-motion content GifSizeBenchmarkTests' NoiseFrame exercises (a worst case for a diff/LZW-based
/// encoder — nothing repeats between frames), at the two configurations the GIF software-encode CPU
/// workstream's own brief measured process CPU against: Quality@25fps and Max@50fps, both at a
/// 640x420 canvas. Exists purely to answer "where does the CPU actually go" BEFORE attacking any one
/// stage — see this class's own test bodies for the reported breakdown, and the workstream's commit
/// history for what changed as a result.
///
/// No byte-count or ratio assertions here (that is GifSizeBenchmarkTests' job); the only assertions
/// are sanity checks that every stage was actually reached (nonzero) and that the instrumented total
/// is a plausible fraction of the wall-clock time actually spent inside AddFrame, so a future change
/// that accidentally stops timing a stage (e.g. a bucket permanently zero) fails loudly here instead
/// of just quietly under-reporting.</summary>
public class GifEncoderStageProfilingTests
{
    private const int Width = 640;
    private const int Height = 420;
    private const long TicksPerSecond = 1000; // 1 tick == 1 ms, matching GifSizeBenchmarkTests' clock

    private readonly ITestOutputHelper _output;

    public GifEncoderStageProfilingTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>Same xorshift32 full-canvas noise generator as GifSizeBenchmarkTests.NoiseFrame
    /// (deterministic, no <see cref="Random"/>) — duplicated rather than shared across test classes
    /// since it's a five-line pure function and this class has no other dependency on that one's
    /// internals.</summary>
    private static uint XorShift32(uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state;
    }

    private static SdrImage NoiseFrame(int frameIndex)
    {
        var pixels = new byte[Width * 4 * Height];
        uint rng = 0x9E3779B9u ^ (uint)(frameIndex * 2654435761u + 1);
        if (rng == 0)
        {
            rng = 1;
        }
        for (int i = 0; i < pixels.Length; i += 4)
        {
            rng = XorShift32(rng);
            pixels[i + 0] = (byte)rng;
            pixels[i + 1] = (byte)(rng >> 8);
            pixels[i + 2] = (byte)(rng >> 16);
            pixels[i + 3] = 255;
        }
        return new SdrImage(Width, Height, pixels);
    }

    private readonly record struct StageBreakdown(
        double TotalMs, double WallMs, int FrameCount,
        double BboxScanPct, double CollectPaintedPct, double PalettePct, double ClassifyAndPaintPct, double PackAndHeaderPct, double LzwPct);

    private StageBreakdown Profile(string label, GifSizePreset preset, int fps, int durationSeconds)
    {
        var options = GifSizePresets.ForPreset(preset);
        int frameCount = fps * durationSeconds;
        long frameIntervalTicks = TicksPerSecond / fps;
        string path = Path.Combine(Path.GetTempPath(), $"gifprofile_{label}_{Guid.NewGuid():N}.gif");

        GifEncoderStageTimings.Reset();
        GifEncoderStageTimings.Enabled = true;
        var wallClock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var encoder = GifEncoder.Create(path, Width, Height, timestampTicksPerSecond: TicksPerSecond, options: options);
            for (int i = 0; i < frameCount; i++)
            {
                long tick = i * frameIntervalTicks;
                encoder.AddFrame(NoiseFrame(i), tick);
            }
            encoder.FinalizeAndClose(frameCount * frameIntervalTicks);
        }
        finally
        {
            wallClock.Stop();
            GifEncoderStageTimings.Enabled = false;
            File.Delete(path);
        }

        long totalTicks = GifEncoderStageTimings.TotalTicks;
        double totalMs = totalTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        double Pct(long bucket) => totalTicks == 0 ? 0 : bucket * 100.0 / totalTicks;

        var breakdown = new StageBreakdown(
            TotalMs: totalMs,
            WallMs: wallClock.Elapsed.TotalMilliseconds,
            FrameCount: frameCount,
            BboxScanPct: Pct(GifEncoderStageTimings.BboxScanTicks),
            CollectPaintedPct: Pct(GifEncoderStageTimings.CollectPaintedTicks),
            PalettePct: Pct(GifEncoderStageTimings.PaletteTicks),
            ClassifyAndPaintPct: Pct(GifEncoderStageTimings.ClassifyAndPaintTicks),
            PackAndHeaderPct: Pct(GifEncoderStageTimings.PackAndHeaderTicks),
            LzwPct: Pct(GifEncoderStageTimings.LzwTicks));

        _output.WriteLine(
            $"[{label}] {frameCount} frames, wall={breakdown.WallMs:F1}ms, instrumented-total={breakdown.TotalMs:F1}ms " +
            $"({breakdown.TotalMs / breakdown.WallMs:P1} of wall) -- " +
            $"bboxScan={breakdown.BboxScanPct:F1}% collectPainted={breakdown.CollectPaintedPct:F1}% " +
            $"octree/LUT={breakdown.PalettePct:F1}% classifyAndPaint={breakdown.ClassifyAndPaintPct:F1}% " +
            $"packAndHeader={breakdown.PackAndHeaderPct:F1}% lzw={breakdown.LzwPct:F1}%");

        // Drill-down INSIDE classifyAndPaint: how much of ITS time is GifNearestColorLut's
        // cache-miss bucket fill (a brute-force scan over the palette), and what fraction of all
        // lut.Lookup calls actually missed the cache this run.
        double lutFillMs = GifEncoderStageTimings.LutFillTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        double lutFillPctOfClassify = GifEncoderStageTimings.ClassifyAndPaintTicks <= 0 ? 0 : GifEncoderStageTimings.LutFillTicks * 100.0 / GifEncoderStageTimings.ClassifyAndPaintTicks;
        double missRate = GifEncoderStageTimings.LutLookupCount == 0 ? 0 : GifEncoderStageTimings.LutFillCount * 100.0 / GifEncoderStageTimings.LutLookupCount;
        _output.WriteLine(
            $"[{label}] LUT drill-down: fillTime={lutFillMs:F1}ms ({lutFillPctOfClassify:F1}% of classifyAndPaint's own time), " +
            $"lookups={GifEncoderStageTimings.LutLookupCount}, fills={GifEncoderStageTimings.LutFillCount} (miss rate {missRate:F1}%)");

        return breakdown;
    }

    [Fact]
    public void Quality_25fps_FullMotion_StageBreakdown()
    {
        var b = Profile("quality25", GifSizePreset.Quality, fps: 25, durationSeconds: 5);
        AssertSane(b);
    }

    [Fact]
    public void Max_50fps_FullMotion_StageBreakdown()
    {
        var b = Profile("max50", GifSizePreset.Max, fps: 50, durationSeconds: 5);
        AssertSane(b);
    }

    /// <summary>Every worst-case-content frame at Quality/Max changes and emits (noise never
    /// stabilizes within tolerance), so every stage should have done real, nonzero work, and the sum
    /// of the six instrumented buckets should account for the large majority of AddFrame's own wall
    /// time (the remainder being un-instrumented glue — see GifEncoderStageTimings' own doc comment).
    /// A bucket stuck at exactly zero, or an instrumented total wildly exceeding wall time (a timing
    /// bug, e.g. double-counting), fails loudly here rather than silently mis-reporting a future
    /// optimization's before/after comparison.</summary>
    private static void AssertSane(StageBreakdown b)
    {
        Assert.True(b.FrameCount > 0);
        Assert.True(b.TotalMs > 0, "no stage recorded any time at all — instrumentation is not wired up");
        Assert.True(b.BboxScanPct > 0, "bbox scan bucket never recorded time");
        Assert.True(b.CollectPaintedPct > 0, "collect-painted bucket never recorded time");
        Assert.True(b.PalettePct > 0, "octree/LUT (palette) bucket never recorded time");
        Assert.True(b.ClassifyAndPaintPct > 0, "ClassifyAndPaint bucket never recorded time");
        Assert.True(b.PackAndHeaderPct > 0, "pack/header bucket never recorded time");
        Assert.True(b.LzwPct > 0, "LZW bucket never recorded time");
        // instrumented total must be a plausible fraction of real wall time: at least 20% (leaves
        // generous room for JIT warmup/GC/test-harness overhead on the very first profiling run in
        // a process) and never more than 100% plus a small timer-granularity slack.
        Assert.True(b.TotalMs <= b.WallMs * 1.05,
            $"instrumented total ({b.TotalMs:F1}ms) exceeds wall time ({b.WallMs:F1}ms) by more than timer slack — likely double-counted timing");
        Assert.True(b.TotalMs >= b.WallMs * 0.20,
            $"instrumented total ({b.TotalMs:F1}ms) is implausibly small next to wall time ({b.WallMs:F1}ms) — a stage may have stopped being timed");
    }

    /// <summary>Raw production-path wall-clock time — <see cref="GifEncoderStageTimings.Enabled"/> is
    /// deliberately left false/untouched here, unlike <see cref="Profile"/> above. The per-stage
    /// instrumentation is cheap when off (a single false branch per stage per frame — see that
    /// class's own doc comment) but NOT free when on: in particular, <c>GifNearestColorLut.Lookup</c>
    /// increments a [ThreadStatic] counter on every call (hundreds of millions of times across a
    /// full-motion take), and thread-local storage access has real per-access cost that measurably
    /// distorts wall-clock comparisons at that call frequency — observed directly while calibrating
    /// this workstream's own before/after numbers. This method is the one this class's own Facts use
    /// to report the number that actually matters: encode wall time with zero instrumentation
    /// overhead, exactly what a real recording take pays.</summary>
    private static double CleanWallClockMs(GifSizePreset preset, int fps, int durationSeconds)
    {
        var options = GifSizePresets.ForPreset(preset);
        int frameCount = fps * durationSeconds;
        long frameIntervalTicks = TicksPerSecond / fps;
        string path = Path.Combine(Path.GetTempPath(), $"gifcleanwall_{Guid.NewGuid():N}.gif");

        var wallClock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var encoder = GifEncoder.Create(path, Width, Height, timestampTicksPerSecond: TicksPerSecond, options: options);
            for (int i = 0; i < frameCount; i++)
            {
                long tick = i * frameIntervalTicks;
                encoder.AddFrame(NoiseFrame(i), tick);
            }
            encoder.FinalizeAndClose(frameCount * frameIntervalTicks);
        }
        finally
        {
            wallClock.Stop();
            File.Delete(path);
        }
        return wallClock.Elapsed.TotalMilliseconds;
    }

    [Fact]
    public void Quality_25fps_FullMotion_CleanWallClock()
    {
        double ms = CleanWallClockMs(GifSizePreset.Quality, fps: 25, durationSeconds: 5);
        _output.WriteLine($"[quality25] clean (uninstrumented) wall time for 125 frames: {ms:F1}ms");
        Assert.True(ms > 0);
    }

    [Fact]
    public void Max_50fps_FullMotion_CleanWallClock()
    {
        double ms = CleanWallClockMs(GifSizePreset.Max, fps: 50, durationSeconds: 5);
        _output.WriteLine($"[max50] clean (uninstrumented) wall time for 250 frames: {ms:F1}ms");
        Assert.True(ms > 0);
    }
}
