using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RoeSnip.Capture;
using RoeSnip.Imaging;
using RoeSnip.Recording;
using RoeSnip.Recording.Gif;
using Xunit;

namespace RoeSnip.Tests;

/// <summary>End-to-end behavioral coverage of the timestamped <c>AddFrame(SdrImage, long)</c> delta
/// pipeline — transparency-mask correctness, palette reuse, and keyframing — all verified against
/// the actual on-disk bytes rather than trusting <c>GifBitmapDecoder</c>'s already-composited output
/// (which can't show per-pixel transparent-index or LCT-byte-identity facts). The emit-rate-shaping
/// coverage this class used to carry (a motion-keyed delay floor test) was deleted along with the
/// mechanism itself — see GifEncoderOptions' class doc comment on the quality/framerate decoupling
/// workstream; rate control lives at capture cadence now, not in this encoder.
///
/// Everything here is parsed with a self-contained walker (<see cref="ParseAllFrames"/>) independent
/// of both <see cref="GifEncoder"/>'s own internals and <see cref="GifEncoderTests"/>'s parser, using
/// the shared <see cref="GifLzwTestDecoder"/> to turn each frame's LZW data back into palette
/// indices.</summary>
public class GifDeltaBehaviorTests
{
    private static string TempGifPath() => Path.Combine(Path.GetTempPath(), $"gifdeltatest_{Guid.NewGuid():N}.gif");

