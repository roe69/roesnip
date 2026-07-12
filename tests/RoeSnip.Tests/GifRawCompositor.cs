using System;

namespace RoeSnip.Tests;

/// <summary>Shared test-only GIF frame walker + disposal-do-not-dispose compositor. Extracted as
/// its own file (rather than duplicated once more) because GifSizeBenchmarkTests needs the exact
/// same technique GifDeltaBehaviorTests' own private ParseAllFrames/Composite pair already uses
/// (see that class's own doc comment) for its visual-sanity check.
///
/// This is NOT the same thing as reading <c>GifBitmapDecoder.Frames[i]</c> directly: each
/// <c>BitmapFrame</c> WPF hands back is that ONE Image Descriptor's own raw sub-rect pixels,
/// uncomposited against every earlier frame — grabbing just the LAST frame that way shows only its
/// own small delta rect against an otherwise blank/undefined canvas, not the true final displayed
/// image (the exact bug this class exists to avoid: an earlier version of
/// GifSizeBenchmarkTests.Minimal_Scroll_VisuallySane_MeanErrorBounded measured a ~40-per-channel
/// "error" this way that was actually just comparing against mostly-uninitialized pixels, not a
/// real encoding-fidelity problem). GifLzwTestDecoder's own doc comment explains why every other
/// pixel-level test in this project already avoids GifBitmapDecoder's raw Frames for the same
/// reason.</summary>
internal static class GifRawCompositor
{
    /// <summary>Composites every frame in <paramref name="gif"/> onto a
    /// <paramref name="canvasWidth"/> x <paramref name="canvasHeight"/> BGRA8 buffer using GIF's "do
    /// not dispose" disposal rule (later frames paint over earlier ones inside their own sub-rect;
    /// pixels whose decoded index is that frame's transparent index leave whatever was already
    /// there untouched — this encoder only ever writes disposal method 1, see GifEncoder's own
    /// DisposalDoNotDispose constant) and returns the FINAL state: exactly what a real viewer shows
    /// once the whole animation has played through once.</summary>
    public static byte[] CompositeFinalFrame(byte[] gif, int canvasWidth, int canvasHeight)
    {
        var canvas = new byte[canvasWidth * 4 * canvasHeight];
        int pos = 13;
        if ((gif[10] & 0x80) != 0)
        {
            pos += 3 * (1 << ((gif[10] & 0x07) + 1));
        }

        bool haveGce = false;
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
                    pendingTransparentIndex = gif[pos + 6];
                    pendingHasTransparency = (packed & 0x01) != 0;
                    haveGce = true;
                }
                pos = SkipSubBlocks(gif, pos + 2);
                continue;
            }
            if (marker == 0x2C) // Image Descriptor
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

                bool hasTransparency = haveGce && pendingHasTransparency;
                int transparentIndex = pendingTransparentIndex;
                for (int y = 0; y < height; y++)
                {
                    int canvasY = top + y;
                    if (canvasY < 0 || canvasY >= canvasHeight)
                    {
                        continue;
                    }
                    for (int x = 0; x < width; x++)
                    {
                        int index = indices[y * width + x];
                        if (hasTransparency && index == transparentIndex)
                        {
                            continue;
                        }
                        int canvasX = left + x;
                        if (canvasX < 0 || canvasX >= canvasWidth)
                        {
                            continue;
                        }
                        // GIF89a Color Table entries are stored Red, Green, Blue (in that order —
                        // see GifEncoder.WriteFrame's own comment on the LCT write); flip into this
                        // canvas's BGRA layout here.
                        int po = (canvasY * canvasWidth + canvasX) * 4;
                        int lo = index * 3;
                        canvas[po + 0] = lct[lo + 2]; // B
                        canvas[po + 1] = lct[lo + 1]; // G
                        canvas[po + 2] = lct[lo + 0]; // R
                        canvas[po + 3] = 255;
                    }
                }

                haveGce = false;
                pos = endPos;
                continue;
            }

            throw new InvalidOperationException($"Unexpected marker 0x{marker:X2} at {pos}");
        }
        return canvas;
    }

    private static int SkipSubBlocks(byte[] gif, int pos)
    {
        while (gif[pos] != 0x00)
        {
            int size = gif[pos];
            pos += 1 + size;
        }
        return pos + 1;
    }
}
