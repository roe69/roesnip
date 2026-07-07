namespace RoeSnip.Capture;

public sealed class CaptureException : Exception
{
    public CaptureException(string message, Exception? inner = null) : base(message, inner) { }
}

public interface IScreenCapturer
{
    /// <summary>Captures a single frame of the given monitor. Throws CaptureException on any
    /// unrecoverable failure (including after this capturer's own internal retries) — callers
    /// decide fallback policy, this method never falls back to a different capturer itself.</summary>
    CapturedFrame Capture(MonitorInfo monitor);
}
