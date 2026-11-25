using System;
using System.Collections.Generic;

namespace RoeSnip.Core.Overlay;

/// <summary>Pure, UI-framework-free geometry/color-math helpers backing an overlay's Select-tool
/// hit-testing and selection-handle contrast rule (WPF's Feature B, src/RoeSnip/Overlay/
/// AnnotationLayer.cs:308-921). Every method here takes plain doubles/bytes rather than a
/// WPF/Avalonia Point/Color - the two ports each have their own incompatible geometry types, so
/// this is the slice that genuinely has zero framework dependency and is worth sharing (and
/// unit-testing directly) rather than duplicating. The WPF app is frozen and keeps its own
/// hand-rolled copy of this same math inline; only the Avalonia AnnotationLayer calls into this.</summary>
public static class AnnotationGeometry
{
    /// <summary>Plain point-to-segment distance - the Line/Arrow body hit test (a click counts as
    /// "on" the shape when it's within strokeWidth/2 + padding of the segment, not just its
    /// bounding box, which for a diagonal line is mostly empty space).</summary>
    public static double DistanceToSegment(double px, double py, double ax, double ay, double bx, double by)
    {
        double abx = bx - ax, aby = by - ay;
        double lengthSq = abx * abx + aby * aby;
        if (lengthSq < 1e-9)
        {
            double dxp = px - ax, dyp = py - ay;
            return Math.Sqrt(dxp * dxp + dyp * dyp); // degenerate segment - distance to the single point
        }
        double apx = px - ax, apy = py - ay;
        double u = Math.Clamp((apx * abx + apy * aby) / lengthSq, 0.0, 1.0);
        double cx = ax + abx * u, cy = ay + aby * u;
        double ddx = px - cx, ddy = py - cy;
        return Math.Sqrt(ddx * ddx + ddy * ddy);
    }

    /// <summary>Closed outline of an arrow as x,y pairs: a sharp swept-back head whose tip is exactly
    /// (tx,ty), a shaft that ends inside the head (never past the tip) and a slightly tapered tail.
    /// Fill it with no stroke. Empty for a degenerate (sub-pixel) arrow. The head scales with the
    /// stroke width, has a minimum so thin arrows keep a visible head, and is capped at half the
    /// arrow's length so a short arrow stays an arrow.</summary>
    public static double[] ArrowOutline(double fx, double fy, double tx, double ty, double width)
    {
        double dx = tx - fx, dy = ty - fy;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1.0)
        {
            return Array.Empty<double>();
        }
        double ux = dx / len, uy = dy / len;
        double nx = -uy, ny = ux;
        double w = Math.Max(1.0, width);

        double headLen = Math.Max(16.0, w * 4.0 + 6.0);
        double headHalf = Math.Max(headLen * 0.45, w * 1.25);
        if (headLen > len * 0.5)
        {
            double k = len * 0.5 / headLen;
            headLen *= k;
            headHalf *= k;
        }
        headHalf = Math.Max(headHalf, w * 0.5 + 0.75);

        double shaftHalf = w * 0.5;
        double tailHalf = Math.Max(0.5, shaftHalf * 0.65);
        double notchBack = headLen * 0.72;

        double bx = tx - ux * headLen, by = ty - uy * headLen;     // barb baseline centre
        double mx = tx - ux * notchBack, my = ty - uy * notchBack; // shaft/head join

        return new[]
        {
            fx + nx * tailHalf, fy + ny * tailHalf,
            mx + nx * shaftHalf, my + ny * shaftHalf,
            bx + nx * headHalf, by + ny * headHalf,
            tx, ty,
            bx - nx * headHalf, by - ny * headHalf,
            mx - nx * shaftHalf, my - ny * shaftHalf,
            fx - nx * tailHalf, fy - ny * tailHalf,
        };
    }

    /// <summary>Snaps a stroke centre coordinate so an axis-aligned stroke of this width covers whole
    /// device pixels: odd widths sit on a pixel centre (n + 0.5), even widths on a pixel edge.</summary>
    public static double PixelAlign(double v, double width)
    {
        long w = (long)Math.Round(Math.Max(1.0, width));
        return (w & 1) == 1 ? Math.Floor(v) + 0.5 : Math.Round(v);
    }

    /// <summary>Freehand path: a line to the first midpoint, quadratic curves (control = each interior
    /// point, end = the midpoint to the next) and a final line to the last point. Rounds the corners
    /// of sparse, fast mouse samples; passes through the first and last points. Needs 2+ points.</summary>
    public static (bool Quad, double Cx, double Cy, double Ex, double Ey)[] SmoothPath(
        IReadOnlyList<(double X, double Y)> pts)
    {
        int n = pts.Count;
        if (n < 2)
        {
            return Array.Empty<(bool, double, double, double, double)>();
        }
        if (n == 2)
        {
            return new[] { (false, 0.0, 0.0, pts[1].X, pts[1].Y) };
        }
        var res = new List<(bool, double, double, double, double)>(n + 1)
        {
            (false, 0.0, 0.0, (pts[0].X + pts[1].X) / 2, (pts[0].Y + pts[1].Y) / 2),
        };
        for (int i = 1; i < n - 1; i++)
        {
            res.Add((true, pts[i].X, pts[i].Y, (pts[i].X + pts[i + 1].X) / 2, (pts[i].Y + pts[i + 1].Y) / 2));
        }
        res.Add((false, 0.0, 0.0, pts[n - 1].X, pts[n - 1].Y));
        return res.ToArray();
    }

    /// <summary>WCAG relative luminance (sRGB) - a standard, cheap "how light does this read"
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
    /// color, rather than falling back to plain black/white - false when the inverse is too close
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

    /// <summary>Whether this color reads as "dark" (luminance below the WCAG midpoint) - used both
    /// for the near-mid-gray fill fallback and for the handle border's "opposite pole" rule.</summary>
    public static bool IsDark(byte r, byte g, byte b) => RelativeLuminance(r, g, b) < 0.5;
}
