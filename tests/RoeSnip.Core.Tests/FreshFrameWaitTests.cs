using RoeSnip.Core.Capture;
using Xunit;

namespace RoeSnip.Core.Tests;

public class FreshFrameWaitTests
{
    private static readonly TimeSpan[] Steps = { TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2), TimeSpan.FromMilliseconds(3) };

    [Fact]
    public void FirstStepFrameNeedsNoKick()
    {
        bool ok = FreshFrameWait.Await(5, Steps, (b, t) => true, () => throw new Exception("kicked"), out int kicks);
        Assert.True(ok);
        Assert.Equal(0, kicks);
    }

    [Fact]
    public void SilentStreamIsKickedBetweenStepsThenGivesUp()
    {
        int waits = 0;
        bool ok = FreshFrameWait.Await(5, Steps, (b, t) => { waits++; return false; }, () => { }, out int kicks);
        Assert.False(ok);
        Assert.Equal(3, waits);
        Assert.Equal(2, kicks); // no kick after the last step
    }

    [Fact]
    public void FrameAfterAKickCounts()
    {
        int waits = 0;
        bool ok = FreshFrameWait.Await(5, Steps, (b, t) => ++waits == 2, () => { }, out int kicks);
        Assert.True(ok);
        Assert.Equal(1, kicks);
    }

    [Fact]
    public void BaselineIsPassedUnchanged()
    {
        long seen = -1;
        FreshFrameWait.Await(42, Steps, (b, t) => { seen = b; return true; }, () => { }, out _);
        Assert.Equal(42, seen);
    }
}
