using RoeSnip.Core.Imaging;
using RoeSnip.Core.Recording.Gif;

namespace RoeSnip.Core.Recording;

/// <summary>The GIF <see cref="IVideoEncoder"/>: a thin adapter over this assembly's own portable
/// <see cref="GifEncoder"/> (recording-core-extraction workstream, item 04). GIF has zero Windows
/// dependency, so unlike the MP4 encoder this lives in Core itself and is constructed directly by
/// callers on every OS — no registry, no platform check, always available.
///
/// The inner <see cref="GifEncoder"/> is opened with a 10,000,000-ticks-per-second clock (100ns
/// units) specifically so <see cref="IVideoEncoder"/>'s WriteFrame/FinishAsync timestamps pass
/// straight through with no per-call conversion — see IVideoEncoder's own class doc for why that
/// domain was chosen (MP4's native unit, not GifEncoder's own Stopwatch-ticks default).
///
/// GIF has no audio track of any kind (the format itself has none): <see cref="HasAudio"/> is always
/// false and <see cref="WriteAudioSamples"/> is a documented no-op, never a thrown exception — so
/// RecordingController (item 20) can call it unconditionally on whichever <see cref="IVideoEncoder"/>
/// the current take opened, without a format check of its own.</summary>
public sealed class GifVideoEncoder : IVideoEncoder
{
    /// <summary>100ns per tick — Media Foundation's native unit and this seam's pinned timestamp
    /// domain (see <see cref="IVideoEncoder"/>'s class doc).</summary>
    private const long Timestamp100NsTicksPerSecond = 10_000_000;

    private readonly GifEncoder _inner;

    private GifVideoEncoder(GifEncoder inner) => _inner = inner;

    /// <summary>See <see cref="GifEncoder.Create"/> for the sink-opening contract. <paramref name="fps"/>
    /// and <paramref name="withAudio"/> exist only so this factory's parameter shape matches
    /// <c>Mp4VideoEncoderRegistry.Create</c>'s exactly (one call shape for either format) — GIF has
    /// no per-frame-rate encoder setting of its own (its capture-cadence throttle lives upstream, see
    /// GifEncoder's class doc) and no audio track ever, so both are ignored here. <paramref name="width"/>/
    /// <paramref name="height"/> must already be the SCALED canvas size for Compact/Minimal tiers
    /// (<see cref="GifEncoderOptions.RenderScale"/>) — same pre-scaling contract GifEncoder.Create
    /// has always had; this adapter does no scaling of its own.</summary>
    public static GifVideoEncoder Create(
        string tempFilePath, int width, int height, int fps, bool withAudio, GifSizePreset preset)
    {
        var options = GifSizePresets.ForPreset(preset);
        return new GifVideoEncoder(GifEncoder.Create(tempFilePath, width, height, Timestamp100NsTicksPerSecond, options));
    }

    public bool HasAudio => false;

    public bool WriteFrame(SdrImage frame, long timestamp100ns) => _inner.AddFrame(frame, timestamp100ns);

    /// <summary>No-op — GIF has no audio track, see class doc.</summary>
    public void WriteAudioSamples(byte[] pcm, int length, long timestamp100ns)
    {
    }

    public Task FinishAsync(long? endTimestampTicks = null)
    {
        if (endTimestampTicks is long end)
        {
            _inner.FinalizeAndClose(end);
        }
        else
        {
            _inner.FinalizeAndClose();
        }
        return Task.CompletedTask;
    }

    public void Dispose() => _inner.Dispose();
}
