using System.Runtime.InteropServices;
using RoeSnip.Core.Imaging;
using RoeSnip.Core.Recording;
using RoeSnip.Core.Recording.Gif;
using Vortice.MediaFoundation;
using Vortice.Multimedia;

namespace RoeSnip.Platform.Windows;

/// <summary>Encodes BGRA8 <see cref="SdrImage"/> frames to H.264/MP4 via Media Foundation's
/// IMFSinkWriter — ported near-verbatim from the WPF app's own <c>src/RoeSnip/Recording/Mp4Encoder.cs</c>
/// (see that file's own doc comment for why this exact API surface is pinned — verified by
/// reflecting the real Vortice.MediaFoundation 3.8.3 DLL, not assumed). The only real changes from
/// the WPF original: this class now implements <see cref="IVideoEncoder"/> (the recording-seams
/// workstream's Core contract — WriteFrame returns bool, FinishAsync replaces FinalizeAndClose as
/// the interface member, both still exposed as concrete members below for direct callers/tests) and
/// speaks the portable <see cref="RoeSnip.Core.Imaging.SdrImage"/> instead of the WPF app's own
/// richer <c>RoeSnip.Imaging.SdrImage</c>.
///
/// Encoder-thread-only (RecordingController's dedicated thread) — never the capture callback thread,
/// never the UI thread.</summary>
public sealed class Mp4Encoder : IVideoEncoder
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

    /// <summary>Bitrate heuristic — the actual math lives in the portable <see cref="Mp4BitrateEstimator"/>
    /// (RoeSnip.Core.Recording); this is a straight passthrough kept here so every call site that
    /// mirrors the WPF app's own <c>Mp4Encoder.ComputeBitrate</c> keeps working unchanged.</summary>
    public static long ComputeBitrate(int width, int height, int fps) =>
        Mp4BitrateEstimator.ComputeBitrate(width, height, fps);

    /// <summary>Recording-size-tiers overload — see <see cref="Mp4BitrateEstimator.ComputeBitrate(int,int,int,GifSizePreset)"/>.</summary>
    public static long ComputeBitrate(int width, int height, int fps, GifSizePreset preset) =>
        Mp4BitrateEstimator.ComputeBitrate(width, height, fps, preset);

    /// <summary>The one PCM format every audio path speaks: 48 kHz, stereo, 16-bit — 4 bytes per
    /// sample block. Matches <see cref="AudioCaptureDevice"/>'s own target format exactly.</summary>
    public const int AudioSampleRate = 48_000;
    public const int AudioChannels = 2;
    public const int AudioBitsPerSample = 16;
    public const int AudioBlockAlign = AudioChannels * AudioBitsPerSample / 8;

    /// <summary>128 kbps AAC — see <see cref="Mp4BitrateEstimator.AudioAacBytesPerSecond"/> for the
    /// one source of truth this constant delegates to.</summary>
    public const int AudioAacBytesPerSecond = Mp4BitrateEstimator.AudioAacBytesPerSecond;

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

    /// <summary>Same contract as the WPF app's own <c>Mp4Encoder.Create</c> — see that method's doc
    /// comment for the full media-type/attribute reasoning (byte-identical here, only the SdrImage
    /// type differs).</summary>
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
        // CRITICAL, easy to get backwards: MF_MT_DEFAULT_STRIDE's SIGN encodes row order — POSITIVE
        // means top-down, NEGATIVE means bottom-up (the DIB-native default for RGB formats).
        // SdrImage rows are top-down (see that class's own doc comment), so this must be +stride —
        // see the WPF original's own comment for the upside-down-video regression this guards
        // against (Mp4VideoEncoderTests' decode-back orientation test re-verifies it here).
        int stride = width * 4;
        inputType.Set(MediaTypeAttributeKeys.DefaultStride, (uint)stride).CheckError();

        using var attrs = MediaFactory.MFCreateAttributes(2);
        // Frames already arrive throttled by our own capture cadence — don't let MF pace-block
        // WriteSample a second time on top of that.
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
    /// (Stride == Width * 4), exactly the buffer MF's RGB32 subtype expects — no repacking or row
    /// reversal needed. Always returns true — every call is a straight encode, matching
    /// <see cref="IVideoEncoder.WriteFrame"/>'s own documented MP4 contract (unlike GIF, MP4 never
    /// dedupes).</summary>
    public bool WriteFrame(SdrImage bgra8, long timestamp100ns)
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
        return true;
    }

    /// <summary>Encoder thread only, same single-thread discipline as <see cref="WriteFrame"/>.
    /// <paramref name="pcm"/> is 48 kHz stereo 16-bit PCM; duration is derived from the byte count so
    /// callers only supply the chunk's start timestamp. A no-op when this take has no audio track —
    /// matches <see cref="IVideoEncoder.WriteAudioSamples"/>'s documented contract, callers never
    /// need to gate this themselves.</summary>
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

    /// <summary>IMFSinkWriter's own interface method, not System.Object.Finalize. Kept as a public
    /// concrete member (not just reachable via <see cref="FinishAsync"/>) since direct callers/tests
    /// porting the WPF app's own Mp4EncoderTests call it by this name.</summary>
    public void FinalizeAndClose() => _writer.Finalize();

    /// <summary><see cref="IVideoEncoder"/> member. MP4 ignores <paramref name="endTimestampTicks"/>
    /// entirely (H.264/MP4 has no "final frame duration" concept — see IVideoEncoder's own doc
    /// comment); this is a synchronous <see cref="FinalizeAndClose"/> wrapped in a completed Task.</summary>
    public Task FinishAsync(long? endTimestampTicks = null)
    {
        FinalizeAndClose();
        return Task.CompletedTask;
    }

    public void Dispose() => _writer.Dispose();
}

file static class ModuleInit
{
    // CA2255 warns against [ModuleInitializer] in a library; this exact usage is the seam this
    // codebase's registries all use (CaptureBackendRegistry, AudioCaptureDeviceRegistry) so
    // Core/App never name a concrete Platform.* type. The App shell force-loads the platform
    // assemblies at startup (see Program.cs's RegisterPlatformHooks), which is what makes this
    // initializer's timing deterministic.
#pragma warning disable CA2255
    [System.Runtime.CompilerServices.ModuleInitializer]
#pragma warning restore CA2255
    internal static void Init()
    {
        Mp4VideoEncoderRegistry.Register(
            () => OperatingSystem.IsWindows(),
            (path, width, height, fps, withAudio, preset) => Mp4Encoder.Create(path, width, height, fps, withAudio, preset));

        // Registered once here (not duplicated in AudioCapture.cs's own ModuleInit) since on Windows
        // all three capabilities are true together — MP4 encode and WASAPI mic/loopback capture both
        // live in this one assembly.
        RecordingCapabilitiesRegistry.Register(
            () => OperatingSystem.IsWindows(),
            () => new RecordingCapabilities(SupportsMp4: true, SupportsMicrophone: true, SupportsLoopback: true));
    }
}
