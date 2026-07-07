using System.Linq;
using RoeSnip.Overlay;
using Xunit;

namespace RoeSnip.Tests;

/// <summary>The pure parse/clamp/format logic behind the toolbar's numeric size ComboBox
/// (UX round 5, item 2).</summary>
public class SizeInputTests
{
    [Theory]
    [InlineData("4", 4.0)]
    [InlineData("4px", 4.0)]
    [InlineData("4PX", 4.0)]
    [InlineData("4 px", 4.0)]
    [InlineData("18pt", 18.0)]
    [InlineData("18 PT", 18.0)]
    [InlineData("  12  ", 12.0)]
    [InlineData("8.5", 8.5)]
    [InlineData("0", 0.0)]
    public void TryParse_AcceptsPlainAndSuffixedNumbers(string text, double expected)
    {
        Assert.True(SizeInput.TryParse(text, out double value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("px")]
    [InlineData("abc")]
    [InlineData("12em")]
    [InlineData("-4")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void TryParse_RejectsGarbage(string? text)
    {
        Assert.False(SizeInput.TryParse(text, out _));
    }

    [Theory]
    [InlineData(0.0, 1.0)]
    [InlineData(1.0, 1.0)]
    [InlineData(16.0, 16.0)]
    [InlineData(32.0, 32.0)]
    [InlineData(500.0, 32.0)]
    public void ClampStroke_EnforcesTheAuthoritative1To32Range(double input, double expected)
    {
        Assert.Equal(expected, SizeInput.ClampStroke(input));
    }

    [Theory]
    [InlineData(0.0, 6.0)]
    [InlineData(6.0, 6.0)]
    [InlineData(20.0, 20.0)]
    [InlineData(96.0, 96.0)]
    [InlineData(500.0, 96.0)]
    public void ClampFont_EnforcesTheAuthoritative6To96Range(double input, double expected)
    {
        Assert.Equal(expected, SizeInput.ClampFont(input));
    }

    [Fact]
    public void Presets_AllSitWithinTheirClampRanges()
    {
        // The clamps are authoritative: no dropdown preset may exceed what a typed value could be.
        Assert.All(SizeInput.StrokePresetsPx, p => Assert.Equal(p, SizeInput.ClampStroke(p)));
        Assert.All(SizeInput.FontPresetsPt, p => Assert.Equal(p, SizeInput.ClampFont(p)));
    }

    [Fact]
    public void Presets_AreStrictlyIncreasing()
    {
        Assert.Equal(SizeInput.StrokePresetsPx.OrderBy(p => p), SizeInput.StrokePresetsPx);
        Assert.Equal(SizeInput.FontPresetsPt.OrderBy(p => p), SizeInput.FontPresetsPt);
        Assert.Equal(SizeInput.StrokePresetsPx.Distinct().Count(), SizeInput.StrokePresetsPx.Length);
        Assert.Equal(SizeInput.FontPresetsPt.Distinct().Count(), SizeInput.FontPresetsPt.Length);
    }

    [Fact]
    public void Format_ProducesTheCanonicalSuffixedForm()
    {
        Assert.Equal("4px", SizeInput.FormatPx(4.0));
        Assert.Equal("18pt", SizeInput.FormatPt(18.0));
    }

    [Fact]
    public void Format_RoundTripsThroughTryParse()
    {
        foreach (double preset in SizeInput.StrokePresetsPx)
        {
            Assert.True(SizeInput.TryParse(SizeInput.FormatPx(preset), out double value));
            Assert.Equal(preset, value);
        }
        foreach (double preset in SizeInput.FontPresetsPt)
        {
            Assert.True(SizeInput.TryParse(SizeInput.FormatPt(preset), out double value));
            Assert.Equal(preset, value);
        }
    }
}
