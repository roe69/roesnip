using RoeSnip.Core.Sharing;
using SkiaSharp;
using Xunit;

namespace RoeSnip.Core.Tests.Sharing;

/// <summary>NEW in the Core port: ShareTestImage was rewritten against SdrImage/PngWriter instead of
/// System.Drawing (Windows-only since .NET 7, incompatible with Core's portable net8.0 TFM) - pixel-
/// perfect equality with the old GDI+ gradient is not required (see that class' own doc comment),
/// only "a valid small PNG the provider accepts".</summary>
public class ShareTestImageTests
{
    [Fact]
    public void CreatePngBytes_ProducesADecodable32x32Png()
    {
        byte[] png = ShareTestImage.CreatePngBytes();

        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);

        using var decoded = SKBitmap.Decode(png);
        Assert.Equal(32, decoded.Width);
        Assert.Equal(32, decoded.Height);
    }

    [Fact]
    public void CreatePngBytes_IsFullyOpaqueAndVariesAcrossTheDiagonal()
    {
        byte[] png = ShareTestImage.CreatePngBytes();
        using var decoded = SKBitmap.Decode(png);

        var topLeft = decoded.GetPixel(0, 0);
        var bottomRight = decoded.GetPixel(31, 31);

        Assert.Equal(255, topLeft.Alpha);
        Assert.Equal(255, bottomRight.Alpha);
        Assert.NotEqual(topLeft, bottomRight); // a real gradient, not a flat fill
    }
}
