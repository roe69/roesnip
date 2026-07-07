using System;
using System.Globalization;

namespace RoeSnip.Overlay;

/// <summary>Pure string-formatting / color-space conversions for the ColorPickerWindow's format
/// rows (HEX/RGB/HSL/NITS) and its lightness-shades strip. Deliberately has no WPF dependency so
/// it's trivially unit-testable (see tests/RoeSnip.Tests/ColorFormattingTests.cs).</summary>
public static class ColorFormatting
{
    /// <summary>Lowercase, no leading '#' — matches the ColorPickerWindow's HEX row exactly
    /// (e.g. "0d1117"). This is a display format, distinct from the "#RRGGBB" storage format used
    /// by settings persistence (RecentPickedColors/CustomColors) and the rest of the app's existing
    /// clipboard-copy convention — see HexWithHash.</summary>
    public static string Hex(byte r, byte g, byte b) =>
        string.Create(CultureInfo.InvariantCulture, $"{r:x2}{g:x2}{b:x2}");

    /// <summary>Uppercase "#RRGGBB" — the format the rest of the app already uses for clipboard
    /// copies (Magnifier, the old inline color-info panel) and the one used for settings storage
    /// (RecentPickedColors/CustomColors), so it round-trips cleanly through
    /// System.Windows.Media.ColorConverter.</summary>
    public static string HexWithHash(byte r, byte g, byte b) =>
        string.Create(CultureInfo.InvariantCulture, $"#{r:X2}{g:X2}{b:X2}");

    public static string Rgb(byte r, byte g, byte b) =>
        string.Create(CultureInfo.InvariantCulture, $"rgb({r}, {g}, {b})");

    public static string Hsl(byte r, byte g, byte b)
    {
        var (h, s, l) = RgbToHsl(r, g, b);
        return string.Create(CultureInfo.InvariantCulture,
            $"hsl({Math.Round(h, MidpointRounding.AwayFromZero):0}, " +
            $"{Math.Round(s * 100.0, MidpointRounding.AwayFromZero):0}%, " +
            $"{Math.Round(l * 100.0, MidpointRounding.AwayFromZero):0}%)");
    }

    public static string Nits(double nits) =>
        string.Create(CultureInfo.InvariantCulture, $"{nits.ToString("0.0", CultureInfo.InvariantCulture)} nits");

    /// <summary>Standard (CSS-style) RGB -> HSL. H in [0,360), S and L in [0,1].</summary>
    public static (double H, double S, double L) RgbToHsl(byte r8, byte g8, byte b8)
    {
        double r = r8 / 255.0, g = g8 / 255.0, b = b8 / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2.0;

        if (max == min)
        {
            return (0.0, 0.0, l); // achromatic
        }

        double d = max - min;
        double s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);

        double h;
        if (max == r)
        {
            h = (g - b) / d + (g < b ? 6.0 : 0.0);
        }
        else if (max == g)
        {
            h = (b - r) / d + 2.0;
        }
        else
        {
            h = (r - g) / d + 4.0;
        }
        h *= 60.0;

        return (h, s, l);
    }

    /// <summary>Inverse of <see cref="RgbToHsl"/> — used to generate the lightness-shades strip
    /// (same hue/saturation, varying lightness) without round-tripping through a string.</summary>
    public static (byte R, byte G, byte B) HslToRgb(double h, double s, double l)
    {
        h = ((h % 360.0) + 360.0) % 360.0;
        s = Math.Clamp(s, 0.0, 1.0);
        l = Math.Clamp(l, 0.0, 1.0);

        if (s <= 0.0)
        {
            byte gray = ToByte(l);
            return (gray, gray, gray);
        }

        double q = l < 0.5 ? l * (1.0 + s) : l + s - l * s;
        double p = 2.0 * l - q;
        double hk = h / 360.0;

        double r = HueToChannel(p, q, hk + 1.0 / 3.0);
        double g = HueToChannel(p, q, hk);
        double b = HueToChannel(p, q, hk - 1.0 / 3.0);

        return (ToByte(r), ToByte(g), ToByte(b));
    }

    private static double HueToChannel(double p, double q, double t)
    {
        t = ((t % 1.0) + 1.0) % 1.0;
        if (t < 1.0 / 6.0) return p + (q - p) * 6.0 * t;
        if (t < 1.0 / 2.0) return q;
        if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6.0;
        return p;
    }

    private static byte ToByte(double c01) =>
        (byte)Math.Clamp(Math.Round(c01 * 255.0, MidpointRounding.AwayFromZero), 0.0, 255.0);
}
