using System.Diagnostics;
using System.Threading.Channels;
using RoeSnip.Core.Capture;
using RoeSnip.Core.Diagnostics;
using RoeSnip.Core.Imaging;
using RoeSnip.Core.Recording;
using RoeSnip.Core.Recording.Gif;

namespace RoeSnip.Platform.MacOS;

/// <summary>One recording take on macOS, shared by the three Core seams RecordingSession calls in
/// BeginCapture: the region source is created first (it opens the take), the audio factory then
/// records the mic/system choice, and the MP4 encoder factory starts the native recording in the
/// helper (capture and H.264/AAC encode both happen there). A GIF take never reaches the MP4
/// factory, so its source pulls cropped frames from the helper instead. RecordingController allows
/// one session at a time, which is what makes a single pending slot safe.</summary>
internal sealed class MacRecordingTake
{
    private static MacRecordingTake? s_pending;

    public MacRecordingTake(MacRegionCaptureSource source) => Source = source;

    public MacRegionCaptureSource Source { get; }
    public bool Microphone;
    public bool SystemAudio;

    public static void Open(MacRecordingTake take) => Volatile.Write(ref s_pending, take);
    public static MacRecordingTake? Pending => Volatile.Read(ref s_pending);
    public static MacRecordingTake? Claim() => Interlocked.Exchange(ref s_pending, null);
    public static void Close(MacRecordingTake take) => Interlocked.CompareExchange(ref s_pending, null, take);
}

/// <summary>macOS <see cref="IRegionCaptureSource"/>. MP4 takes: produces no frames (the helper
/// records the file itself); Paused/SetOrigin/faults are forwarded to and from the helper. GIF takes:
/// crops of the display's kept-open stream on the <see cref="RecordingSchedule"/> cadence, emitted
/// only when the stream has a new frame or the region moved.</summary>
public sealed class MacRegionCaptureSource : IRegionCaptureSource
{
    private readonly ScksnapServer _server;
    private readonly MonitorInfo _monitor;
    private readonly uint _displayId;
    private readonly int _width;
    private readonly int _height;
    private readonly int _targetFps;
    private readonly Channel<RegionCaptureFrame> _queue;
    private readonly MacRecordingTake _take;
    private readonly AutoResetEvent _originChanged = new(false);
    private long _originPacked;
    private Thread? _worker;
    private MacNativeMp4Encoder? _native;
    private int _started;
    private int _stopped;
    private int _faulted;
    private volatile bool _paused;

    public event Action<Exception>? Faulted;
    public ChannelReader<RegionCaptureFrame> Frames => _queue.Reader;

