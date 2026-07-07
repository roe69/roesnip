using System;
using System.IO;
using RoeSnip.Core.Imaging;
using SkiaSharp;
using Xunit;

namespace RoeSnip.Core.Tests;

/// <summary>NEW in the Core port (not in the WPF test suite): the SkiaSharp PngWriter replaced the
/// WPF PngBitmapEncoder implementation (PLAN-XPLAT.md §2.7), so its BGRA byte-exact round-trip is
/// verified here — this is also why this test project references SkiaSharp.NativeAssets.Win32
/// (PLAN-XPLAT.md §1.2).</summary>
public class PngWriterTests : IDisposable
{
    private readonly string _tempDir;

    public PngWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"roesnip_core_pngwriter_test_{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private static SdrImage MakeTestImage()
    {
        // 2x2, distinct opaque colors per pixel (BGRA order).
        var pixels = new byte[]
        {
            255,   0,   0, 255,    0, 255,   0, 255,  // blue, green
              0,   0, 255, 255,   10,  20,  30, 255,  // red, dark mix
        };
        return new SdrImage(2, 2, pixels);
    }

    [Fact]
    public void Encode_RoundTripsBgraBytesExactly()
    {
        var image = MakeTestImage();

        byte[] png = PngWriter.Encode(image);
        using var decoded = SKBitmap.Decode(png);

        Assert.Equal(2, decoded.Width);
        Assert.Equal(2, decoded.Height);
        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 2; x++)
            {
                int o = (y * 2 + x) * 4;
                var expected = new SKColor(
                    image.Pixels[o + 2], image.Pixels[o + 1], image.Pixels[o + 0], image.Pixels[o + 3]);
                Assert.Equal(expected, decoded.GetPixel(x, y));
            }
        }
    }

    [Fact]
    public void WriteFile_ProducesValidPngOnDisk()
    {
        Directory.CreateDirectory(_tempDir);
        string path = Path.Combine(_tempDir, "out.png");
        var image = MakeTestImage();

        PngWriter.WriteFile(path, image);

        Assert.True(File.Exists(path));
        byte[] bytes = File.ReadAllBytes(path);
        // PNG signature.
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, bytes[..8]);
        using var decoded = SKBitmap.Decode(bytes);
        Assert.Equal(2, decoded.Width);
        Assert.Equal(2, decoded.Height);
    }
}
