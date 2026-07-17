namespace RoeSnip.Capture;

public sealed class CaptureException : Exception
{
    public CaptureException(string message, Exception? inner = null) : base(message, inner) { }

    /// <summary>True only for failures that prove the capturer is PERMANENTLY broken for this
    /// monitor (today: Desktop Duplication's all-zero-frame NVIDIA+HDR quirk — the one failure the
    /// persisted DD-broken memo exists to memoize). Everything else (no DXGI output during a
    /// topology transition, DuplicateOutput1 denied because another duplication is live — e.g. a
    /// deadline-abandoned capture still holding it — access lost, timeouts, device-creation
    /// failures) is transient or environmental: the capture still falls back to WGC, but
    /// CaptureService must not persist a forever memo from it.</summary>
    public bool IndicatesPermanentlyBroken { get; init; }
}

public interface IScreenCapturer
{
    /// <summary>Captures a single frame of the given monitor. Throws CaptureException on any
    /// unrecoverable failure (including after this capturer's own internal retries) — callers
    /// decide fallback policy, this method never falls back to a different capturer itself.</summary>
    CapturedFrame Capture(MonitorInfo monitor);
}
