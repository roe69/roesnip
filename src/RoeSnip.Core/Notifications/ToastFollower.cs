using RoeSnip.Core.Capture;

namespace RoeSnip.Core.Notifications;

/// <summary>Keeps a visible toast next to a moving pointer (ToastPlacement.NextToCursor) until the
/// user goes for it. Following keeps the gap constant, so a pointer heading for the toast would
/// chase it forever: while the pointer is closer to the toast than the gap it was placed with, the
/// toast holds still, and once the pointer is inside it, or within <see cref="FreezeDistanceDip"/>
/// and <see cref="ApproachDip"/> closer than that gap, it freezes for good so it can be clicked.
/// Pure math in physical px; one instance per toast window.</summary>
public sealed class ToastFollower
{
    /// <summary>An approach only freezes within this distance of the toast, in DIPs.</summary>
    public const int FreezeDistanceDip = 40;

    /// <summary>How much closer than the placement gap the pointer must come to freeze, in DIPs.</summary>
    public const int ApproachDip = 4;

    private int? _lastX;
    private int? _lastY;
    private double _gap; // pointer-to-toast distance of the current placement, px

    public bool Frozen { get; private set; }

    /// <summary>Stops following for good (the pointer entered the toast).</summary>
    public void Freeze() => Frozen = true;

    /// <summary><paramref name="placedFor"/> is the pointer the toast was placed next to (null when
    /// it went to the monitor fallback: the first pointer seen then places it).</summary>
    public ToastFollower((int X, int Y)? placedFor, RectPhysical placed)
    {
        if (placedFor is { } p)
        {
            _lastX = p.X;
            _lastY = p.Y;
            _gap = Distance(p.X, p.Y, placed);
        }
        else
        {
            _gap = -1;
        }
    }

    /// <summary>Called on every poll with the pointer and the toast's current rect. Returns the new
    /// top-left when the toast should move, else null. <paramref name="width"/>/<paramref name="height"/>
    /// and <paramref name="scale"/> are for the monitor the pointer is on.</summary>
    public (int X, int Y)? Step(int cursorX, int cursorY, RectPhysical current, RectPhysical workArea,
        int width, int height, double scale)
    {
        if (Frozen || (cursorX == _lastX && cursorY == _lastY))
        {
            return null;
        }
        _lastX = cursorX;
        _lastY = cursorY;

        double d = Distance(cursorX, cursorY, current);
        if (d <= 0)
        {
            Frozen = true;
            return null;
        }
        if (_gap >= 0 && d < _gap)
        {
            if (d <= FreezeDistanceDip * scale && d <= _gap - ApproachDip * scale)
            {
                Frozen = true;
            }
            return null; // heading for the toast: hold still
        }

        var (x, y) = ToastPlacement.NextToCursor(cursorX, cursorY, width, height, workArea,
            (int)Math.Round(ToastPlacement.CursorOffsetDip * scale));
        _gap = Distance(cursorX, cursorY, RectPhysical.FromSize(x, y, width, height));
        if (x == current.Left && y == current.Top)
        {
            return null;
        }
        return (x, y);
    }

    /// <summary>Euclidean distance from a pixel to a rect (Right/Bottom exclusive); 0 inside.</summary>
    public static double Distance(int x, int y, RectPhysical r)
    {
        int dx = x < r.Left ? r.Left - x : x >= r.Right ? x - r.Right + 1 : 0;
        int dy = y < r.Top ? r.Top - y : y >= r.Bottom ? y - r.Bottom + 1 : 0;
        return Math.Sqrt((double)dx * dx + (double)dy * dy);
    }
}
