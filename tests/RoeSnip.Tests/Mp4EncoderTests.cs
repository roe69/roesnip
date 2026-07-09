using RoeSnip.Recording;
using Xunit;

namespace RoeSnip.Tests;

/// <summary>The pure bitrate heuristic behind Mp4Encoder.Create — no live MF SinkWriter needed.</summary>
public class Mp4EncoderTests
{
    [Fact]
    public void ComputeBitrate_TypicalSelection_IsWithinTheClampedBand()
    {
        long bitrate = Mp4Encoder.ComputeBitrate(1280, 720, 30);
        // 0.1 * 1280 * 720 * 30 = 2,764,800 — inside [2e6, 16e6], not clamped.
        Assert.Equal(2_764_800, bitrate);
    }

    [Fact]
    public void ComputeBitrate_TinySelection_ClampsToTheTwoMbpsFloor()
    {
        long bitrate = Mp4Encoder.ComputeBitrate(64, 64, 12);
        Assert.Equal(2_000_000, bitrate);
    }

    [Fact]
    public void ComputeBitrate_HugeSelection_ClampsToTheSixteenMbpsCeiling()
    {
        long bitrate = Mp4Encoder.ComputeBitrate(3840, 2160, 60);
        Assert.Equal(16_000_000, bitrate);
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(7680, 4320, 60)]
    public void ComputeBitrate_NeverEscapesTheClampedBand(int width, int height, int fps)
    {
        long bitrate = Mp4Encoder.ComputeBitrate(width, height, fps);
        Assert.InRange(bitrate, 2_000_000, 16_000_000);
    }
}
