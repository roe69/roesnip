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
/// and reused frame to frame — this runs at recording cadence, so nothing here allocates.</summary>
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
        byte ditherErrorFloor)
    {
        for (int y = box.Top; y < box.Bottom; y++)
        {
            int rowOffset = y * canvasWidth * 4;
            int bayerRow = (y & 3) * 4;
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
                        indexScratch[po >> 2] = (byte)transparentIndex;
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
            }
        }
    }

    private static byte ClampByte(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);
}
