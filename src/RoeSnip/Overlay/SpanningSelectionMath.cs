using System;
using System.Collections.Generic;
using System.Windows;
using RoeSnip.Capture;

namespace RoeSnip.Overlay;

// RoeSnip.csproj enables both UseWPF and UseWindowsForms, so System.Windows.Forms/System.Drawing
// are in scope alongside System.Windows — alias the colliding name to WPF's, matching every other
// Overlay/*.cs file's own alias block (see e.g. OverlayWindow.xaml.cs).
using Point = System.Windows.Point;

/// <summary>Which of a spanning selection's LOCAL (per-monitor) edges are also edges of the TRUE
/// virtual-desktop rect, versus merely where that monitor's own screen boundary happened to cut the
/// selection off. A corner/side resize handle only ever makes sense on a real edge — dragging a
/// "handle" that's actually just a monitor-boundary clip would silently be dragging the wrong thing
/// (see <see cref="SpanningSelectionMath.Distribute"/>'s doc comment for how these are computed).
/// <see cref="SelectionAdorner.RealEdges"/> defaults to <see cref="All"/>, which is exactly every
/// ordinary (non-spanning) selection's answer — this type only ever varies while spanning.</summary>
[Flags]
public enum SelectionEdges
{
    None = 0,
    Left = 1,
    Top = 2,
    Right = 4,
    Bottom = 8,
    All = Left | Top | Right | Bottom,
}

/// <summary>One monitor's own slice of a spanning candidate rect, as computed by
/// <see cref="SpanningSelectionMath.Distribute"/>: which monitor (by index into the list passed in),
/// its local (monitor-relative) intersection rect, and which of that rect's 4 edges are real edges
/// of the shared virtual rect rather than a clip at this monitor's own screen boundary.</summary>
public readonly record struct SpanningHit(int MonitorIndex, RectPhysical LocalRect, SelectionEdges RealEdges);

/// <summary>Result of one <see cref="SpanningSelectionMath.Distribute"/> call: the candidate rect
/// after clamping to the virtual desktop's own bounding box, every monitor that ended up with a
/// non-empty intersection, and whether the result counts as "spanning" (touches 2+ monitors).</summary>
public readonly record struct SpanningDistribution(
    RectPhysical ClampedVirtual, IReadOnlyList<SpanningHit> Hits, bool IsSpanning);

/// <summary>Pure geometry for the cross-monitor ("spanning") selection feature — no WPF Window, no
/// mutable session/window state, nothing here touches pixels. Deliberately factored out of
/// OverlayController.OverlaySession (which owns the actual per-window side effects) and out of
/// OverlayWindow (which owns the actual mouse-drag plumbing) so this math — the part with real
/// correctness risk (off-by-one clamps, which edges are "real") — can be unit-tested directly
/// without spinning up any WPF window. See docs/DESIGN-MULTIMON-SELECTION.md.</summary>
public static class SpanningSelectionMath
{
    /// <summary>The union of every monitor's own bounds — the outer box a spanning drag candidate
    /// gets clamped to before being distributed (a drag can't select space with no monitor at all,
    /// e.g. the gap between two non-adjacent displays, even though it CAN span the monitors on
    /// either side of that gap).</summary>
    public static RectPhysical ComputeVirtualDesktopBounds(IReadOnlyList<RectPhysical> monitorBounds)
    {
        if (monitorBounds.Count == 0)
        {
            return default;
        }

        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        foreach (var b in monitorBounds)
        {
            left = Math.Min(left, b.Left);
            top = Math.Min(top, b.Top);
            right = Math.Max(right, b.Right);
            bottom = Math.Max(bottom, b.Bottom);
        }
        return new RectPhysical(left, top, right, bottom);
    }

    public static RectPhysical ClampToVirtualDesktop(RectPhysical r, RectPhysical virtualDesktopBounds) => new(
        Math.Clamp(r.Left, virtualDesktopBounds.Left, virtualDesktopBounds.Right),
        Math.Clamp(r.Top, virtualDesktopBounds.Top, virtualDesktopBounds.Bottom),
        Math.Clamp(r.Right, virtualDesktopBounds.Left, virtualDesktopBounds.Right),
        Math.Clamp(r.Bottom, virtualDesktopBounds.Top, virtualDesktopBounds.Bottom));

    private static RectPhysical IntersectRects(RectPhysical a, RectPhysical b) => new(
        Math.Max(a.Left, b.Left), Math.Max(a.Top, b.Top),
        Math.Min(a.Right, b.Right), Math.Min(a.Bottom, b.Bottom));

