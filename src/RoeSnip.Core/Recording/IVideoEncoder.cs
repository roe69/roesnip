using RoeSnip.Core.Imaging;
using RoeSnip.Core.Recording.Gif;

namespace RoeSnip.Core.Recording;

/// <summary>The per-format entry point for recording video: GIF (this assembly's own portable
/// pipeline — see <see cref="GifVideoEncoder"/>) and MP4 (Media Foundation, Windows-only — see
/// RoeSnip.Platform.Windows.Mp4Encoder) speak this ONE shape so RecordingController (item 20) can
/// drive either without a format-specific call site. Mirrors the <c>ICaptureBackend</c> seam
/// (RoeSnip.Core.Capture) that already unifies this app's per-OS capture backends the same way.
///
/// TIMESTAMP DOMAIN: every timestamp this interface takes is 100ns units (Media Foundation's own
/// native unit — <see cref="RoeSnip.Core.Imaging.SdrImage"/> callers already compute this for the
/// MP4 path today; see RecordingController.cs's <c>timestamp100ns</c> local). <see cref="GifVideoEncoder"/>
/// configures its inner <c>GifEncoder</c> with a matching 10,000,000-ticks-per-second clock so BOTH
/// formats accept the exact same timestamp a caller already has in hand — no per-format conversion
/// needed at the call site, unlike the WPF app's own two RecordingController branches (one of which
/// feeds GifEncoder raw QPC/Stopwatch ticks) which this interface intentionally does NOT carry
/// forward; whoever wires RecordingController onto this seam converts once, the same way the MP4
/// branch already does.
///
/// Call sequence, encoder-thread-only (never the capture callback thread, never the UI thread — same
/// discipline the WPF app's own Mp4Encoder/GifEncoder document): a factory opens the sink, then any
/// number of WriteFrame/WriteAudioSamples calls in non-decreasing timestamp order, then exactly one
/// FinishAsync, then Dispose.</summary>
public interface IVideoEncoder : IDisposable
{
    /// <summary>True only when this take actually has an audio track: WithAudio was requested AND
    /// the concrete encoder supports audio at all. <see cref="GifVideoEncoder"/> is always false
    /// (the GIF format has no audio track, full stop); the MP4 encoder mirrors whichever
    /// <see cref="IAudioCaptureDevice"/> its caller actually managed to start.</summary>
    bool HasAudio { get; }

    /// <summary>Encodes one video frame at <paramref name="timestamp100ns"/>. Returns true when the
    /// frame was actually EMITTED into the output: MP4 always returns true (every call is a straight
    /// encode); GIF may return false when the frame is a no-change duplicate of the last emitted one
    /// (GifEncoder's own delta/dedupe scheme — see that class's doc comment). Callers that need to
    /// know whether a frame landed (e.g. their own double-buffering housekeeping) can use this return
    /// value instead of special-casing by format.</summary>
    bool WriteFrame(SdrImage frame, long timestamp100ns);

    /// <summary>Encodes one chunk of 48 kHz/stereo/16-bit PCM audio (the format every
    /// <see cref="IAudioCaptureDevice"/> and MP4's own AAC media type are pinned to — see
    /// <see cref="Mp4BitrateEstimator.AudioAacBytesPerSecond"/>'s doc comment) at
    /// <paramref name="timestamp100ns"/>. A documented no-op whenever <see cref="HasAudio"/> is
    /// false (GIF always; MP4 when no audio source came up) — callers never need to gate this call
    /// themselves.</summary>
    void WriteAudioSamples(byte[] pcm, int length, long timestamp100ns);

    /// <summary><paramref name="endTimestampTicks"/> is the take's stop moment, in the SAME 100ns
    /// domain as <see cref="WriteFrame"/>'s own timestamps. GIF uses it to patch its last frame's
    /// provisional delay to its real, final display duration (GifEncoder's own
    /// <c>FinalizeAndClose(long)</c> doc comment); MP4 ignores it — H.264/MP4 has no equivalent
    /// "final frame duration" concept, its sink writer just flushes and closes. Null skips that
    /// GIF-only patch (a hard/error stop with no known end moment). Writes the trailer/flushes and
    /// closes the output stream or sink; the encoder is unusable afterward except for Dispose.
    /// Synchronous work wrapped in a completed Task today (neither concrete encoder's own finalize
    /// is actually async), named FinishAsync so a genuinely async close (e.g. an eventual awaited MF
    /// drain) can land later without another interface change.</summary>
    Task FinishAsync(long? endTimestampTicks = null);
}

/// <summary>Factory seam for the MP4 <see cref="IVideoEncoder"/> specifically — GIF needs no
/// registry, <see cref="GifVideoEncoder"/> is Core-native and constructed directly by callers on
/// every OS. Registered by RoeSnip.Platform.Windows's <c>Mp4Encoder</c> via a
/// <c>[ModuleInitializer]</c>, the exact same pattern as <c>RoeSnip.Core.Capture.CaptureBackendRegistry</c>.
/// Linux/macOS register no candidate today (<see cref="RecordingCapabilities.SupportsMp4"/> is false
/// there) — callers are expected to check that flag before ever calling <see cref="Create"/>, so
/// reaching this with no registrant is a caller bug, not a graceful-degrade path (hence the throw,
/// mirroring <c>CaptureBackendRegistry.CreateForCurrentPlatform</c>'s own contract, unlike
/// <see cref="RecordingCapabilitiesRegistry"/>'s intentionally non-throwing fallback).</summary>
public static class Mp4VideoEncoderRegistry
{
    private static readonly List<(
        Func<bool> IsSupported,
        Func<string, int, int, int, bool, GifSizePreset, IVideoEncoder> Factory)> _candidates = new();

    public static void Register(
        Func<bool> isSupported,
        Func<string, int, int, int, bool, GifSizePreset, IVideoEncoder> factory)
        => _candidates.Add((isSupported, factory));

    /// <summary>Mirrors <c>Mp4Encoder.Create</c>'s own parameter shape exactly (tempFilePath, width,
    /// height, fps, withAudio, preset) so whoever wires RecordingController onto this seam can swap
    /// a direct <c>Mp4Encoder.Create(...)</c> call for <c>Mp4VideoEncoderRegistry.Create(...)</c>
    /// with no argument reshaping.</summary>
    public static IVideoEncoder Create(
        string tempFilePath, int width, int height, int fps, bool withAudio, GifSizePreset preset)
    {
        foreach (var (isSupported, factory) in _candidates)
        {
            if (isSupported()) return factory(tempFilePath, width, height, fps, withAudio, preset);
        }
        throw new PlatformNotSupportedException("No MP4 IVideoEncoder registered for this OS.");
    }
}
