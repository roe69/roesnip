using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using RoeSnip.Core.Capture;
using SelectionHandle = RoeSnip.Core.Overlay.SelectionHandle;
using SelectionEdges = RoeSnip.Core.Overlay.SelectionEdges;

namespace RoeSnip.App.Overlay;

/// <summary>Renders the selection border, resize handles, and a physical-pixel size badge
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

    /// <summary>Cross-monitor selection: which of the 4 edges of this window's own local selection
    /// rect are REAL edges of the true (possibly multi-monitor) selection, versus merely a clip at
    /// THIS monitor's own screen boundary (see RoeSnip.Core.Overlay.SpanningSelectionMath.Distribute's
    /// doc comment for exactly how these are computed). Gates both hit-testing (<see
    /// cref="HitTestHandle"/> never returns a corner/side handle on a non-real edge) and rendering (a
    /// clipped edge's handle is never drawn — there is nothing there to grab). Defaults to
    /// <see cref="SelectionEdges.All"/>, which is every ordinary (non-spanning) selection's answer on
    /// every window — this only ever varies while spanning, and can differ between the primary and
    /// secondary windows of the SAME spanning selection (each has its own real/clipped edges).</summary>
    public SelectionEdges RealEdges { get; set; } = SelectionEdges.All;

    /// <summary>Cross-monitor selection: true on a SECONDARY window's own adorner while the current
    /// selection spans multiple monitors — that window only ever shows its own local INTERSECTION
    /// with the true selection, so a "W x H" badge here would show that slice's own size rather than
    /// the true selection's (only the PRIMARY window's adorner gets <see cref="OverrideSizeLabel"/>
    /// set, showing the true composite size instead). The border and any real-edge handles still
    /// render on a secondary window (gated by <see cref="RealEdges"/> like every other window) —
    /// resize handles work from secondary windows too; only the badge (and the toolbar) stay
    /// primary-only.</summary>
    public bool SuppressBadge { get; set; }

    /// <summary>Cross-monitor selection: on the PRIMARY window's own adorner while spanning, this
    /// overrides the badge text with the TRUE composite selection's "W x H" instead of this window's
    /// own local slice size, which would otherwise be the only number ever computed here. Null means
    /// "use the local rect's own size" (every non-spanning selection, on every window).</summary>
    public string? OverrideSizeLabel { get; set; }

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

        // Cross-monitor selection: a corner/side only counts as a real handle when RealEdges says
        // its edge(s) are genuine edges of the true selection, not just where THIS monitor's own
        // screen boundary cut it off (RealEdges.All for every ordinary, non-spanning selection, so
        // this is a no-op there). A near-miss on a non-real corner still falls through to the side
        // checks below it.
        bool leftReal = (RealEdges & SelectionEdges.Left) != 0;
        bool topReal = (RealEdges & SelectionEdges.Top) != 0;
        bool rightReal = (RealEdges & SelectionEdges.Right) != 0;
        bool bottomReal = (RealEdges & SelectionEdges.Bottom) != 0;

        if (nearLeft && nearTop && leftReal && topReal) return SelectionHandle.TopLeft;
        if (nearRight && nearTop && rightReal && topReal) return SelectionHandle.TopRight;
        if (nearLeft && nearBottom && leftReal && bottomReal) return SelectionHandle.BottomLeft;
        if (nearRight && nearBottom && rightReal && bottomReal) return SelectionHandle.BottomRight;
        if (nearTop && withinX && topReal) return SelectionHandle.Top;
        if (nearBottom && withinX && bottomReal) return SelectionHandle.Bottom;
        if (nearLeft && withinY && leftReal) return SelectionHandle.Left;
        if (nearRight && withinY && rightReal) return SelectionHandle.Right;

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
        foreach (var (center, real) in HandleCentersWithRealFlag(dipRect))
        {
            if (!real)
            {
                // Cross-monitor selection: a handle sitting on a monitor-boundary clip (not a real
                // edge of the true selection) has nothing to grab there — see RealEdges' own doc
                // comment. The dashed border above still marks the clip either way.
                continue;
            }
            var handleRect = new Rect(center.X - handleDip / 2, center.Y - handleDip / 2, handleDip, handleDip);
            dc.DrawRectangle(HandleFill, borderPen, handleRect);
        }

        if (SuppressBadge)
        {
            return; // secondary window of a spanning selection — no "W x H" badge, see the doc comment
        }

        string label = OverrideSizeLabel ?? string.Create(CultureInfo.InvariantCulture, $"{r.Width} x {r.Height}");
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

    /// <summary>Same 8 handle positions as before, paired with whether that handle sits on a REAL
    /// edge of the true selection per <see cref="RealEdges"/> (a corner needs BOTH adjacent edges
    /// real; a side handle needs just its own edge) — every ordinary, non-spanning selection has
    /// RealEdges == All, so every handle comes back true there, matching the pre-spanning behavior
    /// exactly.</summary>
    private IEnumerable<(Point Center, bool Real)> HandleCentersWithRealFlag(Rect r)
    {
        bool leftReal = (RealEdges & SelectionEdges.Left) != 0;
        bool topReal = (RealEdges & SelectionEdges.Top) != 0;
        bool rightReal = (RealEdges & SelectionEdges.Right) != 0;
        bool bottomReal = (RealEdges & SelectionEdges.Bottom) != 0;

        yield return (new Point(r.Left, r.Top), leftReal && topReal);
        yield return (new Point(r.Left + r.Width / 2, r.Top), topReal);
        yield return (new Point(r.Right, r.Top), rightReal && topReal);
        yield return (new Point(r.Right, r.Top + r.Height / 2), rightReal);
        yield return (new Point(r.Right, r.Bottom), rightReal && bottomReal);
        yield return (new Point(r.Left + r.Width / 2, r.Bottom), bottomReal);
        yield return (new Point(r.Left, r.Bottom), leftReal && bottomReal);
        yield return (new Point(r.Left, r.Top + r.Height / 2), leftReal);
    }
}
