using System.Globalization;
using System.Text;

namespace RoeSnip.Core.Color;

/// <summary>The custom-color-format template language (PowerToys Color Picker parity, plus the
/// RoeSnip-only <c>%Nt</c> nits parameter): a format string is literal text with two-letter
/// <c>%Xx</c> parameters substituted per pixel. No UI-framework dependency. Ported verbatim from
/// the frozen WPF app's own src/RoeSnip/Overlay/ColorFormatTemplate.cs (item 22).
///
/// Parameters: %Re red, %Gr green, %Bl blue, %Al alpha (always 255 — RoeSnip samples are opaque),
/// %Cy cyan, %Ma magenta, %Ye yellow, %Bk black key (CMYK, 0-100), %Hu hue (0-360),
/// %Hn natural hue ("R64"), %Si saturation HSI, %Sl saturation HSL, %Sb saturation HSB/HSV,
/// %Br brightness, %Va value, %In intensity, %Ll lightness (HSL), %Wh whiteness, %Bn blackness
/// (all 0-100), %Lc lightness (CIE), %Ca/%Cb chromaticity A/B (CIELAB), %Lo lightness
/// (Oklab/Oklch), %Oa/%Ob Oklab a/b, %Oc chroma (Oklch), %Oh hue (Oklch), %Xv/%Yv/%Zv CIEXYZ,
/// %Dv decimal (BGR), %Dr decimal (RGB), %Na nearest CSS color name, %Nt nits (needs a live HDR
/// sample; renders "n/a" when unavailable).
///
/// Format suffixes — %Re/%Gr/%Bl/%Al accept one of: b byte (default), h/H hex one digit
/// (low/uppercase), x/X hex two digits, f float 0-1 with leading zero, F float without leading
/// zero (e.g. "%ReX" = red as uppercase two-digit hex). %Lc/%Ca/%Cb accept i (round to integer).
/// Anything that isn't a recognized parameter passes through verbatim.</summary>
public static class ColorFormatTemplate
{
    /// <summary>Expands <paramref name="template"/> for the given sRGB pixel.
    /// <paramref name="nits"/> is the live HDR luminance sample if one exists (magnifier / a
    /// fresh pick); null (a shade, a reloaded recent color) renders %Nt as "n/a".</summary>
    public static string Format(string template, byte r, byte g, byte b, double? nits)
    {
        var sb = new StringBuilder(template.Length + 16);
        int i = 0;
        while (i < template.Length)
        {
            // A parameter is '%' + a two-letter code (+ at most one format-suffix char).
            if (template[i] == '%' && i + 2 < template.Length)
            {
                string code = template.Substring(i + 1, 2);
                char suffix = i + 3 < template.Length ? template[i + 3] : '\0';
                if (TryExpand(code, suffix, r, g, b, nits, out string expanded, out bool consumedSuffix))
                {
                    sb.Append(expanded);
                    i += 3 + (consumedSuffix ? 1 : 0);
                    continue;
                }
            }
            sb.Append(template[i]);
            i++;
        }
        return sb.ToString();
    }

