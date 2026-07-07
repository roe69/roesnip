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

/// <summary>Renders the selection border, 8 resize handles, and a physical-pixel size badge
/// ("1920 x 1080"); also answers hit-tests so OverlayWindow can decide what a mouse-down means.
/// All geometry is tracked in physical pixels and scaled down to DIPs only at render/hit-test
/// time via <see cref="DeviceScaleX"/>/<see cref="DeviceScaleY"/> (kept in sync by the owning
/// window from CompositionTarget.TransformToDevice) — the mixed-DPI contract requires physical
/// pixels everywhere except this view-layer conversion.</summary>
public sealed class SelectionAdorner : FrameworkElement
{
    private const double HandleSizePx = 10.0;
    private const double HandleHitPaddingPx = 7.0;

    private static readonly Brush BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0xC8, 0xFF));
    private static readonly Brush HandleFill = Brushes.White;
    private static readonly Brush BadgeBackground = new SolidColorBrush(Color.FromArgb(0xD0, 0x10, 0x10, 0x12));

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

        var borderPen = new Pen(BorderBrush, 1.5);
        dc.DrawRectangle(null, borderPen, dipRect);

        double handleDip = HandleSizePx / Math.Max(sx, sy);
        foreach (var center in HandleCenters(dipRect))
        {
            var handleRect = new Rect(center.X - handleDip / 2, center.Y - handleDip / 2, handleDip, handleDip);
            dc.DrawRectangle(HandleFill, borderPen, handleRect);
        }

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

    private static IEnumerable<Point> HandleCenters(Rect r)
    {
        yield return new Point(r.Left, r.Top);
        yield return new Point(r.Left + r.Width / 2, r.Top);
        yield return new Point(r.Right, r.Top);
        yield return new Point(r.Right, r.Top + r.Height / 2);
        yield return new Point(r.Right, r.Bottom);
        yield return new Point(r.Left + r.Width / 2, r.Bottom);
        yield return new Point(r.Left, r.Bottom);
        yield return new Point(r.Left, r.Top + r.Height / 2);
    }
}
