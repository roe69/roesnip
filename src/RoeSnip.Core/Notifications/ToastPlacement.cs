using RoeSnip.Core.Capture;

namespace RoeSnip.Core.Notifications;

/// <summary>Where a toast window goes: next to the mouse cursor, flipped left/up so it stays fully
/// inside the work area of the monitor the cursor is on. Pure math in physical pixels; each app
/// converts to its own window position API.</summary>
public static class ToastPlacement
{
    /// <summary>Gap between the cursor hotspot and the toast's nearest corner, in DIPs.</summary>
    public const int CursorOffsetDip = 16;

    /// <summary>Gap from the work area's bottom edge for the monitor fallback, in DIPs.</summary>
    public const int EdgeMarginDip = 16;

    /// <summary>Top-left for a <paramref name="width"/> x <paramref name="height"/> toast placed
    /// <paramref name="offset"/> px down-right of the cursor. Flips to the left of the cursor when
    /// it would cross the work area's right edge and above it when it would cross the bottom, then
    /// clamps into the work area (a toast larger than the work area pins to its top-left).</summary>
    public static (int X, int Y) NextToCursor(int cursorX, int cursorY, int width, int height, RectPhysical workArea, int offset)
    {
        int x = cursorX + offset;
        if (x + width > workArea.Right)
        {
            x = cursorX - offset - width;
        }
        int y = cursorY + offset;
        if (y + height > workArea.Bottom)
        {
            y = cursorY - offset - height;
        }
        return (Clamp(x, workArea.Left, workArea.Right - width), Clamp(y, workArea.Top, workArea.Bottom - height));
    }

    /// <summary>Top-left for a toast centred horizontally at the bottom of the work area.</summary>
    public static (int X, int Y) BottomCentre(int width, int height, RectPhysical workArea, int margin)
    {
        int x = workArea.Left + (workArea.Width - width) / 2;
        int y = workArea.Bottom - margin - height;
        return (Clamp(x, workArea.Left, workArea.Right - width), Clamp(y, workArea.Top, workArea.Bottom - height));
    }

    /// <summary>Index of the monitor whose bounds contain the point, or -1 when none does.</summary>
    public static int MonitorIndexAt(IReadOnlyList<RectPhysical> monitorBounds, int x, int y)
    {
        for (int i = 0; i < monitorBounds.Count; i++)
        {
            var b = monitorBounds[i];
            if (x >= b.Left && x < b.Right && y >= b.Top && y < b.Bottom)
            {
                return i;
            }
        }
        return -1;
    }

    private static int Clamp(int value, int min, int max) => max < min ? min : Math.Clamp(value, min, max);
}
