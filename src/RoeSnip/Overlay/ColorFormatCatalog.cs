using System;
using System.Collections.Generic;
using System.Linq;

namespace RoeSnip.Overlay;

/// <summary>The built-in color-format catalog (PowerToys Color Picker's set plus RoeSnip's Nits)
/// and the settings-migration seam for <see cref="RoeSnipSettings.ColorFormats"/> — the exact
/// EffectivePalette/PaletteColors pattern (see SwatchPalette): an empty stored list means "not
/// migrated yet" and is seeded from <see cref="BuiltIns"/> with the legacy ColorFormatShow* bools
/// applied; a non-empty list is the user's ordered, edited truth and is returned as-is.</summary>
public static class ColorFormatCatalog
{
    /// <summary>Every built-in, defined as a ColorFormatTemplate string so built-ins and custom
    /// formats flow through the exact same formatter (and a curious user can copy a built-in's
    /// template as the starting point for their own).</summary>
    public static readonly IReadOnlyList<(string Name, string Format)> BuiltIns = new (string, string)[]
    {
        ("HEX", "%Rex%Grx%Blx"),
        ("RGB", "rgb(%Re, %Gr, %Bl)"),
        ("HSL", "hsl(%Hu, %Sl%, %Ll%)"),
        ("HSV", "hsv(%Hu, %Sb%, %Va%)"),
        ("CMYK", "cmyk(%Cy%, %Ma%, %Ye%, %Bk%)"),
        ("HSB", "hsb(%Hu, %Sb%, %Br%)"),
        ("HSI", "hsi(%Hu, %Si%, %In%)"),
        ("HWB", "hwb(%Hu, %Wh%, %Bn%)"),
        ("NCol", "%Hn, %Wh%, %Bn%"),
        ("CIEXYZ", "XYZ(%Xv, %Yv, %Zv)"),
        ("CIELAB", "CIELab(%Lc, %Ca, %Cb)"),
        ("Oklab", "oklab(%Lo, %Oa, %Ob)"),
        ("Oklch", "oklch(%Lo, %Oc, %Oh)"),
        ("VEC4", "(%Reff, %Grff, %Blff, %Alff)"),
        ("Decimal", "%Dv"),
        ("HEX Int", "0x%AlX%ReX%GrX%BlX"),
        ("Name", "%Na"),
        ("Nits", "%Nt nits"),
    };

    /// <summary>The formats to display, in display order. Non-empty stored list = the user's
    /// truth, returned as-is. Empty = seed every built-in, enabling the ones the legacy
    /// ColorFormatShow* bools had on (HEX/RGB/HSL/Nits defaulted true there, so a fresh install
    /// gets exactly those four — compact out of the box).</summary>
    public static List<ColorFormatEntry> EffectiveFormats(RoeSnipSettings settings)
    {
        if (settings.ColorFormats.Count > 0)
        {
            return new List<ColorFormatEntry>(settings.ColorFormats);
        }

        bool LegacyEnabled(string name) => name switch
        {
            "HEX" => settings.ColorFormatShowHex,
            "RGB" => settings.ColorFormatShowRgb,
            "HSL" => settings.ColorFormatShowHsl,
            "HSV" => settings.ColorFormatShowHsv,
            "CMYK" => settings.ColorFormatShowCmyk,
            "Nits" => settings.ColorFormatShowNits,
            _ => false,
        };

        return BuiltIns
            .Select(b => new ColorFormatEntry { Name = b.Name, Format = b.Format, Enabled = LegacyEnabled(b.Name) })
            .ToList();
    }
}
