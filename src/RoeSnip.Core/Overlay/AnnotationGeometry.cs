using System;

namespace RoeSnip.Core.Overlay;

/// <summary>Pure, UI-framework-free geometry/color-math helpers backing an overlay's Select-tool
/// hit-testing and selection-handle contrast rule (WPF's Feature B, src/RoeSnip/Overlay/
/// AnnotationLayer.cs:308-921). Every method here takes plain doubles/bytes rather than a
/// WPF/Avalonia Point/Color — the two ports each have their own incompatible geometry types, so
/// this is the slice that genuinely has zero framework dependency and is worth sharing (and
/// unit-testing directly) rather than duplicating. The WPF app is frozen and keeps its own
/// hand-rolled copy of this same math inline; only the Avalonia AnnotationLayer calls into this.</summary>
public static class AnnotationGeometry
{
    /// <summary>Plain point-to-segment distance — the Line/Arrow body hit test (a click counts as
    /// "on" the shape when it's within strokeWidth/2 + padding of the segment, not just its
    /// bounding box, which for a diagonal line is mostly empty space).</summary>
    public static double DistanceToSegment(double px, double py, double ax, double ay, double bx, double by)
    {
        double abx = bx - ax, aby = by - ay;
        double lengthSq = abx * abx + aby * aby;
        if (lengthSq < 1e-9)
        {
            double dxp = px - ax, dyp = py - ay;
            return Math.Sqrt(dxp * dxp + dyp * dyp); // degenerate segment — distance to the single point
        }
        double apx = px - ax, apy = py - ay;
        double u = Math.Clamp((apx * abx + apy * aby) / lengthSq, 0.0, 1.0);
        double cx = ax + abx * u, cy = ay + aby * u;
        double ddx = px - cx, ddy = py - cy;
        return Math.Sqrt(ddx * ddx + ddy * ddy);
    }

    /// <summary>WCAG relative luminance (sRGB) — a standard, cheap "how light does this read"
    /// metric; more than precise enough for a coarse contrast gate like the selection handle
    /// fill/border rule below.</summary>
    public static double RelativeLuminance(byte r, byte g, byte b)
    {
        static double Linearize(byte channel)
        {
            double s = channel / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linearize(r) + 0.7152 * Linearize(g) + 0.0722 * Linearize(b);
    }

    /// <summary>Whether a selection handle should fill with the plain RGB inverse of this stroke
    /// color, rather than falling back to plain black/white — false when the inverse is too close
    /// in luminance to the original (the near-mid-gray case, e.g. ~128,128,128 inverts to
    /// ~127,127,127, which is indistinguishable): callers should fall back to
    /// <see cref="IsDark"/>-chosen black/white in that case so the handle stays visible against the
    /// shape it's attached to.</summary>
    public static bool ShouldUseInverseFill(byte r, byte g, byte b)
    {
        double original = RelativeLuminance(r, g, b);
        double inverse = RelativeLuminance((byte)(255 - r), (byte)(255 - g), (byte)(255 - b));
        return Math.Abs(original - inverse) >= 0.3;
    }

    /// <summary>Whether this color reads as "dark" (luminance below the WCAG midpoint) — used both
    /// for the near-mid-gray fill fallback and for the handle border's "opposite pole" rule.</summary>
    public static bool IsDark(byte r, byte g, byte b) => RelativeLuminance(r, g, b) < 0.5;
}
