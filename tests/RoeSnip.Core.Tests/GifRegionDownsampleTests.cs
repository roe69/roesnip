using RoeSnip.Core.Recording;
using Xunit;

namespace RoeSnip.Core.Tests;

/// <summary>ScaledCanvasDimension/BoxDownsample — ported verbatim from the WPF app's
/// RecordingController (ScaledCanvasDimension/BoxDownsampleGifFrame), the GIF Compact/Minimal
/// size-tier downscale math.</summary>
public class GifRegionDownsampleTests
{
    [Theory]
    [InlineData(802, 0.5, 400)]  // 401 floored to even 400
    [InlineData(402, 0.75, 300)] // 301.5 -> 301 floored to even 300
    [InlineData(800, 0.5, 400)]  // already even after scaling
    [InlineData(3, 0.5, 2)]      // floor at the minimum of 2
    public void ScaledCanvasDimension_FloorsToEven_MinimumTwo(int fullSize, double scale, int expected)
    {
        Assert.Equal(expected, GifRegionDownsample.ScaledCanvasDimension(fullSize, scale));
    }

    [Fact]
    public void BoxDownsample_HalfScale_AveragesTwoByTwoBlocks()
    {
        // 4x4 source, 2x2 destination — each destination pixel averages a 2x2 source block.
        // Block (0,0): four pixels of (10,20,30) — average is exactly (10,20,30).
        // Block (1,1): four pixels of (100,150,200) — average is exactly (100,150,200).
        byte[] src = new byte[4 * 4 * 4];
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                int o = (y * 4 + x) * 4;
                bool bottomRight = x >= 2 && y >= 2;
                src[o + 0] = bottomRight ? (byte)200 : (byte)30; // B
                src[o + 1] = bottomRight ? (byte)150 : (byte)20; // G
                src[o + 2] = bottomRight ? (byte)100 : (byte)10; // R
                src[o + 3] = 255;
            }
        }

        byte[] dst = new byte[2 * 2 * 4];
        GifRegionDownsample.BoxDownsample(src, 4, 4, dst, 2, 2);

        // Top-left destination pixel (0,0) averages source block (0,0)-(1,1).
        Assert.Equal(30, dst[0]);
        Assert.Equal(20, dst[1]);
        Assert.Equal(10, dst[2]);
        Assert.Equal(255, dst[3]);

        // Bottom-right destination pixel (1,1) averages source block (2,2)-(3,3).
        int bro = (1 * 2 + 1) * 4;
        Assert.Equal(200, dst[bro + 0]);
        Assert.Equal(150, dst[bro + 1]);
        Assert.Equal(100, dst[bro + 2]);
        Assert.Equal(255, dst[bro + 3]);
    }

    [Fact]
    public void BoxDownsample_UniformSource_ProducesUniformDestination()
    {
        // A flat-color source at a non-integer scale ratio (0.75-equivalent 4->3) must still produce
        // the exact same flat color throughout — the mixed 1/2-pixel source-rect pattern must never
        // introduce a rounding artifact on uniform content.
        byte[] src = new byte[4 * 4 * 4];
        for (int i = 0; i < src.Length; i += 4)
        {
            src[i + 0] = 50; src[i + 1] = 100; src[i + 2] = 150; src[i + 3] = 255;
        }

        byte[] dst = new byte[3 * 3 * 4];
        GifRegionDownsample.BoxDownsample(src, 4, 4, dst, 3, 3);

        for (int i = 0; i < dst.Length; i += 4)
        {
            Assert.Equal(50, dst[i + 0]);
            Assert.Equal(100, dst[i + 1]);
            Assert.Equal(150, dst[i + 2]);
            Assert.Equal(255, dst[i + 3]);
        }
    }
}
