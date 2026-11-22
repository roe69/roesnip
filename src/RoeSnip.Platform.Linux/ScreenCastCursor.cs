using System.Diagnostics;
using RoeSnip.Core.Capture;

namespace RoeSnip.Platform.Linux;

/// <summary>The Wayland pointer as the snapshot screencast's cursor metadata reports it, in
/// virtual-desktop px (XWayland RandR space, same as Avalonia's Screens). Never opens the session
/// and never shows a dialog: false when it is not live or has no cursor metadata.</summary>
public static class ScreenCastCursor
{
    private static readonly TimeSpan MonitorsFor = TimeSpan.FromSeconds(5);
    private static readonly object s_gate = new();
    private static IReadOnlyList<MonitorInfo>? s_monitors;
    private static long s_monitorsAt;

    /// <summary>True when the live session has a cursor metadata reader.</summary>
    public static bool Available => ScreenCastSession.Snapshot.CurrentLive?.Cursor is not null;

    public static bool TryGetDesktopPosition(out int x, out int y)
    {
        x = y = 0;
        var live = ScreenCastSession.Snapshot.CurrentLive;
        if (live?.Cursor is not { } reader) return false;
        var monitors = Monitors();
        PipeWireCursorReader.State? best = null;
        MonitorInfo? bestMonitor = null;
        foreach (var m in monitors)
        {
            if (live.StreamFor(m, monitors) is not { } g) continue;
            if (reader.Get(g.NodeId) is not { Valid: true } s) continue;
            if (best is null || s.At > best.At)
            {
                best = s;
                bestMonitor = m;
            }
        }
        if (best is null || bestMonitor is null) return false;
        var (mx, my) = ToMonitorPx(best, bestMonitor);
        x = bestMonitor.BoundsPx.Left + mx;
        y = bestMonitor.BoundsPx.Top + my;
        return true;
    }

    /// <summary>Stream buffer px to monitor-local px (they differ only when the stream is scaled).</summary>
    internal static (int X, int Y) ToMonitorPx(PipeWireCursorReader.State s, MonitorInfo m)
    {
        int bw = s.BufferWidth > 0 ? s.BufferWidth : m.BoundsPx.Width;
        int bh = s.BufferHeight > 0 ? s.BufferHeight : m.BoundsPx.Height;
        return ((int)((long)s.X * m.BoundsPx.Width / bw), (int)((long)s.Y * m.BoundsPx.Height / bh));
    }

    private static IReadOnlyList<MonitorInfo> Monitors()
    {
        lock (s_gate)
        {
            if (s_monitors is null || Stopwatch.GetElapsedTime(s_monitorsAt) > MonitorsFor)
            {
                s_monitors = X11Capturer.EnumerateMonitorsViaRandR();
                s_monitorsAt = Stopwatch.GetTimestamp();
            }
            return s_monitors;
        }
    }
}
