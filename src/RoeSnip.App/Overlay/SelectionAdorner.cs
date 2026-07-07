using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using RoeSnip.Core.Capture;

namespace RoeSnip.App.Overlay;

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
/// window from the correlated Screen's Scaling) — the mixed-DPI contract requires physical pixels
/// everywhere except this view-layer conversion. Ported from the frozen WPF app's
/// src/RoeSnip/Overlay/SelectionAdorner.cs.</summary>
public sealed class SelectionAdorner : Control
{
    private const double HandleSizePx = 10.0;
    private const double HandleHitPaddingPx = 7.0;

    private static readonly IBrush BorderBrushColor = new SolidColorBrush(Color.FromRgb(0x2E, 0xC8, 0xFF));
    private static readonly IBrush HandleFill = Brushes.White;
    private static readonly IBrush BadgeBackground = new SolidColorBrush(Color.FromArgb(0xD0, 0x10, 0x10, 0x12));

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

    public override void Render(DrawingContext dc)
    {
        if (SelectionPx is not { } sel)
        {
            return;
        }

        var r = sel.Normalized();
        double sx = DeviceScaleX <= 0 ? 1.0 : DeviceScaleX;
        double sy = DeviceScaleY <= 0 ? 1.0 : DeviceScaleY;

        var dipRect = new Rect(r.Left / sx, r.Top / sy, Math.Max(0, r.Width / sx), Math.Max(0, r.Height / sy));

        var borderPen = new Pen(BorderBrushColor, 1.5);
        dc.DrawRectangle(null, borderPen, dipRect);

        double handleDip = HandleSizePx / Math.Max(sx, sy);
        foreach (var center in HandleCenters(dipRect))
        {
            var handleRect = new Rect(center.X - handleDip / 2, center.Y - handleDip / 2, handleDip, handleDip);
            dc.DrawRectangle(HandleFill, borderPen, handleRect);
        }

        string label = string.Create(CultureInfo.InvariantCulture, $"{r.Width} x {r.Height}");
        var typeface = new Typeface(OverlayFonts.Ui, FontStyle.Normal, FontWeight.SemiBold);
        var formatted = new FormattedText(
            label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 13.0, Brushes.White);

        const double badgePad = 6.0;
        double badgeX = dipRect.Left;
        double badgeY = dipRect.Top - formatted.Height - badgePad * 2 - 4.0;
        if (badgeY < 0)
        {
            badgeY = dipRect.Bottom + 4.0; // flip below if the badge would go off the top edge
        }
        var badgeRect = new Rect(Math.Max(0, badgeX), badgeY, formatted.Width + badgePad * 2, formatted.Height + badgePad * 2);
        dc.DrawRectangle(BadgeBackground, null, badgeRect, 3.0, 3.0);
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
