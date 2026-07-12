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
/// bytes/second on three deterministic synthetic 10-second, 640x400 sequences that stand in for
/// the recording content shapes that matter in practice — a mostly-static UI, a scrolling text
/// panel, and a video-like worst case. All three are generated with fixed formulas/seeded xorshift
/// only (no <see cref="Random"/>, no wall-clock), so results are reproducible run to run and only
/// change when GifEncoder's encoding actually changes.
///
/// REDESIGNED for the quality/framerate decoupling workstream: content generators are driven by
/// ELAPSED REAL TIME (milliseconds), not by a fixed frame index tied to one hardcoded fps, so the
/// same real-world motion (cursor moving 4x/sec, text scrolling 200px/sec) can be sampled at
/// whatever capture fps a test picks — frame interval is always 1000/fps ms on this benchmark's
/// ticksPerSecond=1000 clock (1 tick == 1ms), mirroring production where the capture-side schedule
/// throttle (RegionRecorder), not the encoder, is what turns a chosen fps into an actual sampling
/// interval. Noise is the one exception: each sampled frame is independently random regardless of
/// when it was sampled, so it stays keyed off the frame's sequence index rather than elapsed time.
///
/// Gates were rebuilt around the deleted motion-floor mechanism (see GifEncoderOptions' and
/// GifSizePresets' class docs): with no emit-rate shaping left in the encoder, EVERY candidate
/// frame that clears tolerance now emits, so bytes/sec on genuinely-moving content (scroll, noise)
/// is expected to land noticeably HIGHER than the old floor-throttled numbers — that is the whole
/// point of the workstream (framerate is now the user's capture-fps choice, not a side effect of
/// picking a quality tier) and is deliberately accepted here, not treated as a regression:
///   - Quality @ the new default 25fps: static/scroll/noise absolute ceilings, each set at ~1.7x
///     the measured new-behavior baseline. Measured once this class was rebuilt: static 894.4 B/s
///     (unchanged from the pre-decoupling figure — the cursor's fixed 4 steps/REAL-second means
///     capture fps barely matters for a near-static take), scroll 225,593.6 B/s (up from the old
///     floor-throttled ~173,279 B/s — every frame now emits instead of being held behind the
///     deleted large/huge-motion floors), and noise 8,587,672.4 B/s (roughly DOUBLE the old
///     floor-throttled ~4,293,334 B/s figure, consistent with the old ~12.5fps-effective huge-motion
///     floor being replaced by this benchmark's unthrottled 25fps capture rate — almost exactly 2x
///     the old effective rate, and the byte-rate follows suit almost exactly 2x).
///   - Quality-axis sanity at 25fps: on noise, Minimal &lt;= Compact &lt;= Balanced &lt;= Quality bytes
///     must hold (MaxPaletteColors/lossy-run/RenderScale are, together, a monotone lever on
///     color-rich content — see QUALITY/FPS EXPANSION below for the full picture, which SUPERSEDES
///     the old palette-only "Balanced/Compact are the ONLY surviving size lever" framing).
///   - Framerate axis at Quality on scroll: 10fps must produce meaningfully fewer bytes than 25fps
///     (&lt;=0.55x, linear-with-slack — 10/25 = 0.40x is the exact-linear expectation).
/// Every produced GIF is still decode-verified through the real WPF GifBitmapDecoder end users'
/// viewers ultimately rely on (see <see cref="EncodeAndMeasure"/>).
///
/// QUALITY/FPS EXPANSION WORKSTREAM (Balanced/Compact/Minimal's lossy run-extension + Minimal's
/// half-resolution render — see GifSizePresets' own class doc for what the levers ARE and why each
/// tier's numbers are what they are): the old scroll-only "every tier stays within 1.10x of Quality"
/// sanity band is GONE, superseded by
/// <see cref="QualityAxis_BlendedScrollNoiseRatio_MeetsCalibratedTargets"/> below, which measures
/// each tier's BLENDED (mean of scroll and noise) byte ratio against Quality/High and gates it
/// against a calibrated target band. Measured at 640x400@25fps (GifSizePresets' own doc comment
/// carries the identical table, since RecordingSizeEstimator's qFactors are set to these exact
/// numbers too):
///   tier      scroll ratio   noise ratio   blended   target band
///   Max       1.068          1.000         1.034     (>= 1.0, no band)
///   Quality   1.000          1.000         1.000     (reference)
///   Balanced  0.961          0.884         0.922     0.85-0.97
///   Compact   0.588          0.409         0.498     0.42-0.58
///   Minimal   0.371          0.007         0.189     &lt;= 0.2
/// Monotonicity Minimal &lt; Compact &lt; Balanced &lt; Quality &lt;= Max is asserted directly.
///
/// TIER SPREAD (2026-07-13, after the VISUAL RETUNE below): the retune left Compact's blended
/// ratio at 0.865, bunched within ~6% of Balanced's 0.922 — a visually constrained lossy threshold
/// only buys ~13% on this content mix, so the "Low" tier was a near-alias of "Medium". Compact
/// gained RenderScale 0.75 (see GifSizePresets' own TIER SPREAD doc paragraph for the full
/// reasoning) and the table row above is the re-measurement with that scale folded in — landing
/// right on the 0.5625 x 0.865 ~= 0.49 pixel-factor prediction. The visual gates below needed NO
/// structural change for this: <see cref="EncodeAndMeasureVisualMeanError"/> was already
/// scale-aware (it box-downsamples the gate's ground-truth source whenever the preset's
/// RenderScale &lt; 1.0 — the same convention Minimal's gate established), so Compact's scroll
/// meanErr simply re-measured 1.13 -> 1.26 at 0.75 scale, and its gradient meanErr 19.33 -> 19.26
/// (the lossy threshold, not the scale, dominates gradient error). The RETUNE paragraph below
/// predates this pass — its Compact byte figures are full-resolution history.
///
/// VISUAL RETUNE (2026-07-13): Balanced's and Compact's LossyRunThresholdSq (and, downstream, the
/// blended ratios/target bands above) were retuned from their original values (90,000/350,000,
/// blended 0.742/0.415) after a visual QA pass — a scratch harness, since deleted, encoded a
/// diagonal-RGB-gradient and a plasma/photo-like scene at every tier and the decoded, GifRawCompositor
/// -composited results were eyeballed directly — found the original Compact threshold's redmean-
/// squared distance ceiling was ~77% of the maximum possible (black-vs-white) distance, generous
/// enough to visibly STREAK smooth content: the lossy run tracker's anchor color stays fixed for the
/// whole run, so a threshold that generous let dozens of consecutive gradient pixels merge into one
/// flat band. See GifSizePresets.ForPreset's own per-tier comments for the exact retuned numbers and
/// reasoning. <see cref="Balanced_Compact_Scroll_VisuallySane_MeanErrorBounded"/> and
/// <see cref="Compact_Gradient_VisuallySane_MeanErrorBounded"/> below are the PERMANENT quality-floor
/// gates this retune pass added specifically so a future recalibration chasing a byte target can
/// never silently reintroduce streaking: composited-final-frame meanErr bounds (~2x margin over this
/// retune's own measured numbers) on Balanced/Compact scroll and on Compact's gradient scene.
///
/// <see cref="Minimal_Scroll_VisuallySane_MeanErrorBounded"/> is the "calibration didn't converge on
/// garbage" guard the mandate requires: Minimal is decoded and compared, mean-per-channel-error
/// bounded, against its OWN (box-downsampled) source — not pixel-exact, but recognizably the same
/// content.
///
/// DROPPED from the pre-decoupling version of this class (each row was earning its keep against a
/// mechanism that no longer exists):
///   - Per-tier STATIC rows (Balanced/Compact static measurements) — static was already measured
///     Quality-only before this redesign; still true, nothing changed there.
///   - The wide Max-preset budget band (MaxMinFraction/ScrollMaxMaxFraction/NoiseMaxMaxFraction) —
///     that band existed specifically to bound how much bigger Max could get from disabling the
///     motion floors relative to Quality's floor-throttled rate. With no floors left on ANY tier,
///     Max and Quality use an identical emit policy (every candidate frame emits) and only differ
///     in palette-reuse strictness, which the new blanket "within 1.10x of Quality" scroll check
///     and noise monotonicity check already cover without a Max-specific band.
///   - Compact's scroll effective-frame-rate floor (>=50 emitted frames/10s) — that gate existed to
///     catch a tier's motion floor collapsing emit cadence into a slideshow. No tier has a motion
///     floor anymore, so emitted frame count on scroll is bounded below by the capture fps itself,
///     not by anything a quality tier could still get wrong.</summary>
public class GifSizeBenchmarkTests
{
    private const int Width = 640;
    private const int Height = 400;
    private const int DurationSeconds = 10;
    private const long TicksPerSecond = 1000; // 1 tick == 1 ms

