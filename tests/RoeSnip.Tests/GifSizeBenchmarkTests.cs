using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media.Imaging;
using RoeSnip.Imaging;
using RoeSnip.Recording;
using RoeSnip.Recording.Gif;
using Xunit;
using Xunit.Abstractions;

namespace RoeSnip.Tests;

/// <summary>PERMANENT benchmark (not a correctness test): measures GifEncoder's output
/// bytes/second on three deterministic synthetic 10-second, 640x400, 50fps sequences that stand
/// in for the recording content shapes that matter in practice — a mostly-static UI, a scrolling
/// text panel, and a video-like worst case. All three are generated with fixed formulas/seeded
/// xorshift only (no <see cref="Random"/>, no wall-clock), so results are reproducible run to run
/// and only change when GifEncoder's encoding actually changes.
///
/// Each test appends one line PER MEASURED PRESET to a scratch results file (path in
/// <see cref="ResultsFilePath"/>), tagged with the run label from the ROESNIP_BENCH_LABEL
/// environment variable, so a caller can diff bytes/sec across encoder changes by running this
/// suite twice with two labels.
///
/// Regression gates: measured post-overhaul throughput (Quality preset, byte-identical to the
/// pre-preset defaults) was static=894.4, scroll=173279.0, noise=4293334.0 bytes/sec. Static and
/// scroll thresholds below are set at ~2x the measured value (headroom for machine-to-machine
/// variance while still catching a real regression back towards the old ~865/~412k baseline or
/// worse). Noise is capped at ~1.5x its measured value — full-canvas random noise is the accepted
/// GIF-format worst case (nothing compresses, LZW output approaches the theoretical ceiling), so
/// its margin is tighter but still allows normal jitter. Static is measured under Quality only —
/// the calibration sweep behind <see cref="GifSizePresets"/> found it preset-invariant (no swept
/// tolerance/palette/motion-floor knob moved static's bytes at all), so a Balanced/Compact rerun
/// would only burn CI time re-confirming the same number. Scroll and Noise additionally gate
/// Balanced/Compact as a FRACTION of the same run's own Quality measurement (not a second fixed
/// threshold) — see <see cref="AssertPresetBudget"/> — so the ratio gate tracks whatever the
/// machine's Quality baseline actually was, rather than assuming it matches the number above.</summary>
public class GifSizeBenchmarkTests
{
    private const int Width = 640;
    private const int Height = 400;
    private const int Fps = 50;
    private const int DurationSeconds = 10;
    private const int FrameCount = Fps * DurationSeconds; // 500
    private const long TicksPerSecond = 1000; // 1 tick == 1 ms
    private const long FrameIntervalTicks = TicksPerSecond / Fps; // 20 ticks == 20ms

    // Gating thresholds (bytes/sec), ~2x measured for static/scroll and ~1.5x measured for noise
    // (see class doc comment for the measured baseline these were derived from). Apply to the
    // Quality preset only — see AssertPresetBudget for Balanced/Compact's ratio-based gates.
    private const double StaticThresholdBytesPerSec = 2_000d;
    private const double ScrollThresholdBytesPerSec = 350_000d;
    private const double NoiseThresholdBytesPerSec = 6_500_000d;

    // Balanced/Compact size budgets, as a fraction of the SAME run's measured Quality bytes (not
    // an independent fixed threshold) — see the spec these were commissioned against: Balanced
    // must land at <=70% of Quality, Compact at <=55%, on both scroll and noise content.
    private const double BalancedMaxFraction = 0.70;
    private const double CompactMaxFraction = 0.55;

    // Compact's motion-floor tuning constraint (see GifSizePresets.ForPreset's doc comment): on
    // scroll content, Compact's emitted frame count must not collapse below 5fps effective (>=50
    // frames emitted over this benchmark's 10s take) — the size reduction has to come from fewer
    // bytes per frame and a longer-but-still-live emit cadence, not from the take degrading into a
    // slideshow.
    private const int CompactScrollMinFrameCount = 50;

    private static readonly string ResultsFilePath = Path.Combine(
        @"C:\Users\Roelof\AppData\Local\Temp\claude\E--GitHub-RoeLite\75d3a910-4e39-42fa-816c-534f29ff40ed\scratchpad",
        "gif-bench.txt");

    private readonly ITestOutputHelper _output;

    public GifSizeBenchmarkTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static string TempGifPath(string name) =>
        Path.Combine(Path.GetTempPath(), $"gifbench_{name}_{Guid.NewGuid():N}.gif");

