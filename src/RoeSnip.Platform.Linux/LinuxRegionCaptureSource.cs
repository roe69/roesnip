using System.Diagnostics;
using System.Threading.Channels;
using RoeSnip.Core.Capture;
using RoeSnip.Core.Diagnostics;
using RoeSnip.Core.Recording;

namespace RoeSnip.Platform.Linux;

/// <summary>Wayland recording source: crops frames straight out of a screencast stream on the
/// <see cref="RecordingSchedule"/> cadence, emitting only when the stream delivered a new frame
/// (same as WGC, which only fires on change). Uses the cursor-embedded
/// <see cref="ScreenCastSession.Recording"/> session (closed again at Stop) and falls back to the
/// cursorless snapshot session when that one cannot open.</summary>
public sealed class LinuxRegionCaptureSource : IRegionCaptureSource
{
    private static readonly TimeSpan OpenWait = TimeSpan.FromSeconds(10);

    private readonly MonitorInfo _monitor;
    private readonly int _width;
    private readonly int _height;
    private readonly int _targetFps;
    private readonly Channel<RegionCaptureFrame> _queue;
    private long _originPacked;
    private Thread? _pump;
    private int _started;
    private int _stopped;
    private ScreenCastStream? _stream;

    public bool Paused { get; set; }
    public event Action<Exception>? Faulted;
    public ChannelReader<RegionCaptureFrame> Frames => _queue.Reader;

    private LinuxRegionCaptureSource(MonitorInfo monitor, RectPhysical selectionPx, int targetFps)
    {
        _monitor = monitor;
        _width = selectionPx.Width;
        _height = selectionPx.Height;
        _targetFps = targetFps;
        _originPacked = Pack(selectionPx.Left, selectionPx.Top);
        _queue = Channel.CreateBounded<RegionCaptureFrame>(new BoundedChannelOptions(3)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        }, static dropped => dropped.Frame.Dispose());
    }

    /// <summary>A screencast-backed source, or the portable polled source when no stream for the
    /// monitor can be opened (the polled one then goes through the capture backend).</summary>
    public static IRegionCaptureSource Create(MonitorInfo monitor, RectPhysical selectionPx, int targetFps)
    {
        var source = new LinuxRegionCaptureSource(monitor, selectionPx, targetFps);
        string? why = source.TryAttach();
        if (why is null) return source;
        FileLog.Write($"RoeSnip: screencast recording source unavailable ({why}); using the polled capture source");
        return new PolledRegionCaptureSource(monitor, selectionPx, targetFps);
    }

    private string? TryAttach()
    {
        var monitors = X11Capturer.EnumerateMonitorsViaRandR();
        string? lastWhy = null;
        foreach (var session in new[] { ScreenCastSession.Recording, ScreenCastSession.Snapshot })
        {
            var live = session.Acquire(OpenWait, out string reason);
            if (live is null) { lastWhy = reason; continue; }
            var geometry = live.StreamFor(_monitor, monitors);
            if (geometry is null) { lastWhy = $"no stream for monitor {_monitor.DeviceName}"; continue; }
            var stream = live.GetStream(geometry.Value.NodeId);
            if (stream is null || !stream.WaitForFirstSample(TimeSpan.FromSeconds(2)))
            {
                lastWhy = $"stream node {geometry.Value.NodeId} delivered no frame";
                continue;
            }
            _stream = stream;
            FileLog.Write($"RoeSnip: recording from the {(ReferenceEquals(session, ScreenCastSession.Recording) ? "recording (cursor)" : "snapshot (no cursor)")} " +
                $"screencast, node {geometry.Value.NodeId}, region {_width}x{_height} on {_monitor.DeviceName}");
            return null;
        }
        ScreenCastSession.Recording.Close();
        return lastWhy ?? "no screencast session";
    }

    private static long Pack(int left, int top) => ((long)left << 32) | (uint)top;

    public void SetOrigin(int left, int top)
    {
        int maxL = Math.Max(0, _monitor.BoundsPx.Width - _width);
        int maxT = Math.Max(0, _monitor.BoundsPx.Height - _height);
        Volatile.Write(ref _originPacked, Pack(Math.Clamp(left, 0, maxL), Math.Clamp(top, 0, maxT)));
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _pump = new Thread(PumpLoop) { IsBackground = true, Name = "RoeSnip-ScreenCastRegionCapture" };
        _pump.Start();
    }

    private void PumpLoop()
    {
        var stream = _stream!;
        var schedule = new RecordingSchedule(_targetFps, Stopwatch.Frequency);
        int pollMs = Math.Clamp(250 / Math.Max(1, _targetFps), 2, 50);
        long lastSequence = -1;
        long lastPacked = long.MinValue;
        try
        {
            while (Volatile.Read(ref _stopped) == 0)
            {
                if (stream.IsDead)
                {
                    throw new InvalidOperationException("screencast stream stopped: " + stream.DeadReason);
                }
                long packed = Volatile.Read(ref _originPacked);
                if (Paused || (stream.Sequence == lastSequence && packed == lastPacked))
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

                int left = (int)(packed >> 32), top = (int)(uint)packed;
                var pixels = new byte[_width * _height * 4];
                if (!stream.TryCopy(left, top, _width, _height, _monitor.BoundsPx.Width, _monitor.BoundsPx.Height,
                        pixels, out long sequence, out _))
                {
                    Thread.Sleep(pollMs);
                    continue;
                }
                lastSequence = sequence;
                lastPacked = packed;
                var frame = new CapturedFrame(FrameFormat.Bgra8Srgb, _width, _height, _width * 4, pixels,
                    _monitor, sdrWhiteInBufferUnits: 1.0);
                if (!_queue.Writer.TryWrite(new RegionCaptureFrame(frame, now)))
                {
                    frame.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Faulted?.Invoke(ex);
        }
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        _pump?.Join(TimeSpan.FromSeconds(2));
        _queue.Writer.TryComplete();
        // Also when this take fell back to the snapshot session: a late answer to the share
        // dialog may have opened the recording session after all.
        ScreenCastSession.Recording.Close();
    }

    public void Dispose() => Stop();
}

file static class ModuleInit
{
#pragma warning disable CA2255
    [System.Runtime.CompilerServices.ModuleInitializer]
#pragma warning restore CA2255
    internal static void Init() => RegionCaptureSourceRegistry.Register(
        () => OperatingSystem.IsLinux() && ScreenCastSession.IsWaylandSession,
        (monitor, selectionPx, targetFps) => LinuxRegionCaptureSource.Create(monitor, selectionPx, targetFps));
}