    private static byte[] SolidCanvas(int width, int height, byte b, byte g, byte r)
    {
        var pixels = new byte[width * 4 * height];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i + 0] = b;
            pixels[i + 1] = g;
            pixels[i + 2] = r;
            pixels[i + 3] = 255;
        }
        return pixels;
    }

    private static void SetPixel(byte[] pixels, int width, int x, int y, byte b, byte g, byte r)
    {
        int i = (y * width + x) * 4;
        pixels[i + 0] = b;
        pixels[i + 1] = g;
        pixels[i + 2] = r;
        pixels[i + 3] = 255;
    }

    // ---- Self-contained GIF walker: one Image Descriptor + its preceding GCE = one ParsedGifFrame,
    // with the LZW data already decoded to raw palette indices (box-local, row-major). ----

    private sealed record ParsedGifFrame(
        int Delay,
        bool HasTransparency,
        int TransparentIndex,
        int Left,
        int Top,
        int Width,
        int Height,
        byte[] Lct,              // physical LCT bytes: real colors, the transparent slot, then zero padding
        int LctPhysicalEntries,
        byte[] Indices);         // decoded palette indices, box-local, row-major, length == Width*Height

    private static int SkipSubBlocks(byte[] gif, int pos)
    {
        while (gif[pos] != 0x00)
        {
            int size = gif[pos];
            pos += 1 + size;
        }
        return pos + 1;
    }

    private static List<ParsedGifFrame> ParseAllFrames(byte[] gif)
    {
        var frames = new List<ParsedGifFrame>();
        int pos = 13;
        if ((gif[10] & 0x80) != 0)
        {
            pos += 3 * (1 << ((gif[10] & 0x07) + 1));
        }

        int? pendingDelay = null;
        bool pendingHasTransparency = false;
        int pendingTransparentIndex = 0;

        while (pos < gif.Length)
        {
            byte marker = gif[pos];
            if (marker == 0x3B)
            {
                break; // trailer
            }
            if (marker == 0x21)
            {
                if (gif[pos + 1] == 0xF9) // Graphic Control Extension
                {
                    byte packed = gif[pos + 3];
                    pendingDelay = gif[pos + 4] | (gif[pos + 5] << 8);
                    pendingTransparentIndex = gif[pos + 6];
                    pendingHasTransparency = (packed & 0x01) != 0;
                }
                pos = SkipSubBlocks(gif, pos + 2);
            }
            else if (marker == 0x2C) // Image Descriptor
            {
                int left = gif[pos + 1] | (gif[pos + 2] << 8);
                int top = gif[pos + 3] | (gif[pos + 4] << 8);
                int width = gif[pos + 5] | (gif[pos + 6] << 8);
                int height = gif[pos + 7] | (gif[pos + 8] << 8);
                byte descPacked = gif[pos + 9];
                bool hasLct = (descPacked & 0x80) != 0;
                int lctEntries = 1 << ((descPacked & 0x07) + 1);

                int dataStart = pos + 10;
                byte[] lct = Array.Empty<byte>();
                if (hasLct)
                {
                    lct = new byte[lctEntries * 3];
                    Array.Copy(gif, dataStart, lct, 0, lctEntries * 3);
                    dataStart += lctEntries * 3;
                }

                byte[] indices = GifLzwTestDecoder.Decode(gif, dataStart, out int endPos);
                Assert.Equal(width * height, indices.Length);

                Assert.NotNull(pendingDelay); // every Image Descriptor must be preceded by a GCE
                frames.Add(new ParsedGifFrame(
                    pendingDelay!.Value, pendingHasTransparency, pendingTransparentIndex,
                    left, top, width, height, lct, lctEntries, indices));
                pendingDelay = null;

                pos = endPos;
            }
            else
            {
                throw new InvalidOperationException($"Unexpected marker 0x{marker:X2} at {pos}");
            }
        }
        return frames;
    }

    // GIF89a Color Table entries are stored Red, Green, Blue (in that order) — see GifEncoder's
    // WriteFrame comment on the LCT write for why the byte order flips there relative to every
    // internal B,G,R palette buffer. Read back and re-expose as (B, G, R) so callers below, written
    // against the app's usual BGR convention, don't have to care.
    private static (byte B, byte G, byte R) LctColor(ParsedGifFrame frame, int index) =>
        (frame.Lct[index * 3 + 2], frame.Lct[index * 3 + 1], frame.Lct[index * 3 + 0]);

    /// <summary>Composites the whole parsed sequence into a final canvas exactly the way a GIF
    /// viewer under "do not dispose" would: later frames paint over earlier ones inside their own
    /// sub-rect, except pixels whose decoded index is the frame's transparent index, which leave
    /// whatever was already there untouched.</summary>
    private static byte[] Composite(List<ParsedGifFrame> frames, int width, int height)
    {
        var canvas = new byte[width * 4 * height];
        foreach (var f in frames)
        {
            for (int y = 0; y < f.Height; y++)
            {
                for (int x = 0; x < f.Width; x++)
                {
                    int index = f.Indices[y * f.Width + x];
                    if (f.HasTransparency && index == f.TransparentIndex)
                    {
                        continue;
                    }
                    int po = ((f.Top + y) * width + (f.Left + x)) * 4;
                    var c = LctColor(f, index);
                    canvas[po + 0] = c.B;
                    canvas[po + 1] = c.G;
                    canvas[po + 2] = c.R;
                    canvas[po + 3] = 255;
                }
            }
        }
        return canvas;
    }

    // ---- (1) Transparency mask pixel-exactness ----

    [Fact]
    public void AddFrame_Timestamped_TransparencyMask_IsPixelExact()
    {
        string path = TempGifPath();
        try
        {
            const int w = 6, h = 6;
            var background = SolidCanvas(w, h, 80, 80, 80);
            var encoder = GifEncoder.Create(path, w, h, timestampTicksPerSecond: 1000);
            encoder.AddFrame(new SdrImage(w, h, background), 0L);

            var changed = (byte[])background.Clone();
            SetPixel(changed, w, 1, 1, 250, 10, 10); // clearly-changed corner
            SetPixel(changed, w, 4, 4, 10, 250, 10); // clearly-changed corner
            encoder.AddFrame(new SdrImage(w, h, changed), 500L);
            encoder.FinalizeAndClose();

            var frames = ParseAllFrames(File.ReadAllBytes(path));
            Assert.Equal(2, frames.Count);
            var delta = frames[1];
            Assert.True(delta.HasTransparency);
            // Bbox is the tight rectangle enclosing both changed corners, so it also spans the
            // untouched pixels between them — exactly the shape the transparency mask exists for.
            Assert.Equal((1, 1, 4, 4), (delta.Left, delta.Top, delta.Width, delta.Height));

            for (int y = 0; y < delta.Height; y++)
            {
                for (int x = 0; x < delta.Width; x++)
                {
                    int canvasX = delta.Left + x;
                    int canvasY = delta.Top + y;
                    int index = delta.Indices[y * delta.Width + x];
                    bool isPaintedCorner = (canvasX == 1 && canvasY == 1) || (canvasX == 4 && canvasY == 4);

                    if (isPaintedCorner)
                    {
                        Assert.NotEqual(delta.TransparentIndex, index);
                    }
                    else
                    {
                        Assert.Equal(delta.TransparentIndex, index);
                    }
                }
            }

            // The two painted corners resolve to their exact source colors: two distinct, far-apart
            // colors easily fit an up-to-255-color palette with zero quantization error.
            int idx11 = delta.Indices[(1 - delta.Top) * delta.Width + (1 - delta.Left)];
            int idx44 = delta.Indices[(4 - delta.Top) * delta.Width + (4 - delta.Left)];
            Assert.Equal(((byte)250, (byte)10, (byte)10), LctColor(delta, idx11));
            Assert.Equal(((byte)10, (byte)250, (byte)10), LctColor(delta, idx44));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- (2) Drift regression: a pixel drifting +1/frame must repaint once cumulative drift
    // exceeds tolerance, never silently accumulate past it, interleaved with unrelated changes
    // elsewhere that must not disturb its baseline while it is still within tolerance. ----

    [Fact]
    public void AddFrame_Timestamped_SlowDrift_RepaintsOnceToleranceExceeded_AndConvergesToTrueValue()
    {
        string path = TempGifPath();
        try
        {
            const int w = 10, h = 10;
            const byte background = 30;
            byte tolerance = new GifEncoderOptions().ChannelTolerance; // default 4

            var encoder = GifEncoder.Create(path, w, h, timestampTicksPerSecond: 1000);
            var frame0 = SolidCanvas(w, h, background, background, background);
            encoder.AddFrame(new SdrImage(w, h, frame0), 0L);

            const int frameCount = 59; // 60 total AddFrame calls including the initial frame
            byte trueFinalDrift = background;
            for (int i = 1; i <= frameCount; i++)
            {
                var pixels = SolidCanvas(w, h, background, background, background);

                // The drift pixel: +1/frame off whatever it was last painted at, never crossing
                // tolerance frame-to-frame relative to a single prior emit, but drifting away from
                // its last-painted baseline (which only updates when THIS pixel is actually painted).
                byte driftB = (byte)(background + i);
                SetPixel(pixels, w, 0, 0, driftB, background, background);

                // An unrelated pixel elsewhere that always differs hugely from whatever it was last
                // painted at — forces an emit (and, once the drift pixel also differs, sweeps the
                // drift pixel into the same bbox) without ever touching the drift pixel's own baseline.
                byte unrelated = (i % 2 == 1) ? (byte)220 : (byte)10;
                SetPixel(pixels, w, 9, 9, unrelated, unrelated, unrelated);

                encoder.AddFrame(new SdrImage(w, h, pixels), i * 100L);
                trueFinalDrift = driftB;
            }
            encoder.FinalizeAndClose();

            var frames = ParseAllFrames(File.ReadAllBytes(path));
            Assert.Equal(frameCount + 1, frames.Count); // every candidate here always differs from baseline

            // Frames for drift = 1..4 (within tolerance) must not touch the drift pixel at all: the
            // changed bbox stays pinned to the unrelated pixel alone, proving no false repaint.
            for (int k = 1; k <= 4; k++)
            {
                Assert.Equal((9, 9, 1, 1), (frames[k].Left, frames[k].Top, frames[k].Width, frames[k].Height));
            }

            // Frame for drift = 5 (> tolerance 4) is the first to sweep the drift pixel into the
            // bbox and repaint it — the regression this test guards against is EITHER repainting too
            // early (frames 1-4 above) OR never repainting at all (checked here).
            var repaint = frames[5];
            Assert.Equal((0, 0, w, h), (repaint.Left, repaint.Top, repaint.Width, repaint.Height));
            int driftIndex = repaint.Indices[0]; // box left/top == (0,0), so index 0 IS canvas (0,0)
            Assert.NotEqual(repaint.TransparentIndex, driftIndex);
            Assert.Equal((byte)(background + 5), LctColor(repaint, driftIndex).B); // exact: 2-color palette

            // Composite the whole sequence (sub-rects + transparency, exactly as a GIF viewer would)
            // and check the final drift pixel is within tolerance of the TRUE final source value —
            // it can never have drifted further than that, since the tolerance check runs every frame.
            byte[] composite = Composite(frames, w, h);
            byte finalComposited = composite[0];
            Assert.True(Math.Abs(finalComposited - trueFinalDrift) <= tolerance,
                $"final composited drift pixel {finalComposited} should be within {tolerance} of true final value {trueFinalDrift}");
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- (3) Palette-reuse fast path: two successive frames with small similar changes emit
    // byte-identical LCTs. ----

    [Fact]
    public void AddFrame_Timestamped_SimilarSuccessiveChanges_ReusePaletteByteIdentical()
    {
        string path = TempGifPath();
        try
        {
            const int w = 6, h = 6;
            // Deliberately separate the two thresholds: ChannelTolerance small enough that the
            // perturbation between frame1 and frame2 still counts as a change worth repainting,
            // PaletteReuseErrorThreshold large enough that the SAME perturbation still fits the
            // existing palette without a rebuild.
            var options = new GifEncoderOptions(ChannelTolerance: 2, PaletteReuseErrorThreshold: 6);
            var encoder = GifEncoder.Create(path, w, h, timestampTicksPerSecond: 1000, options: options);

            var background = SolidCanvas(w, h, 80, 80, 80);
            encoder.AddFrame(new SdrImage(w, h, background), 0L);

            var frame1 = (byte[])background.Clone();
            SetPixel(frame1, w, 1, 1, 200, 60, 60);
            SetPixel(frame1, w, 2, 2, 60, 200, 60);
            encoder.AddFrame(new SdrImage(w, h, frame1), 100L);

            var frame2 = (byte[])background.Clone();
            SetPixel(frame2, w, 1, 1, 203, 63, 57); // +3/+3/-3 from frame1 — over ChannelTolerance(2)...
            SetPixel(frame2, w, 2, 2, 63, 197, 63); // ...but within PaletteReuseErrorThreshold(6)
            encoder.AddFrame(new SdrImage(w, h, frame2), 200L);

            encoder.FinalizeAndClose();

            var frames = ParseAllFrames(File.ReadAllBytes(path));
            Assert.Equal(3, frames.Count);
            var delta1 = frames[1];
            var delta2 = frames[2];

            // Both frames actually repainted the same region — the perturbation was big enough to
            // count as a change, this isn't just "nothing happened twice".
            Assert.Equal((1, 1, 2, 2), (delta1.Left, delta1.Top, delta1.Width, delta1.Height));
            Assert.Equal((1, 1, 2, 2), (delta2.Left, delta2.Top, delta2.Width, delta2.Height));

            Assert.Equal(delta1.LctPhysicalEntries, delta2.LctPhysicalEntries);
            Assert.Equal(delta1.Lct, delta2.Lct); // byte-identical LCT: the reuse-first fast path kept it

            var idx1 = delta1.Indices[0]; // box-local (0,0) == canvas (1,1) for both frames
            var idx2 = delta2.Indices[0];
            Assert.Equal(idx1, idx2);
            Assert.Equal(((byte)200, (byte)60, (byte)60), LctColor(delta2, idx2)); // frame1's EXACT color
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- (4) Keyframe: full-canvas opaque re-baselines at the configured cadence, everything else
    // stays sub-rect/transparent. ----

    [Fact]
    public void AddFrame_Timestamped_Keyframes_RebaselineAtConfiguredCadence()
    {
        string path = TempGifPath();
        try
        {
            const int w = 5, h = 5;
            const int intervalMs = 100;
            const int totalMs = 3000;
            var options = new GifEncoderOptions(KeyframeInterval: TimeSpan.FromSeconds(1));
            var encoder = GifEncoder.Create(path, w, h, timestampTicksPerSecond: 1000, options: options);

            int steps = totalMs / intervalMs; // 30
            for (int i = 0; i <= steps; i++)
            {
                // A single drifting pixel is the only thing that ever changes — its value sequence
                // (i*50 mod 256) always differs from the immediately preceding step by exactly 50 or
                // 206 absolute units (a mod-256 ramp never "wraps small"), so every step is a genuine,
                // easily-over-tolerance change and nothing here is ever skipped as a no-op.
                byte val = (byte)((i * 50) % 256);
                var pixels = SolidCanvas(w, h, 60, 60, 60);
                SetPixel(pixels, w, 0, 0, val, val, val);
                encoder.AddFrame(new SdrImage(w, h, pixels), i * (long)intervalMs);
            }
            encoder.FinalizeAndClose();

            var frames = ParseAllFrames(File.ReadAllBytes(path));
            Assert.Equal(steps + 1, frames.Count);

            for (int k = 0; k <= steps; k++)
            {
                bool isKeyframeStep = k % 10 == 0; // KeyframeInterval=1s == 10 steps of 100ms
                var f = frames[k];
                if (isKeyframeStep)
                {
                    Assert.False(f.HasTransparency);
                    Assert.Equal((0, 0, w, h), (f.Left, f.Top, f.Width, f.Height));
                }
                else
                {
                    Assert.True(f.HasTransparency);
                    Assert.Equal((0, 0, 1, 1), (f.Left, f.Top, f.Width, f.Height));
                }
            }

            // Exactly one keyframe every 1s of media clock: steps 0 (initial), 10, 20, 30.
            var keyframeSteps = Enumerable.Range(0, steps + 1).Where(k => k % 10 == 0).ToList();
            Assert.Equal(new[] { 0, 10, 20, 30 }, keyframeSteps);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- (5) Lossy run-extension (quality/fps expansion workstream) — GifDelta.ClassifyAndPaint's
    // gifsicle-style "reuse the adjacent already-painted palette entry when close enough" lever.
    // Exercised directly against ClassifyAndPaint (not through a full GifEncoder file) so each test
    // can inspect exactly which pixels the lossy path touched, rather than inferring it from
    // already-composited/decoded bytes. GifColorDistance.RedmeanSquared is `internal` (no
    // InternalsVisibleTo edit for this repo's test-access convention — see e.g.
    // RecordingSizeEstimator's own doc comment on making the testable slice public instead), so
    // RedmeanSquared below is the same documented formula, duplicated for verification purposes. ----

    /// <summary>Same formula as (internal) GifColorDistance.RedmeanSquared, duplicated here since
    /// this test assembly has no InternalsVisibleTo edit into RoeSnip (this repo's convention is to
    /// make the testable slice public rather than add one — see GifColorDistance's own doc comment
    /// for why it stays internal: it's an implementation detail of the Gif namespace, not a public
    /// surface).</summary>
    private static double RedmeanSquared(byte b1, byte g1, byte r1, byte b2, byte g2, byte r2)
    {
        double rmean = (r1 + r2) / 2.0;
        double dr = r1 - r2;
        double dg = g1 - g2;
        double db = b1 - b2;
        return (2.0 + rmean / 256.0) * dr * dr
             + 4.0 * dg * dg
             + (2.0 + (255.0 - rmean) / 256.0) * db * db;
    }

    /// <summary>An N-entry grayscale palette where palette index i IS exactly gray level i — every
    /// pixel in a 0..N-1 gradient therefore has an EXACT (distance-0) nearest-color match, so
    /// without the lossy mechanism a fine gradient produces a fresh, distinct index almost every
    /// pixel (no accidental merging from palette coarseness muddying what's being measured).</summary>
    private static byte[] BuildExactGrayscalePalette(int count)
    {
        var palette = new byte[count * 3];
        for (int i = 0; i < count; i++)
        {
            palette[i * 3 + 0] = (byte)i;
            palette[i * 3 + 1] = (byte)i;
            palette[i * 3 + 2] = (byte)i;
        }
        return palette;
    }

    [Fact]
    public void ClassifyAndPaint_ZeroThreshold_NeverDivergesFromTheFreshLutLookup()
    {
        // threshold 0 = byte-identical output to before this feature existed: with the lossy
        // mechanism disabled (lossyRunThresholdSq: 0, the default for every pre-existing call site
        // and Max/Quality), every painted pixel's index must be EXACTLY what a plain LUT.Lookup on
        // its own source color would produce — the lossy branch must never fire.
        const int w = 128;
        byte[] palette = BuildExactGrayscalePalette(128);
        var lut = new GifNearestColorLut();
        lut.Rebuild(palette, 128);

        var current = new byte[w * 4];
        for (int x = 0; x < w; x++)
        {
            byte v = (byte)x;
            current[x * 4] = v; current[x * 4 + 1] = v; current[x * 4 + 2] = v; current[x * 4 + 3] = 255;
        }

        var lastPainted = new byte[w * 4];
        var indexScratch = new byte[w];
        var box = RectPhysical.FromSize(0, 0, w, 1);
        GifDelta.ClassifyAndPaint(
            current, lastPainted, indexScratch, w, box, lut, palette,
            transparentIndex: 128, allowTransparency: false, channelTolerance: 0, ditherErrorFloor: 255,
            lossyRunThresholdSq: 0);

        for (int x = 0; x < w; x++)
        {
            int fresh = lut.Lookup(current[x * 4], current[x * 4 + 1], current[x * 4 + 2]);
            Assert.Equal(fresh, indexScratch[x]);
        }
    }

    [Fact]
    public void ClassifyAndPaint_GenerousLossyThreshold_ProducesFewerDistinctIndicesThanZeroThreshold()
    {
        // A fine 1-unit-step grayscale gradient against an EXACT-per-level palette. Even with the
        // lossy mechanism OFF (threshold 0), GifNearestColorLut's own 5-5-5 bucket cache (8 units/
        // channel — see that class's doc comment) already merges SOME neighbors purely from bucket
        // quantization, so the baseline is not literally "one distinct index per pixel" - this test
        // only asserts the RELATIVE effect the lossy mechanism adds on top of that baseline, not an
        // absolute transition count. With a generous threshold on, nearby gray levels reuse the
        // run's anchor index instead of a fresh (bucket-cached) lookup, producing measurably longer
        // runs — exactly the size lever this mechanism exists to add (see GifDelta's class doc).
        const int w = 200;
        byte[] palette = BuildExactGrayscalePalette(w);
        var current = new byte[w * 4];
        for (int x = 0; x < w; x++)
        {
            byte v = (byte)x;
            current[x * 4] = v; current[x * 4 + 1] = v; current[x * 4 + 2] = v; current[x * 4 + 3] = 255;
        }
        var box = RectPhysical.FromSize(0, 0, w, 1);

        byte[] EncodeIndices(int lossyThresholdSq)
        {
            var lut = new GifNearestColorLut();
            lut.Rebuild(palette, w);
            var lastPainted = new byte[w * 4];
            var indexScratch = new byte[w];
            GifDelta.ClassifyAndPaint(
                current, lastPainted, indexScratch, w, box, lut, palette,
                transparentIndex: w, allowTransparency: false, channelTolerance: 0, ditherErrorFloor: 255,
                lossyRunThresholdSq: lossyThresholdSq);
            return indexScratch;
        }

        byte[] baseline = EncodeIndices(0);
        byte[] lossy = EncodeIndices(2000); // Δ_max ≈ sqrt(2000/9) ≈ 14.9 gray levels merge per run

        static int Transitions(byte[] indices)
        {
            int count = 0;
            for (int i = 1; i < indices.Length; i++)
            {
                if (indices[i] != indices[i - 1])
                {
                    count++;
                }
            }
            return count;
        }

        int baselineTransitions = Transitions(baseline);
        int lossyTransitions = Transitions(lossy);
        int baselineDistinct = baseline.Distinct().Count();
        int lossyDistinct = lossy.Distinct().Count();

        // Sanity: the baseline still has meaningful variation to shrink (not already collapsed to
        // a handful of runs purely by GifNearestColorLut's own bucket quantization).
        Assert.True(baselineTransitions > 10, $"baseline transitions ({baselineTransitions}) too low for this test to be meaningful");
        Assert.True(lossyTransitions < baselineTransitions,
            $"lossy transitions ({lossyTransitions}) should be far fewer than baseline ({baselineTransitions})");
        Assert.True(lossyDistinct < baselineDistinct,
            $"lossy distinct index count ({lossyDistinct}) should be fewer than baseline ({baselineDistinct})");
    }

    [Fact]
    public void ClassifyAndPaint_LossyRunExtension_ReusedPixelsStayWithinTheThresholdOfTheirOwnSourceColor()
    {
        // Bounded-error guarantee: for every pixel where the lossy mechanism actually fired (its
        // final index differs from what a fresh, independent LUT lookup on that pixel's own source
        // color would have produced), the SOURCE color must be within lossyRunThresholdSq (redmean-
        // squared) of the reused entry's actual palette color — the exact condition the code checks
        // before taking that branch, verified here against the real output rather than trusted by
        // inspection alone.
        const int w = 150;
        const int lossyThresholdSq = 900; // sqrt(900) = 30 (redmean-squared units, not plain distance)
        byte[] palette = BuildExactGrayscalePalette(w);
        var lut = new GifNearestColorLut();
        lut.Rebuild(palette, w);

        var current = new byte[w * 4];
        for (int x = 0; x < w; x++)
        {
            byte v = (byte)x;
            current[x * 4] = v; current[x * 4 + 1] = v; current[x * 4 + 2] = v; current[x * 4 + 3] = 255;
        }

        var lastPainted = new byte[w * 4];
        var indexScratch = new byte[w];
        var box = RectPhysical.FromSize(0, 0, w, 1);
        GifDelta.ClassifyAndPaint(
            current, lastPainted, indexScratch, w, box, lut, palette,
            transparentIndex: w, allowTransparency: false, channelTolerance: 0, ditherErrorFloor: 255,
            lossyRunThresholdSq: lossyThresholdSq);

        int reusedPixelCount = 0;
        for (int x = 0; x < w; x++)
        {
            int actual = indexScratch[x];
            int fresh = lut.Lookup(current[x * 4], current[x * 4 + 1], current[x * 4 + 2]);
            if (actual == fresh)
            {
                continue; // resolved through the normal LUT path, not the lossy mechanism
            }

            reusedPixelCount++;
            int po = actual * 3;
            double dist = RedmeanSquared(
                current[x * 4], current[x * 4 + 1], current[x * 4 + 2],
                palette[po], palette[po + 1], palette[po + 2]);
            Assert.True(dist <= lossyThresholdSq,
                $"pixel {x}: reused index {actual}'s color is {dist:F1} redmean-squared from its own source, exceeding the {lossyThresholdSq} threshold");
        }

        // The gradient/threshold combination above is chosen generously enough that the mechanism
        // must actually have fired at least once, or this test would trivially pass without
        // exercising anything.
        Assert.True(reusedPixelCount > 0, "expected at least one pixel to take the lossy reuse path");
    }

    [Fact]
    public void ClassifyAndPaint_LossyRunExtension_NeverProducesTheTransparentIndex()
    {
        // A changed pixel must never be classified transparent by the lossy mechanism — that is
        // tolerance's job, with its own bound (see GifDelta's class doc comment). Mixes genuinely
        // unchanged pixels (within channelTolerance of the baseline, must map to transparentIndex)
        // with genuinely changed ones (must never map to transparentIndex, lossy-reused or not),
        // under an aggressively generous lossy threshold to maximize the chance a bug would leak
        // transparentIndex through the reuse path.
        const int w = 40;
        byte[] palette = BuildExactGrayscalePalette(w);
        const int transparentIndex = w;
        var lut = new GifNearestColorLut();
        lut.Rebuild(palette, w);

        var lastPainted = new byte[w * 4];
        for (int x = 0; x < w; x++)
        {
            lastPainted[x * 4] = 20; lastPainted[x * 4 + 1] = 20; lastPainted[x * 4 + 2] = 20; lastPainted[x * 4 + 3] = 255;
        }

        var current = new byte[w * 4];
        for (int x = 0; x < w; x++)
        {
            // Even x: within channelTolerance(2) of the baseline(20) — must classify transparent.
            // Odd x: a FIXED, far-outside-tolerance gray (200) — must be genuinely painted. A
            // constant (rather than x-derived) value deliberately avoids landing back near 20 for
            // some odd x, which a naive x%w mapping would (e.g. x=19,21 map to 19,21 - themselves
            // within tolerance of 20).
            byte v = (x % 2 == 0) ? (byte)21 : (byte)200;
            current[x * 4] = v; current[x * 4 + 1] = v; current[x * 4 + 2] = v; current[x * 4 + 3] = 255;
        }

        var indexScratch = new byte[w];
        var box = RectPhysical.FromSize(0, 0, w, 1);
        GifDelta.ClassifyAndPaint(
            current, lastPainted, indexScratch, w, box, lut, palette,
            transparentIndex, allowTransparency: true, channelTolerance: 2, ditherErrorFloor: 255,
            lossyRunThresholdSq: 50_000); // deliberately huge — see class doc above for why

        for (int x = 0; x < w; x++)
        {
            bool shouldBeTransparent = x % 2 == 0;
            if (shouldBeTransparent)
            {
                Assert.Equal(transparentIndex, indexScratch[x]);
            }
            else
            {
                Assert.NotEqual(transparentIndex, indexScratch[x]);
            }
        }
    }
}