    private double RecordAndReport(string name, long bytes)
    {
        double bytesPerSec = bytes / (double)DurationSeconds;
        string line = $"{name}: {bytes} bytes over {DurationSeconds}s = {bytesPerSec:F1} bytes/sec";
        _output.WriteLine(line);

        string? label = Environment.GetEnvironmentVariable("ROESNIP_BENCH_LABEL");
        if (string.IsNullOrEmpty(label))
        {
            label = "unlabeled";
        }

        Directory.CreateDirectory(Path.GetDirectoryName(ResultsFilePath)!);
        File.AppendAllText(ResultsFilePath, $"[{label}] {line}{Environment.NewLine}");
        return bytesPerSec;
    }

    /// <summary>Encodes <paramref name="frameAt"/> across the full <see cref="FrameCount"/>
    /// sequence with the given (or default/Quality) <paramref name="options"/> and returns both the
    /// resulting file size and the actual number of GIF frames a real decoder sees — the latter is
    /// how <see cref="CompactScrollMinFrameCount"/> is checked without a second, separate decode
    /// pass. Decode-verifies through the same real WPF decoder end users' GIF viewers ultimately
    /// rely on (frame count > 1 is a basic sanity check that this genuinely is the multi-frame
    /// animation the encoder was asked to produce, not just "didn't throw") — every preset variant
    /// goes through this same check, not just the default/Quality one.</summary>
    private static (long Bytes, int EmittedFrameCount) EncodeAndMeasure(
        string name, Func<int, SdrImage> frameAt, GifEncoderOptions? options = null)
    {
        string path = TempGifPath(name);
        try
        {
            var encoder = GifEncoder.Create(path, Width, Height, timestampTicksPerSecond: TicksPerSecond, options: options);
            for (int i = 0; i < FrameCount; i++)
            {
                encoder.AddFrame(frameAt(i), i * FrameIntervalTicks);
            }
            encoder.FinalizeAndClose(FrameCount * FrameIntervalTicks);
            long length = new FileInfo(path).Length;

            int decodedFrameCount;
            using (var stream = File.OpenRead(path))
            {
                var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                decodedFrameCount = decoder.Frames.Count;
                if (decodedFrameCount <= 1)
                {
                    throw new InvalidOperationException(
                        $"benchmark '{name}' produced a GIF with only {decodedFrameCount} decodable frame(s); expected a multi-frame animation.");
                }
            }

            return (length, decodedFrameCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private readonly record struct PresetMeasurement(long Bytes, double BytesPerSec, int EmittedFrameCount);

    /// <summary>Encodes one content generator under all three presets, reporting each as its own
    /// bench line (Quality keeps the plain "{contentLabel}" line historical tooling already parses;
    /// Balanced/Compact get "{contentLabel}-balanced"/"-compact"). Quality is measured exactly once
    /// here and its bytes are what <see cref="AssertPresetBudget"/> compares the other two against
    /// — reusing this one pass rather than each gated assertion re-encoding its own Quality
    /// reference keeps the multiplied encode work (three presets, not one) from tripling again.</summary>
    private Dictionary<GifSizePreset, PresetMeasurement> MeasureAcrossPresets(string contentLabel, Func<int, SdrImage> frameAt)
    {
        var results = new Dictionary<GifSizePreset, PresetMeasurement>();
        foreach (var preset in new[] { GifSizePreset.Quality, GifSizePreset.Balanced, GifSizePreset.Compact })
        {
            string label = preset == GifSizePreset.Quality ? contentLabel : $"{contentLabel}-{preset.ToString().ToLowerInvariant()}";
            var (bytes, frameCount) = EncodeAndMeasure($"{contentLabel}_{preset}", frameAt, GifSizePresets.ForPreset(preset));
            double bytesPerSec = RecordAndReport(label, bytes);
            results[preset] = new PresetMeasurement(bytes, bytesPerSec, frameCount);
        }
        return results;
    }

    /// <summary>Asserts <paramref name="preset"/>'s measured bytes are at most
    /// <paramref name="maxFraction"/> of the SAME run's Quality bytes (see
    /// <see cref="MeasureAcrossPresets"/>) — a relative gate, not a second fixed threshold, so it
    /// tracks whatever this machine's own Quality baseline actually was.</summary>
    private static void AssertPresetBudget(
        string contentLabel, IReadOnlyDictionary<GifSizePreset, PresetMeasurement> results, GifSizePreset preset, double maxFraction)
    {
        var quality = results[GifSizePreset.Quality];
        var candidate = results[preset];
        double actualFraction = candidate.Bytes / (double)quality.Bytes;
        Assert.True(
            candidate.Bytes <= quality.Bytes * maxFraction,
            $"{contentLabel}-{preset.ToString().ToLowerInvariant()}: {candidate.Bytes} bytes is {actualFraction:P1} of " +
            $"Quality's {quality.Bytes} bytes; budget is <={maxFraction:P0}");
    }

    // ---- Deterministic pixel setters, shared by all three scenarios ----

    private static void SetPixel(byte[] pixels, int width, int x, int y, byte b, byte g, byte r)
    {
        int i = (y * width + x) * 4;
        pixels[i + 0] = b;
        pixels[i + 1] = g;
        pixels[i + 2] = r;
        pixels[i + 3] = 255;
    }

    private static void FillRect(byte[] pixels, int width, int height, int left, int top, int w, int h, byte b, byte g, byte r)
    {
        int right = Math.Min(left + w, width);
        int bottom = Math.Min(top + h, height);
        for (int y = Math.Max(0, top); y < bottom; y++)
        {
            for (int x = Math.Max(0, left); x < right; x++)
            {
                SetPixel(pixels, width, x, y, b, g, r);
            }
        }
    }

    /// <summary>A UI-like flat background: a title bar band, a body fill, and a grid of small
    /// "glyph block" rectangles (stand-ins for rendered text) at fixed positions — all constant
    /// across frames except a 16x16 "cursor" block that steps to a new position 4 times per
    /// second (every 12.5 frames at 50fps, i.e. every 13th/12th frame alternating).</summary>
    private static SdrImage StaticUiFrame(int frameIndex)
    {
        var pixels = new byte[Width * 4 * Height];

        // Body background.
        FillRect(pixels, Width, Height, 0, 0, Width, Height, 40, 40, 40);
        // Title bar.
        FillRect(pixels, Width, Height, 0, 0, Width, 28, 60, 60, 60);

        // A grid of glyph-like blocks standing in for static text.
        for (int row = 0; row < 12; row++)
        {
            for (int col = 0; col < 30; col++)
            {
                int gx = 8 + col * 20;
                int gy = 40 + row * 28;
                if (gx + 12 > Width || gy + 10 > Height)
                {
                    continue;
                }
                bool glyphOn = ((row * 31 + col * 17) % 5) != 0; // fixed sparse pattern, deterministic
                if (glyphOn)
                {
                    FillRect(pixels, Width, Height, gx, gy, 12, 10, 210, 210, 210);
                }
            }
        }

        // Cursor block moving 4 times/second: position updates in steps of Fps/4 frames (12.5 ->
        // integer step schedule below keeps exactly 4 moves/sec over any 1-second window).
        int stepIndex = (frameIndex * 4) / Fps; // increments 4 times per 50 frames
        int cursorX = 20 + (stepIndex % 30) * 20;
        int cursorY = 40 + ((stepIndex / 30) % 12) * 28;
        FillRect(pixels, Width, Height, cursorX, cursorY, 16, 16, 30, 200, 255);

        return new SdrImage(Width, Height, pixels);
    }

    /// <summary>A 640x200 band of text-like rows scrolling upward 4px/frame: each row is a solid
    /// background stripe with a few "glyph" segments in 2-3 flat colors plus a handful of
    /// anti-aliased-looking gray shades along glyph edges (fixed per-row/col formula, not random),
    /// wrapped vertically so the content never runs out. The area outside the band is a static
    /// background — only the band itself changes frame to frame.</summary>
    private static SdrImage ScrollFrame(int frameIndex)
    {
        var pixels = new byte[Width * 4 * Height];
        FillRect(pixels, Width, Height, 0, 0, Width, Height, 24, 24, 24);

        const int bandTop = 100;
        const int bandHeight = 200;
        const int rowHeight = 16;
        const int scrollPxPerFrame = 4;
        int scrollOffset = frameIndex * scrollPxPerFrame;

        for (int by = 0; by < bandHeight; by++)
        {
            int y = bandTop + by;
            int contentY = by + scrollOffset; // scrolls upward as frames advance
            int rowIndex = contentY / rowHeight;
            int rowLocalY = contentY % rowHeight;

            for (int x = 0; x < Width; x++)
            {
                int col = x / 6; // 6px-wide "glyph cell"
                int cellLocalX = x % 6;

                // Deterministic per-row/col pattern of glyph presence.
                bool glyphCell = ((rowIndex * 13 + col * 7) % 4) == 0;

                byte b, g, r;
                if (!glyphCell || rowLocalY < 2 || rowLocalY > 12)
                {
                    // Row background, alternating two flat shades per row for a "text line" feel.
                    if ((rowIndex % 2) == 0)
                    {
                        b = 18; g = 18; r = 18;
                    }
                    else
                    {
                        b = 22; g = 22; r = 22;
                    }
                }
                else if (cellLocalX == 0 || cellLocalX == 5)
                {
                    // Anti-aliased-looking edge gray shades (a few fixed shades, not random).
                    int shadeIdx = (rowIndex + col + cellLocalX) % 4;
                    byte shade = (byte)(60 + shadeIdx * 30);
                    b = shade; g = shade; r = shade;
                }
                else
                {
                    // Glyph fill: 2-3 flat colors selected deterministically per column/row.
                    int colorIdx = (rowIndex + col) % 3;
                    (b, g, r) = colorIdx switch
                    {
                        0 => ((byte)230, (byte)230, (byte)230),
                        1 => ((byte)120, (byte)200, (byte)255),
                        _ => ((byte)255, (byte)200, (byte)120),
                    };
                }

                SetPixel(pixels, Width, x, y, b, g, r);
            }
        }

        return new SdrImage(Width, Height, pixels);
    }

    /// <summary>Deterministic xorshift32 PRNG (fixed seed) — never <see cref="Random"/> — used to
    /// regenerate full-canvas noise every frame, the worst case for a diff/LZW-based encoder since
    /// nothing repeats between frames and nothing compresses well within one.</summary>
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
        // Seed varies deterministically by frame index so every frame's noise is independent of
        // the others, without ever reading wall-clock time or an unseeded Random.
        uint rng = 0x9E3779B9u ^ (uint)(frameIndex * 2654435761u + 1);
        if (rng == 0)
        {
            rng = 1; // xorshift32 has a fixed point at 0
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

    [Fact]
    public void Static_UiLikeContent_BytesPerSecond()
    {
        // Quality only — see class doc comment for why static is preset-invariant per the sweep.
        var (bytes, _) = EncodeAndMeasure("static", StaticUiFrame, GifSizePresets.ForPreset(GifSizePreset.Quality));
        double bytesPerSec = RecordAndReport("static", bytes);
        Assert.True(bytes > 0);
        Assert.True(
            bytesPerSec < StaticThresholdBytesPerSec,
            $"static throughput regressed: {bytesPerSec:F1} bytes/sec >= {StaticThresholdBytesPerSec:F1} bytes/sec threshold");
    }

    [Fact]
    public void Scroll_TextLikeContent_BytesPerSecond()
    {
        var results = MeasureAcrossPresets("scroll", ScrollFrame);

        // Existing absolute gate, unchanged, applies to Quality (byte-identical to the pre-preset
        // default GifEncoderOptions()).
        var quality = results[GifSizePreset.Quality];
        Assert.True(quality.Bytes > 0);
        Assert.True(
            quality.BytesPerSec < ScrollThresholdBytesPerSec,
            $"scroll throughput regressed: {quality.BytesPerSec:F1} bytes/sec >= {ScrollThresholdBytesPerSec:F1} bytes/sec threshold");

        AssertPresetBudget("scroll", results, GifSizePreset.Balanced, BalancedMaxFraction);
        AssertPresetBudget("scroll", results, GifSizePreset.Compact, CompactMaxFraction);

        var compact = results[GifSizePreset.Compact];
        Assert.True(
            compact.EmittedFrameCount >= CompactScrollMinFrameCount,
            $"scroll-compact effective frame rate collapsed: {compact.EmittedFrameCount} frames emitted over {DurationSeconds}s " +
            $"(< {CompactScrollMinFrameCount}, i.e. < 5fps effective)");
    }

    [Fact]
    public void Noise_VideoLikeWorstCase_BytesPerSecond()
    {
        var results = MeasureAcrossPresets("noise", NoiseFrame);

        // Existing absolute gate, unchanged, applies to Quality (byte-identical to the pre-preset
        // default GifEncoderOptions()).
        var quality = results[GifSizePreset.Quality];
        Assert.True(quality.Bytes > 0);
        Assert.True(
            quality.BytesPerSec < NoiseThresholdBytesPerSec,
            $"noise throughput regressed: {quality.BytesPerSec:F1} bytes/sec >= {NoiseThresholdBytesPerSec:F1} bytes/sec threshold");

        AssertPresetBudget("noise", results, GifSizePreset.Balanced, BalancedMaxFraction);
        AssertPresetBudget("noise", results, GifSizePreset.Compact, CompactMaxFraction);
    }
}
