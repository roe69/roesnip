using RoeSnip.Core.Capture;

namespace RoeSnip.Core.Overlay;

/// <summary>Pure helpers backing the Windows flash-dimmer's monitor set/ordering decisions
/// (RoeSnip.App/Overlay/FlashDimmer.cs, item 18) — pulled into Core so they're unit-testable
/// without a live window/display, matching PLAN-XPLAT.md's "portable logic lands in Core" rule.
/// Ported from the WPF app's src/RoeSnip/Overlay/FlashDimmer.cs (Matches/OrderCursorMonitorFirst,
/// both private instance-state methods there); this port keeps the exact same semantics as free
/// functions over MonitorInfo plus an explicit cursor position.</summary>
public static class MonitorPresentationOrder
{
    /// <summary>Order-independent set compare by (DeviceName, BoundsPx) — see the WPF app's
    /// FlashDimmer.Matches doc comment for why a positional compare would be wrong here: it would
    /// force a needless close+rebuild (~100 ms/monitor) whenever a caller's own enumeration order
    /// differs from whatever order the last build happened to store, and could tear down windows
    /// out from under a reentrant double-trigger that is genuinely mid-flash. A monitor set in ANY
    /// order is a match; only a real add/remove/move triggers "false".</summary>
    public static bool SetsMatch(IReadOnlyList<MonitorInfo> have, IReadOnlyList<MonitorInfo> want)
    {
        if (have.Count != want.Count)
        {
            return false;
        }
        foreach (var h in have)
        {
            bool found = false;
            foreach (var w in want)
            {
                if (string.Equals(h.DeviceName, w.DeviceName, StringComparison.OrdinalIgnoreCase)
                    && h.BoundsPx == w.BoundsPx)
                {
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Reorders <paramref name="monitors"/> so the one containing
    /// (<paramref name="cursorX"/>, <paramref name="cursorY"/>) comes first — the monitor the user
    /// is actually looking at should dim/build earliest. Falls back to the given order when fewer
    /// than 2 monitors are given or the cursor isn't over any of them (or is already over the first
    /// one). Ported verbatim (semantics-for-semantics) from the WPF app's
    /// FlashDimmer.OrderCursorMonitorFirst.</summary>
    public static IReadOnlyList<MonitorInfo> OrderCursorMonitorFirst(
        IReadOnlyList<MonitorInfo> monitors, int cursorX, int cursorY)
    {
        if (monitors.Count < 2)
        {
            return monitors;
        }

        int cursorIndex = -1;
        for (int i = 0; i < monitors.Count; i++)
        {
            var b = monitors[i].BoundsPx;
            if (cursorX >= b.Left && cursorX < b.Right && cursorY >= b.Top && cursorY < b.Bottom)
            {
                cursorIndex = i;
                break;
            }
        }
        if (cursorIndex <= 0)
        {
            return monitors;
        }

        var reordered = new List<MonitorInfo>(monitors);
        var cursorMonitor = reordered[cursorIndex];
        reordered.RemoveAt(cursorIndex);
        reordered.Insert(0, cursorMonitor);
        return reordered;
    }
}
