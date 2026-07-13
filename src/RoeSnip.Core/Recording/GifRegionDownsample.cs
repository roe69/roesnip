using System;

namespace RoeSnip.Core.Recording;

/// <summary>The GIF Compact/Minimal size-tier downscale math, ported verbatim from the WPF app's
/// RecordingController (ScaledCanvasDimension ~2358, BoxDownsampleGifFrame ~2379) - extracted to Core
/// so it is unit-testable directly rather than only observable through a live encoder, matching this
/// item's other pure-math extractions (RecordingSchedule, RawFrameEquality).</summary>
public static class GifRegionDownsample
{
    /// <summary>Floors a RenderScale-scaled dimension to the nearest even number, minimum 2 -
    /// scaling an already-even selection dimension can still land on an odd number (e.g. 802 -> 401
    /// at 0.5, or 402 -> 301 at 0.75), and GifEncoder.Create has no tolerance for odd canvas
    /// dimensions any more than the unscaled path does.</summary>
    public static int ScaledCanvasDimension(int fullSize, double scale)
    {
        int scaled = (int)(fullSize * scale);
        return Math.Max(2, scaled - (scaled % 2));
    }

    /// <summary>Box-filter downsample of a tone-mapped BGRA8 buffer into a smaller, caller-owned
    /// destination buffer (GIF downscaled render, Compact 0.75 / Minimal 0.5 -
    /// <see cref="RoeSnip.Core.Recording.Gif.GifEncoderOptions.RenderScale"/>). Each destination
    /// pixel averages the axis-aligned source rect that maps to it under uniform (srcSize/dstSize)
    /// scaling - at 0.5 that rect is always a plain 2x2 block; at 0.75 (non-integer ratio) the
    /// integer-floored boundaries produce a repeating 1,1,2-pixel rect pattern per axis, i.e. an
    /// integer-snapped area average in which every source pixel contributes wholly to exactly one
    /// destination pixel (no fractional edge weights - a deliberate approximation of the exact
    /// fractional-coverage box filter, accepted because the per-pixel weight error is at most one
    /// source pixel's worth). Alloc-free: <paramref name="dst"/> is the caller's reused scratch
    /// buffer, never allocated here (LOH-avoidance - see RecordingController's own encoder-loop
    /// buffer-reuse discipline).</summary>
    public static void BoxDownsample(byte[] src, int srcWidth, int srcHeight, byte[] dst, int dstWidth, int dstHeight)
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
}
