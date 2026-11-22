using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using RoeSnip.Core.Diagnostics;
using RoeSnip.Core.Recording;

namespace RoeSnip.Platform.Linux;

/// <summary>Linux recording audio through GStreamer + PulseAudio (pipewire-pulse): microphone =
/// the default source, system audio = the default sink's monitor (<c>@DEFAULT_MONITOR@</c>), both
/// converted to 48 kHz stereo S16LE and mixed by audiomixer when both are on. Same contract as the
/// Windows WASAPI device: TryStart blocks (bounded) until samples flow, Stop stops the queue from
/// growing, Dispose drains the last samples into the queue and tears down.
///
/// Chunk timestamps run on a continuous sample-count timeline anchored to the Stopwatch at the
/// first buffer (pulse delivers silence rather than gaps, so the count tracks wall time).</summary>
public sealed unsafe class GstAudioCaptureDevice : IAudioCaptureDevice
{
    private const int SampleRate = 48_000;
    private const int BlockAlign = 4;
    private const string Format = "audio/x-raw,format=S16LE,layout=interleaved,rate=48000,channels=2";

    private readonly ConcurrentQueue<AudioChunk> _queue = new();
    private readonly IntPtr _pipeline;
    private readonly IntPtr _sink;
    private readonly IntPtr _bus;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _firstSample = new(false);
    private volatile bool _stopRequested;
    private volatile bool _exit;
    private volatile string? _error;
    private long _anchorTicks;
    private long _emittedSamples;
    private int _disposed;

    public static bool CanCapture =>
        Gst.Available && new[] { "pulsesrc", "audioconvert", "audioresample", "audiomixer", "appsink" }.All(Gst.HasElement);

    private GstAudioCaptureDevice(IntPtr pipeline, IntPtr sink, IntPtr bus)
    {
        _pipeline = pipeline;
        _sink = sink;
        _bus = bus;
        _thread = new Thread(Pump) { IsBackground = true, Name = "RoeSnip-Recording-Audio" };
        _thread.Start();
    }

    public static IAudioCaptureDevice? TryStart(bool microphone, bool systemAudio)
    {
        if (!microphone && !systemAudio) return null;
        if (!CanCapture)
        {
            FileLog.Write("RoeSnip: audio capture unavailable: GStreamer pulsesrc/audiomixer missing");
            return null;
        }

        const string branch = "audioconvert ! audioresample ! " + Format + " ! queue";
        const string sinkPart = "appsink name=sink sync=false max-buffers=0 drop=false";
        string mic = "pulsesrc client-name=RoeSnip ! " + branch;
        string monitor = "pulsesrc client-name=RoeSnip device=@DEFAULT_MONITOR@ ! " + branch;
        string description = microphone && systemAudio
            ? $"audiomixer name=mix ! {Format} ! {sinkPart} {mic} ! mix. {monitor} ! mix."
            : $"{(microphone ? mic : monitor)} ! {sinkPart}";

        IntPtr pipeline;
        try
        {
            pipeline = Gst.ParseLaunch(description);
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: audio capture unavailable: {ex.Message}");
            return null;
        }
        IntPtr sink = Gst.gst_bin_get_by_name(pipeline, "sink");
        IntPtr bus = Gst.gst_element_get_bus(pipeline);
        if (sink == IntPtr.Zero || bus == IntPtr.Zero
            || Gst.gst_element_set_state(pipeline, Gst.StatePlaying) == Gst.StateChangeFailure)
        {
            string why = bus != IntPtr.Zero ? Gst.PopFailure(bus) ?? "could not start" : "could not start";
            FileLog.Write($"RoeSnip: audio capture unavailable: {why}");
            if (sink != IntPtr.Zero) Gst.gst_object_unref(sink);
            if (bus != IntPtr.Zero) Gst.gst_object_unref(bus);
            Gst.Shutdown(pipeline);
            return null;
        }

        var device = new GstAudioCaptureDevice(pipeline, sink, bus);
        if (!device._firstSample.Wait(3000) || device._error is not null)
        {
            FileLog.Write($"RoeSnip: audio capture unavailable: {device._error ?? "no samples within 3 s"}");
            device.Dispose();
            return null;
        }
        FileLog.Write($"RoeSnip: audio capture started (mic={microphone}, system={systemAudio})");
        return device;
    }

    public bool TryDequeue(out AudioChunk chunk) => _queue.TryDequeue(out chunk);

    public void Stop() => _stopRequested = true;

    private void Pump()
    {
        double ticksPerSample = (double)Stopwatch.Frequency / SampleRate;
        while (!_exit)
        {
            IntPtr sample = Gst.gst_app_sink_try_pull_sample(_sink, 50_000_000);
            if (sample == IntPtr.Zero)
            {
                string? failure = Gst.PopFailure(_bus);
                if (failure is not null)
                {
                    _error = failure;
                    FileLog.Write($"RoeSnip: audio capture stopped: {failure}");
                    _firstSample.Set();
                    return;
                }
                continue;
            }
            try
            {
                byte[]? pcm = Read(sample);
                if (pcm is null || pcm.Length < BlockAlign) continue;
                long samples = pcm.Length / BlockAlign;
                if (_anchorTicks == 0)
                {
                    _anchorTicks = Stopwatch.GetTimestamp() - (long)(samples * ticksPerSample);
                    _firstSample.Set();
                }
                long ts = _anchorTicks + (long)(_emittedSamples * ticksPerSample);
                _emittedSamples += samples;
                if (!_stopRequested) _queue.Enqueue(new AudioChunk(pcm, ts));
            }
            finally
            {
                Gst.gst_mini_object_unref(sample);
            }
        }
    }

    private static byte[]? Read(IntPtr sample)
    {
        IntPtr buffer = Gst.gst_sample_get_buffer(sample);
        if (buffer == IntPtr.Zero) return null;
        Gst.MapInfo map;
        if (Gst.gst_buffer_map(buffer, &map, Gst.MapRead) == 0) return null;
        try
        {
            var pcm = new byte[(int)map.Size];
            Marshal.Copy(map.Data, pcm, 0, pcm.Length);
            return pcm;
        }
        finally
        {
            Gst.gst_buffer_unmap(buffer, &map);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _exit = true;
        bool joined = _thread.Join(2000);
        // Final flush: samples the appsink already holds still land in the queue (pre-stop audio).
        if (joined && !_stopRequested && _anchorTicks != 0)
        {
            double ticksPerSample = (double)Stopwatch.Frequency / SampleRate;
            IntPtr sample;
            while ((sample = Gst.gst_app_sink_try_pull_sample(_sink, 0)) != IntPtr.Zero)
            {
                byte[]? pcm = Read(sample);
                Gst.gst_mini_object_unref(sample);
                if (pcm is null || pcm.Length < BlockAlign) continue;
                _queue.Enqueue(new AudioChunk(pcm, _anchorTicks + (long)(_emittedSamples * ticksPerSample)));
                _emittedSamples += pcm.Length / BlockAlign;
            }
        }
        _stopRequested = true;
        if (!joined) return; // the pump may still touch the pipeline: leak it rather than crash
        Gst.gst_object_unref(_sink);
        Gst.gst_object_unref(_bus);
        Gst.Shutdown(_pipeline);
    }
}
