using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RoeSnip.Imaging;
using RoeSnip.Recording;
using RoeSnip.Recording.Gif;
using Xunit;

namespace RoeSnip.Tests;

/// <summary>Ground-truth regression coverage for <see cref="GifLzwEncoder"/>: decodes its output
/// through REAL, independent GIF decoders (WPF's <see cref="GifBitmapDecoder"/> and GDI+'s
/// <see cref="Image.FromStream(Stream)"/>) rather than only the test-only <see cref="GifLzwTestDecoder"/>
/// helper — a shared test decoder that happens to agree with the encoder proves nothing if both sides
/// share the same bug (exactly what happened here: a code-width-growth off-by-one in the encoder
/// went undetected for months because <c>GifLzwTestDecoder</c> was tuned to match it instead of the
/// GIF89a spec, and both real decoders rejected the result). See <see cref="GifLzwEncoder"/>'s
/// <c>Encode</c> doc comment and <see cref="GifLzwTestDecoder"/>'s class doc comment for the actual
/// defect: the encoder must grow its code width when its post-increment <c>nextCode</c> reaches
/// <c>(1 &lt;&lt; codeWidth) + 1</c>, not <c>1 &lt;&lt; codeWidth</c> — growing one entry too early
/// desyncs the bitstream from every real-world decoder.</summary>
public class GifLzwEncoderRealDecoderTests
{
    // ---- A minimal, hand-built single-frame GIF container around raw LZW payload bytes, so these
    // tests exercise GifLzwEncoder.Encode's bitstream in isolation from GifEncoder's palette/box/GCE
    // orchestration. The palette is a pure grayscale ramp (index i -> gray level i), so a decoded
    // pixel's blue channel IS the original palette index — real-decoder output can be checked
    // directly against the source indices with no LUT/quantization step in between. ----

