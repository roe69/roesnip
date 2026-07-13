using RoeSnip.Core.Color;
using Xunit;

namespace RoeSnip.Core.Tests;

/// <summary>Golden values for the eyedropper's format rows (see the round-2 UX brief's worked
/// example: #0d1117 -> rgb(13, 17, 23) -> hsl(216, 28%, 7%) -> "116.4 nits"). Ported from the WPF
/// app's own tests/RoeSnip.Tests/ColorFormattingTests.cs (item 22).</summary>
public class ColorFormattingTests
{
    [Fact]
    public void Hex_MatchesSpecExample()
    {
        Assert.Equal("0d1117", ColorFormatting.Hex(0x0d, 0x11, 0x17));
    }

    [Fact]
    public void HexWithHash_IsUppercaseWithHash()
    {
        Assert.Equal("#0D1117", ColorFormatting.HexWithHash(0x0d, 0x11, 0x17));
    }

    [Fact]
    public void Rgb_MatchesSpecExample()
    {
        Assert.Equal("rgb(13, 17, 23)", ColorFormatting.Rgb(13, 17, 23));
    }

    [Fact]
    public void Hsl_MatchesSpecExample()
    {
        Assert.Equal("hsl(216, 28%, 7%)", ColorFormatting.Hsl(13, 17, 23));
    }

    [Fact]
    public void Nits_MatchesSpecExample()
    {
        Assert.Equal("116.4 nits", ColorFormatting.Nits(116.4));
    }

    [Fact]
    public void Nits_AlwaysShowsOneDecimal()
    {
        Assert.Equal("100.0 nits", ColorFormatting.Nits(100.0));
    }

    [Fact]
    public void Hex_BlackAndWhite()
    {
        Assert.Equal("000000", ColorFormatting.Hex(0, 0, 0));
        Assert.Equal("ffffff", ColorFormatting.Hex(255, 255, 255));
    }

    [Fact]
    public void Hsl_White_IsAchromatic()
    {
        Assert.Equal("hsl(0, 0%, 100%)", ColorFormatting.Hsl(255, 255, 255));
    }

    [Fact]
    public void Hsl_Black_IsAchromatic()
    {
        Assert.Equal("hsl(0, 0%, 0%)", ColorFormatting.Hsl(0, 0, 0));
    }

    [Fact]
    public void RgbToHsl_MatchesSpecExample()
    {
        var (h, s, l) = ColorFormatting.RgbToHsl(13, 17, 23);
        Assert.Equal(216.0, h, precision: 0);
        Assert.Equal(0.28, s, precision: 2);
        Assert.Equal(0.07, l, precision: 2);
    }

    [Theory]
    [InlineData(13, 17, 23)]
    [InlineData(255, 0, 0)]
    [InlineData(0, 255, 0)]
    [InlineData(0, 0, 255)]
    [InlineData(128, 64, 200)]
    [InlineData(10, 200, 190)]
    public void HslToRgb_RoundTripsRgbToHsl(byte r, byte g, byte b)
    {
        var (h, s, l) = ColorFormatting.RgbToHsl(r, g, b);
        var (r2, g2, b2) = ColorFormatting.HslToRgb(h, s, l);

        // Byte quantization through the HSL round trip can be off by a hair — allow +-1.
        Assert.InRange(r2, Math.Max(0, r - 1), Math.Min(255, r + 1));
        Assert.InRange(g2, Math.Max(0, g - 1), Math.Min(255, g + 1));
        Assert.InRange(b2, Math.Max(0, b - 1), Math.Min(255, b + 1));
    }

    [Fact]
    public void Hsv_MatchesSpecExampleColor()
    {
        // Same #0d1117 worked example as the other formats: hue matches HSL's 216, V = max = 23/255.
        Assert.Equal("hsv(216, 43%, 9%)", ColorFormatting.Hsv(13, 17, 23));
    }

    [Fact]
    public void Hsv_WhiteAndBlack_AreAchromatic()
    {
        Assert.Equal("hsv(0, 0%, 100%)", ColorFormatting.Hsv(255, 255, 255));
        Assert.Equal("hsv(0, 0%, 0%)", ColorFormatting.Hsv(0, 0, 0));
    }

    [Fact]
    public void Hsv_PureRed()
    {
        Assert.Equal("hsv(0, 100%, 100%)", ColorFormatting.Hsv(255, 0, 0));
    }

    [Fact]
    public void Cmyk_MatchesSpecExampleColor()
    {
        Assert.Equal("cmyk(43%, 26%, 0%, 91%)", ColorFormatting.Cmyk(13, 17, 23));
    }

    [Fact]
    public void Cmyk_PureBlack_IsKOnly()
    {
        Assert.Equal("cmyk(0%, 0%, 0%, 100%)", ColorFormatting.Cmyk(0, 0, 0));
    }

    [Fact]
    public void Cmyk_PureRedAndWhite()
    {
        Assert.Equal("cmyk(0%, 100%, 100%, 0%)", ColorFormatting.Cmyk(255, 0, 0));
        Assert.Equal("cmyk(0%, 0%, 0%, 0%)", ColorFormatting.Cmyk(255, 255, 255));
    }
}
