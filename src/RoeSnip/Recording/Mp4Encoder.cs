using System;
using System.Runtime.InteropServices;
using RoeSnip.Imaging;
using RoeSnip.Recording.Gif;
using Vortice.MediaFoundation;
using Vortice.Multimedia;

namespace RoeSnip.Recording;

/// <summary>Encodes BGRA8 <see cref="SdrImage"/> frames to H.264/MP4 via Media Foundation's
/// IMFSinkWriter — the exact API surface pinned in PLAN.md §1 (verified by reflecting the real
/// Vortice.MediaFoundation 3.8.3 DLL, not assumed). Encoder-thread-only (RecordingSession's
/// dedicated thread) — never the WGC callback thread, never the UI thread.
///
/// Public (not internal), same reasoning as Overlay/SizeInput.cs and Overlay/ToolCursorCache.cs:
/// <see cref="ComputeBitrate"/> is pure logic worth unit-testing directly, and this app's
/// convention is to make a class public rather than add an InternalsVisibleTo edit for it.</summary>
public sealed class Mp4Encoder : IDisposable
{
    private static bool s_mfStarted;
    private static readonly object s_mfGate = new();

    /// <summary>Called once, lazily, on the first-ever recording. Never calls MFShutdown — this is
    /// a resident tray process; the startup cost is paid once and never unwound, matching how this
    /// app already treats other process-lifetime resources (e.g. WgcCapturer's s_slots cache).</summary>
    public static void EnsureStarted()
    {
        if (s_mfStarted)
        {
            return;
        }
        lock (s_mfGate)
        {
            if (s_mfStarted)
            {
                return;
            }
            // MFStartup returns a raw Result (not auto-throwing) — verified via reflection;
            // CheckError() is SharpGen's idiomatic throw-on-failure call.
            MediaFactory.MFStartup(useLightVersion: false).CheckError();
            s_mfStarted = true;
        }
    }

    /// <summary>Bitrate heuristic: 0.1 bits/pixel/frame (a standard "medium quality" H.264 rule of
    /// thumb), clamped to a sane [2, 16] Mbps band so a tiny selection doesn't starve the encoder
    /// and a huge one doesn't produce an unreasonably large file. Pulled out as its own static
    /// method (no IMFSinkWriter dependency) so it's unit-testable without a live MF session —
    /// see Mp4EncoderTests. This is the parameterless-preset overload: its behavior is pinned
    /// forever by <see cref="ComputeBitrate(int,int,int,GifSizePreset)"/>'s Quality case, which
    /// must return exactly this value — see that overload's own doc comment.</summary>
    public static long ComputeBitrate(int width, int height, int fps) =>
        Math.Clamp((long)(0.1 * width * height * fps), 2_000_000, 16_000_000);

    /// <summary>Recording-size-tiers overload: applies a per-tier multiplier to the same 0.1bpp
    /// heuristic <see cref="ComputeBitrate(int,int,int)"/> uses, each with its own clamp band so a
    /// tiny selection at any tier still gets a usable minimum bitrate and a huge one at any tier
    /// still has a ceiling. <see cref="GifSizePreset.Quality"/> is handled by delegating straight
    /// to the three-argument overload rather than recomputing "1.0x [2M,16M]" here a second time —
    /// that overload's existing tests (and every existing MP4 call site that doesn't pass a preset)
    /// must see byte-identical output to before this tiers workstream existed. The other tiers'
    /// factors/clamps (see the switch below) were chosen to bracket Quality symmetrically: Max
    /// roughly quadruples the target bitrate for near-visually-lossless H.264 at typical recording
    /// resolutions, Balanced/Compact roughly mirror the GIF-side Balanced/Compact size reduction so
    /// the two formats' tiers read as the same promise ("smaller, more compressed") even though the
    /// underlying codecs are unrelated. Minimal (quality/fps expansion workstream) continues that
    /// same downward slope at 0.15x — MP4/H.264 has no GIF-style palette/lossy-run lever to spend
    /// this tier's extra shrink on, so it is simply a lower target bitrate with its own, lower
    /// clamp band.</summary>
    public static long ComputeBitrate(int width, int height, int fps, GifSizePreset preset)
    {
        if (preset == GifSizePreset.Quality)
        {
            return ComputeBitrate(width, height, fps);
        }

        (double factor, long min, long max) = preset switch
        {
            GifSizePreset.Max => (4.0, 8_000_000L, 64_000_000L),
            GifSizePreset.Balanced => (0.6, 1_500_000L, 10_000_000L),
            GifSizePreset.Compact => (0.35, 1_000_000L, 6_000_000L),
            GifSizePreset.Minimal => (0.15, 500_000L, 3_000_000L),
            _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Unknown GifSizePreset."),
        };
        return Math.Clamp((long)(factor * 0.1 * width * height * fps), min, max);
    }

