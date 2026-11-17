using RoeSnip.Core.Overlay;
using Xunit;

namespace RoeSnip.Core.Tests;

public class PointerScalingTests
{
    [Fact]
    public void FactorForRadius_MaxZoomOut_IsOne()
    {
        Assert.Equal(1.0, PointerScaling.FactorForRadius(15, 15));
    }

    [Fact]
    public void FactorForRadius_FallsAsRadiusShrinks()
    {
        double previous = 2.0;
        for (int r = 15; r >= 1; r--)
        {
            double f = PointerScaling.FactorForRadius(r, 15);
            Assert.True(f < previous);
            previous = f;
        }
    }

    [Fact]
    public void FactorForRadius_MatchesPixelCountRatio()
    {
        Assert.Equal(11.0 / 31.0, PointerScaling.FactorForRadius(5, 15), 12);
    }

    [Fact]
    public void FactorForRadius_TightestZoomIsFlooredAtMinFactor()
    {
        // 3/31 = 0.0968 is below the floor.
        Assert.Equal(PointerScaling.MinFactor, PointerScaling.FactorForRadius(1, 15));
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(0)]
    public void FactorForRadius_OutOfRangeLowRadius_ClampsToFloor(int radius)
    {
        Assert.Equal(PointerScaling.MinFactor, PointerScaling.FactorForRadius(radius, 15));
    }

    [Fact]
    public void FactorForRadius_OverMax_ClampsToOne()
    {
        Assert.Equal(1.0, PointerScaling.FactorForRadius(99, 15));
    }

    [Fact]
    public void FactorForRadius_DegenerateMax_IsOne()
    {
        Assert.Equal(1.0, PointerScaling.FactorForRadius(3, 0));
    }

    [Fact]
    public void Scale_FactorOne_PassesDeltaThroughExactly()
    {
        var s = new PointerDeltaScaler();
        s.Scale(7, -3, 1.0, out int x, out int y);
        Assert.Equal(7, x);
        Assert.Equal(-3, y);
    }

    [Fact]
    public void Scale_SubPixelMovesAccumulateInsteadOfVanishing()
    {
        var s = new PointerDeltaScaler();
        int totalX = 0;
        for (int i = 0; i < 10; i++)
        {
            s.Scale(1, 0, 0.1, out int x, out _);
            totalX += x;
        }
        Assert.Equal(1, totalX);
    }

    [Fact]
    public void Scale_LongRun_TotalMatchesExactProductWithinOnePixel()
    {
        var s = new PointerDeltaScaler();
        int totalX = 0, totalY = 0;
        for (int i = 0; i < 1000; i++)
        {
            s.Scale(3, -2, 0.35, out int x, out int y);
            totalX += x;
            totalY += y;
        }
        Assert.InRange(totalX, 1050 - 1, 1050 + 1); // 3000 * 0.35
        Assert.InRange(totalY, -700 - 1, -700 + 1);
    }

    [Fact]
    public void Scale_OppositeDirections_AreSymmetric()
    {
        var a = new PointerDeltaScaler();
        var b = new PointerDeltaScaler();
        for (int i = 0; i < 20; i++)
        {
            a.Scale(5, 0, 0.27, out int ax, out _);
            b.Scale(-5, 0, 0.27, out int bx, out _);
            Assert.Equal(-ax, bx);
        }
    }

    [Fact]
    public void Reset_DropsTheCarriedRemainder()
    {
        var s = new PointerDeltaScaler();
        s.Scale(1, 1, 0.9, out _, out _); // remainder 0.9 carried
        s.Reset();
        s.Scale(1, 1, 0.5, out int x, out int y);
        Assert.Equal(0, x);
        Assert.Equal(0, y);
    }
}
