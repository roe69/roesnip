using RoeSnip.Core.Color;
using Xunit;

namespace RoeSnip.Core.Tests;

public class ColorMathTests
{
    [Fact]
    public void SrgbDecode_Zero_IsZero()
    {
        Assert.Equal(0f, ColorMath.SrgbDecode(0f), precision: 6);
    }

    [Fact]
    public void SrgbEncode_Zero_IsZero()
    {
        Assert.Equal(0f, ColorMath.SrgbEncode(0f), precision: 6);
    }

    [Fact]
    public void SrgbEncode_One_IsOne()
    {
        Assert.Equal(1f, ColorMath.SrgbEncode(1f), precision: 5);
    }

    [Fact]
    public void SrgbDecode_One_IsOne()
    {
        Assert.Equal(1f, ColorMath.SrgbDecode(1f), precision: 5);
    }

    [Fact]
    public void SrgbTransfer_BoundaryBelow_UsesLinearSegment()
    {
        // Just below the 0.0031308 boundary: encode must equal the linear (*12.92) branch.
        const float linear = 0.0020f;
        float expected = linear * 12.92f;
        Assert.Equal(expected, ColorMath.SrgbEncode(linear), precision: 6);
    }

    [Fact]
    public void SrgbTransfer_BoundaryAbove_UsesPowSegment()
    {
        // Just above the 0.0031308 boundary: encode must equal the pow branch.
        const float linear = 0.0040f;
        float expected = 1.055f * MathF.Pow(linear, 1f / 2.4f) - 0.055f;
        Assert.Equal(expected, ColorMath.SrgbEncode(linear), precision: 6);
    }

    [Fact]
    public void SrgbTransfer_BoundaryValue_IsContinuous()
    {
        // At exactly 0.0031308, both branches should agree closely (C0 continuity of the sRGB EOTF).
        const float boundary = 0.0031308f;
        float viaLinear = boundary * 12.92f;
        float encoded = ColorMath.SrgbEncode(boundary);
        Assert.Equal(viaLinear, encoded, precision: 4);
    }

    [Fact]
    public void SrgbRoundTrip_Half_IsApproximatelyStable()
    {
        float roundTripped = ColorMath.SrgbEncode(ColorMath.SrgbDecode(0.5f));
        Assert.Equal(0.5f, roundTripped, precision: 4);
    }

    [Fact]
    public void SrgbEncode_Half_QuantizesTo188()
    {
        // Golden pair from DESIGN.md: scRGB linear 0.5 -> byte 188.
        byte quantized = ColorMath.QuantizeRoundNearest(ColorMath.SrgbEncode(0.5f));
        Assert.Equal(188, quantized);
    }

    [Fact]
    public void QuantizeRoundNearest_ClampsBelowZero()
    {
        Assert.Equal(0, ColorMath.QuantizeRoundNearest(-1f));
    }

    [Fact]
    public void QuantizeRoundNearest_ClampsAboveOne()
    {
        Assert.Equal(255, ColorMath.QuantizeRoundNearest(2f));
    }

    [Fact]
    public void SrgbByteToLinear_White_IsOne()
    {
        Assert.Equal(1f, ColorMath.SrgbByteToLinear(255), precision: 5);
    }

    [Fact]
    public void SrgbByteToLinear_Black_IsZero()
    {
        Assert.Equal(0f, ColorMath.SrgbByteToLinear(0), precision: 6);
    }
}