    private MacRegionCaptureSource(ScksnapServer server, MonitorInfo monitor, uint displayId,
        RectPhysical selectionPx, int targetFps)
    {
        _server = server;
        _monitor = monitor;
        _displayId = displayId;
        _width = selectionPx.Width;
        _height = selectionPx.Height;
        _targetFps = Math.Max(1, targetFps);
        _originPacked = Pack(selectionPx.Left, selectionPx.Top);
        _queue = Channel.CreateBounded<RegionCaptureFrame>(new BoundedChannelOptions(3)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        }, static dropped => dropped.Frame.Dispose());
        _take = new MacRecordingTake(this);
    }

    /// <summary>The helper-backed source, or the portable polled source when serve mode cannot run.</summary>
    public static IRegionCaptureSource Create(MonitorInfo monitor, RectPhysical selectionPx, int targetFps)
    {
        var server = ScksnapServer.Shared;
        if (uint.TryParse(monitor.BackendKey, out uint displayId) && server.EnsureRunning())
        {
            var source = new MacRegionCaptureSource(server, monitor, displayId, selectionPx, targetFps);
            MacRecordingTake.Open(source._take);
            return source;
        }
        FileLog.Write("RoeSnip: scksnap serve unavailable; recording through the polled capture source (GIF only)");
        return new PolledRegionCaptureSource(monitor, selectionPx, targetFps);
    }

    internal uint DisplayId => _displayId;
    internal (int Left, int Top) Origin => Unpack(Volatile.Read(ref _originPacked));

    private static long Pack(int left, int top) => ((long)left << 32) | (uint)top;
    private static (int, int) Unpack(long packed) => ((int)(packed >> 32), (int)(uint)packed);

    /// <summary>Called by the MP4 factory once the helper is recording this take.</summary>
    internal void AttachNative(MacNativeMp4Encoder encoder)
    {
        _native = encoder;
        _server.RecordingFailed += OnHelperRecordingFailed;
        _server.Exited += OnHelperExited;
    }

    public bool Paused
    {
        get => _paused;
        set
        {
            if (_paused == value) return;
            _paused = value;
            if (_native is null || Volatile.Read(ref _stopped) != 0) return;
            try
            {
                var reply = _server.Send(value ? ScksnapProtocol.OpPause : ScksnapProtocol.OpResume,
                    Array.Empty<byte>(), TimeSpan.FromSeconds(3));
                if (reply.Status != ScksnapProtocol.StatusOk)
                {
                    FileLog.Write($"RoeSnip: scksnap recording {(value ? "pause" : "resume")} failed: {reply.Text}");
                }
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or ScksnapUnavailableException)
            {
                FileLog.Write($"RoeSnip: scksnap recording {(value ? "pause" : "resume")} failed: {ex.Message}");
            }
        }
    }

    public void SetOrigin(int left, int top)
    {
        int maxL = Math.Max(0, _monitor.BoundsPx.Width - _width);
        int maxT = Math.Max(0, _monitor.BoundsPx.Height - _height);
        Volatile.Write(ref _originPacked, Pack(Math.Clamp(left, 0, maxL), Math.Clamp(top, 0, maxT)));
        _originChanged.Set();
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        MacRecordingTake.Close(_take);
        _worker = _native is not null
            ? new Thread(OriginLoop) { IsBackground = true, Name = "RoeSnip-Scksnap-Origin" }
            : new Thread(GifPumpLoop) { IsBackground = true, Name = "RoeSnip-Scksnap-RegionCapture" };
        _worker.Start();
    }

    /// <summary>MP4: drag updates go to the helper's stream, coalesced to the latest origin.</summary>
    private void OriginLoop()
    {
        long sent = Volatile.Read(ref _originPacked);
        while (Volatile.Read(ref _stopped) == 0)
        {
            _originChanged.WaitOne(250);
            long packed = Volatile.Read(ref _originPacked);
            if (packed == sent || Volatile.Read(ref _stopped) != 0) continue;
            sent = packed;
            var (left, top) = Unpack(packed);
            try
            {
                var reply = _server.Send(ScksnapProtocol.OpSetOrigin, ScksnapProtocol.EncodeOrigin(left, top),
                    TimeSpan.FromSeconds(3));
                if (reply.Status != ScksnapProtocol.StatusOk)
                {
                    FileLog.Write($"RoeSnip: scksnap recording move failed: {reply.Text}");
                }
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or ScksnapUnavailableException)
            {
                FileLog.Write($"RoeSnip: scksnap recording move failed: {ex.Message}");
            }
        }
    }

    /// <summary>GIF: region crops of the kept-open stream frame (no cursor, like a snip).</summary>
    private void GifPumpLoop()
    {
        var schedule = new RecordingSchedule(_targetFps, Stopwatch.Frequency);
        int pollMs = Math.Clamp(250 / _targetFps, 2, 50);
        ulong lastSequence = ulong.MaxValue;
        long lastPacked = long.MinValue;
        int failures = 0;
        FileLog.Write($"RoeSnip: GIF recording from the scksnap stream, region {_width}x{_height} on {_monitor.DeviceName}");
        while (Volatile.Read(ref _stopped) == 0)
        {
            if (_paused)
            {
                Thread.Sleep(pollMs);
                continue;
            }
            long now = Stopwatch.GetTimestamp();
            if (!schedule.ShouldAccept(now))
            {
                Thread.Sleep(pollMs);
                continue;
            }
            long packed = Volatile.Read(ref _originPacked);
            var (left, top) = Unpack(packed);
            ScksnapFrame raw;
            try
            {
                raw = _server.SnapshotRegion(_displayId, left, top, _width, _height);
                failures = 0;
            }
            catch (Exception ex)
            {
                if (++failures >= 3)
                {
                    RaiseFaulted(ex);
                    return;
                }
                Thread.Sleep(pollMs);
                continue;
            }
            if ((raw.Sequence == lastSequence && packed == lastPacked) || raw.Width != _width || raw.Height != _height)
            {
                continue;
            }
            lastSequence = raw.Sequence;
            lastPacked = packed;
            CapturedFrame frame;
            try
            {
                frame = MacCaptureBackend.ToCapturedFrame(raw, _monitor);
            }
            catch (CaptureException ex)
            {
                RaiseFaulted(ex);
                return;
            }
            if (!_queue.Writer.TryWrite(new RegionCaptureFrame(frame, now)))
            {
                frame.Dispose();
            }
        }
    }

    private void OnHelperRecordingFailed(string message)
        => RaiseFaulted(new InvalidOperationException("macOS recording failed: " + message));

    private void OnHelperExited(string summary)
        => RaiseFaulted(new InvalidOperationException(summary));

    private void RaiseFaulted(Exception ex)
    {
        if (Volatile.Read(ref _stopped) != 0 || Interlocked.Exchange(ref _faulted, 1) != 0) return;
        Faulted?.Invoke(ex);
    }

    /// <summary>Completes <see cref="Frames"/>; for an MP4 take the encoder loop then finalizes the
    /// file through <see cref="MacNativeMp4Encoder.FinishAsync"/>.</summary>
    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        MacRecordingTake.Close(_take);
        _originChanged.Set();
        _worker?.Join(TimeSpan.FromSeconds(3));
        _queue.Writer.TryComplete();
        if (_native is not null)
        {
            _server.RecordingFailed -= OnHelperRecordingFailed;
            _server.Exited -= OnHelperExited;
        }
    }

    public void Dispose() => Stop();
}

