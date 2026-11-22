using System.Runtime.InteropServices;
using RoeSnip.Core.Diagnostics;
using RoeSnip.Core.Imaging;
using RoeSnip.Core.Recording;
using RoeSnip.Core.Recording.Gif;

namespace RoeSnip.Platform.Linux;

/// <summary>Linux MP4 encoder through GStreamer:
/// appsrc (BGRA) -> videoconvert -> best working H.264 encoder -> h264parse -> mp4mux -> filesink,
/// plus appsrc (S16LE 48 kHz stereo) -> audioconvert -> AAC (Opus when no AAC encoder exists)
/// into the same mux when the take has audio. Same call contract as the Windows Mp4Encoder:
/// encoder-thread only, non-decreasing 100 ns timestamps, one FinishAsync, then Dispose.
/// The video appsrc blocks once three raw frames are queued, so a slow encoder back-pressures
/// WriteFrame like Media Foundation's WriteSample does. H.264 needs even dimensions: an odd
/// last column/row is dropped.</summary>
public sealed unsafe class GstMp4Encoder : IVideoEncoder
{
    private const int AudioSampleRate = 48_000;
    private const int AudioBlockAlign = 4; // stereo 16-bit
    private const long AudioBitrateBps = Mp4BitrateEstimator.AudioAacBytesPerSecond * 8L;

    private static readonly object s_probeGate = new();
    private static GstEncoderCandidate? s_video;
    private static GstEncoderCandidate? s_audio;
    private static bool s_videoProbed, s_audioProbed;

    private readonly IntPtr _pipeline;
    private readonly IntPtr _videoSrc;
    private readonly IntPtr _audioSrc; // zero when the take has no audio track
    private readonly IntPtr _bus;
    private readonly int _srcWidth, _srcHeight, _width, _height;
    private readonly long _frameDurationNs;
    private bool _anyAudio;
    private bool _finished;
    private int _disposed;

    public bool HasAudio => _audioSrc != IntPtr.Zero;

    private GstMp4Encoder(IntPtr pipeline, IntPtr videoSrc, IntPtr audioSrc, IntPtr bus,
        int srcWidth, int srcHeight, int width, int height, int fps)
    {
        _pipeline = pipeline;
        _videoSrc = videoSrc;
        _audioSrc = audioSrc;
        _bus = bus;
        _srcWidth = srcWidth;
        _srcHeight = srcHeight;
        _width = width;
        _height = height;
        _frameDurationNs = 1_000_000_000L / Math.Max(1, fps);
    }

    /// <summary>True when GStreamer and every element the MP4 path needs are installed.</summary>
    public static bool CanEncodeMp4 =>
        Gst.Available
        && new[] { "appsrc", "videoconvert", "h264parse", "mp4mux", "filesink", "queue" }.All(Gst.HasElement)
        && GstEncoderCatalog.Available(GstEncoderCatalog.H264, Gst.HasElement).Count > 0;

    /// <summary>True when an audio encoder the mux accepts is installed.</summary>
    public static bool CanEncodeAudio =>
        Gst.Available && Gst.HasElement("audioconvert")
        && GstEncoderCatalog.Available(GstEncoderCatalog.Audio, Gst.HasElement).Count > 0;

