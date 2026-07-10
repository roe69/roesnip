using System;
using System.Collections.Generic;
using System.Linq;

namespace RoeSnip.Overlay;

/// <summary>Pure list operations behind the toolbar's editable swatch palette (UX round 5, item 3):
/// the whole palette — formerly a hardcoded 6-preset row plus a separate CustomColors row — is now
/// one persisted, user-editable list (RoeSnipSettings.PaletteColors, "#RRGGBB" strings in display
/// order). Every mutation returns a new list (settings records are immutable); no WPF dependency —
/// unit-testable in isolation, matching BoundedColorList's convention.</summary>
public static class SwatchPalette
{
    /// <summary>Migration cap: a legacy seed (defaults plus old CustomColors migrants) never
    /// exceeds this many swatches. The palette has no append path anymore (swatches are fixed
    /// slots, editable only via right-click Replace), so this only bounds the one-time seed.</summary>
    public const int MaxColors = 13;

    /// <summary>The built-in seed palette: the practical annotation hues in spectrum order, then
    /// the two neutrals last. The toolbar's bottom row is a fixed set of these slots; each is
    /// recolorable via the right-click Replace menu but the count never changes.</summary>
    public static readonly IReadOnlyList<string> DefaultColors = new[]
    {
        "#E53935", "#FB8C00", "#FFB300", "#43A047", "#00ACC1",
        "#1E88E5", "#8E24AA", "#D81B60", "#FFFFFF", "#212121",
    };

    private static readonly Dictionary<string, string> DefaultNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["#E53935"] = "Red",
        ["#FB8C00"] = "Orange",
        ["#FFB300"] = "Amber",
        ["#43A047"] = "Green",
        ["#00ACC1"] = "Cyan",
        ["#1E88E5"] = "Blue",
        ["#8E24AA"] = "Purple",
        ["#D81B60"] = "Pink",
        ["#FFFFFF"] = "White",
        ["#212121"] = "Black",
    };

    /// <summary>Human tooltip for a swatch: the friendly name for the six built-in defaults, the
    /// raw hex otherwise.</summary>
    public static string NameFor(string hex) =>
        DefaultNames.TryGetValue(hex, out var name) ? name : hex;

    /// <summary>One-time migration (item 3): an empty <paramref name="paletteColors"/> means the
    /// settings file predates the editable palette — seed from the built-in defaults plus any
    /// legacy CustomColors ("+"-swatch colors, stored newest-first; appended here oldest-last so
    /// the visual order matches what the old two-row toolbar showed), deduplicated and capped at
    /// <see cref="MaxColors"/>. A non-empty palette is returned as-is (already migrated). The
    /// legacy CustomColors field itself is preserved for downgrade compat — read, never displayed
    /// separately again.</summary>
    public static List<string> EffectivePalette(IReadOnlyList<string> paletteColors, IReadOnlyList<string> legacyCustomColors)
    {
        if (paletteColors.Count > 0)
        {
            return paletteColors.ToList();
        }

        var result = new List<string>(DefaultColors);
        foreach (var hex in legacyCustomColors)
        {
            if (result.Count >= MaxColors)
            {
                break;
            }
            if (!Contains(result, hex))
            {
                result.Add(hex);
            }
        }
        return result;
    }

    /// <summary>Right-click "Replace...": swaps the entry at <paramref name="index"/> in place.
    /// Out-of-range indices return the list unchanged.</summary>
    public static List<string> ReplaceAt(IReadOnlyList<string> palette, int index, string hex)
    {
        var result = palette.ToList();
        if (index >= 0 && index < result.Count)
        {
            result[index] = hex;
        }
        return result;
    }

    private static bool Contains(IReadOnlyList<string> palette, string hex)
    {
        foreach (var existing in palette)
        {
            if (string.Equals(existing, hex, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