/// <summary>macOS MP4 <see cref="IVideoEncoder"/>: Create starts the recording in the helper
/// (ScreenCaptureKit region stream with cursor -> AVAssetWriter, H.264 + AAC with system audio and
/// microphone mixed), so the encoder thread never receives frames; FinishAsync stops and finalizes
/// the file. Pause is the source's Paused (the helper drops paused samples and closes the gap).</summary>
public sealed class MacNativeMp4Encoder : IVideoEncoder
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);
    // Inside RecordingSession's 5 s encoder-thread join.
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(4);

    private readonly ScksnapServer _server;
    private int _finished;

    public bool HasAudio { get; }

    private MacNativeMp4Encoder(ScksnapServer server, bool hasAudio)
    {
        _server = server;
        HasAudio = hasAudio;
    }

    public static IVideoEncoder Create(string tempFilePath, int width, int height, int fps, bool withAudio, GifSizePreset preset)
    {
        var take = MacRecordingTake.Claim()
            ?? throw new InvalidOperationException("macOS MP4 recording has no scksnap recording source for this take.");
        var server = ScksnapServer.Shared;
        var (left, top) = take.Source.Origin;
        fps = Math.Max(1, fps);
        long bitrate = Mp4BitrateEstimator.ComputeBitrate(width, height, fps, preset);
        byte flags = ScksnapProtocol.RecordCursor;
        if (withAudio && take.Microphone) flags |= ScksnapProtocol.RecordMicrophone;
        if (withAudio && take.SystemAudio) flags |= ScksnapProtocol.RecordSystemAudio;

        var reply = server.Send(ScksnapProtocol.OpStartRecord,
            ScksnapProtocol.EncodeStartRecord(take.Source.DisplayId, left, top, width, height, fps, flags,
                (uint)Math.Min(bitrate, uint.MaxValue), (uint)(Mp4BitrateEstimator.AudioAacBytesPerSecond * 8), tempFilePath),
            StartTimeout);
        ScksnapServer.ThrowIfTccDenied(reply);
        if (reply.Status != ScksnapProtocol.StatusOk || reply.Payload.Length < 1)
        {
            throw new InvalidOperationException($"macOS recording could not start: {reply.Text}");
        }
        byte audio = reply.Payload[0];
        if ((flags & ScksnapProtocol.RecordMicrophone) != 0 && (audio & ScksnapProtocol.AudioMicrophoneActive) == 0)
        {
            FileLog.Write("RoeSnip: microphone unavailable for this take; recording without it");
        }
        server.RecordingActive = true;
        var encoder = new MacNativeMp4Encoder(server, audio != 0);
        take.Source.AttachNative(encoder);
        FileLog.Write($"RoeSnip: macOS MP4 recording {width}x{height} {fps}fps {bitrate / 1000} kbps, " +
            $"mic={(audio & ScksnapProtocol.AudioMicrophoneActive) != 0}, system={(audio & ScksnapProtocol.AudioSystemActive) != 0}");
        return encoder;
    }

    public bool WriteFrame(SdrImage frame, long timestamp100ns) => false; // the helper records the frames

    public void WriteAudioSamples(byte[] pcm, int length, long timestamp100ns) { } // the helper records audio

    public Task FinishAsync(long? endTimestampTicks = null)
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0) return Task.CompletedTask;
        try
        {
            var reply = _server.Send(ScksnapProtocol.OpStop, Array.Empty<byte>(), StopTimeout);
            if (reply.Status != ScksnapProtocol.StatusOk)
            {
                throw new InvalidOperationException($"macOS recording finalize failed: {reply.Text}");
            }
            var summary = ScksnapProtocol.ParseStop(reply.Payload);
            FileLog.Write($"RoeSnip: macOS MP4 finalized: {summary.VideoFrames} frame(s), " +
                $"{summary.DroppedFrames} dropped, {summary.DurationMs / 1000.0:0.0} s");
        }
        finally
        {
            _server.RecordingActive = false;
        }
        return Task.CompletedTask;
    }

    /// <summary>A take abandoned without FinishAsync still stops the helper's recording.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0) return;
        try
        {
            _server.Send(ScksnapProtocol.OpStop, Array.Empty<byte>(), StopTimeout);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or ScksnapUnavailableException)
        {
            FileLog.Write($"RoeSnip: scksnap recording stop on dispose failed: {ex.Message}");
        }
        finally
        {
            _server.RecordingActive = false;
        }
    }
}

