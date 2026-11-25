using System.Diagnostics;
using System.Threading.Channels;
using RoeSnip.Core.Capture;
using RoeSnip.Core.Diagnostics;
using RoeSnip.Core.Recording;

namespace RoeSnip.Platform.Linux;

/// <summary>Wayland recording source: crops frames straight out of a screencast stream on the
/// <see cref="RecordingSchedule"/> cadence, emitting only when the stream delivered a new frame
/// (same as WGC, which only fires on change). Uses the already-allowed
/// <see cref="ScreenCastSession.Snapshot"/> session, so a recording never opens a second share
/// dialog. Its pixels have no cursor, so the sprite from the session's cursor metadata
/// (<see cref="PipeWireCursorReader"/>) is drawn into each frame; a cursor-only move emits a frame
/// too. Without cursor metadata recordings have no cursor.</summary>
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
    private IScreenCastStream? _stream;

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
        var live = ScreenCastSession.Snapshot.Acquire(OpenWait, out string reason);
        if (live is null) return reason;
        var geometry = live.StreamFor(_monitor, monitors);
        if (geometry is null) return $"no stream for monitor {_monitor.DeviceName}";
        var stream = live.GetStream(geometry.Value.NodeId);
        if (stream is null || !stream.WaitForFirstSample(TimeSpan.FromSeconds(2)))
        {
            return $"stream node {geometry.Value.NodeId} delivered no frame";
        }
        _stream = stream;
        _live = live;
        _nodeId = geometry.Value.NodeId;
        FileLog.Write($"RoeSnip: recording from the snapshot screencast, node {geometry.Value.NodeId}, " +
            $"region {_width}x{_height} on {_monitor.DeviceName}, " +
            (live.Cursor is not null ? "cursor from screencast metadata" : "without cursor (no screencast cursor metadata)"));
        return null;
    }

    private ScreenCastSession.Live? _live;
    private uint _nodeId;

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
        PipeWireCursorReader.State? lastCursor = null;
        try
        {
            while (Volatile.Read(ref _stopped) == 0)
            {
                if (stream.IsDead)
                {
                    throw new InvalidOperationException("screencast stream stopped: " + stream.DeadReason);
                }
                long packed = Volatile.Read(ref _originPacked);
                var cursor = _live?.Cursor?.Get(_nodeId);
                if (Paused || (stream.Sequence == lastSequence && packed == lastPacked && ReferenceEquals(cursor, lastCursor)))
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
                lastCursor = cursor;
                if (cursor is { Valid: true, Sprite: { } sprite })
                {
                    var (mx, my) = ScreenCastCursor.ToMonitorPx(cursor, _monitor);
                    CursorCompositor.Draw(pixels, _width, _height, sprite, mx - left, my - top);
                }
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