    private static byte[] BuildMinimalGif(byte[] lzwPayload, int width, int height, int minCodeSize)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[]
        {
            (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a',
            (byte)(width & 0xFF), (byte)(width >> 8),
            (byte)(height & 0xFF), (byte)(height >> 8),
            0x00, 0x00, 0x00,
        });
        ms.WriteByte(0x2C); // image descriptor
        ms.WriteByte(0); ms.WriteByte(0);
        ms.WriteByte(0); ms.WriteByte(0);
        ms.WriteByte((byte)(width & 0xFF)); ms.WriteByte((byte)(width >> 8));
        ms.WriteByte((byte)(height & 0xFF)); ms.WriteByte((byte)(height >> 8));
        int colorCount = 1 << minCodeSize;
        ms.WriteByte((byte)(0x80 | (minCodeSize - 1))); // LCT present, size bits = minCodeSize-1
        for (int i = 0; i < colorCount; i++)
        {
            ms.WriteByte((byte)i); ms.WriteByte((byte)i); ms.WriteByte((byte)i); // grayscale ramp
        }
        ms.Write(lzwPayload);
        ms.WriteByte(0x3B); // trailer
        return ms.ToArray();
    }

    /// <summary>Decodes via WPF's <see cref="GifBitmapDecoder"/>, converts to a known pixel format,
    /// and returns the raw BGRA bytes — throws if the real decoder rejects the stream.</summary>
    private static byte[] DecodeWpfBgra(byte[] gif, int width, int height)
    {
        using var ms = new MemoryStream(gif);
        var decoder = new GifBitmapDecoder(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var buf = new byte[width * height * 4];
        converted.CopyPixels(buf, width * 4, 0);
        return buf;
    }

    /// <summary>Decodes via GDI+ — an entirely separate real-world decoder implementation from WPF's,
    /// so the two catching the SAME defect (as they did before the fix) is strong evidence the defect
    /// is in the bitstream itself, not a quirk of one decoder.</summary>
    private static void AssertGdiPlusDecodes(byte[] gif)
    {
        using var ms = new MemoryStream(gif);
        using var img = Image.FromStream(ms);
        using var bmp = new Bitmap(img);
        bmp.GetPixel(0, 0); // forces the decoder to actually materialize pixel data
    }

    private static byte[] RunLengthHeavyIndices(int alphabetSize, int length, int seed)
    {
        var rng = new Random(seed);
        var data = new byte[length];
        int i = 0;
        while (i < length)
        {
            byte value = (byte)rng.Next(alphabetSize);
            int runLength = Math.Min(length - i, rng.Next(5, 40));
            for (int j = 0; j < runLength; j++) data[i++] = value;
        }
        return data;
    }

    private static byte[] HighEntropyIndices(int alphabetSize, int length, int seed)
    {
        var rng = new Random(seed);
        var data = new byte[length];
        for (int i = 0; i < length; i++) data[i] = (byte)rng.Next(alphabetSize);
        return data;
    }

    /// <summary>Decodes <paramref name="gif"/> through BOTH real decoders and the shared
    /// <see cref="GifLzwTestDecoder"/>, asserting every one of them recovers exactly
    /// <paramref name="indices"/> — the real decoders via their composited pixel output (blue channel
    /// == index, thanks to the grayscale-ramp palette), the shared helper directly against the raw
    /// index stream.</summary>
    /// <summary><paramref name="width"/>/<paramref name="height"/> must multiply out to
    /// <paramref name="indices"/>.Length and each stay under the GIF format's 16-bit dimension limit
    /// (65535) — a caller-picked single-row layout would silently truncate the LE16 width field for
    /// anything over 65535 indices, corrupting the container independent of anything under test.</summary>
    private static void AssertAllDecodersAgree(byte[] indices, int minCodeSize, int width, int height)
    {
        Assert.Equal(indices.Length, width * height);
        Assert.InRange(width, 1, 65535);
        Assert.InRange(height, 1, 65535);

        var encoder = new GifLzwEncoder();
        using var ms = new MemoryStream();
        encoder.Encode(ms, indices, minCodeSize);
        byte[] gif = BuildMinimalGif(ms.ToArray(), width, height, minCodeSize);

        byte[] wpfBgra = DecodeWpfBgra(gif, width, height);
        for (int i = 0; i < indices.Length; i++)
        {
            Assert.Equal(indices[i], wpfBgra[i * 4]); // blue channel == palette index == source index
        }

        AssertGdiPlusDecodes(gif);

        byte[] testDecoded = GifLzwTestDecoder.Decode(gif, 13 + 10 + (1 << minCodeSize) * 3, out int endPos);
        Assert.Equal(gif.Length - 1, endPos); // consumed exactly up to (not including) the trailer
        Assert.Equal(indices, testDecoded);
    }

    // ---- (a) Run-heavy data crossing the 8->9 code-width boundary, across every minCodeSize. ----

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void Encode_RunHeavyDataCrossing8To9BitBoundary_DecodesIdenticallyOnRealAndTestDecoders(int minCodeSize)
    {
        int alphabetSize = 1 << minCodeSize;
        // Long enough, and varied enough (run-length mixed with singletons breaking runs), that the
        // dictionary reliably grows past 256 entries and crosses the 8->9 bit code-width boundary —
        // the exact region the original bug manifested in on real recording content.
        byte[] indices = RunLengthHeavyIndices(alphabetSize, length: 30_000, seed: 1000 + minCodeSize);
        AssertAllDecodersAgree(indices, minCodeSize, width: 300, height: 100);
    }

    // ---- (a) High-entropy data forcing a 4096-entry dictionary-full clear-code reset mid-stream. ----

    [Fact]
    public void Encode_HighEntropyData_ForcesClearCodeReset_DecodesIdenticallyOnRealAndTestDecoders()
    {
        byte[] indices = HighEntropyIndices(alphabetSize: 256, length: 200_000, seed: 2024);
        AssertAllDecodersAgree(indices, minCodeSize: 8, width: 500, height: 400);
    }

    // ---- (a) The exact bisected regression shape from the original bug report: a long run of a
    // single solid-color index mixed with just enough variety to force real code-width growth
    // (a literal all-one-value run alone compresses to only a handful of dictionary entries via
    // LZW's exponential match-doubling and can never reach the 8->9 boundary; this interleaves a
    // sparse "glyph" index periodically, matching what real UI content quantizes to). ----

    [Fact]
    public void Encode_SolidColorWithSparseVariation_PastOldFailureBoundary_Decodes()
    {
        const int minCodeSize = 3; // matches the real StaticUiFrame keyframe's palette size
        var indices = new byte[80_000];
        for (int i = 0; i < indices.Length; i++)
        {
            indices[i] = (i % 37 == 0) ? (byte)1 : (byte)0; // mostly-solid, sparse breaks
        }
        AssertAllDecodersAgree(indices, minCodeSize, width: 400, height: 200);
    }

    // ---- (b) End-to-end: a 640x400 solid+glyphs keyframe (same shape as GifSizeBenchmarkTests'
    // static scenario) through the REAL GifEncoder pipeline, decoded by GifBitmapDecoder with
    // pixel-content assertions against known painted regions — not just "no throw". ----

    private static void SetPixel(byte[] pixels, int width, int x, int y, byte b, byte g, byte r)
    {
        int i = (y * width + x) * 4;
        pixels[i + 0] = b; pixels[i + 1] = g; pixels[i + 2] = r; pixels[i + 3] = 255;
    }

    private static void FillRect(byte[] pixels, int width, int height, int left, int top, int w, int h, byte b, byte g, byte r)
    {
        int right = Math.Min(left + w, width);
        int bottom = Math.Min(top + h, height);
        for (int y = Math.Max(0, top); y < bottom; y++)
            for (int x = Math.Max(0, left); x < right; x++)
                SetPixel(pixels, width, x, y, b, g, r);
    }

    /// <summary>Same shape as <c>GifSizeBenchmarkTests.StaticUiFrame</c> (flat background, title bar
    /// band, glyph-block grid, a moving cursor block) — the exact content shape whose keyframe the
    /// original bug report says "fails to decode".</summary>
    private static SdrImage StaticUiFrame(int width, int height)
    {
        var pixels = new byte[width * 4 * height];
        FillRect(pixels, width, height, 0, 0, width, height, 40, 40, 40);   // body background
        FillRect(pixels, width, height, 0, 0, width, 28, 60, 60, 60);       // title bar
        for (int row = 0; row < 12; row++)
        {
            for (int col = 0; col < 30; col++)
            {
                int gx = 8 + col * 20, gy = 40 + row * 28;
                if (gx + 12 > width || gy + 10 > height) continue;
                if (((row * 31 + col * 17) % 5) != 0)
                {
                    FillRect(pixels, width, height, gx, gy, 12, 10, 210, 210, 210); // glyph block
                }
            }
        }
        FillRect(pixels, width, height, 20, 40, 16, 16, 30, 200, 255); // cursor block
        return new SdrImage(width, height, pixels);
    }

    [Fact]
    public void GifEncoder_StaticUiKeyframe_640x400_DecodesViaGifBitmapDecoder_WithCorrectPixelContent()
    {
        string path = Path.Combine(Path.GetTempPath(), $"gifrealdecodertest_{Guid.NewGuid():N}.gif");
        try
        {
            var encoder = GifEncoder.Create(path, 640, 400, timestampTicksPerSecond: 1000);
            encoder.AddFrame(StaticUiFrame(640, 400), 0L);
            encoder.FinalizeAndClose(20L);

            byte[] gif = File.ReadAllBytes(path);

            // Must not throw on either real decoder — this exact content shape (solid regions +
            // sparse glyph blocks) is precisely what triggered the original bug.
            AssertGdiPlusDecodes(gif);

            using var ms = new MemoryStream(gif);
            var decoder = new GifBitmapDecoder(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            Assert.Single(decoder.Frames);
            var frame = decoder.Frames[0];
            Assert.Equal(640, frame.PixelWidth);
            Assert.Equal(400, frame.PixelHeight);

            var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            var buf = new byte[640 * 400 * 4];
            converted.CopyPixels(buf, 640 * 4, 0);

            (byte B, byte G, byte R) At(int x, int y)
            {
                int o = (y * 640 + x) * 4;
                return (buf[o], buf[o + 1], buf[o + 2]);
            }

            // Body background, away from every painted feature.
            Assert.Equal(((byte)40, (byte)40, (byte)40), At(400, 200));
            // Title bar band.
            Assert.Equal(((byte)60, (byte)60, (byte)60), At(300, 5));
            // A known-on glyph block clear of the cursor's rect (20,40)-(36,56): row=0,col=2 ->
            // (0*31+2*17)%5 == 34%5 == 4 != 0 -> ON, block at gx=48,gy=40.
            Assert.Equal(((byte)210, (byte)210, (byte)210), At(50, 44));
            // The cursor block at (20,40)-(36,56).
            Assert.Equal(((byte)30, (byte)200, (byte)255), At(25, 45));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