    public static GstMp4Encoder Create(string tempFilePath, int width, int height, int fps, bool withAudio, GifSizePreset preset)
    {
        if (!Gst.Available) throw new PlatformNotSupportedException("GStreamer unavailable: " + Gst.UnavailableReason);
        var video = ProbeVideo() ?? throw new PlatformNotSupportedException("no working H.264 encoder in GStreamer");
        var audio = withAudio ? ProbeAudio() : null;
        if (withAudio && audio is null)
        {
            FileLog.Write("RoeSnip: no working GStreamer audio encoder; recording video only");
        }

        int w = Math.Max(2, width & ~1), h = Math.Max(2, height & ~1);
        fps = Math.Max(1, fps);
        long bitrate = Mp4BitrateEstimator.ComputeBitrate(w, h, fps, preset);
        int frameBytes = width * height * 4;
        string location = tempFilePath.Replace("\"", "");

        string description =
            $"mp4mux name=mux faststart=true ! filesink location=\"{location}\" " +
            $"appsrc name=vsrc format=time is-live=false block=true max-bytes={frameBytes * 3} " +
            $"caps=\"video/x-raw,format=BGRA,width={w},height={h},framerate={fps}/1\" ! " +
            $"videoconvert ! {video.Describe(bitrate)} ! h264parse ! " +
            "queue max-size-buffers=0 max-size-bytes=0 max-size-time=0 ! mux. ";
        if (audio is not null)
        {
            description +=
                "appsrc name=asrc format=time is-live=false block=false max-bytes=0 " +
                $"caps=\"audio/x-raw,format=S16LE,layout=interleaved,rate={AudioSampleRate},channels=2\" ! " +
                $"audioconvert ! {audio.Describe(AudioBitrateBps)} ! " +
                "queue max-size-buffers=0 max-size-bytes=0 max-size-time=0 ! mux. ";
        }

        IntPtr pipeline = Gst.ParseLaunch(description);
        IntPtr vsrc = Gst.gst_bin_get_by_name(pipeline, "vsrc");
        IntPtr asrc = audio is null ? IntPtr.Zero : Gst.gst_bin_get_by_name(pipeline, "asrc");
        IntPtr bus = Gst.gst_element_get_bus(pipeline);
        if (vsrc == IntPtr.Zero || bus == IntPtr.Zero || (audio is not null && asrc == IntPtr.Zero)
            || Gst.gst_element_set_state(pipeline, Gst.StatePlaying) == Gst.StateChangeFailure)
        {
            string why = bus != IntPtr.Zero ? Gst.PopFailure(bus) ?? "could not start" : "could not start";
            if (vsrc != IntPtr.Zero) Gst.gst_object_unref(vsrc);
            if (asrc != IntPtr.Zero) Gst.gst_object_unref(asrc);
            if (bus != IntPtr.Zero) Gst.gst_object_unref(bus);
            Gst.Shutdown(pipeline);
            throw new InvalidOperationException("MP4 pipeline: " + why);
        }

        FileLog.Write($"RoeSnip: MP4 encoder {video.Element} {w}x{h} {fps}fps {bitrate / 1000} kbps" +
            (audio is not null ? $", audio {audio.Element}" : ", no audio"));
        return new GstMp4Encoder(pipeline, vsrc, asrc, bus, width, height, w, h, fps);
    }

    public bool WriteFrame(SdrImage frame, long timestamp100ns)
    {
        int rowBytes = _width * 4;
        int size = rowBytes * _height;
        IntPtr buffer = NewBuffer(size, out Gst.MapInfo map);
        try
        {
            byte* dst = (byte*)map.Data;
            int srcStride = frame.Width * 4;
            int rows = Math.Min(_height, frame.Height);
            int copy = Math.Min(rowBytes, srcStride);
            fixed (byte* src = frame.Pixels)
            {
                for (int y = 0; y < rows; y++)
                {
                    Buffer.MemoryCopy(src + (long)y * srcStride, dst + (long)y * rowBytes, rowBytes, copy);
                }
            }
        }
        finally
        {
            Gst.gst_buffer_unmap(buffer, &map);
        }
        Gst.SetBufferTiming(buffer, timestamp100ns * 100, _frameDurationNs);
        Push(_videoSrc, buffer, "video");
        return true;
    }

    public void WriteAudioSamples(byte[] pcm, int length, long timestamp100ns)
    {
        if (_audioSrc == IntPtr.Zero || length <= 0) return;
        PushAudio(pcm, length, timestamp100ns * 100);
    }

    private void PushAudio(byte[] pcm, int length, long ptsNs)
    {
        IntPtr buffer = NewBuffer(length, out Gst.MapInfo map);
        try
        {
            Marshal.Copy(pcm, 0, map.Data, length);
        }
        finally
        {
            Gst.gst_buffer_unmap(buffer, &map);
        }
        long durationNs = 1_000_000_000L * (length / AudioBlockAlign) / AudioSampleRate;
        Gst.SetBufferTiming(buffer, ptsNs, durationNs);
        Push(_audioSrc, buffer, "audio");
        _anyAudio = true;
    }

    private static IntPtr NewBuffer(int size, out Gst.MapInfo map)
    {
        IntPtr buffer = Gst.gst_buffer_new_allocate(IntPtr.Zero, (nuint)size, IntPtr.Zero);
        if (buffer == IntPtr.Zero) throw new OutOfMemoryException("gst_buffer_new_allocate failed");
        Gst.MapInfo info;
        if (Gst.gst_buffer_map(buffer, &info, Gst.MapWrite) == 0)
        {
            Gst.gst_mini_object_unref(buffer);
            throw new InvalidOperationException("gst_buffer_map failed");
        }
        map = info;
        return buffer;
    }

    private void Push(IntPtr appsrc, IntPtr buffer, string what)
    {
        int flow = Gst.gst_app_src_push_buffer(appsrc, buffer); // takes ownership
        if (flow != 0)
        {
            string why = Gst.PopFailure(_bus) ?? $"flow {flow}";
            throw new InvalidOperationException($"MP4 {what} push failed: {why}");
        }
    }

