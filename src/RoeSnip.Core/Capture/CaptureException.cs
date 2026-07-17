namespace RoeSnip.Core.Capture;

public sealed class CaptureException : Exception
{
    public CaptureException(string message, Exception? inner = null) : base(message, inner) { }

    /// <summary>True only for failures that prove the capturer is PERMANENTLY broken for this
    /// monitor (today: Windows Desktop Duplication's all-zero-frame NVIDIA+HDR quirk — the one
    /// failure the persisted per-capturer-slot memo in FallbackCaptureBackend exists to memoize).
    /// Everything else (no output during a topology transition, a duplication denied because
    /// another one is live, e.g. a deadline-abandoned capture still holding it, access lost,
    /// timeouts, device-creation failures) is transient or environmental: the capture still falls
    /// back to the next capturer, but FallbackCaptureBackend must not persist a forever memo from
    /// it.</summary>
    public bool IndicatesPermanentlyBroken { get; init; }
}
