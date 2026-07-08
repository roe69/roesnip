using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using RoeSnip.Capture;

namespace RoeSnip.Overlay;

// RoeSnip.csproj enables both UseWPF and UseWindowsForms, so System.Windows.Forms/System.Drawing
// are in scope alongside System.Windows/System.Windows.Media — alias the colliding names to WPF's.
// (Declared after the namespace line — see AnnotationLayer.cs for why: RoeSnip.Color, a sibling
// WP-A namespace, would otherwise shadow an outer-scope alias for "Color".)
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;
using FontFamily = System.Windows.Media.FontFamily;
// Not a WinForms/System.Drawing collision — FrameworkElement itself declares an instance
// FlowDirection property, which shadows the enum type name "FlowDirection" inside any method of a
// class deriving from it (SelectionAdorner here). Alias to a distinct name to avoid CS0176.
using FlowDir = System.Windows.FlowDirection;

/// <summary>Which part of the selection rect a physical-pixel point landed on, for deciding
/// whether a mouse-down should start a resize drag, a move drag, or a brand-new selection.</summary>
public enum SelectionHandle
{
    None,
    TopLeft, Top, TopRight,
    Right, BottomRight, Bottom,
    BottomLeft, Left,
    Body,
}

/// <summary>Renders the selection border (a two-tone dashed "marching-ants" line with white
/// corner brackets), and a physical-pixel size badge ("1920 x 1080"); also answers hit-tests so
/// OverlayWindow can decide what a mouse-down means. All geometry is tracked in physical pixels and
/// scaled down to DIPs only at render/hit-test time via <see cref="DeviceScaleX"/>/
/// <see cref="DeviceScaleY"/> (kept in sync by the owning window from
/// CompositionTarget.TransformToDevice) — the mixed-DPI contract requires physical pixels
/// everywhere except this view-layer conversion.</summary>
public sealed class SelectionAdorner : FrameworkElement
{
    private const double HandleHitPaddingPx = 7.0;

    // Two-tone border: a dark under-stroke gives contrast on light content, light dashes ride on
    // top — a neutral, non-garish alternative to the old solid cyan line (user feedback). Frozen
    // so WPF can share them across renders. Widths are in DIPs (this is view-layer chrome).
    private static readonly Pen BorderUnderPen = CreateFrozenPen(Color.FromArgb(0xB0, 0x00, 0x00, 0x00), 1.0, null);
    private static readonly Pen BorderDashPen = CreateFrozenPen(
        Color.FromArgb(0xFF, 0xDC, 0xDC, 0xE0), 1.0, new DashStyle(new double[] { 3, 3 }, 0));
    private static readonly Pen CornerUnderPen = CreateFrozenPen(Color.FromArgb(0xC0, 0x00, 0x00, 0x00), 3.0, null);
    private static readonly Pen CornerPen = CreateFrozenPen(Color.FromArgb(0xFF, 0xF5, 0xF5, 0xF8), 2.0, null);

    private const double CornerArmDip = 13.0; // length of each corner-bracket arm

    private static readonly Brush BadgeBackground = new SolidColorBrush(Color.FromArgb(0xD0, 0x10, 0x10, 0x12));

    private static Pen CreateFrozenPen(Color color, double thickness, DashStyle? dash)
    {
        var pen = new Pen(new SolidColorBrush(color), thickness)
        {
            StartLineCap = PenLineCap.Flat,
            EndLineCap = PenLineCap.Flat,
        };
        if (dash is not null)
        {
            pen.DashStyle = dash;
            pen.DashCap = PenLineCap.Flat;
        }
        pen.Freeze();
        return pen;
    }

    public RectPhysical? SelectionPx { get; set; }
    public double DeviceScaleX { get; set; } = 1.0;
    public double DeviceScaleY { get; set; } = 1.0;

    public SelectionAdorner()
    {
        // OverlayWindow does its own hit-testing via HitTestHandle; this element only renders.
        IsHitTestVisible = false;
    }

