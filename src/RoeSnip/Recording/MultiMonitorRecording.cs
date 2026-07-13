using System;
using System.Collections.Generic;
using RoeSnip.Capture;

namespace RoeSnip.Recording;

/// <summary>Pure, unit-testable geometry helpers for multi-monitor recording, PHASE 1 (drag a live
/// recording region across a monitor boundary — snap to the destination monitor, not true spanning
/// capture; see PLAN-MULTIMON-RECORDING.md §3). No live WGC/device/window/thread dependency — same
/// "make the testable slice a public class instead of an InternalsVisibleTo edit" convention this
/// codebase already uses for <see cref="Mp4Encoder"/>'s pure math (see that class's own doc
/// comment). Every rect here is virtual-desktop-absolute physical pixels — the convention
/// <see cref="RecordingSession"/>'s own selection state migrated to (plan §4) — never
/// monitor-relative, except where a method name says otherwise (<see cref="ToMonitorRelative"/>,
/// the one conversion point back into <see cref="RegionRecorder"/>'s own monitor-relative
/// contract, which this workstream deliberately left unchanged).
///
/// RECORDING-CORE-EXTRACTION (2026-07): NOT retargeted onto RoeSnip.Core.Recording.MultiMonitorRecording
/// despite that portable copy existing now — this file's own <see cref="MonitorInfo"/> carries a
/// live Win32 HMONITOR handle that <see cref="WgcCapturer"/>/<see cref="DesktopDuplicationCapturer"/>
/// read directly, while the portable <c>RoeSnip.Core.Capture.MonitorInfo</c> deliberately has no
/// equivalent (an opaque cross-platform <c>BackendKey</c> string instead). Unifying the two would
/// mean refactoring this app's live monitor-enumeration/capture layer, out of scope for a
/// recording-math extraction — see docs/PARITY.md's 04-recording-core-extraction entry. Both
/// copies implement the exact same geometry and are independently unit-tested.</summary>
public static class MultiMonitorRecording
{
    /// <summary>The bounding box of every enumerated monitor's own bounds — NOT necessarily a
    /// filled rectangle (monitor layouts can have gaps/notches), just the smallest axis-aligned
    /// rect that contains all of them. Used as the drag-clamp limit for <see cref="RegionOutline"/>
    /// so a region can be dragged onto/across any monitor without hitting a false "edge of one
    /// monitor" clamp (plan §4). Returns a degenerate (0,0,0,0) rect for an empty list — callers
    /// that can hit this (a total enumeration failure) already treat that defensively elsewhere.</summary>
    public static RectPhysical UnionBounds(IReadOnlyList<MonitorInfo> monitors)
    {
        if (monitors.Count == 0)
        {
            return default;
        }

        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        foreach (var m in monitors)
        {
            var b = m.BoundsPx;
            if (b.Left < left) left = b.Left;
            if (b.Top < top) top = b.Top;
            if (b.Right > right) right = b.Right;
            if (b.Bottom > bottom) bottom = b.Bottom;
        }
        return new RectPhysical(left, top, right, bottom);
    }

    /// <summary>True when <paramref name="inner"/> sits entirely within <paramref name="outer"/>
    /// (touching the edge is fine; crossing it is not). This is the handoff TRIGGER: the instant a
    /// recording's live selection stops being fully contained in its current monitor's bounds is
    /// the moment phase 1 hands off (plan §3 — "snap to the destination monitor at the first moment
    /// of would-be straddle").</summary>
    public static bool Contains(RectPhysical outer, RectPhysical inner) =>
        inner.Left >= outer.Left && inner.Top >= outer.Top &&
        inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;

    private static long IntersectionArea(RectPhysical a, RectPhysical b)
    {
        int left = Math.Max(a.Left, b.Left);
        int top = Math.Max(a.Top, b.Top);
        int right = Math.Min(a.Right, b.Right);
        int bottom = Math.Min(a.Bottom, b.Bottom);
        if (right <= left || bottom <= top)
        {
            return 0;
        }
        return (long)(right - left) * (bottom - top);
    }

    /// <summary>Which monitor should "own" a recording region at its current absolute position: the
    /// one with the LARGEST overlap against the region — "snap to majority overlap", matching a
    /// drag's own direction-of-travel intuition (plan §3), never a split. Ties (a region centered
    /// dead-on a seam) break toward the lower <see cref="MonitorInfo.Index"/> for determinism, not
    /// because one choice is more correct than the other. Returns null only when the region doesn't
    /// intersect ANY monitor at all (dragged into a dead gap between non-adjacent monitors) — the
    /// caller's job to decide what "stay put" means in that case (see
    /// <see cref="RecordingSession.OnRegionMoved"/>).</summary>
    public static MonitorInfo? FindOwningMonitor(RectPhysical selectionAbs, IReadOnlyList<MonitorInfo> monitors)
    {
        MonitorInfo? best = null;
        long bestArea = 0;
        foreach (var m in monitors)
        {
            long area = IntersectionArea(selectionAbs, m.BoundsPx);
            if (area <= 0)
            {
                continue;
            }
            if (best is null || area > bestArea || (area == bestArea && m.Index < best.Index))
            {
                bestArea = area;
                best = m;
            }
        }
        return best;
    }

