using System.Collections.Generic;
using System.Linq;
using RoeSnip.Overlay;
using Xunit;

namespace RoeSnip.Tests;

/// <summary>The pure list operations behind the toolbar's swatch palette: migration from the
/// legacy defaults+CustomColors layout, and the right-click Replace edit (the only mutation the
/// palette supports; the slot count is fixed).</summary>
public class SwatchPaletteTests
{
    [Fact]
    public void EffectivePalette_EmptyPalette_SeedsFromDefaults()
    {
        var result = SwatchPalette.EffectivePalette(new List<string>(), new List<string>());
        Assert.Equal(SwatchPalette.DefaultColors, result);
    }

    [Fact]
    public void EffectivePalette_EmptyPalette_AppendsLegacyCustomColorsAfterDefaults()
    {
        var legacy = new List<string> { "#FF00FF", "#00FFAA" };
        var result = SwatchPalette.EffectivePalette(new List<string>(), legacy);

        Assert.Equal(SwatchPalette.DefaultColors.Count + 2, result.Count);
        Assert.Equal(SwatchPalette.DefaultColors, result.Take(SwatchPalette.DefaultColors.Count));
        Assert.Equal("#FF00FF", result[^2]);
        Assert.Equal("#00FFAA", result[^1]);
    }

    [Fact]
    public void EffectivePalette_LegacyDuplicateOfDefault_IsNotAddedTwice()
    {
        // Case-insensitive: a legacy custom color equal to a built-in default is skipped.
        var legacy = new List<string> { "#e53935", "#123456" };
        var result = SwatchPalette.EffectivePalette(new List<string>(), legacy);

        Assert.Equal(SwatchPalette.DefaultColors.Count + 1, result.Count);
        Assert.Single(result, hex => string.Equals(hex, "#E53935", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EffectivePalette_MigrationCapsAtMaxColors()
    {
        var legacy = Enumerable.Range(0, 20).Select(i => $"#0000{i:00}").ToList();
        var result = SwatchPalette.EffectivePalette(new List<string>(), legacy);
        Assert.Equal(SwatchPalette.MaxColors, result.Count);
    }

    [Fact]
    public void EffectivePalette_NonEmptyPalette_ReturnedAsIs_IgnoringLegacy()
    {
        var palette = new List<string> { "#111111", "#222222" };
        var legacy = new List<string> { "#333333" };
        var result = SwatchPalette.EffectivePalette(palette, legacy);
        Assert.Equal(palette, result);
    }

    [Fact]
    public void DefaultColors_TenSlots_NoGray()
    {
        Assert.Equal(10, SwatchPalette.DefaultColors.Count);
        Assert.DoesNotContain("#9E9E9E", SwatchPalette.DefaultColors);
    }

    [Fact]
    public void ReplaceAt_SwapsEntryInPlace()
    {
        var result = SwatchPalette.ReplaceAt(new List<string> { "#111111", "#222222" }, 1, "#333333");
        Assert.Equal(new[] { "#111111", "#333333" }, result);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void ReplaceAt_OutOfRange_ReturnsUnchanged(int index)
    {
        var palette = new List<string> { "#111111", "#222222" };
        var result = SwatchPalette.ReplaceAt(palette, index, "#333333");
        Assert.Equal(palette, result);
    }

    [Fact]
    public void NameFor_KnowsBuiltInDefaults_FallsBackToHex()
    {
        Assert.Equal("Red", SwatchPalette.NameFor("#E53935"));
        Assert.Equal("Red", SwatchPalette.NameFor("#e53935"));
        Assert.Equal("#123456", SwatchPalette.NameFor("#123456"));
    }

    [Fact]
    public void Mutations_NeverAliasTheInputList()
    {
        var palette = new List<string> { "#111111", "#222222" };
        Assert.NotSame(palette, SwatchPalette.ReplaceAt(palette, 9, "#333333"));
        Assert.NotSame(palette, SwatchPalette.EffectivePalette(palette, new List<string>()));
    }
}
