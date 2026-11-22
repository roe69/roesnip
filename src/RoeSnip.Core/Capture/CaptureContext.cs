namespace RoeSnip.Core.Capture;

/// <summary>Marks a capture nobody asked for (startup warmup, re-warm, keepalive). Capturers with a
/// user-visible side effect (GNOME's Screenshot portal plays a shutter sound and flashes the screen)
/// refuse inside such a scope. AsyncLocal, so it flows into the Parallel.For workers of
/// <see cref="FallbackCaptureBackend.CaptureAll"/>.</summary>
public static class CaptureContext
{
    private static readonly AsyncLocal<bool> s_background = new();

    public static bool IsBackground => s_background.Value;

    /// <summary><c>using var _ = CaptureContext.BeginBackground();</c> around a background capture.</summary>
    public static IDisposable BeginBackground()
    {
        bool previous = s_background.Value;
        s_background.Value = true;
        return new Scope(previous);
    }

    private sealed class Scope : IDisposable
    {
        private readonly bool _previous;
        public Scope(bool previous) => _previous = previous;
        public void Dispose() => s_background.Value = _previous;
    }
}
