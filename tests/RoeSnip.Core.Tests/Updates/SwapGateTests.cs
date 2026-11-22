using RoeSnip.Core.Updates;
using Xunit;

namespace RoeSnip.Core.Tests.Updates;

public sealed class SwapGateTests
{
    [Fact]
    public void SwapFirst_DeadlineCannotAbandon()
    {
        var gate = new SwapGate();
        Assert.True(gate.TryBeginSwap());
        Assert.False(gate.TryAbandon());
        Assert.True(gate.SwapStarted);
    }

    [Fact]
    public void AbandonFirst_SwapIsRefused()
    {
        var gate = new SwapGate();
        Assert.True(gate.TryAbandon());
        Assert.False(gate.TryBeginSwap());
        Assert.False(gate.SwapStarted);
    }

    [Fact]
    public void EachSideClaimsOnce()
    {
        var gate = new SwapGate();
        Assert.True(gate.TryBeginSwap());
        Assert.False(gate.TryBeginSwap());

        var other = new SwapGate();
        Assert.True(other.TryAbandon());
        Assert.False(other.TryAbandon());
    }
}
