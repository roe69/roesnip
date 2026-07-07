using RoeSnip.Capture;
using Xunit;

namespace RoeSnip.Tests;

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
}