    public Task FinishAsync(long? endTimestampTicks = null)
    {
        if (_finished) return Task.CompletedTask;
        _finished = true;
        if (_audioSrc != IntPtr.Zero)
        {
            // An audio track with no samples can fail the mux: give it 20 ms of silence.
            if (!_anyAudio) PushAudio(new byte[AudioSampleRate / 50 * AudioBlockAlign], AudioSampleRate / 50 * AudioBlockAlign, 0);
            Gst.gst_app_src_end_of_stream(_audioSrc);
        }
        Gst.gst_app_src_end_of_stream(_videoSrc);
        string? result = Gst.WaitEos(_bus, 30_000_000_000L);
        Gst.gst_element_set_state(_pipeline, Gst.StateNull);
        if (result is not null)
        {
            throw new InvalidOperationException("MP4 finalize: " + result);
        }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Gst.gst_object_unref(_videoSrc);
        if (_audioSrc != IntPtr.Zero) Gst.gst_object_unref(_audioSrc);
        Gst.gst_object_unref(_bus);
        Gst.Shutdown(_pipeline);
    }

    /// <summary>First H.264 encoder (probe order) that actually encodes a few test frames; a
    /// factory can exist while its hardware path fails (no CUDA, no VA driver).</summary>
    private static GstEncoderCandidate? ProbeVideo()
    {
        lock (s_probeGate)
        {
            if (s_videoProbed) return s_video;
            s_videoProbed = true;
            foreach (var c in GstEncoderCatalog.Available(GstEncoderCatalog.H264, Gst.HasElement))
            {
                string? why = RunProbe(
                    "videotestsrc num-buffers=5 ! video/x-raw,width=320,height=240,framerate=30/1 ! videoconvert ! " +
                    $"{c.Describe(1_000_000)} ! h264parse ! fakesink");
                FileLog.Write($"RoeSnip: H.264 encoder probe {c.Element}: {why ?? "ok"}");
                if (why is null) { s_video = c; break; }
            }
            return s_video;
        }
    }

    private static GstEncoderCandidate? ProbeAudio()
    {
        lock (s_probeGate)
        {
            if (s_audioProbed) return s_audio;
            s_audioProbed = true;
            foreach (var c in GstEncoderCatalog.Available(GstEncoderCatalog.Audio, Gst.HasElement))
            {
                string? why = RunProbe(
                    $"audiotestsrc num-buffers=5 ! audio/x-raw,format=S16LE,layout=interleaved,rate={AudioSampleRate},channels=2 ! " +
                    $"audioconvert ! {c.Describe(AudioBitrateBps)} ! fakesink");
                FileLog.Write($"RoeSnip: audio encoder probe {c.Element}: {why ?? "ok"}");
                if (why is null) { s_audio = c; break; }
            }
            return s_audio;
        }
    }

    private static string? RunProbe(string description)
    {
        IntPtr pipeline;
        try
        {
            pipeline = Gst.ParseLaunch(description);
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        IntPtr bus = Gst.gst_element_get_bus(pipeline);
        try
        {
            if (Gst.gst_element_set_state(pipeline, Gst.StatePlaying) == Gst.StateChangeFailure)
            {
                return Gst.PopFailure(bus) ?? "state change failed";
            }
            return Gst.WaitEos(bus, 5_000_000_000L);
        }
        finally
        {
            Gst.gst_object_unref(bus);
            Gst.Shutdown(pipeline);
        }
    }
}

file static class ModuleInit
{
#pragma warning disable CA2255
    [System.Runtime.CompilerServices.ModuleInitializer]
#pragma warning restore CA2255
    internal static void Init()
    {
        Mp4VideoEncoderRegistry.Register(
            () => OperatingSystem.IsLinux(),
            (path, width, height, fps, withAudio, preset) => GstMp4Encoder.Create(path, width, height, fps, withAudio, preset));

        AudioCaptureDeviceRegistry.Register(
            () => OperatingSystem.IsLinux(),
            (microphone, systemAudio) => GstAudioCaptureDevice.TryStart(microphone, systemAudio));

        // Evaluated per recording session; degrades to GIF-only and silent exactly as before
        // when GStreamer or an encoder is missing.
        RecordingCapabilitiesRegistry.Register(
            () => OperatingSystem.IsLinux(),
            () =>
            {
                bool mp4 = GstMp4Encoder.CanEncodeMp4;
                bool audio = mp4 && GstMp4Encoder.CanEncodeAudio && GstAudioCaptureDevice.CanCapture;
                return new RecordingCapabilities(SupportsMp4: mp4, SupportsMicrophone: audio, SupportsLoopback: audio);
            });
    }
}
