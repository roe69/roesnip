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

    public static string Hsv(byte r, byte g, byte b)
    {
        var (h, s, v) = RgbToHsv(r, g, b);
        return string.Create(CultureInfo.InvariantCulture,
            $"hsv({Math.Round(h, MidpointRounding.AwayFromZero):0}, " +
            $"{Math.Round(s * 100.0, MidpointRounding.AwayFromZero):0}%, " +
            $"{Math.Round(v * 100.0, MidpointRounding.AwayFromZero):0}%)");
    }

    public static string Cmyk(byte r, byte g, byte b)
    {
        var (c, m, y, k) = RgbToCmyk(r, g, b);
        return string.Create(CultureInfo.InvariantCulture,
            $"cmyk({Math.Round(c * 100.0, MidpointRounding.AwayFromZero):0}%, " +
            $"{Math.Round(m * 100.0, MidpointRounding.AwayFromZero):0}%, " +
            $"{Math.Round(y * 100.0, MidpointRounding.AwayFromZero):0}%, " +
            $"{Math.Round(k * 100.0, MidpointRounding.AwayFromZero):0}%)");
    }

    public static string Nits(double nits) =>
        string.Create(CultureInfo.InvariantCulture, $"{nits.ToString("0.0", CultureInfo.InvariantCulture)} nits");

    /// <summary>Standard RGB -> HSV (a.k.a. HSB). H in [0,360), S and V in [0,1]. Shares its hue
    /// derivation with <see cref="RgbToHsl"/>; only the second/third components differ.</summary>
    public static (double H, double S, double V) RgbToHsv(byte r8, byte g8, byte b8)
    {
        double r = r8 / 255.0, g = g8 / 255.0, b = b8 / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double v = max;
        double d = max - min;
        double s = max <= 0.0 ? 0.0 : d / max;

        if (d == 0.0)
        {
            return (0.0, 0.0, v); // achromatic
        }

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

        return (h, s, v);
    }

    /// <summary>Naive (device-independent) RGB -> CMYK, the same formula every picker tool uses
    /// for a display value: K = 1 - max(R,G,B), remaining channels scaled by (1-K). All in [0,1];
    /// pure black is (0,0,0,1) by convention.</summary>
    public static (double C, double M, double Y, double K) RgbToCmyk(byte r8, byte g8, byte b8)
    {
        double r = r8 / 255.0, g = g8 / 255.0, b = b8 / 255.0;
        double k = 1.0 - Math.Max(r, Math.Max(g, b));
        if (k >= 1.0)
        {
            return (0.0, 0.0, 0.0, 1.0);
        }
        double c = (1.0 - r - k) / (1.0 - k);
        double m = (1.0 - g - k) / (1.0 - k);
        double y = (1.0 - b - k) / (1.0 - k);
        return (c, m, y, k);
    }

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

    // ---------- Additional color spaces (PowerToys-parity formats; see ColorFormatTemplate) ----------

    /// <summary>RGB -> HSI (intensity model). H is the standard hue in [0,360) (what every picker
    /// tool displays for HSI too), S and I in [0,1]: I = mean(R,G,B), S = 1 - min/I.</summary>
    public static (double H, double S, double I) RgbToHsi(byte r8, byte g8, byte b8)
    {
        double r = r8 / 255.0, g = g8 / 255.0, b = b8 / 255.0;
        double i = (r + g + b) / 3.0;
        double min = Math.Min(r, Math.Min(g, b));
        double s = i <= 0.0 ? 0.0 : 1.0 - min / i;
        var (h, _, _) = RgbToHsv(r8, g8, b8);
        return (h, s, i);
    }

    /// <summary>RGB -> HWB: standard hue, whiteness = min(R,G,B), blackness = 1 - max(R,G,B).</summary>
    public static (double H, double W, double B) RgbToHwb(byte r8, byte g8, byte b8)
    {
        double r = r8 / 255.0, g = g8 / 255.0, b = b8 / 255.0;
        var (h, _, _) = RgbToHsv(r8, g8, b8);
        return (h, Math.Min(r, Math.Min(g, b)), 1.0 - Math.Max(r, Math.Max(g, b)));
    }

    /// <summary>Natural-color (NCol) hue notation: the 60-degree hue sextant's letter (R, Y, G,
    /// C, B, M) followed by the 0-99 position within it — e.g. hue 38 => "R64".</summary>
    public static string NaturalHue(byte r8, byte g8, byte b8)
    {
        var (h, _, _) = RgbToHsv(r8, g8, b8);
        int sextant = (int)(h / 60.0) % 6;
        int position = (int)Math.Round(h % 60.0 / 60.0 * 100.0, MidpointRounding.AwayFromZero);
        if (position >= 100)
        {
            sextant = (sextant + 1) % 6;
            position = 0;
        }
        return string.Create(CultureInfo.InvariantCulture, $"{"RYGCBM"[sextant]}{position}");
    }

    /// <summary>sRGB byte -> linear-light channel (IEC 61966-2-1 EOTF).</summary>
    public static double SrgbToLinear(byte c8)
    {
        double c = c8 / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    /// <summary>sRGB -> CIE XYZ (D65, 2-degree observer), scaled x100 the way picker tools
    /// display it (white = X 95.05, Y 100, Z 108.9).</summary>
    public static (double X, double Y, double Z) RgbToXyz(byte r8, byte g8, byte b8)
    {
        double r = SrgbToLinear(r8), g = SrgbToLinear(g8), b = SrgbToLinear(b8);
        double x = 0.4124564 * r + 0.3575761 * g + 0.1804375 * b;
        double y = 0.2126729 * r + 0.7151522 * g + 0.0721750 * b;
        double z = 0.0193339 * r + 0.1191920 * g + 0.9503041 * b;
        return (x * 100.0, y * 100.0, z * 100.0);
    }

    /// <summary>CIE XYZ (x100, D65) -> CIELAB (L in [0,100], a/b roughly [-128,128]).</summary>
    public static (double L, double A, double B) RgbToLab(byte r8, byte g8, byte b8)
    {
        var (x, y, z) = RgbToXyz(r8, g8, b8);
        double fx = LabF(x / 95.047), fy = LabF(y / 100.0), fz = LabF(z / 108.883);
        return (116.0 * fy - 16.0, 500.0 * (fx - fy), 200.0 * (fy - fz));

        static double LabF(double t) =>
            t > 216.0 / 24389.0 ? Math.Cbrt(t) : (24389.0 / 27.0 * t + 16.0) / 116.0;
    }

    /// <summary>sRGB -> Oklab (Björn Ottosson's reference constants). L in [0,1].</summary>
    public static (double L, double A, double B) RgbToOklab(byte r8, byte g8, byte b8)
    {
        double r = SrgbToLinear(r8), g = SrgbToLinear(g8), b = SrgbToLinear(b8);
        double l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        double m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        double s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
        return (
            0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
            1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
            0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
    }

    /// <summary>sRGB -> Oklch (Oklab in polar form): L in [0,1], C >= 0, H in [0,360).</summary>
    public static (double L, double C, double H) RgbToOklch(byte r8, byte g8, byte b8)
    {
        var (l, a, b) = RgbToOklab(r8, g8, b8);
        double c = Math.Sqrt(a * a + b * b);
        double h = Math.Atan2(b, a) * 180.0 / Math.PI;
        if (h < 0) h += 360.0;
        return (l, c, h);
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
