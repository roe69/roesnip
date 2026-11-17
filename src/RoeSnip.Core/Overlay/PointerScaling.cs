namespace RoeSnip.Core.Overlay;

/// <summary>Pointer sensitivity while the loupe is up: the more the loupe is zoomed in, the slower
/// the pointer moves, so a pixel-perfect snip does not need a steady hand. Pure math only; the
/// per-OS hook that applies it lives in the Platform.* assemblies (and the WPF app's Overlay/).</summary>
public static class PointerScaling
{
    /// <summary>Slowest the pointer ever gets (10% of normal speed).</summary>
    public const double MinFactor = 0.1;

    /// <summary>Speed multiplier for a loupe sample radius. The loupe shows (2r+1) source pixels
    /// across a fixed-size widget, so one cursor pixel moves the loupe content by 1/(2r+1) of its
    /// width. Scaling the pointer by (2r+1)/(2*max+1) keeps that apparent motion equal to what it is
    /// at max zoom out (factor 1.0), at every zoom level. Floored at <see cref="MinFactor"/> (the
    /// tightest zoom, r=1, would be 3/31 = 0.097 for max=15).</summary>
    public static double FactorForRadius(int radius, int maxRadius)
    {
        if (maxRadius < 1)
        {
            return 1.0;
        }
        int r = Math.Clamp(radius, 0, maxRadius);
        double factor = (2.0 * r + 1.0) / (2.0 * maxRadius + 1.0);
        return Math.Clamp(factor, MinFactor, 1.0);
    }
}

/// <summary>Scales integer pointer deltas by a factor, carrying the fractional remainder to the
/// next call so slow movement is never lost to truncation (0.3 px per event still adds up to a
/// pixel). Not thread-safe; one instance per hook. Allocation-free.</summary>
public sealed class PointerDeltaScaler
{
    private double _remainderX, _remainderY;

    public void Reset()
    {
        _remainderX = 0;
        _remainderY = 0;
    }

    /// <summary>Returns the whole-pixel delta to apply. Truncates toward zero, so the same movement
    /// in opposite directions yields opposite results.</summary>
    public void Scale(int dx, int dy, double factor, out int scaledX, out int scaledY)
    {
        double x = dx * factor + _remainderX;
        double y = dy * factor + _remainderY;
        scaledX = (int)x;
        scaledY = (int)y;
        _remainderX = x - scaledX;
        _remainderY = y - scaledY;
    }
}

/// <summary>Installs the OS-level pointer scaling for one overlay session. Windows implements it
/// with a WH_MOUSE_LL hook; other platforms register nothing and the overlay runs unscaled.</summary>
public interface IPointerSensitivityBackend
{
    /// <summary>Starts scaling pointer movement by whatever <paramref name="getFactor"/> returns
    /// (1.0 = untouched). The delegate is called on the installing (UI) thread for every real mouse
    /// move, so it must be fast and allocation-free. Dispose the result to remove the hook. Returns
    /// null when the hook could not be installed.</summary>
    IDisposable? Install(Func<double> getFactor);
}

/// <summary>Where the platform assembly registers its <see cref="IPointerSensitivityBackend"/>
/// (same pattern as CaptureBackendRegistry; Core never names a Platform.* type).</summary>
public static class PointerSensitivityRegistry
{
    public static IPointerSensitivityBackend? Current { get; set; }
}
