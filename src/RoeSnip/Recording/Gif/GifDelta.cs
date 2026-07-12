using System;
using RoeSnip.Capture;

namespace RoeSnip.Recording.Gif;

/// <summary>The one pixel-level pass that turns a changed bounding box into GIF palette indices,
/// combining three things that would otherwise each need their own scan over the same pixels:
/// deciding which pixels are close enough to the last-painted baseline to skip (mapped to the
/// reserved transparent index instead), mapping the rest through the nearest-color LUT with a
/// gated ordered dither, and advancing the last-painted baseline for exactly the pixels this frame
/// actually paints (see <see cref="RoeSnip.Recording.GifEncoder"/>'s class doc for why the
/// baseline must hold SOURCE values, not quantized ones).
///
/// Every output buffer (<paramref name="indexScratch"/> and the baseline itself) is caller-owned
/// and reused frame to frame — this runs at recording cadence, so nothing here allocates.
///
/// LOSSY RUN-EXTENSION (quality/fps expansion workstream, <paramref name="lossyRunThresholdSq"/>
/// on the tiers that enable it — see <see cref="RoeSnip.Recording.Gif.GifEncoderOptions.LossyRunThresholdSq"/>'s
/// own doc comment): a gifsicle-style lever, applied per row, left to right. Every row tracks the
/// most recently PAINTED pixel's palette index (never the transparent index — a pixel classified
/// transparent by the tolerance test above leaves that tracker untouched, so a lossy candidate can
/// still look back across a transparent gap to the last real paint). When a candidate pixel would
/// otherwise need a fresh LUT lookup, if the tracked index is a real palette entry and this pixel's
/// SOURCE color is within <paramref name="lossyRunThresholdSq"/> (redmean-squared) of THAT entry's
/// actual palette color, the pixel reuses the tracked index outright instead of resolving its own
/// nearest color — no dither, no new LUT bucket fill. This is what turns a near-solid run (a UI
/// fill, an anti-aliased glyph edge, a scroll-band background) into a genuinely long run of
/// IDENTICAL indices for the LZW stage to compress, rather than alternating between two or three
/// perceptually-indistinguishable-but-numerically-different palette entries. The baseline still
/// records the pixel's own SOURCE value on a reuse (same drift rule as every other painted pixel —
/// see the class doc above), so the bounded-error guarantee this buys is against the SOURCE, not
/// against whatever the run's first pixel happened to look like: every reused pixel's displayed
/// color is within threshold of its own true value, not just close to its neighbor.</summary>
public static class GifDelta
{
    // Standard 4x4 Bayer ordered-dither threshold matrix, values 0-15.
    private static readonly int[] Bayer4x4 =
    {
        0, 8, 2, 10,
        12, 4, 14, 6,
        3, 11, 1, 9,
        15, 7, 13, 5,
    };

