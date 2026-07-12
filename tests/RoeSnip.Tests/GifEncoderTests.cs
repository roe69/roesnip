using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media.Imaging;
using RoeSnip.Imaging;
using RoeSnip.Recording;
using Xunit;

namespace RoeSnip.Tests;

/// <summary>GifEncoder streams: each frame's GCE + Image Descriptor + Local Color Table + LZW data
/// is built and appended to the output file as it arrives, via the encoder's own octree
/// quantizer/LZW compressor (see <c>RoeSnip.Recording.Gif</c>) rather than a live WPF encode salvage.
/// Covers the streaming lifecycle (Create/AddFrame/FinalizeAndClose end to end, decoded back through
/// <see cref="GifBitmapDecoder"/>), the timestamped delta path's skip/changed-region/delay
/// patch-behind behavior, and <see cref="GifEncoder.TryGetChangedBounds"/> directly.</summary>
public class GifEncoderTests
{
    private static string TempGifPath() => Path.Combine(Path.GetTempPath(), $"gifencodertest_{Guid.NewGuid():N}.gif");

    private static SdrImage SolidFrame(int width, int height, byte b, byte g, byte r)
    {
        var pixels = new byte[width * 4 * height];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i + 0] = b;
            pixels[i + 1] = g;
            pixels[i + 2] = r;
            pixels[i + 3] = 255;
        }
        return new SdrImage(width, height, pixels);
    }

    // ---- Streaming lifecycle, through the live WPF encoder ----

    [Fact]
    public void Create_ThenFinalize_ZeroFrames_YieldsWellFormedEmptyGif()
    {
        string path = TempGifPath();
        try
        {
            var encoder = GifEncoder.Create(path, 4, 3);
            encoder.FinalizeAndClose();

            byte[] bytes = File.ReadAllBytes(path);

            Assert.Equal((byte)'G', bytes[0]);
            Assert.Equal((byte)'I', bytes[1]);
            Assert.Equal((byte)'F', bytes[2]);
            Assert.Equal((byte)'8', bytes[3]);
            Assert.Equal((byte)'9', bytes[4]);
            Assert.Equal((byte)'a', bytes[5]);
            Assert.Equal(4, bytes[6] | (bytes[7] << 8));
            Assert.Equal(3, bytes[8] | (bytes[9] << 8));
            Assert.Equal(0x00, bytes[10]); // no Global Color Table

            // NETSCAPE2.0 loop extension starts right after the 13-byte header (no GCT).
            Assert.Equal(0x21, bytes[13]);
            Assert.Equal(0xFF, bytes[14]);
            Assert.Equal((byte)'N', bytes[16]);
            Assert.Equal((byte)'0', bytes[26]);
            Assert.Equal(0x00, bytes[31]); // block terminator

            // Nothing but the trailer follows: zero frames were added.
            Assert.Equal(0x3B, bytes[32]);
            Assert.Equal(33, bytes.Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FinalizeAndClose_IsIdempotent()
    {
        string path = TempGifPath();
        try
        {
            var encoder = GifEncoder.Create(path, 2, 2);
            encoder.FinalizeAndClose();
            encoder.FinalizeAndClose(); // must not throw
            encoder.Dispose();          // must not throw either
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Dispose_AfterFinalizeAndClose_IsSafe()
    {
        string path = TempGifPath();
        try
        {
            var encoder = GifEncoder.Create(path, 2, 2);
            encoder.AddFrame(SolidFrame(2, 2, 10, 20, 30), 50);
            encoder.FinalizeAndClose();
            encoder.Dispose(); // must not throw, must not corrupt the already-closed file
            byte[] bytes = File.ReadAllBytes(path);
            Assert.Equal(0x3B, bytes[^1]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // Minimal test-side block walker, independent of GifEncoder's own internals, so these tests
    // verify the actual on-disk byte layout rather than trusting the production code that wrote it.
    private sealed record ParsedFrame(int Delay, bool HasLct, int Left, int Top, int Width, int Height);

    private static List<ParsedFrame> ParseFrames(byte[] gif)
    {
        var frames = new List<ParsedFrame>();
        int pos = 13;
        if ((gif[10] & 0x80) != 0)
        {
            pos += 3 * (1 << ((gif[10] & 0x07) + 1));
        }

        int? pendingDelay = null;
        while (pos < gif.Length)
        {
            byte marker = gif[pos];
            if (marker == 0x3B)
            {
                Assert.Equal(gif.Length - 1, pos); // exactly one trailer, and it's the last byte
                break;
            }
            if (marker == 0x21)
            {
                if (gif[pos + 1] == 0xF9)
                {
                    pendingDelay = gif[pos + 4] | (gif[pos + 5] << 8);
                }
                pos = SkipSubBlocks(gif, pos + 2);
            }
            else if (marker == 0x2C)
            {
                int left = gif[pos + 1] | (gif[pos + 2] << 8);
                int top = gif[pos + 3] | (gif[pos + 4] << 8);
                int width = gif[pos + 5] | (gif[pos + 6] << 8);
                int height = gif[pos + 7] | (gif[pos + 8] << 8);
                byte packed = gif[pos + 9];
                bool hasLct = (packed & 0x80) != 0;

                Assert.NotNull(pendingDelay); // every image descriptor must be preceded by a GCE
                frames.Add(new ParsedFrame(pendingDelay!.Value, hasLct, left, top, width, height));
                pendingDelay = null;

                int dataStart = pos + 10;
                if (hasLct)
                {
                    dataStart += 3 * (1 << ((packed & 0x07) + 1));
                }
                pos = SkipSubBlocks(gif, dataStart + 1);
            }
            else
            {
                throw new InvalidOperationException($"Unexpected marker 0x{marker:X2} at {pos}");
            }
        }
        return frames;
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

    [Fact]
    public void AddFrame_ThreeFramesWithDistinctDelays_ProducesThreeWellFormedFrames()
    {
        string path = TempGifPath();
        try
        {
            var encoder = GifEncoder.Create(path, 3, 2);
            encoder.AddFrame(SolidFrame(3, 2, 255, 0, 0), 10);
            encoder.AddFrame(SolidFrame(3, 2, 0, 255, 0), 20);
            encoder.AddFrame(SolidFrame(3, 2, 0, 0, 255), 30);
            encoder.FinalizeAndClose();

            byte[] bytes = File.ReadAllBytes(path);
            List<ParsedFrame> frames = ParseFrames(bytes);

            Assert.Equal(3, frames.Count);
            Assert.Equal(new[] { 10, 20, 30 }, frames.ConvertAll(f => f.Delay));
            foreach (var f in frames)
            {
                Assert.True(f.HasLct);
                Assert.Equal(0, f.Left);
                Assert.Equal(0, f.Top);
                Assert.Equal(3, f.Width);
                Assert.Equal(2, f.Height);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AddFrame_FileSizeGrowsRoughlyLinearlyWithFrameCount()
    {
        string path1 = TempGifPath();
        string path3 = TempGifPath();
        try
        {
            var encoder1 = GifEncoder.Create(path1, 8, 8);
            encoder1.AddFrame(SolidFrame(8, 8, 1, 2, 3), 10);
            encoder1.FinalizeAndClose();
            long size1 = new FileInfo(path1).Length;

            var encoder3 = GifEncoder.Create(path3, 8, 8);
            encoder3.AddFrame(SolidFrame(8, 8, 1, 2, 3), 10);
            encoder3.AddFrame(SolidFrame(8, 8, 4, 5, 6), 10);
            encoder3.AddFrame(SolidFrame(8, 8, 7, 8, 9), 10);
            encoder3.FinalizeAndClose();
            long size3 = new FileInfo(path3).Length;

            // Not exact (per-frame LCT/LZW overhead varies a little with palette), but three frames
            // must land well above one and well below a wildly super-linear blowup.
            Assert.True(size3 > size1, $"expected {size3} > {size1}");
            Assert.True(size3 < size1 * 6, $"expected {size3} < {size1 * 6}");
        }
        finally
        {
            File.Delete(path1);
            File.Delete(path3);
        }
    }

    [Fact]
    public void AddFrame_OutputDecodesViaGifBitmapDecoder_RoundTrip()
    {
        string path = TempGifPath();
        try
        {
            var encoder = GifEncoder.Create(path, 5, 4);
            encoder.AddFrame(SolidFrame(5, 4, 255, 0, 0), 10);
            encoder.AddFrame(SolidFrame(5, 4, 0, 255, 0), 15);
            encoder.AddFrame(SolidFrame(5, 4, 0, 0, 255), 25);
            encoder.FinalizeAndClose();

            using var stream = File.OpenRead(path);
            var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

            Assert.Equal(3, decoder.Frames.Count);
            foreach (var frame in decoder.Frames)
            {
                Assert.Equal(5, frame.PixelWidth);
                Assert.Equal(4, frame.PixelHeight);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- Timestamped AddFrame: skip-identical, changed-region delta, delay patch-behind ----
    // Timestamps below use ticksPerSecond=1000 so a tick reads as a millisecond.

    private static SdrImage WithChangedRect(SdrImage source, int left, int top, int width, int height, byte b, byte g, byte r)
    {
        var pixels = (byte[])source.Pixels.Clone();
        for (int y = top; y < top + height; y++)
        {
            for (int x = left; x < left + width; x++)
            {
                int i = (y * source.Width + x) * 4;
                pixels[i + 0] = b;
                pixels[i + 1] = g;
                pixels[i + 2] = r;
            }
        }
        return new SdrImage(source.Width, source.Height, pixels);
    }

    [Fact]
    public void AddFrame_Timestamped_IdenticalFrames_AreSkipped()
    {
        string path = TempGifPath();
        try
        {
            var encoder = GifEncoder.Create(path, 4, 4, timestampTicksPerSecond: 1000);
            encoder.AddFrame(SolidFrame(4, 4, 10, 20, 30), 0L);
            encoder.AddFrame(SolidFrame(4, 4, 10, 20, 30), 100L);
            encoder.AddFrame(SolidFrame(4, 4, 10, 20, 30), 200L);
            encoder.FinalizeAndClose();

            var frames = ParseFrames(File.ReadAllBytes(path));
            var frame = Assert.Single(frames);
            Assert.Equal((0, 0, 4, 4), (frame.Left, frame.Top, frame.Width, frame.Height));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AddFrame_Timestamped_ChangedRegion_EmitsSubRectAtItsCanvasPosition()
    {
        string path = TempGifPath();
        try
        {
            var first = SolidFrame(6, 5, 10, 20, 30);
            var second = WithChangedRect(first, left: 3, top: 2, width: 2, height: 3, 200, 100, 50);

            var encoder = GifEncoder.Create(path, 6, 5, timestampTicksPerSecond: 1000);
            encoder.AddFrame(first, 0L);
            encoder.AddFrame(second, 250L);
            encoder.FinalizeAndClose();

            byte[] bytes = File.ReadAllBytes(path);
            var frames = ParseFrames(bytes);
            Assert.Equal(2, frames.Count);
            Assert.Equal((0, 0, 6, 5), (frames[0].Left, frames[0].Top, frames[0].Width, frames[0].Height));
            Assert.Equal(25, frames[0].Delay); // patched to the real 250ms once frame 2 landed
            Assert.Equal((3, 2, 2, 3), (frames[1].Left, frames[1].Top, frames[1].Width, frames[1].Height));

            // And the whole file still decodes as a 2-frame animation.
            using var stream = new MemoryStream(bytes);
            var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            Assert.Equal(2, decoder.Frames.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AddFrame_Timestamped_DelaysArePatchedToRealDurations()
    {
        string path = TempGifPath();
        try
        {
            var encoder = GifEncoder.Create(path, 3, 3, timestampTicksPerSecond: 1000);
            encoder.AddFrame(SolidFrame(3, 3, 255, 0, 0), 0L);
            encoder.AddFrame(SolidFrame(3, 3, 0, 255, 0), 500L);
            encoder.AddFrame(SolidFrame(3, 3, 0, 0, 255), 800L);
            encoder.FinalizeAndClose();

            var frames = ParseFrames(File.ReadAllBytes(path));
            Assert.Equal(3, frames.Count);
            Assert.Equal(50, frames[0].Delay);
            Assert.Equal(30, frames[1].Delay);
            // The final frame has no successor to measure against; it keeps the provisional delay.
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AddFrame_Timestamped_SkippedTimeFoldsIntoPreviousDelay()
    {
        string path = TempGifPath();
        try
        {
            var encoder = GifEncoder.Create(path, 3, 3, timestampTicksPerSecond: 1000);
            encoder.AddFrame(SolidFrame(3, 3, 255, 0, 0), 0L);
            encoder.AddFrame(SolidFrame(3, 3, 255, 0, 0), 300L); // identical — skipped
            encoder.AddFrame(SolidFrame(3, 3, 0, 255, 0), 600L);
            encoder.FinalizeAndClose();

            var frames = ParseFrames(File.ReadAllBytes(path));
            Assert.Equal(2, frames.Count);
            Assert.Equal(60, frames[0].Delay); // the full 600ms, skipped frame included
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AddFrame_Timestamped_DelayNeverDropsBelowTwoCentiseconds()
    {
        string path = TempGifPath();
        try
        {
            // A single-pixel change (not a full-canvas one) keeps this under the emit-rate shaping's
            // large/huge-motion delay floors, so the only floor left in play is MinDelayCs itself.
            var first = SolidFrame(3, 3, 255, 0, 0);
            var second = WithChangedRect(first, left: 0, top: 0, width: 1, height: 1, 0, 255, 0);
            var encoder = GifEncoder.Create(path, 3, 3, timestampTicksPerSecond: 1000);
            encoder.AddFrame(first, 0L);
            encoder.AddFrame(second, 5L); // 0.5cs apart
            encoder.FinalizeAndClose();

            var frames = ParseFrames(File.ReadAllBytes(path));
            Assert.Equal(2, frames.Count);
            Assert.Equal(2, frames[0].Delay); // floored — browsers clamp 0-1cs to 10cs
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FinalizeAndClose_WithEndTimestamp_PatchesTheFinalFrameDelay()
    {
        string path = TempGifPath();
        try
        {
            var encoder = GifEncoder.Create(path, 3, 3, timestampTicksPerSecond: 1000);
            encoder.AddFrame(SolidFrame(3, 3, 255, 0, 0), 0L);
            encoder.AddFrame(SolidFrame(3, 3, 0, 255, 0), 400L);
            encoder.AddFrame(SolidFrame(3, 3, 0, 255, 0), 700L);  // identical — skipped
            encoder.FinalizeAndClose(2400L); // user held the final state for 2s before Stop

            var frames = ParseFrames(File.ReadAllBytes(path));
            Assert.Equal(2, frames.Count);
            Assert.Equal(40, frames[0].Delay);
            Assert.Equal(200, frames[1].Delay); // the whole held tail, skipped frame included
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FinalizeAndClose_WithEndTimestamp_ZeroFrames_IsSafe()
    {
        string path = TempGifPath();
        try
        {
            var encoder = GifEncoder.Create(path, 3, 3, timestampTicksPerSecond: 1000);
            encoder.FinalizeAndClose(5000L); // nothing to patch — must not throw or corrupt
            byte[] bytes = File.ReadAllBytes(path);
            Assert.Equal(0x3B, bytes[^1]);
            Assert.Empty(ParseFrames(bytes));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AddFrame_Timestamped_DeltaEncoding_IsMuchSmallerThanFullFrames()
    {
        string deltaPath = TempGifPath();
        string fullPath = TempGifPath();
        try
        {
            // 30 frames on a noisy (real-content-like, poorly compressible) 64x64 canvas where
            // only a 4x4 block moves. Deterministic xorshift noise keeps the test stable.
            var frames = new List<SdrImage>();
            var noisePixels = new byte[64 * 4 * 64];
            uint rng = 0x9E3779B9;
            for (int i = 0; i < noisePixels.Length; i += 4)
            {
                rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
                noisePixels[i + 0] = (byte)rng;
                noisePixels[i + 1] = (byte)(rng >> 8);
                noisePixels[i + 2] = (byte)(rng >> 16);
                noisePixels[i + 3] = 255;
            }
            var baseFrame = new SdrImage(64, 64, noisePixels);
            for (int i = 0; i < 30; i++)
            {
                frames.Add(WithChangedRect(baseFrame, left: i, top: i, width: 4, height: 4, 200, 150, 50));
            }

            var delta = GifEncoder.Create(deltaPath, 64, 64, timestampTicksPerSecond: 1000);
            var full = GifEncoder.Create(fullPath, 64, 64, timestampTicksPerSecond: 1000);
            for (int i = 0; i < frames.Count; i++)
            {
                delta.AddFrame(frames[i], i * 30L);
                full.AddFrame(frames[i], (ushort)3);
            }
            delta.FinalizeAndClose(900L);
            full.FinalizeAndClose();

            long deltaSize = new FileInfo(deltaPath).Length;
            long fullSize = new FileInfo(fullPath).Length;
            Assert.True(deltaSize < fullSize / 3,
                $"Delta GIF ({deltaSize} bytes) should be far smaller than full-frame GIF ({fullSize} bytes).");
        }
        finally
        {
            File.Delete(deltaPath);
            File.Delete(fullPath);
        }
    }

    // ---- TryGetChangedBounds ----

    [Fact]
    public void TryGetChangedBounds_IdenticalFrames_ReturnsFalse()
    {
        var a = SolidFrame(5, 4, 1, 2, 3);
        var b = SolidFrame(5, 4, 1, 2, 3);
        Assert.False(GifEncoder.TryGetChangedBounds(a.Pixels, b.Pixels, 5, 4, out _));
    }

    [Fact]
    public void TryGetChangedBounds_SinglePixel_YieldsTightBox()
    {
        var a = SolidFrame(5, 4, 1, 2, 3);
        var b = WithChangedRect(a, left: 2, top: 1, width: 1, height: 1, 9, 9, 9);
        Assert.True(GifEncoder.TryGetChangedBounds(a.Pixels, b.Pixels, 5, 4, out var box));
        Assert.Equal((2, 1, 1, 1), (box.Left, box.Top, box.Width, box.Height));
    }

    [Fact]
    public void TryGetChangedBounds_OppositeCorners_SpanTheWholeCanvas()
    {
        var a = SolidFrame(5, 4, 1, 2, 3);
        var b = WithChangedRect(a, 0, 0, 1, 1, 9, 9, 9);
        b = WithChangedRect(b, 4, 3, 1, 1, 8, 8, 8);
        Assert.True(GifEncoder.TryGetChangedBounds(a.Pixels, b.Pixels, 5, 4, out var box));
        Assert.Equal((0, 0, 5, 4), (box.Left, box.Top, box.Width, box.Height));
    }
}
