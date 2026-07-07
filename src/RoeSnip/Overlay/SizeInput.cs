using System;
using System.Globalization;

namespace RoeSnip.Overlay;

/// <summary>Pure parsing/clamping/formatting behind the toolbar's numeric size ComboBox (UX round
/// 5, item 2), which replaced the fixed small/medium/big stroke dots: one editable box that shows
/// "Npx" (stroke width, drawing tools) or "Npt" (font size, Text tool). This class is also now the
/// single home of the size clamp ranges — the same 1–32 px stroke and 6–96 pt font limits the
/// scroll-wheel resize in OverlayWindow has always enforced (those literals moved here; the ranges
/// themselves are unchanged and stay authoritative for typed values too). No WPF dependency —
/// unit-testable in isolation, matching the rest of Overlay/*'s pure-helper convention.</summary>
public static class SizeInput
{
    public const double MinStrokePx = 1.0;
    public const double MaxStrokePx = 32.0;
    public const double MinFontPt = 6.0;
    public const double MaxFontPt = 96.0;

    /// <summary>Dropdown presets for stroke width. The spec's jump ladder, truncated at the
    /// authoritative 32 px clamp (48/64 would only ever snap back down to 32).</summary>
    public static readonly double[] StrokePresetsPx = { 1, 2, 3, 4, 6, 8, 12, 16, 24, 32 };

    /// <summary>Dropdown presets for font size (all within the 6–96 pt clamp).</summary>
    public static readonly double[] FontPresetsPt = { 8, 10, 12, 14, 18, 24, 36, 48, 72 };

    public static double ClampStroke(double value) => Math.Clamp(value, MinStrokePx, MaxStrokePx);

    public static double ClampFont(double value) => Math.Clamp(value, MinFontPt, MaxFontPt);

    public static string FormatPx(double value) =>
        string.Create(CultureInfo.InvariantCulture, $"{value:0}px");

    public static string FormatPt(double value) =>
        string.Create(CultureInfo.InvariantCulture, $"{value:0}pt");

    /// <summary>Parses user-typed size text: an optional "px"/"pt" suffix (any case, optional
    /// whitespace before it) after a plain non-negative number ("8", "8px", "8.5 pt", " 12 ").
    /// Returns false for anything else (empty, non-numeric, negative, infinite). Clamping is the
    /// caller's job (stroke vs font ranges differ) via <see cref="ClampStroke"/>/<see cref="ClampFont"/>.</summary>
    public static bool TryParse(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string trimmed = text.Trim();
        if (trimmed.EndsWith("px", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith("pt", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^2].TrimEnd();
        }

        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            && !double.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out parsed))
        {
            return false;
        }

        if (!double.IsFinite(parsed) || parsed < 0)
        {
            return false;
        }

        value = parsed;
        return true;
    }
}