    /// <summary>Processes every pixel inside <paramref name="box"/> (physical canvas coordinates)
    /// and writes its palette index into <paramref name="indexScratch"/> at the SAME canvas
    /// position (row stride <paramref name="canvasWidth"/>) — callers slice the box back out of it
    /// when building the LZW input, rather than this method packing a tightly-cropped buffer
    /// itself, so it never needs a box-sized scratch allocation of its own.
    ///
    /// When <paramref name="allowTransparency"/> is false (first frame / keyframe), every pixel in
    /// the box is unconditionally painted — no tolerance test, no transparent index anywhere in the
    /// output — matching the "keyframes are fully opaque" contract. When true, a pixel within
    /// <paramref name="channelTolerance"/> (per-channel, Chebyshev) of the baseline is left
    /// untouched: its index is written as <paramref name="transparentIndex"/> and the baseline is
    /// NOT updated for it (the whole point of the baseline holding source values — see the class
    /// doc — is that a pixel sitting just inside tolerance must keep comparing against the same
    /// reference next frame, not silently drift).</summary>
    public static void ClassifyAndPaint(
        ReadOnlySpan<byte> currentPixelsBgra,
        byte[] lastPaintedPixelsBgra,
        byte[] indexScratch,
        int canvasWidth,
        RectPhysical box,
        GifNearestColorLut lut,
        byte[] paletteBgr,
        int transparentIndex,
        bool allowTransparency,
        byte channelTolerance,
        byte ditherErrorFloor,
        int lossyRunThresholdSq = 0)
    {
        for (int y = box.Top; y < box.Bottom; y++)
        {
            int rowOffset = y * canvasWidth * 4;
            int bayerRow = (y & 3) * 4;
            // Per-row run tracker (see class doc comment) — reset at the start of every row since a
            // run never spans a row boundary; -1 means "no real paint yet this row".
            int lastPaintedIndex = -1;
            for (int x = box.Left; x < box.Right; x++)
            {
                int po = rowOffset + x * 4;
                byte b = currentPixelsBgra[po];
                byte g = currentPixelsBgra[po + 1];
                byte r = currentPixelsBgra[po + 2];

                if (allowTransparency)
                {
                    byte lb = lastPaintedPixelsBgra[po];
                    byte lg = lastPaintedPixelsBgra[po + 1];
                    byte lr = lastPaintedPixelsBgra[po + 2];
                    int db = Math.Abs(b - lb), dg = Math.Abs(g - lg), dr = Math.Abs(r - lr);
                    if (db <= channelTolerance && dg <= channelTolerance && dr <= channelTolerance)
                    {
                        // Not painted — leave lastPaintedIndex untouched (see class doc: a lossy
                        // candidate later in the row can still look back across this gap).
                        indexScratch[po >> 2] = (byte)transparentIndex;
                        continue;
                    }
                }

                // Lossy run-extension fast path (disabled when lossyRunThresholdSq is 0, the default
                // for every tier that must stay bit-exact — Max/Quality). Tried BEFORE the normal LUT
                // lookup/dither below, never across the transparent index (lastPaintedIndex is only
                // ever a real palette entry — see the class doc for why that's true by construction).
                if (lossyRunThresholdSq > 0 && lastPaintedIndex >= 0)
                {
                    int rpo = lastPaintedIndex * 3;
                    byte rb = paletteBgr[rpo], rg = paletteBgr[rpo + 1], rr = paletteBgr[rpo + 2];
                    double runDist = GifColorDistance.RedmeanSquared(b, g, r, rb, rg, rr);
                    if (runDist <= lossyRunThresholdSq)
                    {
                        indexScratch[po >> 2] = (byte)lastPaintedIndex;
                        lastPaintedPixelsBgra[po] = b;
                        lastPaintedPixelsBgra[po + 1] = g;
                        lastPaintedPixelsBgra[po + 2] = r;
                        lastPaintedPixelsBgra[po + 3] = 255;
                        continue;
                    }
                }

                int nearest = lut.Lookup(b, g, r);
                int pbo = nearest * 3;
                byte pb = paletteBgr[pbo], pg = paletteBgr[pbo + 1], pr = paletteBgr[pbo + 2];
                int maxChannelError = Math.Max(Math.Abs(b - pb), Math.Max(Math.Abs(g - pg), Math.Abs(r - pr)));

                int finalIndex = nearest;
                if (maxChannelError > ditherErrorFloor)
                {
                    // Ordered dither: perturb the source color by a Bayer-thresholded fraction of
                    // its own quantization error (self-scaling — a barely-over-the-floor pixel gets
                    // a small nudge, a badly-matched one gets a big one) and re-quantize. The same
                    // per-pixel threshold is reused across all three channels so the perturbation is
                    // correlated (avoids introducing color fringing that independent per-channel
                    // thresholds would).
                    double t = (Bayer4x4[bayerRow + (x & 3)] + 0.5) / 16.0 - 0.5; // -0.46875..0.46875
                    byte db2 = ClampByte(b + t * maxChannelError);
                    byte dg2 = ClampByte(g + t * maxChannelError);
                    byte dr2 = ClampByte(r + t * maxChannelError);
                    finalIndex = lut.Lookup(db2, dg2, dr2);
                }

                indexScratch[po >> 2] = (byte)finalIndex;
                // Reached only for pixels that are actually painted this frame — either because
                // allowTransparency is false (every pixel in the box counts as painted: first
                // frame / keyframe) or because this one exceeded tolerance above. Either way the
                // baseline advances to this frame's source value, never the quantized one.
                lastPaintedPixelsBgra[po] = b;
                lastPaintedPixelsBgra[po + 1] = g;
                lastPaintedPixelsBgra[po + 2] = r;
                lastPaintedPixelsBgra[po + 3] = 255;
                lastPaintedIndex = finalIndex; // always a real palette entry, never transparentIndex
            }
        }
    }

    private static byte ClampByte(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);
}
