namespace RoeSnip.Core.Recording;

/// <summary>One mixed PCM chunk from an <see cref="IAudioCaptureDevice"/>: samples in the format
/// every MP4 take is pinned to (48 kHz stereo 16-bit — see
/// <see cref="Mp4BitrateEstimator.AudioAacBytesPerSecond"/>'s doc comment for why that one constant,
/// not a whole format-constants set, lives in Core) and the Stopwatch/QPC timestamp of the chunk's
/// FIRST sample, which RecordingController maps onto the video timeline (see the WPF app's own
/// <c>RecordingController.DrainAudio</c> for that mapping, preserved unchanged by this seam).
/// Public (unlike the WPF app's own internal <c>AudioChunk</c>) — an <see cref="IAudioCaptureDevice"/>
/// implementation lives in a different assembly (RoeSnip.Platform.Windows) than whoever dequeues it
/// (RoeSnip.App), so this shape has to cross that assembly boundary.</summary>
public readonly record struct AudioChunk(byte[] Pcm, long QpcTicks);

/// <summary>The per-OS entry point for recording audio: microphone and/or system-loopback capture,
/// mixed into one 48 kHz/stereo/16-bit PCM chunk stream. Mirrors the <c>ICaptureBackend</c> seam
/// (RoeSnip.Core.Capture) the same way <see cref="IVideoEncoder"/> mirrors it for video. Windows
/// implements this via WASAPI (<c>RoeSnip.Platform.Windows.AudioCaptureDevice</c>, ported from the
/// WPF app's own <c>AudioCaptureEngine</c> — see that class's doc comment for the mixing/gap-fill
/// design this interface preserves verbatim, sample callback and all); Linux/macOS register no
/// factory at all (<see cref="RecordingCapabilities.SupportsMicrophone"/>/
/// <see cref="RecordingCapabilities.SupportsLoopback"/> both false there), so RecordingController
/// never even asks <see cref="AudioCaptureDeviceRegistry"/> for one on those OSes.
///
/// All members are the capture-thread's own internal WASAPI polling loop reaching across to whichever
/// thread calls them — see the WPF app's <c>AudioCaptureEngine</c> class doc for the full
/// single-capture-thread COM-apartment discipline this interface's implementations must preserve.</summary>
public interface IAudioCaptureDevice : IDisposable
{
    /// <summary>Encoder thread. Dequeues the next ready mixed chunk, if any — this is the "sample
    /// callback" side of the device: the capture thread pushes chunks as WASAPI delivers them, and
    /// this pulls them off in encode order. False (out default) when nothing is queued yet; never
    /// blocks.</summary>
    bool TryDequeue(out AudioChunk chunk);

    /// <summary>UI thread; idempotent. Stops the queue from growing further; safe to call any number
    /// of times, including never — <see cref="IDisposable.Dispose"/> alone is sufficient to stop and
    /// tear the device down (matches the WPF app's own <c>AudioCaptureEngine.Dispose</c>, which
    /// RecordingController relies on exclusively today).</summary>
    void Stop();
}

/// <summary>Factory seam for <see cref="IAudioCaptureDevice"/> — registered by
/// RoeSnip.Platform.Windows's <c>AudioCaptureDevice</c> via a <c>[ModuleInitializer]</c>, the exact
/// same pattern as <c>RoeSnip.Core.Capture.CaptureBackendRegistry</c> and
/// <see cref="Mp4VideoEncoderRegistry"/>.</summary>
public static class AudioCaptureDeviceRegistry
{
    private static readonly List<(Func<bool> IsSupported, Func<bool, bool, IAudioCaptureDevice?> Factory)> _candidates = new();

    public static void Register(Func<bool> isSupported, Func<bool, bool, IAudioCaptureDevice?> factory)
        => _candidates.Add((isSupported, factory));

    /// <summary>Mirrors the WPF app's own <c>AudioCaptureEngine.TryStart(microphone, systemAudio)</c>
    /// contract exactly: null when no candidate matches this OS (Linux/macOS today, before either
    /// bool is even inspected — same "nothing to capture" short circuit AudioCaptureEngine.TryStart
    /// itself applies when both flags are false) OR the matching candidate's own factory returned
    /// null (a real device/COM failure).</summary>
    public static IAudioCaptureDevice? TryStart(bool microphone, bool systemAudio)
    {
        foreach (var (isSupported, factory) in _candidates)
        {
            if (isSupported()) return factory(microphone, systemAudio);
        }
        return null;
    }
}
