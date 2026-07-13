using RoeSnip.Core.Overlay;
using Xunit;

namespace RoeSnip.Core.Tests;

/// <summary>The pure geometry/color-math helpers behind the Avalonia overlay's Select-tool
/// hit-testing (Feature B) and its selection-handle contrast rule.</summary>
public class AnnotationGeometryTests
{
    [Fact]
    public void DistanceToSegment_PointOnSegment_IsZero()
    {
        double d = AnnotationGeometry.DistanceToSegment(5, 0, 0, 0, 10, 0);
        Assert.Equal(0.0, d, 6);
    }

    [Fact]
    public void DistanceToSegment_PerpendicularOffset_MatchesExpected()
    {
        // Point (5,3) against the horizontal segment (0,0)-(10,0) — the nearest point is (5,0).
        double d = AnnotationGeometry.DistanceToSegment(5, 3, 0, 0, 10, 0);
        Assert.Equal(3.0, d, 6);
    }

    [Fact]
    public void DistanceToSegment_PastTheEndpoint_ClampsToTheEndpointItself()
    {
        // Beyond (10,0) along the segment's line — nearest point clamps to the endpoint, not the
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
        // Red (luminance ~0.21) inverts to cyan (luminance ~0.79) — well past the 0.3 gate.
        Assert.True(AnnotationGeometry.ShouldUseInverseFill(255, 0, 0));
    }

    [Fact]
    public void ShouldUseInverseFill_NearMidGray_IsFalse()
    {
        // ~128,128,128 inverts to ~127,127,127 — indistinguishable luminance, so the fallback
        // black/white rule should be used instead of the (useless) plain inverse.
        Assert.False(AnnotationGeometry.ShouldUseInverseFill(128, 128, 128));
    }

    [Fact]
    public void ShouldUseInverseFill_White_IsTrue()
    {
        // White inverts to black — maximal luminance gap.
        Assert.True(AnnotationGeometry.ShouldUseInverseFill(255, 255, 255));
    }
}
