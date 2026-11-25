using RoeSnip.Core.Overlay;
using Xunit;

namespace RoeSnip.Core.Tests;

/// <summary>The pure geometry/color-math helpers behind the Avalonia overlay's Select-tool
/// hit-testing (Feature B) and its selection-handle contrast rule.</summary>
public class AnnotationGeometryTests
{
    [Theory]
    [InlineData(0, 0, 100, 0, 3)]
    [InlineData(0, 0, 0, 100, 8)]
    [InlineData(10, 10, 70, 40, 1)]
    [InlineData(10, 10, 40, -30, 14)]
    [InlineData(5, 5, 20, 5, 6)]
    public void ArrowOutline_TipIsExactlyTheEndPoint_AndNothingExtendsPastIt(
        double fx, double fy, double tx, double ty, double width)
    {
        var o = AnnotationGeometry.ArrowOutline(fx, fy, tx, ty, width);
        Assert.Equal(14, o.Length);
        Assert.Equal(tx, o[6], 9);
        Assert.Equal(ty, o[7], 9);
        double len = Math.Sqrt((tx - fx) * (tx - fx) + (ty - fy) * (ty - fy));
        double ux = (tx - fx) / len, uy = (ty - fy) / len;
        for (int i = 0; i < o.Length; i += 2)
        {
            double along = (o[i] - tx) * ux + (o[i + 1] - ty) * uy;
            Assert.True(along <= 1e-9, $"point {i / 2} lies past the tip by {along}");
        }
    }

    [Fact]
    public void ArrowOutline_HeadIsCappedForShortArrows_AndWiderThanTheShaft()
    {
        var o = AnnotationGeometry.ArrowOutline(0, 0, 10, 0, 8);
        // Barb baseline is at most half the length back from the tip.
        Assert.True(o[4] >= 5 - 1e-9);
        // Barb half-width exceeds the shaft half-width at the join.
        Assert.True(Math.Abs(o[5]) > Math.Abs(o[3]));
    }

    [Fact]
    public void ArrowOutline_DegenerateArrow_IsEmpty()
    {
        Assert.Empty(AnnotationGeometry.ArrowOutline(5, 5, 5.2, 5.1, 3));
    }

    [Theory]
    [InlineData(10.2, 5, 10.5)]
    [InlineData(10.9, 5, 10.5)]
    [InlineData(10.4, 4, 10.0)]
    [InlineData(10.6, 4, 11.0)]
    [InlineData(10.2, 1, 10.5)]
    public void PixelAlign_OddWidthsOnPixelCentres_EvenOnEdges(double v, double width, double expected)
    {
        Assert.Equal(expected, AnnotationGeometry.PixelAlign(v, width), 9);
    }

    [Fact]
    public void SmoothPath_KeepsEndpoints_AndUsesInteriorPointsAsControls()
    {
        var segs = AnnotationGeometry.SmoothPath(new (double, double)[] { (0, 0), (10, 0), (10, 10) });
        Assert.Equal(3, segs.Length);
        Assert.False(segs[0].Quad);
        Assert.Equal(5, segs[0].Ex, 9);
        Assert.True(segs[1].Quad);
        Assert.Equal(10, segs[1].Cx, 9);
        Assert.Equal(0, segs[1].Cy, 9);
        Assert.Equal(10, segs[2].Ex, 9);
        Assert.Equal(10, segs[2].Ey, 9);
    }

    [Fact]
    public void DistanceToSegment_PointOnSegment_IsZero()
    {
        double d = AnnotationGeometry.DistanceToSegment(5, 0, 0, 0, 10, 0);
        Assert.Equal(0.0, d, 6);
    }

    [Fact]
    public void DistanceToSegment_PerpendicularOffset_MatchesExpected()
    {
        // Point (5,3) against the horizontal segment (0,0)-(10,0) - the nearest point is (5,0).
        double d = AnnotationGeometry.DistanceToSegment(5, 3, 0, 0, 10, 0);
        Assert.Equal(3.0, d, 6);
    }

    [Fact]
    public void DistanceToSegment_PastTheEndpoint_ClampsToTheEndpointItself()
    {
        // Beyond (10,0) along the segment's line - nearest point clamps to the endpoint, not the
        // infinite line, so distance is straight-line to (10,0).
        double d = AnnotationGeometry.DistanceToSegment(15, 4, 0, 0, 10, 0);
        Assert.Equal(Math.Sqrt(5 * 5 + 4 * 4), d, 6);
    }

    [Fact]
    public void DistanceToSegment_DegenerateSegment_IsDistanceToThePoint()
    {
        double d = AnnotationGeometry.DistanceToSegment(3, 4, 0, 0, 0, 0);
        Assert.Equal(5.0, d, 6);
    }

    [Fact]
    public void RelativeLuminance_White_IsOne()
    {
        Assert.Equal(1.0, AnnotationGeometry.RelativeLuminance(255, 255, 255), 3);
    }

    [Fact]
    public void RelativeLuminance_Black_IsZero()
    {
        Assert.Equal(0.0, AnnotationGeometry.RelativeLuminance(0, 0, 0), 3);
    }

    [Fact]
    public void IsDark_Black_IsTrue_White_IsFalse()
    {
        Assert.True(AnnotationGeometry.IsDark(0, 0, 0));
        Assert.False(AnnotationGeometry.IsDark(255, 255, 255));
    }

    [Fact]
    public void ShouldUseInverseFill_PureRed_IsTrue()
    {
        // Red (luminance ~0.21) inverts to cyan (luminance ~0.79) - well past the 0.3 gate.
        Assert.True(AnnotationGeometry.ShouldUseInverseFill(255, 0, 0));
    }

    [Fact]
    public void ShouldUseInverseFill_NearMidGray_IsFalse()
    {
        // ~128,128,128 inverts to ~127,127,127 - indistinguishable luminance, so the fallback
        // black/white rule should be used instead of the (useless) plain inverse.
        Assert.False(AnnotationGeometry.ShouldUseInverseFill(128, 128, 128));
    }

    [Fact]
    public void ShouldUseInverseFill_White_IsTrue()
    {
        // White inverts to black - maximal luminance gap.
        Assert.True(AnnotationGeometry.ShouldUseInverseFill(255, 255, 255));
    }
}
