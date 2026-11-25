namespace RoeSnip.Core.Capture;

/// <summary>Which kept-open screencast nodes should be streaming right now. A node is wanted while it
/// is held (a recording) or until its linger deadline (a capture, a toast cursor read); anything else
/// is idle and its streams are deactivated, so the compositor stops producing frames for it. Not
/// thread-safe: the caller serialises it with the activation it triggers. Time is milliseconds from
/// any monotonic clock.</summary>
public sealed class StreamActivity
{
    private sealed class Entry
    {
        public int Holds;
        public long IdleAt;
        public bool Active;
    }

    private readonly Dictionary<uint, Entry> _nodes = new();

    public bool AnyActive => _nodes.Values.Any(e => e.Active);

    public bool IsActive(uint id) => _nodes.TryGetValue(id, out var e) && e.Active;

    /// <summary>Wants <paramref name="id"/> until <paramref name="nowMs"/> + <paramref name="lingerMs"/>.
    /// True when it was idle and must be activated.</summary>
    public bool Touch(uint id, long nowMs, long lingerMs)
    {
        var e = Get(id);
        e.IdleAt = Math.Max(e.IdleAt, nowMs + lingerMs);
        bool wasIdle = !e.Active;
        e.Active = true;
        return wasIdle;
    }

    /// <summary>Wants <paramref name="id"/> until a matching <see cref="Release"/>. True when it was idle.</summary>
    public bool Hold(uint id, long nowMs)
    {
        var e = Get(id);
        e.Holds++;
        return Touch(id, nowMs, 0);
    }

    public void Release(uint id, long nowMs, long lingerMs)
    {
        var e = Get(id);
        if (e.Holds > 0) e.Holds--;
        e.IdleAt = Math.Max(e.IdleAt, nowMs + lingerMs);
    }

    public List<uint> ActiveIds() => _nodes.Where(kv => kv.Value.Active).Select(kv => kv.Key).ToList();

    /// <summary>Marks <paramref name="id"/> idle when nobody wants it any more; true when it was.</summary>
    public bool TryIdle(uint id, long nowMs)
    {
        if (!_nodes.TryGetValue(id, out var e) || !e.Active || e.Holds != 0 || nowMs < e.IdleAt) return false;
        e.Active = false;
        return true;
    }

    private Entry Get(uint id)
    {
        if (!_nodes.TryGetValue(id, out var e)) _nodes[id] = e = new Entry();
        return e;
    }
}
