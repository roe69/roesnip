using System;
using System.Collections.Generic;
using RoeSnip.Core.Imaging;

namespace RoeSnip.Core.Overlay;

/// <summary>Pure pixel math for the Pixelate annotation tool's mosaic censor effect: box-averages a
/// region of a BGRA8 <see cref="SdrImage"/> into a coarse grid of flat-colored blocks. WPF's
/// AnnotationLayer.DrawPixelate (src/RoeSnip/Overlay/AnnotationLayer.cs:1057-1084) gets the same
/// visual result by cropping the frozen preview, shrinking it with a WPF TransformedBitmap, and
/// drawing it back with BitmapScalingMode.NearestNeighbor; Avalonia has no CroppedBitmap/
/// TransformedBitmap equivalent, so this computes the shrink-then-nearest-neighbor-upscale result
/// directly as a list of flat-colored block rectangles, which both WPF-equivalent behavior AND
/// on-screen rendering AND export can draw identically with a single DrawRectangle per block — and
/// which is unit-testable without any UI framework at all (RoeSnip.Core has zero display
/// dependency). The shrunk grid dimension is round(size/block) (never zero, per WPF's own
/// TransformedBitmap(1/block) scale), and each grid cell spans a proportional slice of the region
/// so blocks tile it exactly with no leftover partial row/column, matching the WPF bitmap-transform
/// pipeline's implicit "stretch the shrunk bitmap back to the exact region rect" step.</summary>
public static class PixelateMosaic
{
    /// <summary>One mosaic tile: <see cref="X"/>/<see cref="Y"/>/<see cref="Width"/>/<see cref="Height"/>
    /// are in the SAME coordinate space as the source image (physical pixels, image-local) — the
    /// caller translates to screen/export space same as every other annotation shape.</summary>
    public readonly record struct Block(int X, int Y, int Width, int Height, byte R, byte G, byte B);

    /// <summary>Computes the mosaic blocks for a region of <paramref name="preview"/>. The caller
    /// must have already intersected <paramref name="x"/>/<paramref name="y"/>/<paramref name="w"/>/
    /// <paramref name="h"/> with the preview's own bounds (same contract as WPF's DrawPixelate,
    /// which intersects before calling this). Returns an empty list for a degenerate region (matches
    /// WPF's <c>w &lt; 4 || h &lt; 4</c> early return — too small to usefully censor).
    /// <paramref name="blockSize"/> is clamped to [3, min(w, h)] exactly like WPF's StrokeWidthPx-as-
    /// block-size clamp, so a whole-region block just averages everything into one tile rather than
    /// rounding to zero grid cells.</summary>
    public static IReadOnlyList<Block> ComputeBlocks(SdrImage preview, int x, int y, int w, int h, double blockSize)
    {
        if (w < 4 || h < 4)
        {
            return Array.Empty<Block>();
        }

        double block = Math.Clamp(blockSize, 3.0, Math.Min(w, h));
        int gridW = Math.Max(1, (int)Math.Round(w / block));
        int gridH = Math.Max(1, (int)Math.Round(h / block));

        var blocks = new List<Block>(gridW * gridH);
        for (int gy = 0; gy < gridH; gy++)
        {
            int srcY0 = y + (int)((long)gy * h / gridH);
            int srcY1 = y + (int)((long)(gy + 1) * h / gridH);
            if (srcY1 <= srcY0)
            {
                srcY1 = srcY0 + 1;
            }

            for (int gx = 0; gx < gridW; gx++)
            {
                int srcX0 = x + (int)((long)gx * w / gridW);
                int srcX1 = x + (int)((long)(gx + 1) * w / gridW);
                if (srcX1 <= srcX0)
                {
                    srcX1 = srcX0 + 1;
                }

                var (r, g, b) = AverageColor(preview, srcX0, srcY0, srcX1, srcY1);
                blocks.Add(new Block(srcX0, srcY0, srcX1 - srcX0, srcY1 - srcY0, r, g, b));
            }
        }
        return blocks;
    }

    private static (byte R, byte G, byte B) AverageColor(SdrImage preview, int x0, int y0, int x1, int y1)
    {
        long sumR = 0, sumG = 0, sumB = 0;
        int count = 0;
        var pixels = preview.Pixels;
        int stride = preview.Stride;
        for (int py = y0; py < y1; py++)
        {
            int rowOffset = py * stride;
            for (int px = x0; px < x1; px++)
            {
                int o = rowOffset + px * 4;
                sumB += pixels[o];
                sumG += pixels[o + 1];
                sumR += pixels[o + 2];
                count++;
            }
        }
        if (count == 0)
        {
            return (0, 0, 0);
        }
        return ((byte)(sumR / count), (byte)(sumG / count), (byte)(sumB / count));
    }
}