    /// <summary>Slides (NEVER resizes) a region so it fits fully within one monitor's bounds — the
    /// "clamp the new selection fully onto the destination monitor" step of a handoff (plan §3).
    /// The encoder canvas is fixed for the whole take (plan §3's own "confirmed, no new work needed
    /// here" note), so width/height are always preserved verbatim; only Left/Top can move. If the
    /// region is itself wider/taller than the destination monitor (a single-monitor-sized region
    /// landing on a smaller monitor — pathological on phase 1's normal hardware, but not
    /// impossible), it's centered instead of clamped into a negative offset.</summary>
    public static RectPhysical ClampToMonitor(RectPhysical selectionAbs, MonitorInfo monitor)
    {
        var b = monitor.BoundsPx;
        int w = selectionAbs.Width, h = selectionAbs.Height;
        int left = w >= b.Width ? b.Left - (w - b.Width) / 2 : Math.Clamp(selectionAbs.Left, b.Left, b.Right - w);
        int top = h >= b.Height ? b.Top - (h - b.Height) / 2 : Math.Clamp(selectionAbs.Top, b.Top, b.Bottom - h);
        return RectPhysical.FromSize(left, top, w, h);
    }

    /// <summary>Converts a virtual-desktop-absolute rect into <paramref name="monitor"/>'s own
    /// 0-based coordinate space — the one conversion point back into <see cref="RegionRecorder"/>'s
    /// unchanged, monitor-relative constructor/<c>SetOrigin</c> contract (plan §1's "RegionRecorder
    /// — unchanged class"). Width/Height pass through unchanged; only the origin shifts.</summary>
    public static RectPhysical ToMonitorRelative(RectPhysical absolute, MonitorInfo monitor)
    {
        var b = monitor.BoundsPx;
        return new RectPhysical(absolute.Left - b.Left, absolute.Top - b.Top, absolute.Right - b.Left, absolute.Bottom - b.Top);
    }

    /// <summary>Virtual-desktop-absolute overlap of two rects — the public counterpart of the
    /// class's own private area-only helper, needed once spanning capture (phase 2,
    /// PLAN-MULTIMON-RECORDING.md §1) has to know WHERE each monitor's own captured pixels land,
    /// not just whether/how-much they overlap. Returns a degenerate (zero-width and/or
    /// zero-height, but never inverted) rect anchored at the higher of the two lefts/tops when
    /// there is no real overlap — callers check <c>Width &gt; 0 &amp;&amp; Height &gt; 0</c> (see
    /// <see cref="IntersectingMonitors"/>) rather than relying on this method to signal "no
    /// overlap" any other way.</summary>
    public static RectPhysical Intersect(RectPhysical a, RectPhysical b)
    {
        int left = Math.Max(a.Left, b.Left);
        int top = Math.Max(a.Top, b.Top);
        int right = Math.Max(left, Math.Min(a.Right, b.Right));
        int bottom = Math.Max(top, Math.Min(a.Bottom, b.Bottom));
        return new RectPhysical(left, top, right, bottom);
    }

    /// <summary>Every monitor <paramref name="selectionAbs"/> overlaps by a positive area, ordered
    /// by <see cref="MonitorInfo.Index"/> for determinism — the SET a spanning recording (phase 2,
    /// §1) builds one <see cref="RegionRecorder"/> per. A selection that sits entirely on one
    /// monitor returns a one-element list: spanning with N==1 is deliberately just the ordinary
    /// non-spanning case at the geometry layer, not a separate code path (see
    /// <see cref="SpanningCanvasCompositor"/>'s own class doc for how that degenerate case stays
    /// exactly as cheap as the pre-spanning single-recorder pipeline). A selection dragged into a
    /// dead gap between non-adjacent monitors returns an EMPTY list — the caller's job to decide
    /// what "stay put"/"snap to nearest" means in that case, same convention
    /// <see cref="FindOwningMonitor"/> already uses for its own null return.</summary>
    public static IReadOnlyList<MonitorInfo> IntersectingMonitors(RectPhysical selectionAbs, IReadOnlyList<MonitorInfo> monitors)
    {
        var result = new List<MonitorInfo>();
        foreach (var m in monitors)
        {
            if (IntersectionArea(selectionAbs, m.BoundsPx) > 0)
            {
                result.Add(m);
            }
        }
        result.Sort(static (x, y) => x.Index.CompareTo(y.Index));
        return result;
    }
}