    /// <summary>The one PCM format every audio path speaks (AudioCaptureEngine converts both
    /// WASAPI sources to it via AUTOCONVERTPCM, this class encodes it to AAC): 48 kHz, stereo,
    /// 16-bit — 4 bytes per sample block.</summary>
    public const int AudioSampleRate = 48_000;
    public const int AudioChannels = 2;
    public const int AudioBitsPerSample = 16;
    public const int AudioBlockAlign = AudioChannels * AudioBitsPerSample / 8;

    /// <summary>128 kbps AAC, the standard screen-recording choice: 128000 / 8 = 16000 bytes/second
    /// — named here (rather than left as the inline magic number the Create's AudioAvgBytesPerSecond
    /// media-type attribute used to be) so RecordingSizeEstimator's MP4 audio-overhead term can
    /// cite the exact same figure instead of re-deriving/duplicating it.</summary>
    public const int AudioAacBytesPerSecond = 16_000;

    private readonly IMFSinkWriter _writer;
    private readonly int _streamIndex;
    private readonly int _audioStreamIndex; // -1 when this recording has no audio track
    private readonly long _frameDuration100ns;

    public bool HasAudio => _audioStreamIndex >= 0;

    private Mp4Encoder(IMFSinkWriter writer, int streamIndex, int audioStreamIndex, int fps)
    {
        _writer = writer;
        _streamIndex = streamIndex;
        _audioStreamIndex = audioStreamIndex;
        _frameDuration100ns = 10_000_000L / fps;
    }