    /// <summary>The one primitive that makes both a fresh drag AND a resize/move of an already-placed
    /// spanning selection correct (see OverlayWindow's NewSelection/SpanningResize/SpanningMove
    /// handlers, which all funnel their candidate rect through here rather than any window's own
    /// local frame): clamps <paramref name="candidateVirtual"/> to the virtual desktop's own bounding
    /// box, intersects the result against every monitor in <paramref name="monitorBounds"/>, and for
    /// each non-empty intersection also works out which of that monitor's own 4 local edges are REAL
    /// edges of the (clamped) candidate rather than just where this monitor's screen happened to cut
    /// it off — an edge is real iff the intersection didn't have to move it inward from the clamped
    /// candidate's own edge, i.e. <c>intersection.Left == clamped.Left</c> and so on for the other 3.
    /// This is exactly the same comparison <see cref="IntersectRects"/> makes internally, just
    /// surfaced so the caller (and the Adorner, transitively) knows which handles are meaningful to
    /// offer: a handle on a clipped edge would resize based on this window's own visible slice, which
    /// silently desyncs from the true rect the moment the selection is redrawn — see Adorner's own
    /// SuppressBadge/RealEdges doc comments. Degenerates to the plain single-monitor case whenever
    /// the candidate only ever touches one monitor (IsSpanning stays false, that monitor's RealEdges
    /// is always <see cref="SelectionEdges.All"/> since nothing else can clip it away from itself).</summary>
    public static SpanningDistribution Distribute(
        RectPhysical candidateVirtual, RectPhysical virtualDesktopBounds, IReadOnlyList<RectPhysical> monitorBounds)
    {
        var clamped = ClampToVirtualDesktop(candidateVirtual.Normalized(), virtualDesktopBounds);

        var hits = new List<SpanningHit>(monitorBounds.Count);
        for (int i = 0; i < monitorBounds.Count; i++)
        {
            var bounds = monitorBounds[i];
            var intersection = IntersectRects(clamped, bounds);
            if (intersection.Width <= 0 || intersection.Height <= 0)
            {
                continue;
            }

            var edges = SelectionEdges.None;
            if (intersection.Left == clamped.Left) edges |= SelectionEdges.Left;
            if (intersection.Top == clamped.Top) edges |= SelectionEdges.Top;
            if (intersection.Right == clamped.Right) edges |= SelectionEdges.Right;
            if (intersection.Bottom == clamped.Bottom) edges |= SelectionEdges.Bottom;

            var localRect = new RectPhysical(
                intersection.Left - bounds.Left, intersection.Top - bounds.Top,
                intersection.Right - bounds.Left, intersection.Bottom - bounds.Top);
            hits.Add(new SpanningHit(i, localRect, edges));
        }

        return new SpanningDistribution(clamped, hits, hits.Count >= 2);
    }

    /// <summary>Applies one resize handle's delta to <paramref name="start"/>, replacing the edge(s)
    /// that handle owns with <paramref name="px"/>'s coordinates — a pure function of (start rect,
    /// handle, pointer position), so it works identically whether "pointer position" is a window's
    /// own local physical pixels (the plain single-monitor Resize drag, OverlayWindow's own call
    /// site) or a virtual-desktop physical position (SpanningResize — see OverlayWindow's
    /// OnPreviewMouseMove). Normalizes the result so a handle dragged past its opposite edge (e.g.
    /// dragging Right left of Left) flips into a valid, non-negative-size rect instead of staying
    /// inverted.</summary>
    public static RectPhysical ApplyResize(RectPhysical start, SelectionHandle handle, Point px)
    {
        int left = start.Left, top = start.Top, right = start.Right, bottom = start.Bottom;
        int x = (int)px.X, y = (int)px.Y;

        switch (handle)
        {
            case SelectionHandle.TopLeft: left = x; top = y; break;
            case SelectionHandle.Top: top = y; break;
            case SelectionHandle.TopRight: right = x; top = y; break;
            case SelectionHandle.Right: right = x; break;
            case SelectionHandle.BottomRight: right = x; bottom = y; break;
            case SelectionHandle.Bottom: bottom = y; break;
            case SelectionHandle.BottomLeft: left = x; bottom = y; break;
            case SelectionHandle.Left: left = x; break;
        }

        return new RectPhysical(left, top, right, bottom).Normalized();
    }
}
