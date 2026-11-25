using RoeSnip.Core.Capture;
using Xunit;

namespace RoeSnip.Core.Tests;

public class StreamActivityTests
{
    [Fact]
    public void TouchActivatesOnceAndIdlesAfterTheLinger()
    {
        var a = new StreamActivity();
        Assert.True(a.Touch(7, 1000, 1500));
        Assert.False(a.Touch(7, 1200, 1500));
        Assert.False(a.TryIdle(7, 2600));
        Assert.True(a.TryIdle(7, 2700));
        Assert.False(a.TryIdle(7, 2800));
        Assert.False(a.IsActive(7));
        Assert.False(a.AnyActive);
        Assert.True(a.Touch(7, 3000, 1500));
    }

    [Fact]
    public void HeldNodeNeverIdlesUntilReleasedAndLingered()
    {
        var a = new StreamActivity();
        Assert.True(a.Hold(1, 0));
        Assert.False(a.Hold(1, 10));
        Assert.False(a.TryIdle(1, 100000));
        a.Release(1, 100000, 500);
        Assert.False(a.TryIdle(1, 100500));
        a.Release(1, 100600, 500);
        Assert.False(a.TryIdle(1, 101000));
        Assert.True(a.TryIdle(1, 101100));
    }

    [Fact]
    public void NodesIdleIndependently()
    {
        var a = new StreamActivity();
        a.Touch(1, 0, 100);
        a.Touch(2, 0, 1000);
        Assert.True(a.TryIdle(1, 200));
        Assert.False(a.TryIdle(2, 200));
        Assert.Equal(new uint[] { 2 }, a.ActiveIds().ToArray());
        Assert.True(a.AnyActive);
        Assert.True(a.IsActive(2));
    }
}

public class FrameSignalTests
{
    [Fact]
    public void WaitAboveReturnsOnANewerFrameAndTimesOutOtherwise()
    {
        var s = new FrameSignal();
        s.Bump();
        Assert.True(s.WaitAbove(0, TimeSpan.Zero));
        Assert.False(s.WaitAbove(1, TimeSpan.FromMilliseconds(30)));
        var t = Task.Run(() => s.WaitAbove(1, TimeSpan.FromSeconds(5)));
        Thread.Sleep(20);
        s.Bump();
        Assert.True(t.Result);
    }

    [Fact]
    public void WaitAboveStopsWhenAborted()
    {
        var s = new FrameSignal();
        Assert.False(s.WaitAbove(0, TimeSpan.FromSeconds(5), () => true));
    }
}