    /// <summary>Hit-tests a physical-pixel point against the current selection's handles/body.</summary>
    public SelectionHandle HitTestHandle(Point physicalPt)
    {
        if (SelectionPx is not { } sel)
        {
            return SelectionHandle.None;
        }

        var r = sel.Normalized();
        double pad = HandleHitPaddingPx;

        bool nearLeft = Math.Abs(physicalPt.X - r.Left) <= pad;
        bool nearRight = Math.Abs(physicalPt.X - r.Right) <= pad;
        bool nearTop = Math.Abs(physicalPt.Y - r.Top) <= pad;
        bool nearBottom = Math.Abs(physicalPt.Y - r.Bottom) <= pad;
        bool withinX = physicalPt.X >= r.Left - pad && physicalPt.X <= r.Right + pad;
        bool withinY = physicalPt.Y >= r.Top - pad && physicalPt.Y <= r.Bottom + pad;

        if (nearLeft && nearTop) return SelectionHandle.TopLeft;
        if (nearRight && nearTop) return SelectionHandle.TopRight;
        if (nearLeft && nearBottom) return SelectionHandle.BottomLeft;
        if (nearRight && nearBottom) return SelectionHandle.BottomRight;
        if (nearTop && withinX) return SelectionHandle.Top;
        if (nearBottom && withinX) return SelectionHandle.Bottom;
        if (nearLeft && withinY) return SelectionHandle.Left;
        if (nearRight && withinY) return SelectionHandle.Right;

        if (physicalPt.X >= r.Left && physicalPt.X <= r.Right && physicalPt.Y >= r.Top && physicalPt.Y <= r.Bottom)
        {
            return SelectionHandle.Body;
        }

        return SelectionHandle.None;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (SelectionPx is not { } sel)
        {
            return;
        }

        var r = sel.Normalized();
        double sx = DeviceScaleX <= 0 ? 1.0 : DeviceScaleX;
        double sy = DeviceScaleY <= 0 ? 1.0 : DeviceScaleY;

        var dipRect = new Rect(r.Left / sx, r.Top / sy, Math.Max(0, r.Width / sx), Math.Max(0, r.Height / sy));

        // Snap to the pixel grid so a 1px line stays crisp (a half-pixel line renders as a 2px
        // blurry gray smear otherwise).
        var edgeRect = new Rect(
            Math.Round(dipRect.Left) + 0.5, Math.Round(dipRect.Top) + 0.5,
            Math.Max(0, Math.Round(dipRect.Width) - 1), Math.Max(0, Math.Round(dipRect.Height) - 1));

        // Dark under-stroke first, light dashes on top: reads on any background, no garish color.
        dc.DrawRectangle(null, BorderUnderPen, edgeRect);
        dc.DrawRectangle(null, BorderDashPen, edgeRect);

        DrawCornerBrackets(dc, edgeRect);

        string label = string.Create(CultureInfo.InvariantCulture, $"{r.Width} x {r.Height}");
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var formatted = new FormattedText(
            label, CultureInfo.InvariantCulture, FlowDir.LeftToRight, typeface, 13.0, Brushes.White, 1.0);

        const double badgePad = 6.0;
        double badgeX = dipRect.Left;
        double badgeY = dipRect.Top - formatted.Height - badgePad * 2 - 4.0;
        if (badgeY < 0)
        {
            badgeY = dipRect.Bottom + 4.0; // flip below if the badge would go off the top edge
        }
        var badgeRect = new Rect(Math.Max(0, badgeX), badgeY, formatted.Width + badgePad * 2, formatted.Height + badgePad * 2);
        dc.DrawRoundedRectangle(BadgeBackground, null, badgeRect, 3.0, 3.0);
        dc.DrawText(formatted, new Point(badgeRect.Left + badgePad, badgeRect.Top + badgePad));
    }

    /// <summary>Solid white L-brackets at the four corners (dark under-stroke for contrast) — the
    /// affordance that signals "resizable", replacing the old white square dots. The arms shrink
    /// for tiny selections so they never overrun a small rect.</summary>
    private static void DrawCornerBrackets(DrawingContext dc, Rect r)
    {
        double arm = Math.Min(CornerArmDip, Math.Min(r.Width, r.Height) / 2);
        if (arm <= 1)
        {
            return; // selection too small for brackets; the dashed edge alone is enough
        }

        foreach (var pen in new[] { CornerUnderPen, CornerPen })
        {
            // Top-left
            dc.DrawLine(pen, new Point(r.Left, r.Top), new Point(r.Left + arm, r.Top));
            dc.DrawLine(pen, new Point(r.Left, r.Top), new Point(r.Left, r.Top + arm));
            // Top-right
            dc.DrawLine(pen, new Point(r.Right, r.Top), new Point(r.Right - arm, r.Top));
            dc.DrawLine(pen, new Point(r.Right, r.Top), new Point(r.Right, r.Top + arm));
            // Bottom-right
            dc.DrawLine(pen, new Point(r.Right, r.Bottom), new Point(r.Right - arm, r.Bottom));
            dc.DrawLine(pen, new Point(r.Right, r.Bottom), new Point(r.Right, r.Bottom - arm));
            // Bottom-left
            dc.DrawLine(pen, new Point(r.Left, r.Bottom), new Point(r.Left + arm, r.Bottom));
            dc.DrawLine(pen, new Point(r.Left, r.Bottom), new Point(r.Left, r.Bottom - arm));
        }
    }
}
