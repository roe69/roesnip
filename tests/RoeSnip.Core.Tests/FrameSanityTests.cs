using RoeSnip.Core.Capture;
using Xunit;

namespace RoeSnip.Core.Tests;

public class FrameSanityTests
{
    [Fact]
    public void IsAllZero_EmptyBuffer_IsTrue()
    {
        Assert.True(FrameSanity.IsAllZero(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void IsAllZero_AllZeroBuffer_IsTrue()
    {
        var buffer = new byte[64 * 1024]; // arrays are zero-initialized
        Assert.True(FrameSanity.IsAllZero(buffer));
    }

    [Fact]
    public void IsAllZero_SingleNonZeroAtStart_IsFalse()
    {
        var buffer = new byte[4096];
        buffer[0] = 1;
        Assert.False(FrameSanity.IsAllZero(buffer));
    }

    [Fact]
    public void IsAllZero_SingleNonZeroAtEnd_IsFalse()
    {
        var buffer = new byte[4096];
        buffer[^1] = 255;
        Assert.False(FrameSanity.IsAllZero(buffer));
    }

    [Fact]
    public void IsAllZero_SingleNonZeroInMiddle_IsFalse()
    {
        var buffer = new byte[4097]; // odd length to exercise the vectorized tail path
        buffer[buffer.Length / 2] = 7;
        Assert.False(FrameSanity.IsAllZero(buffer));
    }

    [Fact]
    public void IsAllZero_SingleByteBuffers()
    {
        Assert.True(FrameSanity.IsAllZero(new byte[] { 0 }));
        Assert.False(FrameSanity.IsAllZero(new byte[] { 1 }));
    }

    [Fact]
    public void IsAllZeroColor_EmptyBuffer_IsTrue()
    {
        Assert.True(FrameSanity.IsAllZeroColor(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void IsAllZeroColor_AllZeroColorWithForcedAlpha_IsTrue()
    {
        var buffer = new byte[4 * 1024];
        for (int i = 3; i < buffer.Length; i += 4)
        {
            buffer[i] = 255; // alpha forced opaque, as ConvertZPixmapToBgra does
        }
        Assert.True(FrameSanity.IsAllZeroColor(buffer));
    }

    [Fact]
    public void IsAllZeroColor_NonZeroBlue_IsFalse()
    {
        var buffer = new byte[] { 1, 0, 0, 255 };
        Assert.False(FrameSanity.IsAllZeroColor(buffer));
    }

    [Fact]
    public void IsAllZeroColor_NonZeroGreen_IsFalse()
    {
        var buffer = new byte[] { 0, 1, 0, 255 };
        Assert.False(FrameSanity.IsAllZeroColor(buffer));
    }

    [Fact]
    public void IsAllZeroColor_NonZeroRed_IsFalse()
    {
        var buffer = new byte[] { 0, 0, 1, 255 };
        Assert.False(FrameSanity.IsAllZeroColor(buffer));
    }

    [Fact]
    public void IsAllZeroColor_NonZeroColorDeepInBuffer_IsFalse()
    {
        var buffer = new byte[4 * 1024];
        for (int i = 3; i < buffer.Length; i += 4)
        {
            buffer[i] = 255;
        }
        buffer[2000] = 7; // a B/G/R byte somewhere in the middle
        Assert.False(FrameSanity.IsAllZeroColor(buffer));
    }
}
