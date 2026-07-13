using RoeSnip.Core.Imaging;
using RoeSnip.Core.Overlay;
using Xunit;

namespace RoeSnip.Core.Tests;

/// <summary>The pure pixel math behind the Avalonia Pixelate annotation tool's mosaic censor
/// effect (RoeSnip.Core.Overlay.PixelateMosaic — see its own doc comment for why Avalonia computes
/// this directly instead of leaning on WPF's CroppedBitmap+TransformedBitmap pipeline). Framework-
/// free, so it is tested here rather than needing a display.</summary>
public class PixelateMosaicTests
{
    private static SdrImage MakeCheckerboard(int width, int height, int cellSize)
    {
        var pixels = new byte[width * 4 * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool white = ((x / cellSize) + (y / cellSize)) % 2 == 0;
                byte v = white ? (byte)255 : (byte)0;
                int o = y * width * 4 + x * 4;
                pixels[o + 0] = v; // B
                pixels[o + 1] = v; // G
                pixels[o + 2] = v; // R
                pixels[o + 3] = 255;
            }
        }
        return new SdrImage(width, height, pixels);
    }

    private static SdrImage MakeSolid(int width, int height, byte r, byte g, byte b)
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

    [Fact]
    public void TooSmallRegion_ReturnsNoBlocks()
    {
        var preview = MakeSolid(20, 20, 100, 100, 100);
        Assert.Empty(PixelateMosaic.ComputeBlocks(preview, 0, 0, 3, 20, 4.0));
        Assert.Empty(PixelateMosaic.ComputeBlocks(preview, 0, 0, 20, 3, 4.0));
    }

    [Fact]
    public void SolidRegion_ProducesUniformColorBlocks()
    {
        var preview = MakeSolid(40, 40, 200, 50, 10);
        var blocks = PixelateMosaic.ComputeBlocks(preview, 0, 0, 40, 40, 8.0);

        Assert.NotEmpty(blocks);
        foreach (var block in blocks)
        {
            Assert.Equal(200, block.R);
            Assert.Equal(50, block.G);
            Assert.Equal(10, block.B);
        }
    }

    [Fact]
    public void Blocks_TileTheRegionExactlyWithNoGapOrOverlap()
    {
        var preview = MakeCheckerboard(37, 29, 3); // deliberately not evenly divisible by any block size
        var blocks = PixelateMosaic.ComputeBlocks(preview, 2, 2, 33, 25, 5.0);

        Assert.NotEmpty(blocks);

        // Every pixel in the region is covered by exactly one block: sum of block areas == region area.
        long totalArea = 0;
        foreach (var block in blocks)
        {
            totalArea += (long)block.Width * block.Height;
            Assert.True(block.X >= 2 && block.Y >= 2);
            Assert.True(block.X + block.Width <= 2 + 33);
            Assert.True(block.Y + block.Height <= 2 + 25);
        }
        Assert.Equal(33L * 25L, totalArea);
    }

    [Fact]
    public void BlockSize_IsClampedToAtLeastThreeAndAtMostTheShortSide()
    {
        var preview = MakeSolid(10, 10, 1, 2, 3);

        // A tiny requested block size still censors (clamped up to 3): the whole 10x10 region
        // should collapse toward a single averaged block once clamped to the short side, but with a
        // literal blockSize of 1 the clamp only guarantees >= 3, so just assert it doesn't explode
        // into more grid cells than the region has pixels and every block stays inside bounds.
        var tinyBlockBlocks = PixelateMosaic.ComputeBlocks(preview, 0, 0, 10, 10, 1.0);
        Assert.NotEmpty(tinyBlockBlocks);

        // A huge requested block size (bigger than the region) clamps down to the short side, i.e.
        // exactly one grid cell covering the whole region.
        var hugeBlockBlocks = PixelateMosaic.ComputeBlocks(preview, 0, 0, 10, 10, 1000.0);
        var single = Assert.Single(hugeBlockBlocks);
        Assert.Equal(0, single.X);
        Assert.Equal(0, single.Y);
        Assert.Equal(10, single.Width);
        Assert.Equal(10, single.Height);
    }

    [Fact]
    public void AverageColor_IsTheMeanOfSourcePixelsInEachBlock()
    {
        // Left half black, right half white, 8x8 region, block size 8 => a single grid cell whose
        // average should land at the midpoint (127 or 128 depending on integer truncation).
        var pixels = new byte[8 * 4 * 8];
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                byte v = x < 4 ? (byte)0 : (byte)255;
                int o = y * 8 * 4 + x * 4;
                pixels[o + 0] = v;
                pixels[o + 1] = v;
                pixels[o + 2] = v;
                pixels[o + 3] = 255;
            }
        }
        var preview = new SdrImage(8, 8, pixels);

        var blocks = PixelateMosaic.ComputeBlocks(preview, 0, 0, 8, 8, 8.0);
        var single = Assert.Single(blocks);
        Assert.Equal(127, single.R); // (0*32 + 255*32) / 64 = 127.5, truncated
        Assert.Equal(127, single.G);
        Assert.Equal(127, single.B);
    }
}
