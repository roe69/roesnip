namespace RoeSnip.Core.Capture;

/// <summary>One stream of an xdg-desktop-portal ScreenCast session: the PipeWire node id plus the
/// portal's "position" and "size" stream properties (compositor logical coordinates).</summary>
public readonly record struct ScreenCastStreamGeometry(uint NodeId, int X, int Y, int Width, int Height);

/// <summary>Pairs portal screencast streams with RandR monitors. Pass 1 is an exact position+size
/// match (the uniform scale-1 case). Pass 2 pairs the rest greedily by the distance between their
/// centres normalised to each set's own union rect, which survives a uniform logical/physical scale
/// difference between the portal and XWayland's RandR; pairs further apart than 0.15 of the union
/// stay unmapped.</summary>
public static class ScreenCastStreamMapping
{
    private const double MaxNormalisedDistance = 0.15;

    /// <returns>Monitor index -> stream, only for monitors that got a stream.</returns>
    public static IReadOnlyDictionary<int, ScreenCastStreamGeometry> Map(
        IReadOnlyList<ScreenCastStreamGeometry> streams, IReadOnlyList<MonitorInfo> monitors)
    {
        var result = new Dictionary<int, ScreenCastStreamGeometry>();
        var freeStreams = new List<ScreenCastStreamGeometry>(streams);
        var freeMonitors = new List<MonitorInfo>();

        foreach (var m in monitors)
        {
            int hit = freeStreams.FindIndex(s =>
                s.X == m.BoundsPx.Left && s.Y == m.BoundsPx.Top
                && s.Width == m.BoundsPx.Width && s.Height == m.BoundsPx.Height);
            if (hit >= 0)
            {
                result[m.Index] = freeStreams[hit];
                freeStreams.RemoveAt(hit);
            }
            else
            {
                freeMonitors.Add(m);
            }
        }

        if (freeStreams.Count == 0 || freeMonitors.Count == 0)
        {
            return result;
        }

        var su = Union(streams.Select(s => (s.X, s.Y, s.X + s.Width, s.Y + s.Height)));
        var mu = Union(monitors.Select(m => (m.BoundsPx.Left, m.BoundsPx.Top, m.BoundsPx.Right, m.BoundsPx.Bottom)));

        var pairs = new List<(double Dist, int Stream, int Monitor)>();
        for (int si = 0; si < freeStreams.Count; si++)
        {
            var s = freeStreams[si];
            var (sx, sy) = Normalise(s.X + s.Width / 2.0, s.Y + s.Height / 2.0, su);
            for (int mi = 0; mi < freeMonitors.Count; mi++)
            {
                var b = freeMonitors[mi].BoundsPx;
                var (mx, my) = Normalise(b.Left + b.Width / 2.0, b.Top + b.Height / 2.0, mu);
                double d = Math.Sqrt((sx - mx) * (sx - mx) + (sy - my) * (sy - my));
                if (d <= MaxNormalisedDistance)
                {
                    pairs.Add((d, si, mi));
                }
            }
        }
        pairs.Sort((a, b) => a.Dist.CompareTo(b.Dist));

        var usedStreams = new HashSet<int>();
        var usedMonitors = new HashSet<int>();
        foreach (var (_, si, mi) in pairs)
        {
            if (usedStreams.Contains(si) || usedMonitors.Contains(mi)) continue;
            usedStreams.Add(si);
            usedMonitors.Add(mi);
            result[freeMonitors[mi].Index] = freeStreams[si];
        }
        return result;
    }

    private static (double L, double T, double W, double H) Union(IEnumerable<(int L, int T, int R, int B)> rects)
    {
        int l = int.MaxValue, t = int.MaxValue, r = int.MinValue, b = int.MinValue;
        foreach (var x in rects)
        {
            l = Math.Min(l, x.L);
            t = Math.Min(t, x.T);
            r = Math.Max(r, x.R);
            b = Math.Max(b, x.B);
        }
        return (l, t, Math.Max(1, r - l), Math.Max(1, b - t));
    }

    private static (double X, double Y) Normalise(double x, double y, (double L, double T, double W, double H) u)
        => ((x - u.L) / u.W, (y - u.T) / u.H);
}
