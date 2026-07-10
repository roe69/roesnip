using System;
using System.Linq;
using RoeSnip.Overlay;
using Xunit;

namespace RoeSnip.Tests;

/// <summary>Golden values for the custom-color-format template engine and the built-in catalog.
/// The worked example color is #FFE4B5 (255, 228, 181) — the exact color PowerToys' own settings
/// UI displays for every built-in format, so each catalog entry is asserted against PowerToys'
/// displayed output (and #FFE4B5 is CSS "moccasin", which pins the %Na name lookup too).</summary>
public class ColorFormatTemplateTests
{
    private const byte R = 255, G = 228, B = 181;

    private static string BuiltIn(string name)
    {
        var entry = ColorFormatCatalog.BuiltIns.Single(b => b.Name == name);
        return ColorFormatTemplate.Format(entry.Format, R, G, B, nits: null);
    }

    [Theory]
    [InlineData("HEX", "ffe4b5")]
    [InlineData("RGB", "rgb(255, 228, 181)")]
    [InlineData("HSL", "hsl(38, 100%, 85%)")]
    [InlineData("HSV", "hsv(38, 29%, 100%)")]
    [InlineData("CMYK", "cmyk(0%, 11%, 29%, 0%)")]
    [InlineData("HSB", "hsb(38, 29%, 100%)")]
    [InlineData("HSI", "hsi(38, 18%, 87%)")]
    [InlineData("HWB", "hwb(38, 71%, 0%)")]
    [InlineData("NCol", "R64, 71%, 0%")]
    [InlineData("VEC4", "(1f, 0.89f, 0.71f, 1f)")]
    [InlineData("Decimal", "11920639")]
    [InlineData("HEX Int", "0xFFFFE4B5")]
    [InlineData("Name", "moccasin")]
    public void BuiltInFormats_MatchPowerToysDisplayedValues(string name, string expected)
    {
        Assert.Equal(expected, BuiltIn(name));
    }

    [Fact]
    public void CustomTemplate_MatchesPowerToysDialogExample()
    {
        Assert.Equal(
            "new Color (R = 255, G = 228, B = 181)",
            ColorFormatTemplate.Format("new Color (R = %Re, G = %Gr, B = %Bl)", R, G, B, null));
    }

    [Theory]
    [InlineData("%ReX", "FF")]  // the dialog's own example: hex uppercase two digits
    [InlineData("%Rex", "ff")]
    [InlineData("%ReH", "F")]
    [InlineData("%Reh", "f")]
    [InlineData("%Reb", "255")]
    [InlineData("%Ref", "1")]
    [InlineData("%GrF", ".89")]
    [InlineData("%Grf", "0.89")]
    public void ChannelSuffixes_FormatRed(string template, string expected)
    {
        Assert.Equal(expected, ColorFormatTemplate.Format(template, R, G, B, null));
    }

    [Fact]
    public void CieLab_SupportsIntegerSuffix()
    {
        // CIELab(91.72, 2.44, 26.36) per PowerToys; 'i' rounds to integers.
        Assert.Equal("92 / 2 / 26", ColorFormatTemplate.Format("%Lci / %Cai / %Cbi", R, G, B, null));
    }

    [Fact]
    public void UnknownParameter_PassesThroughVerbatim()
    {
        Assert.Equal("%Qq stays 100%", ColorFormatTemplate.Format("%Qq stays %Sl%", R, G, B, null));
    }

    [Fact]
    public void Nits_RendersValueOrDash()
    {
        Assert.Equal("116.4 nits", ColorFormatTemplate.Format("%Nt nits", R, G, B, 116.4));
        Assert.Equal("— nits", ColorFormatTemplate.Format("%Nt nits", R, G, B, null));
    }

    [Fact]
    public void Xyz_MatchesPowerToysWithinTolerance()
    {
        var (x, y, z) = ColorFormatting.RgbToXyz(R, G, B);
        Assert.InRange(x, 77.32 - 0.15, 77.32 + 0.15);
        Assert.InRange(y, 80.08 - 0.15, 80.08 + 0.15);
        Assert.InRange(z, 55.10 - 0.15, 55.10 + 0.15);
    }

    [Fact]
    public void Lab_MatchesPowerToysWithinTolerance()
    {
        var (l, a, b) = ColorFormatting.RgbToLab(R, G, B);
        Assert.InRange(l, 91.72 - 0.2, 91.72 + 0.2);
        Assert.InRange(a, 2.44 - 0.3, 2.44 + 0.3);
        Assert.InRange(b, 26.36 - 0.5, 26.36 + 0.5);
    }

    [Fact]
    public void Oklab_And_Oklch_MatchPowerToysWithinTolerance()
    {
        var (l, a, b) = ColorFormatting.RgbToOklab(R, G, B);
        Assert.InRange(l, 0.93 - 0.01, 0.93 + 0.01);
        Assert.InRange(a, 0.01 - 0.01, 0.01 + 0.01);
        Assert.InRange(b, 0.07 - 0.01, 0.07 + 0.01);

        var (_, c, h) = ColorFormatting.RgbToOklch(R, G, B);
        Assert.InRange(c, 0.07 - 0.01, 0.07 + 0.01);
        Assert.InRange(h, 81.38 - 3.0, 81.38 + 3.0);
    }

    [Fact]
    public void ColorNames_ExactAndNearest()
    {
        Assert.Equal("moccasin", ColorNames.Nearest(0xFF, 0xE4, 0xB5)); // exact table hit
        Assert.Equal("black", ColorNames.Nearest(2, 1, 3));             // nearest, not exact
        Assert.Equal("red", ColorNames.Nearest(250, 4, 6));
    }

    [Fact]
    public void EffectiveFormats_DefaultSettings_SeedsCatalogWithLegacyDefaults()
    {
        var formats = ColorFormatCatalog.EffectiveFormats(RoeSnipSettings.Default);

        Assert.Equal(ColorFormatCatalog.BuiltIns.Count, formats.Count);
        Assert.Equal(
            new[] { "HEX", "RGB", "HSL", "Nits" },
            formats.Where(f => f.Enabled).Select(f => f.Name).ToArray());
        Assert.All(formats, f => Assert.False(f.IsCustom));
    }

    [Fact]
    public void EffectiveFormats_StoredList_IsReturnedAsIs()
    {
        var custom = new ColorFormatEntry { Name = "Mine", Format = "%Rex", Enabled = true, IsCustom = true };
        var settings = RoeSnipSettings.Default with
        {
            ColorFormats = new System.Collections.Generic.List<ColorFormatEntry> { custom },
        };

        var formats = ColorFormatCatalog.EffectiveFormats(settings);

        Assert.Single(formats);
        Assert.Equal("Mine", formats[0].Name);
    }

    [Fact]
    public void EffectiveFormats_LegacyToggles_CarryIntoSeed()
    {
        var settings = RoeSnipSettings.Default with
        {
            ColorFormatShowHsl = false,
            ColorFormatShowCmyk = true,
        };

        var formats = ColorFormatCatalog.EffectiveFormats(settings);

        Assert.False(formats.Single(f => f.Name == "HSL").Enabled);
        Assert.True(formats.Single(f => f.Name == "CMYK").Enabled);
    }
}
