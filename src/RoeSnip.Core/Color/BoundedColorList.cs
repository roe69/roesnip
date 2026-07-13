namespace RoeSnip.Core.Color;

/// <summary>Pure helper shared by the two persisted color lists (RoeSnipSettings.RecentPickedColors
/// from the eyedropper, and the toolbar's own palette-related color lists): pushes a value to the
/// front of a newest-first list, removing any case-insensitive duplicate first, and caps the
/// result at <paramref name="maxCount"/> entries (via the Push call, LRU eviction of the oldest).
/// No UI-framework dependency. Ported verbatim from the frozen WPF app's own
/// src/RoeSnip/Overlay/BoundedColorList.cs (item 22).</summary>
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