/// <summary>macOS <see cref="IAudioCaptureDevice"/>: a marker. The helper captures and mixes the
/// audio into the MP4 itself, so this only records the take's mic/system choice for the MP4
/// factory and never yields chunks.</summary>
public sealed class MacNativeAudioDevice : IAudioCaptureDevice
{
    public static IAudioCaptureDevice? TryStart(bool microphone, bool systemAudio)
    {
        if (!microphone && !systemAudio) return null;
        if (MacRecordingTake.Pending is { } take)
        {
            take.Microphone = microphone;
            take.SystemAudio = systemAudio;
        }
        return new MacNativeAudioDevice();
    }

    public bool TryDequeue(out AudioChunk chunk)
    {
        chunk = default;
        return false;
    }

    public void Stop() { }

    public void Dispose() { }
}

file static class ModuleInit
{
#pragma warning disable CA2255
    [System.Runtime.CompilerServices.ModuleInitializer]
#pragma warning restore CA2255
    internal static void Init()
    {
        RegionCaptureSourceRegistry.Register(
            () => OperatingSystem.IsMacOS(),
            (monitor, selectionPx, targetFps) => MacRegionCaptureSource.Create(monitor, selectionPx, targetFps));

        Mp4VideoEncoderRegistry.Register(
            () => OperatingSystem.IsMacOS(),
            (path, width, height, fps, withAudio, preset) => MacNativeMp4Encoder.Create(path, width, height, fps, withAudio, preset));

        AudioCaptureDeviceRegistry.Register(
            () => OperatingSystem.IsMacOS(),
            (microphone, systemAudio) => MacNativeAudioDevice.TryStart(microphone, systemAudio));

        // Evaluated per recording session: MP4 with audio needs the serve helper; without it macOS
        // degrades to GIF-only through the polled source.
        RecordingCapabilitiesRegistry.Register(
            () => OperatingSystem.IsMacOS(),
            () => ScksnapServer.Shared.EnsureRunning()
                ? new RecordingCapabilities(SupportsMp4: true, SupportsMicrophone: true, SupportsLoopback: true)
                : RecordingCapabilities.None);
    }
}
