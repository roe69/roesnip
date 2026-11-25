using System.Diagnostics;
using RoeSnip.Core.Capture;
using RoeSnip.Core.Diagnostics;

namespace RoeSnip.Platform.Linux;

/// <summary>The Wayland pointer as the snapshot screencast's cursor metadata reports it, in
/// virtual-desktop px (XWayland RandR space, same as Avalonia's Screens). Never opens the session
/// and never shows a dialog: false when it is not live or has no cursor metadata.</summary>
public static class ScreenCastCursor
{
    private static readonly TimeSpan MonitorsFor = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WakeLinger = TimeSpan.FromSeconds(2);
    private const int WakeWaitMs = 200;
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
        // The cursor streams idle between uses; a toast polls this, which keeps them awake.
        if (live.WakeAll(WakeLinger)) WaitForAnyCursor(live, reader, monitors);
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

    private static void WaitForAnyCursor(ScreenCastSession.Live live, PipeWireCursorReader reader, IReadOnlyList<MonitorInfo> monitors)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < WakeWaitMs)
        {
            // An invalid state only says the pointer is not on that stream: done once one is valid or all answered.
            bool allAnswered = true;
            foreach (var m in monitors)
            {
                if (live.StreamFor(m, monitors) is not { } g) continue;
                var s = reader.Get(g.NodeId);
                if (s is { Valid: true }) return;
                if (s is null) allAnswered = false;
            }
            if (allAnswered) return;
            Thread.Sleep(4);
        }
        FileLog.Write($"RoeSnip: screencast cursor: no pointer within {WakeWaitMs} ms of waking the streams");
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