    // The new shipped default GIF capture rate (RecordingSizeEstimator.GifDefaultFps) — "Quality at
    // default fps" below measures exactly what a fresh install's first take actually produces.
    private static readonly int DefaultFps = RecordingSizeEstimator.GifDefaultFps;

    // Absolute ceilings (bytes/sec), Quality preset, DefaultFps — ~1.7x the measured new-behavior
    // baseline (894.4 / 225,593.6 / 8,587,672.4 B/s respectively; see class doc comment for the
    // measurement and why these numbers are intentionally far above the OLD floor-throttled
    // thresholds — frame rate is no longer throttled by the quality tier at all).
    private const double StaticThresholdBytesPerSec = 1_600d;
    private const double ScrollThresholdBytesPerSec = 384_000d;
    private const double NoiseThresholdBytesPerSec = 14_600_000d;

    // fps-axis linearity slack on scroll: exact-linear would be 10/25 = 0.40x; slack allows some
    // per-frame overhead (GCE/Image-Descriptor/LCT header bytes are roughly fixed per frame, so
    // fewer, larger deltas at low fps carry relatively more payload per header byte) without
    // masking a real regression back toward "no timing effect at all".
    private const double ScrollLowFpsMaxFraction = 0.55;

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

    /// <summary>Encodes <paramref name="frameAt"/> at <paramref name="fps"/> across
    /// <see cref="DurationSeconds"/> (frame interval == 1000/fps ticks on this benchmark's
    /// ticksPerSecond=1000 clock — production's capture-cadence rate control, reproduced here
    /// exactly instead of the encoder throttling anything itself) with the given (or default/
    /// Quality) <paramref name="options"/>, and returns both the resulting file size and the
    /// actual number of GIF frames a real decoder sees. Decode-verifies through the same real WPF
    /// decoder end users' GIF viewers ultimately rely on (frame count &gt; 1 is a basic sanity
    /// check that this genuinely is the multi-frame animation the encoder was asked to produce, not
    /// just "didn't throw") — every preset/fps variant goes through this same check.</summary>
    private static (long Bytes, int EmittedFrameCount) EncodeAndMeasure(
        string name, Func<int, long, SdrImage> frameAt, int fps, GifEncoderOptions? options = null)
    {
        if (TicksPerSecond % fps != 0)
        {
            throw new ArgumentException($"fps {fps} must evenly divide the {TicksPerSecond}-tick clock.", nameof(fps));
        }
        int frameCount = fps * DurationSeconds;
        long frameIntervalTicks = TicksPerSecond / fps;

        // Compact/Minimal (RenderScale < 1.0) are measured through the SAME downscaled render path
        // RecordingController's EncoderLoop actually uses in production — GifEncoder itself has no
        // notion of RenderScale (see that option's own doc comment: it is purely a pre-encode
        // downsample step), so reproducing it here (not just measuring a smaller palette at full
        // resolution) is what makes this benchmark's Compact/Minimal numbers mean anything.
        double scale = options?.RenderScale ?? 1.0;
        int canvasWidth = scale < 1.0 ? ScaledDim(Width, scale) : Width;
        int canvasHeight = scale < 1.0 ? ScaledDim(Height, scale) : Height;
        byte[]? scaledScratch = scale < 1.0 ? new byte[canvasWidth * 4 * canvasHeight] : null;

        string path = TempGifPath(name);
        try
        {
            var encoder = GifEncoder.Create(path, canvasWidth, canvasHeight, timestampTicksPerSecond: TicksPerSecond, options: options);
            for (int i = 0; i < frameCount; i++)
            {
                long tick = i * frameIntervalTicks;
                var frame = frameAt(i, tick);
                if (scale < 1.0)
                {
                    BoxDownsample(frame.Pixels, frame.Width, frame.Height, scaledScratch!, canvasWidth, canvasHeight);
                    frame = new SdrImage(canvasWidth, canvasHeight, scaledScratch!);
                }
                encoder.AddFrame(frame, tick);
            }
            encoder.FinalizeAndClose(frameCount * frameIntervalTicks);
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

    /// <summary>Same floor-to-even-after-scaling formula as RecordingController.ScaledCanvasDimension
    /// — duplicated here (test-only helper, no production dependency) since this benchmark needs to
    /// open its GifEncoder at the exact canvas size a real Minimal take would.</summary>
    private static int ScaledDim(int fullSize, double scale)
    {
        int scaled = (int)(fullSize * scale);
        return Math.Max(2, scaled - (scaled % 2));
    }

    /// <summary>Same box-filter formula as RecordingController.BoxDownsampleGifFrame — duplicated
    /// here for the same reason as <see cref="ScaledDim"/> above: this benchmark must reproduce the
    /// real downscaled render path (Compact 0.75 / Minimal 0.5, including 0.75's integer-snapped
    /// 1,1,2-pixel rect pattern — see the production method's own doc comment), not just measure a
    /// smaller palette at full resolution.</summary>
    private static void BoxDownsample(byte[] src, int srcWidth, int srcHeight, byte[] dst, int dstWidth, int dstHeight)
    {
        for (int dy = 0; dy < dstHeight; dy++)
        {
            int sy0 = dy * srcHeight / dstHeight;
            int sy1 = Math.Min(srcHeight, Math.Max(sy0 + 1, (dy + 1) * srcHeight / dstHeight));
            for (int dx = 0; dx < dstWidth; dx++)
            {
                int sx0 = dx * srcWidth / dstWidth;
                int sx1 = Math.Min(srcWidth, Math.Max(sx0 + 1, (dx + 1) * srcWidth / dstWidth));

                long sumB = 0, sumG = 0, sumR = 0;
                int count = 0;
                for (int sy = sy0; sy < sy1; sy++)
                {
                    int rowOffset = sy * srcWidth * 4;
                    for (int sx = sx0; sx < sx1; sx++)
                    {
                        int so = rowOffset + sx * 4;
                        sumB += src[so];
                        sumG += src[so + 1];
                        sumR += src[so + 2];
                        count++;
                    }
                }

                int doff = (dy * dstWidth + dx) * 4;
                dst[doff] = (byte)(sumB / count);
                dst[doff + 1] = (byte)(sumG / count);
                dst[doff + 2] = (byte)(sumR / count);
                dst[doff + 3] = 255;
            }
        }
    }

    private readonly record struct PresetMeasurement(long Bytes, double BytesPerSec, int EmittedFrameCount);

    /// <summary>Encodes one content generator, at one fps, under all five presets, reporting each
    /// as its own bench line (Quality keeps the plain "{contentLabel}" line historical tooling
    /// already parses; Max/Balanced/Compact/Minimal get
    /// "{contentLabel}-max"/"-balanced"/"-compact"/"-minimal"). Quality is measured exactly once
    /// here and its bytes are what the tier-sanity assertions below compare the other four against
    /// — reusing this one pass rather than each gated assertion re-encoding its own Quality
    /// reference keeps the multiplied encode work (five presets, not one) from quintupling again.</summary>
    private Dictionary<GifSizePreset, PresetMeasurement> MeasureAcrossPresets(string contentLabel, Func<int, long, SdrImage> frameAt, int fps)
    {
        var results = new Dictionary<GifSizePreset, PresetMeasurement>();
        foreach (var preset in new[] { GifSizePreset.Quality, GifSizePreset.Max, GifSizePreset.Balanced, GifSizePreset.Compact, GifSizePreset.Minimal })
        {
            string label = preset == GifSizePreset.Quality ? contentLabel : $"{contentLabel}-{preset.ToString().ToLowerInvariant()}";
            var (bytes, frameCount) = EncodeAndMeasure($"{contentLabel}_{preset}", frameAt, fps, GifSizePresets.ForPreset(preset));
            double bytesPerSec = RecordAndReport(label, bytes);
            results[preset] = new PresetMeasurement(bytes, bytesPerSec, frameCount);
        }
        return results;
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
    /// across frames except a 16x16 "cursor" block that steps to a new position 4 times per REAL
    /// second (every 250ms of <paramref name="elapsedMs"/>, independent of capture fps).</summary>
    private static SdrImage StaticUiFrame(long elapsedMs)
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

        // Cursor block moving 4 times/REAL second: one step every 250ms of elapsed time, so the
        // same real-world motion is sampled identically regardless of which fps a caller encodes at.
        long stepIndex = elapsedMs / 250;
        int cursorX = 20 + (int)(stepIndex % 30) * 20;
        int cursorY = 40 + (int)((stepIndex / 30) % 12) * 28;
        FillRect(pixels, Width, Height, cursorX, cursorY, 16, 16, 30, 200, 255);

        return new SdrImage(Width, Height, pixels);
    }

    /// <summary>A 640x200 band of text-like rows scrolling upward at a fixed 200px/REAL second
    /// (so encoding the same 10-second take at a lower fps samples the identical motion less often,
    /// rather than scrolling slower): each row is a solid background stripe with a few "glyph"
    /// segments in 2-3 flat colors plus a handful of anti-aliased-looking gray shades along glyph
    /// edges (fixed per-row/col formula, not random), wrapped vertically so the content never runs
    /// out. The area outside the band is a static background — only the band itself changes frame
    /// to frame.</summary>
    private static SdrImage ScrollFrame(long elapsedMs)
    {
        var pixels = new byte[Width * 4 * Height];
        FillRect(pixels, Width, Height, 0, 0, Width, Height, 24, 24, 24);

        const int bandTop = 100;
        const int bandHeight = 200;
        const int rowHeight = 16;
        const int scrollPxPerRealSecond = 200;
        int scrollOffset = (int)(elapsedMs * scrollPxPerRealSecond / 1000);

        for (int by = 0; by < bandHeight; by++)
        {
            int y = bandTop + by;
            int contentY = by + scrollOffset; // scrolls upward as elapsed time advances
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
    /// nothing repeats between frames and nothing compresses well within one. Keyed by the frame's
    /// SEQUENCE INDEX rather than elapsed time: unlike the cursor/scroll motion above, noise has no
    /// real-world timing to stay faithful to — each sampled frame is simply independent of every
    /// other, at any fps.</summary>
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
    public void Static_UiLikeContent_AtDefaultFps_BytesPerSecond()
    {
        // Quality only — static is preset-invariant (no swept tolerance/palette knob ever moved
        // its near-zero bytes at all, see class doc comment on what was dropped and why).
        var (bytes, _) = EncodeAndMeasure("static", (_, ms) => StaticUiFrame(ms), DefaultFps, GifSizePresets.ForPreset(GifSizePreset.Quality));
        double bytesPerSec = RecordAndReport("static", bytes);
        Assert.True(bytes > 0);
        Assert.True(
            bytesPerSec < StaticThresholdBytesPerSec,
            $"static throughput regressed: {bytesPerSec:F1} bytes/sec >= {StaticThresholdBytesPerSec:F1} bytes/sec threshold");
    }

    [Fact]
    public void Scroll_TextLikeContent_AtDefaultFps_BytesPerSecond()
    {
        // QUALITY/FPS EXPANSION WORKSTREAM: the old "every tier stays within 1.10x of Quality on
        // scroll" gate is GONE — that was true only while palette size was the tiers' one lever
        // (see GifSizePresets' class doc for the original finding), and is no longer true, ON
        // PURPOSE, now that Balanced/Compact/Minimal carry a real lossy run-extension threshold:
        // that lever DOES meaningfully shrink scroll/text content, which is the whole point of
        // adding it (see this class's own updated class doc for the calibrated numbers and where
        // the blended-ratio/monotonicity gates that replaced this band now live).
        var results = MeasureAcrossPresets("scroll", (_, ms) => ScrollFrame(ms), DefaultFps);

        var quality = results[GifSizePreset.Quality];
        Assert.True(quality.Bytes > 0);
        Assert.True(
            quality.BytesPerSec < ScrollThresholdBytesPerSec,
            $"scroll throughput regressed: {quality.BytesPerSec:F1} bytes/sec >= {ScrollThresholdBytesPerSec:F1} bytes/sec threshold");
    }

    [Fact]
    public void Noise_VideoLikeWorstCase_AtDefaultFps_BytesPerSecond()
    {
        var results = MeasureAcrossPresets("noise", (i, _) => NoiseFrame(i), DefaultFps);

        var quality = results[GifSizePreset.Quality];
        Assert.True(quality.Bytes > 0);
        Assert.True(
            quality.BytesPerSec < NoiseThresholdBytesPerSec,
            $"noise throughput regressed: {quality.BytesPerSec:F1} bytes/sec >= {NoiseThresholdBytesPerSec:F1} bytes/sec threshold");

        // MaxPaletteColors/lossy-run/RenderScale are, together, a monotone size lever on
        // color-rich content: Minimal <= Compact(64) <= Balanced(128) <= Quality(255) bytes must
        // hold (the fuller monotonicity+blended-ratio gate lives in
        // QualityAxis_BlendedScrollNoiseRatio_MeetsCalibratedTargets below; this is the cheap,
        // noise-only sanity check kept alongside the throughput ceiling above).
        var balanced = results[GifSizePreset.Balanced];
        var compact = results[GifSizePreset.Compact];
        var minimal = results[GifSizePreset.Minimal];
        Assert.True(minimal.Bytes <= compact.Bytes,
            $"noise-minimal ({minimal.Bytes} bytes) exceeded noise-compact ({compact.Bytes} bytes); the size levers are no longer monotone");
        Assert.True(compact.Bytes <= balanced.Bytes,
            $"noise-compact ({compact.Bytes} bytes) exceeded noise-balanced ({balanced.Bytes} bytes); the size levers are no longer monotone");
        Assert.True(balanced.Bytes <= quality.Bytes,
            $"noise-balanced ({balanced.Bytes} bytes) exceeded noise-quality ({quality.Bytes} bytes); the size levers are no longer monotone");
    }

    [Fact]
    public void Scroll_FpsAxis_LowerFpsProducesFewerBytes()
    {
        // Same real-world scroll motion (200px/REAL second — see ScrollFrame's doc comment),
        // sampled at two different capture rates. With no motion-floor throttling left in the
        // encoder, bytes/sec now tracks capture fps directly: this is the core behavior the
        // quality/framerate decoupling workstream exists to enable.
        const int lowFps = 10;
        var (bytesAtDefault, _) = EncodeAndMeasure("scroll_fps_default", (_, ms) => ScrollFrame(ms), DefaultFps, GifSizePresets.ForPreset(GifSizePreset.Quality));
        var (bytesAtLow, _) = EncodeAndMeasure("scroll_fps_low", (_, ms) => ScrollFrame(ms), lowFps, GifSizePresets.ForPreset(GifSizePreset.Quality));

        RecordAndReport($"scroll-fps{DefaultFps}", bytesAtDefault);
        RecordAndReport($"scroll-fps{lowFps}", bytesAtLow);

        double actualFraction = bytesAtLow / (double)bytesAtDefault;
        Assert.True(
            bytesAtLow <= bytesAtDefault * ScrollLowFpsMaxFraction,
            $"scroll@{lowFps}fps: {bytesAtLow} bytes is {actualFraction:P1} of scroll@{DefaultFps}fps's {bytesAtDefault} bytes; " +
            $"budget is <={ScrollLowFpsMaxFraction:P0} (exact-linear expectation is {(double)lowFps / DefaultFps:P0})");
    }

    // ---- Quality/fps expansion workstream: the calibration gate for Balanced/Compact/Minimal's
    // lossy-run/RenderScale levers, and Minimal's own "did calibration converge on garbage" guard.
    // See this class's own class doc comment for the measured ratio table these bands were tuned
    // against. ----

    // Blended-ratio target bands (mean of scroll,noise byte ratio vs. Quality/High), per the
    // calibration mandate: tune ONLY the lossy thresholds (and Minimal's palette, if truly needed)
    // until each tier's measured blended ratio lands inside its own band. RETUNED 2026-07-13 (was
    // 0.6-0.8 / 0.4-0.6) alongside GifSizePresets.ForPreset's LossyRunThresholdSq retune — see this
    // class's own VISUAL RETUNE doc-comment paragraph for why the bands moved so much closer to
    // 1.0: a visual streaking problem, not a byte-target miss, drove the retune. Compact's band
    // moved again the same day (was 0.75-0.92) for the TIER SPREAD pass — RenderScale 0.75 took its
    // measured blended ratio to 0.498, and the band is centered on that measurement with roughly
    // the same +-0.05-0.08 width every other band carries (wide enough to absorb encoder-side
    // drift, tight enough that losing the resolution lever, or doubling it, still fails loudly).
    private const double BalancedBlendedMin = 0.85, BalancedBlendedMax = 0.97;
    private const double CompactBlendedMin = 0.42, CompactBlendedMax = 0.58;
    private const double MinimalBlendedMax = 0.2;

    [Fact]
    public void QualityAxis_BlendedScrollNoiseRatio_MeetsCalibratedTargets()
    {
        var scroll = MeasureAcrossPresets("scroll_blend", (_, ms) => ScrollFrame(ms), DefaultFps);
        var noise = MeasureAcrossPresets("noise_blend", (i, _) => NoiseFrame(i), DefaultFps);

        double Blended(GifSizePreset preset)
        {
            double scrollRatio = scroll[preset].Bytes / (double)scroll[GifSizePreset.Quality].Bytes;
            double noiseRatio = noise[preset].Bytes / (double)noise[GifSizePreset.Quality].Bytes;
            double blended = (scrollRatio + noiseRatio) / 2.0;
            _output.WriteLine($"blended[{preset}]: scroll={scrollRatio:F3} noise={noiseRatio:F3} blended={blended:F3}");
            return blended;
        }

        double maxBlended = Blended(GifSizePreset.Max);
        double balancedBlended = Blended(GifSizePreset.Balanced);
        double compactBlended = Blended(GifSizePreset.Compact);
        double minimalBlended = Blended(GifSizePreset.Minimal);

        // Monotonicity: Minimal < Compact < Balanced < Quality(1.0) <= Max — see this class's own
        // doc comment for the exact measured values this run's numbers should track.
        Assert.True(minimalBlended < compactBlended,
            $"Minimal blended ratio ({minimalBlended:F3}) should be < Compact's ({compactBlended:F3})");
        Assert.True(compactBlended < balancedBlended,
            $"Compact blended ratio ({compactBlended:F3}) should be < Balanced's ({balancedBlended:F3})");
        Assert.True(balancedBlended < 1.0,
            $"Balanced blended ratio ({balancedBlended:F3}) should be < Quality's 1.0");
        Assert.True(maxBlended >= 1.0,
            $"Max blended ratio ({maxBlended:F3}) should be >= Quality's 1.0 (Max's stricter palette-reuse threshold never produces smaller output)");

        Assert.True(balancedBlended >= BalancedBlendedMin && balancedBlended <= BalancedBlendedMax,
            $"Balanced blended ratio {balancedBlended:F3} outside the calibrated {BalancedBlendedMin}-{BalancedBlendedMax} band");
        Assert.True(compactBlended >= CompactBlendedMin && compactBlended <= CompactBlendedMax,
            $"Compact blended ratio {compactBlended:F3} outside the calibrated {CompactBlendedMin}-{CompactBlendedMax} band");
        Assert.True(minimalBlended <= MinimalBlendedMax,
            $"Minimal blended ratio {minimalBlended:F3} exceeds the calibrated <={MinimalBlendedMax} target");
    }

    private const double MinimalVisualMeanErrorBound = 30.0;

    [Fact]
    public void Minimal_Scroll_VisuallySane_MeanErrorBounded()
    {
        // Calibration must never converge on illegible garbage: composite the actual Minimal-tier
        // GIF's frames using GIF's own "do not dispose" disposal rule (GifRawCompositor — see its
        // own doc comment for why this MUST be a from-scratch composite, not just
        // GifBitmapDecoder.Frames[last]: that gives one frame's own raw sub-rect alone, not the true
        // final displayed image) and verify the result stays within a loose mean-error bound of the
        // SAME (box-downsampled) source frame the encoder was actually asked to reproduce — not
        // pixel-exact (a lossy tier is expected to differ from its source), but recognizably the
        // same content, not noise. Measured mean-per-channel error at the calibrated Minimal
        // settings: 5.20 (well inside the 30 bound) — comfortably legible, not the ~40+ an earlier,
        // buggy version of this test (comparing against GifBitmapDecoder's uncomposited last frame
        // instead) mistakenly reported as a real fidelity problem.
        var options = GifSizePresets.ForPreset(GifSizePreset.Minimal);
        int frameCount = DefaultFps * DurationSeconds;
        long frameIntervalTicks = TicksPerSecond / DefaultFps;
        int canvasWidth = ScaledDim(Width, options.RenderScale);
        int canvasHeight = ScaledDim(Height, options.RenderScale);

        string path = TempGifPath("minimal_visual_sanity");
        try
        {
            var encoder = GifEncoder.Create(path, canvasWidth, canvasHeight, timestampTicksPerSecond: TicksPerSecond, options: options);
            var scratch = new byte[canvasWidth * 4 * canvasHeight];
            for (int i = 0; i < frameCount; i++)
            {
                long tick = i * frameIntervalTicks;
                var frame = ScrollFrame(tick);
                BoxDownsample(frame.Pixels, frame.Width, frame.Height, scratch, canvasWidth, canvasHeight);
                encoder.AddFrame(new SdrImage(canvasWidth, canvasHeight, scratch), tick);
            }
            encoder.FinalizeAndClose(frameCount * frameIntervalTicks);
            // 'scratch' still holds the LAST frame's downsampled source — nothing overwrites it
            // after the loop above, and AddFrame never retains a reference to the buffer it's
            // handed (see GifEncoder.AddFrame's own doc comment), so this is exactly the ground
            // truth the final GIF frame should be judged against.
            byte[] lastSourceDownsampled = scratch;

            byte[] decodedFinal = GifRawCompositor.CompositeFinalFrame(File.ReadAllBytes(path), canvasWidth, canvasHeight);

            double totalErr = 0;
            int pixelCount = canvasWidth * canvasHeight;
            for (int p = 0; p < pixelCount; p++)
            {
                int o = p * 4;
                totalErr += Math.Abs(decodedFinal[o] - lastSourceDownsampled[o])
                          + Math.Abs(decodedFinal[o + 1] - lastSourceDownsampled[o + 1])
                          + Math.Abs(decodedFinal[o + 2] - lastSourceDownsampled[o + 2]);
            }
            double meanErrPerChannel = totalErr / (pixelCount * 3.0);
            _output.WriteLine($"minimal scroll visual sanity: meanErrPerChannel={meanErrPerChannel:F2}");
            Assert.True(meanErrPerChannel < MinimalVisualMeanErrorBound,
                $"Minimal scroll output's mean per-channel error ({meanErrPerChannel:F2}) vs its own source exceeds the {MinimalVisualMeanErrorBound} sanity bound — calibration may have converged on garbage");
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- PERMANENT quality-floor gates, added by the 2026-07-13 visual retune pass (see this
    // class's own VISUAL RETUNE doc-comment paragraph for the streaking bug these exist to catch).
    // A byte-ratio target band alone (QualityAxis_BlendedScrollNoiseRatio_MeetsCalibratedTargets
    // above) cannot detect visible streaking — a heavily-merged run of pixels still compresses well,
    // which is exactly how the original 90,000/350,000 thresholds passed their own byte bands while
    // streaking smooth content. These two gates close that hole with composited-final-frame meanErr
    // bounds, so a future recalibration chasing a smaller byte number can never silently reintroduce
    // it. Deliberately a SHORT 3-second sub-benchmark (not this class's usual 10-second
    // DurationSeconds) — these two gates exist purely to bound visual error, not to measure byte
    // throughput, so a shorter clip is exactly as diagnostic while keeping total suite runtime sane. ----

    private const int VisualGateDurationSeconds = 3;

    // ~2-3x margin over the measured numbers (scroll: Balanced 0.94 at full resolution, Compact
    // 1.26 at its 0.75 RenderScale — the tier-spread pass re-measured Compact against its own
    // box-downsampled source, per EncodeAndMeasureVisualMeanError's scale-aware ground-truth
    // convention; the pre-scale full-resolution figure was 1.13). Both stay this low specifically
    // because scroll's flat, well-separated glyph colors barely engage the lossy run-extension
    // mechanism at ANY reasonable threshold; the bound exists to catch the mechanism going
    // aggressive again, not to allow room for it on this particular scene.
    private const double BalancedScrollVisualMeanErrorBound = 3.0;
    private const double CompactScrollVisualMeanErrorBound = 2.5;

    // ~2x margin over the measured number (Compact/gradient: 19.26 at 0.75 RenderScale — barely
    // moved from the full-resolution 19.33, since the lossy threshold, not the scale, dominates
    // gradient error).
    private const double CompactGradientVisualMeanErrorBound = 40.0;

    /// <summary>NEW scene added by the 2026-07-13 visual retune pass: a smooth diagonal RGB gradient
    /// (three sine waves, 120 degrees out of phase, along the x+y diagonal) that slowly translates
    /// over real time. Deliberately the lossy run-extension's worst case, unlike ScrollFrame's
    /// deliberately well-separated flat glyph colors (see that method's own doc comment): every
    /// adjacent pixel differs from its neighbor by only a few redmean-squared units, so a threshold
    /// generous enough to help noise-like content a lot is also generous enough for the lossy run
    /// tracker's FIXED anchor color (see GifDelta's own class doc on why the anchor never updates
    /// mid-run) to hold across dozens of pixels of real color drift, visibly smearing the gradient's
    /// smooth ramp into flat streaked bands. Used only by
    /// <see cref="Compact_Gradient_VisuallySane_MeanErrorBounded"/> below.</summary>
    private static SdrImage GradientFrame(long elapsedMs)
    {
        var pixels = new byte[Width * 4 * Height];
        const double speedPxPerRealSecond = 40.0;
        const double periodPx = 480.0;
        double offset = elapsedMs * speedPxPerRealSecond / 1000.0;

        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                double d = x + y + offset;
                double twoPiOverPeriod = 2.0 * Math.PI / periodPx;
                byte r = (byte)(128 + 127 * Math.Sin(d * twoPiOverPeriod));
                byte g = (byte)(128 + 127 * Math.Sin((d + periodPx / 3.0) * twoPiOverPeriod));
                byte b = (byte)(128 + 127 * Math.Sin((d + 2.0 * periodPx / 3.0) * twoPiOverPeriod));
                SetPixel(pixels, Width, x, y, b, g, r);
            }
        }

        return new SdrImage(Width, Height, pixels);
    }

    /// <summary>Shared plumbing behind both quality-floor gates below: encodes
    /// <paramref name="frameAt"/> at <paramref name="preset"/>, <see cref="DefaultFps"/>, for
    /// <see cref="VisualGateDurationSeconds"/>, decodes the true final composited frame
    /// (<see cref="GifRawCompositor"/> — see that class's own doc comment for why this, never
    /// <c>GifBitmapDecoder.Frames[last]</c> directly), and returns its mean-per-channel absolute
    /// error against that same final frame's own source pixels (box-downsampled first if the
    /// preset's RenderScale &lt; 1.0 — matches <see cref="Minimal_Scroll_VisuallySane_MeanErrorBounded"/>'s
    /// own ground-truth convention above).</summary>
    private static double EncodeAndMeasureVisualMeanError(string name, Func<long, SdrImage> frameAt, GifSizePreset preset)
    {
        var options = GifSizePresets.ForPreset(preset);
        int frameCount = DefaultFps * VisualGateDurationSeconds;
        long frameIntervalTicks = TicksPerSecond / DefaultFps;
        int canvasWidth = options.RenderScale < 1.0 ? ScaledDim(Width, options.RenderScale) : Width;
        int canvasHeight = options.RenderScale < 1.0 ? ScaledDim(Height, options.RenderScale) : Height;

        string path = TempGifPath(name);
        try
        {
            var encoder = GifEncoder.Create(path, canvasWidth, canvasHeight, timestampTicksPerSecond: TicksPerSecond, options: options);
            var scratch = new byte[canvasWidth * 4 * canvasHeight];
            for (int i = 0; i < frameCount; i++)
            {
                long tick = i * frameIntervalTicks;
                var frame = frameAt(tick);
                if (options.RenderScale < 1.0)
                {
                    BoxDownsample(frame.Pixels, frame.Width, frame.Height, scratch, canvasWidth, canvasHeight);
                }
                else
                {
                    Array.Copy(frame.Pixels, scratch, scratch.Length);
                }
                encoder.AddFrame(new SdrImage(canvasWidth, canvasHeight, scratch), tick);
            }
            encoder.FinalizeAndClose(frameCount * frameIntervalTicks);
            // 'scratch' still holds the LAST frame's (possibly downsampled) source — see
            // Minimal_Scroll_VisuallySane_MeanErrorBounded's own comment on why nothing overwrites it
            // after the loop and why AddFrame never retaining its buffer makes this safe.
            byte[] lastSource = scratch;

            byte[] decodedFinal = GifRawCompositor.CompositeFinalFrame(File.ReadAllBytes(path), canvasWidth, canvasHeight);

            double totalErr = 0;
            int pixelCount = canvasWidth * canvasHeight;
            for (int p = 0; p < pixelCount; p++)
            {
                int o = p * 4;
                totalErr += Math.Abs(decodedFinal[o] - lastSource[o])
                          + Math.Abs(decodedFinal[o + 1] - lastSource[o + 1])
                          + Math.Abs(decodedFinal[o + 2] - lastSource[o + 2]);
            }
            return totalErr / (pixelCount * 3.0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Balanced_Compact_Scroll_VisuallySane_MeanErrorBounded()
    {
        double balancedErr = EncodeAndMeasureVisualMeanError("balanced_scroll_visual", ScrollFrame, GifSizePreset.Balanced);
        double compactErr = EncodeAndMeasureVisualMeanError("compact_scroll_visual", ScrollFrame, GifSizePreset.Compact);
        _output.WriteLine($"scroll visual meanErr: balanced={balancedErr:F2} compact={compactErr:F2}");

        Assert.True(balancedErr < BalancedScrollVisualMeanErrorBound,
            $"Balanced scroll output's mean per-channel error ({balancedErr:F2}) vs its own source exceeds the {BalancedScrollVisualMeanErrorBound} sanity bound — the lossy run-extension threshold may be streaking text/UI content again");
        Assert.True(compactErr < CompactScrollVisualMeanErrorBound,
            $"Compact scroll output's mean per-channel error ({compactErr:F2}) vs its own source exceeds the {CompactScrollVisualMeanErrorBound} sanity bound — the lossy run-extension threshold may be streaking text/UI content again");
    }

    [Fact]
    public void Compact_Gradient_VisuallySane_MeanErrorBounded()
    {
        // Gradient-only (not Balanced too): Compact is the tier this retune pass's own visual QA
        // found most at risk of streaking (the larger of the two retuned thresholds), so it is the
        // one worth a permanent smooth-content gate; Balanced's headroom is already covered by the
        // stricter scroll bound above plus the blended-ratio band's own monotonicity requirement
        // (Balanced can never compress MORE aggressively than Compact without failing that gate).
        double err = EncodeAndMeasureVisualMeanError("compact_gradient_visual", GradientFrame, GifSizePreset.Compact);
        _output.WriteLine($"gradient visual meanErr: compact={err:F2}");

        Assert.True(err < CompactGradientVisualMeanErrorBound,
            $"Compact gradient output's mean per-channel error ({err:F2}) vs its own source exceeds the {CompactGradientVisualMeanErrorBound} sanity bound — the lossy run-extension threshold may be streaking smooth/gradient content again");
    }
}
