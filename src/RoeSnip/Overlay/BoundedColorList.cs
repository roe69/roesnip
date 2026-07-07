using System;
using System.Collections.Generic;

namespace RoeSnip.Overlay;

/// <summary>Pure helper shared by the two persisted color lists added in UX round 2
/// (RoeSnipSettings.RecentPickedColors from the ColorPickerWindow's eyedropper, and
/// RoeSnipSettings.CustomColors from the toolbar's "+" swatch): pushes a value to the front of a
/// newest-first list, removing any case-insensitive duplicate first, and caps the result at
/// <paramref name="maxCount"/> entries (LRU eviction of the oldest). No WPF dependency —
/// unit-testable in isolation.</summary>
public static class BoundedColorList
{
    public static List<string> Push(IReadOnlyList<string> current, string value, int maxCount)
    {
        if (maxCount <= 0)
        {
            return new List<string>();
        }

        var result = new List<string>(Math.Min(maxCount, current.Count + 1)) { value };
        foreach (var existing in current)
        {
            if (result.Count >= maxCount)
            {
                break;
            }
            if (!string.Equals(existing, value, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(existing);
            }
        }
        return result;
    }
}