    /// <summary><paramref name="preset"/> is additive (defaults to Quality, today's only behavior
    /// before the recording-size-tiers workstream) — every existing call site that doesn't pass it
    /// compiles unchanged and gets byte-identical bitrate output, since Quality delegates to the
    /// original three-argument <see cref="ComputeBitrate(int,int,int)"/>.</summary>
    public static Mp4Encoder Create(
        string tempFilePath, int width, int height, int fps, bool withAudio = false,
        GifSizePreset preset = GifSizePreset.Quality)
    {
        EnsureStarted();
        long bitrate = ComputeBitrate(width, height, fps, preset);

        using var outputType = MediaFactory.MFCreateMediaType();
        outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
        outputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264).CheckError();
        outputType.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)bitrate).CheckError();
        outputType.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive).CheckError();
        MediaFactory.MFSetAttributeSize(outputType, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height).CheckError();
        MediaFactory.MFSetAttributeRatio(outputType, MediaTypeAttributeKeys.FrameRate, (uint)fps, 1).CheckError();

        using var inputType = MediaFactory.MFCreateMediaType();
        inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
        // MFVideoFormat_RGB32 = 32bpp BGRX in memory — SAME byte layout as SdrImage.Pixels
        // (B,G,R,A/X), no channel-swap needed on write.
        inputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32).CheckError();
        MediaFactory.MFSetAttributeSize(inputType, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height).CheckError();
        MediaFactory.MFSetAttributeRatio(inputType, MediaTypeAttributeKeys.FrameRate, (uint)fps, 1).CheckError();
        inputType.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive).CheckError();
        // CRITICAL, easy to get backwards: MF_MT_DEFAULT_STRIDE's SIGN encodes row order —
        // POSITIVE means top-down, NEGATIVE means bottom-up (the DIB-native default for RGB
        // formats). SdrImage rows are top-down (see that class's own doc comment), so this must
        // be +stride. This shipped as -stride once, which declared the buffer bottom-up and made
        // every .mp4 play upside down — it encodes/plays/seeks fine otherwise, so the bug is
        // invisible until someone looks at a frame; Mp4EncoderTests' decode-back orientation test
        // exists so it can never silently regress again.
        int stride = width * 4;
        inputType.Set(MediaTypeAttributeKeys.DefaultStride, (uint)stride).CheckError();

        using var attrs = MediaFactory.MFCreateAttributes(2);
        // Frames already arrive throttled by our own capture cadence (RegionRecorder) — don't let
        // MF pace-block WriteSample a second time on top of that.
        attrs.Set(SinkWriterAttributeKeys.DisableThrottling, true).CheckError();
        attrs.Set(SinkWriterAttributeKeys.LowLatency, true).CheckError();

        IMFSinkWriter writer = MediaFactory.MFCreateSinkWriterFromURL(tempFilePath, null, attrs);
        int streamIndex = writer.AddStream(outputType);
        writer.SetInputMediaType(streamIndex, inputType, null);

        // The AAC audio track, when any capture source is enabled. Both streams MUST be added and
        // typed before the single shared BeginWriting call — MF rejects AddStream afterwards.
        int audioStreamIndex = -1;
        if (withAudio)
        {
            using var audioOutputType = MediaFactory.MFCreateMediaType();
            audioOutputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio).CheckError();
            audioOutputType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac).CheckError();
            audioOutputType.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)AudioSampleRate).CheckError();
            audioOutputType.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)AudioChannels).CheckError();
            audioOutputType.Set(MediaTypeAttributeKeys.AudioBitsPerSample, (uint)AudioBitsPerSample).CheckError();
            audioOutputType.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)AudioAacBytesPerSecond).CheckError();
            audioStreamIndex = writer.AddStream(audioOutputType);

            // PCM input type built straight from a WaveFormat — MFCreateAudioMediaType fills in
            // every audio attribute (rate/channels/bits/block-align/avg-bytes) from the struct.
            var pcmFormat = new WaveFormat(AudioSampleRate, AudioBitsPerSample, AudioChannels);
            using var audioInputType = MediaFactory.MFCreateAudioMediaType(ref pcmFormat);
            writer.SetInputMediaType(audioStreamIndex, audioInputType, null);
        }

        writer.BeginWriting();

        return new Mp4Encoder(writer, streamIndex, audioStreamIndex, fps);
    }

    /// <summary>Encoder thread only. SdrImage.Pixels is guaranteed tightly packed
    /// (Stride == Width * 4 — SdrImage's own doc comment), which is exactly the buffer MF's RGB32
    /// subtype expects — no repacking or row reversal needed: the input type's positive
    /// DefaultStride declares the buffer top-down, matching SdrImage's layout verbatim.</summary>
    public void WriteFrame(SdrImage bgra8, long timestamp100ns)
    {
        using var buffer = MediaFactory.MFCreateMemoryBuffer(bgra8.Pixels.Length);
        buffer.Lock(out IntPtr ptr, out _, out _);
        try
        {
            Marshal.Copy(bgra8.Pixels, 0, ptr, bgra8.Pixels.Length);
        }
        finally
        {
            buffer.Unlock();
        }
        buffer.CurrentLength = bgra8.Pixels.Length;

        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = timestamp100ns;
        sample.SampleDuration = _frameDuration100ns;
        _writer.WriteSample(_streamIndex, sample); // void-returning — SharpGen auto-throws on HRESULT failure
    }

    /// <summary>Encoder thread only, same single-thread discipline as <see cref="WriteFrame"/>.
    /// <paramref name="pcm"/> is 48 kHz stereo 16-bit PCM (see the Audio* constants); duration is
    /// derived from the byte count so callers only supply the chunk's start timestamp.</summary>
    public void WriteAudioSamples(byte[] pcm, int length, long timestamp100ns)
    {
        if (_audioStreamIndex < 0 || length <= 0)
        {
            return;
        }

        using var buffer = MediaFactory.MFCreateMemoryBuffer(length);
        buffer.Lock(out IntPtr ptr, out _, out _);
        try
        {
            Marshal.Copy(pcm, 0, ptr, length);
        }
        finally
        {
            buffer.Unlock();
        }
        buffer.CurrentLength = length;

        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = timestamp100ns;
        sample.SampleDuration = 10_000_000L * (length / AudioBlockAlign) / AudioSampleRate;
        _writer.WriteSample(_audioStreamIndex, sample);
    }

    public void FinalizeAndClose() => _writer.Finalize(); // IMFSinkWriter's own interface method, not System.Object.Finalize

    public void Dispose() => _writer.Dispose();
}