    private static bool TryExpand(
        string code, char suffix, byte r, byte g, byte b, double? nits,
        out string expanded, out bool consumedSuffix)
    {
        consumedSuffix = false;
        switch (code)
        {
            // ---- channel params: honor the byte/hex/float suffixes ----
            case "Re": expanded = Channel(r, suffix, ref consumedSuffix); return true;
            case "Gr": expanded = Channel(g, suffix, ref consumedSuffix); return true;
            case "Bl": expanded = Channel(b, suffix, ref consumedSuffix); return true;
            case "Al": expanded = Channel(255, suffix, ref consumedSuffix); return true;

            // ---- CMYK (0-100) ----
            case "Cy": expanded = Percent(ColorFormatting.RgbToCmyk(r, g, b).C); return true;
            case "Ma": expanded = Percent(ColorFormatting.RgbToCmyk(r, g, b).M); return true;
            case "Ye": expanded = Percent(ColorFormatting.RgbToCmyk(r, g, b).Y); return true;
            case "Bk": expanded = Percent(ColorFormatting.RgbToCmyk(r, g, b).K); return true;

            // ---- hue family ----
            case "Hu": expanded = Round0(ColorFormatting.RgbToHsv(r, g, b).H); return true;
            case "Hn": expanded = ColorFormatting.NaturalHue(r, g, b); return true;

            // ---- saturations / lightness / value / whiteness / blackness (0-100) ----
            case "Si": expanded = Percent(ColorFormatting.RgbToHsi(r, g, b).S); return true;
            case "Sl": expanded = Percent(ColorFormatting.RgbToHsl(r, g, b).S); return true;
            case "Sb": expanded = Percent(ColorFormatting.RgbToHsv(r, g, b).S); return true;
            case "Br": expanded = Percent(ColorFormatting.RgbToHsv(r, g, b).V); return true;
            case "Va": expanded = Percent(ColorFormatting.RgbToHsv(r, g, b).V); return true;
            case "In": expanded = Percent(ColorFormatting.RgbToHsi(r, g, b).I); return true;
            case "Ll": expanded = Percent(ColorFormatting.RgbToHsl(r, g, b).L); return true;
            case "Wh": expanded = Percent(ColorFormatting.RgbToHwb(r, g, b).W); return true;
            case "Bn": expanded = Percent(ColorFormatting.RgbToHwb(r, g, b).B); return true;

            // ---- CIELAB: honor the 'i' suffix ----
            case "Lc": expanded = CieValue(ColorFormatting.RgbToLab(r, g, b).L, suffix, ref consumedSuffix); return true;
            case "Ca": expanded = CieValue(ColorFormatting.RgbToLab(r, g, b).A, suffix, ref consumedSuffix); return true;
            case "Cb": expanded = CieValue(ColorFormatting.RgbToLab(r, g, b).B, suffix, ref consumedSuffix); return true;

            // ---- Oklab / Oklch ----
            case "Lo": expanded = Round2(ColorFormatting.RgbToOklab(r, g, b).L); return true;
            case "Oa": expanded = Round2(ColorFormatting.RgbToOklab(r, g, b).A); return true;
            case "Ob": expanded = Round2(ColorFormatting.RgbToOklab(r, g, b).B); return true;
            case "Oc": expanded = Round2(ColorFormatting.RgbToOklch(r, g, b).C); return true;
            case "Oh": expanded = Round2(ColorFormatting.RgbToOklch(r, g, b).H); return true;

            // ---- CIEXYZ (x100, 4 decimals like PowerToys) ----
            case "Xv": expanded = Round4(ColorFormatting.RgbToXyz(r, g, b).X); return true;
            case "Yv": expanded = Round4(ColorFormatting.RgbToXyz(r, g, b).Y); return true;
            case "Zv": expanded = Round4(ColorFormatting.RgbToXyz(r, g, b).Z); return true;

            // ---- packed integers ----
            case "Dv": expanded = ((b << 16) | (g << 8) | r).ToString(CultureInfo.InvariantCulture); return true;
            case "Dr": expanded = ((r << 16) | (g << 8) | b).ToString(CultureInfo.InvariantCulture); return true;

            // ---- name / nits ----
            case "Na": expanded = ColorNames.Nearest(r, g, b); return true;
            case "Nt": expanded = nits is { } n ? n.ToString("0.0", CultureInfo.InvariantCulture) : "n/a"; return true;

            default:
                expanded = string.Empty;
                return false;
        }
    }

    private static string Channel(byte value, char suffix, ref bool consumedSuffix)
    {
        switch (suffix)
        {
            case 'b': consumedSuffix = true; return value.ToString(CultureInfo.InvariantCulture);
            case 'h': consumedSuffix = true; return (value >> 4).ToString("x", CultureInfo.InvariantCulture);
            case 'H': consumedSuffix = true; return (value >> 4).ToString("X", CultureInfo.InvariantCulture);
            case 'x': consumedSuffix = true; return value.ToString("x2", CultureInfo.InvariantCulture);
            case 'X': consumedSuffix = true; return value.ToString("X2", CultureInfo.InvariantCulture);
            case 'f': consumedSuffix = true; return Float01(value, leadingZero: true);
            case 'F': consumedSuffix = true; return Float01(value, leadingZero: false);
            default: return value.ToString(CultureInfo.InvariantCulture); // byte is the default
        }
    }

    /// <summary>Channel as 0-1 float, two decimals with trailing zeros trimmed ("1", "0.89") —
    /// pairs with a literal "f" in a template (e.g. VEC4's "%Reff") to render "0.89f"/"1f".</summary>
    private static string Float01(byte value, bool leadingZero)
    {
        double f = Math.Round(value / 255.0, 2, MidpointRounding.AwayFromZero);
        string s = f.ToString("0.##", CultureInfo.InvariantCulture);
        if (!leadingZero && s.StartsWith("0.", StringComparison.Ordinal))
        {
            s = s.Substring(1);
        }
        return s;
    }

    private static string CieValue(double value, char suffix, ref bool consumedSuffix)
    {
        if (suffix == 'i')
        {
            consumedSuffix = true;
            return Round0(value);
        }
        return Round2(value);
    }

    private static string Percent(double fraction01) =>
        Math.Round(fraction01 * 100.0, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);

    private static string Round0(double v) =>
        Math.Round(v, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);

    private static string Round2(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Round4(double v) => v.ToString("0.0000", CultureInfo.InvariantCulture);
}
