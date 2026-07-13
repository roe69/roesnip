using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using RoeSnip.Core.Capture;

namespace RoeSnip.Core.Recording;

/// <summary>Portable (non-Windows) <see cref="IRegionCaptureSource"/>: a background thread polls
/// <see cref="ICaptureBackend.CaptureAll"/> at the <see cref="RecordingSchedule"/> cadence and crops
/// the result to the requested region in managed code. Correct, but pays a full one-shot
/// capture-backend round trip per accepted frame instead of a persistent low-latency GPU session -
/// see <see cref="IRegionCaptureSource"/>'s own class doc comment and docs/PARITY.md item 20's
/// accepted-limitation note. This is the fallback <see cref="RegionCaptureSourceRegistry.Create"/>
/// returns whenever no Platform.* project has registered a native implementation (every OS except
/// Windows today).</summary>
public sealed class PolledRegionCaptureSource : IRegionCaptureSource
{
    private readonly ICaptureBackend _backend;
    private readonly MonitorInfo _monitor;
    private readonly MonitorInfo[] _monitorList; // single-element array reused every poll - avoids an alloc per tick
    private readonly int _regionWidth;
    private readonly int _regionHeight;
    private readonly int _targetFps;
    private long _originPacked; // (left << 32) | (uint)top - Volatile-accessed, same packing as the Windows ring
    private readonly Channel<RegionCaptureFrame> _queue;
    private Thread? _pumpThread;
    private int _disposed;
    private int _started;

    public bool Paused { get; set; }
    public event Action<Exception>? Faulted;

    public PolledRegionCaptureSource(MonitorInfo monitor, RectPhysical selectionPx, int targetFps)
        : this(CaptureBackendRegistry.CreateForCurrentPlatform(), monitor, selectionPx, targetFps)
    {
    }

    /// <summary>Backend-injecting overload for tests - avoids requiring a real Platform.* capture
    /// backend to exercise the polling/crop/throttle/pause logic. Public (this codebase's own
    /// convention: a testable slice becomes a public constructor/class rather than an
    /// InternalsVisibleTo edit - see RoeSnip.Core.Recording.MultiMonitorRecording's own doc
    /// comment).</summary>
    public PolledRegionCaptureSource(ICaptureBackend backend, MonitorInfo monitor, RectPhysical selectionPx, int targetFps)
    {
        _backend = backend;
        _monitor = monitor;
        _monitorList = new[] { monitor };
        _regionWidth = selectionPx.Width;
        _regionHeight = selectionPx.Height;
        _targetFps = targetFps;
        _originPacked = Pack(selectionPx.Left, selectionPx.Top);
        _queue = Channel.CreateBounded<RegionCaptureFrame>(new BoundedChannelOptions(3)
        {
            FullMode = BoundedChannelFullMode.DropOldest, // never block the pump thread
            SingleReader = true,
            SingleWriter = true,
        }, static dropped => dropped.Frame.Dispose());
    }

    private static long Pack(int left, int top) => ((long)left << 32) | (uint)top;

    public ChannelReader<RegionCaptureFrame> Frames => _queue.Reader;

    /// <summary>Same clamp-to-monitor, lock-free packed-write contract as the Windows ring's own
    /// SetOrigin - see that class's doc comment for why a single Volatile write needs no lock.</summary>
    public void SetOrigin(int left, int top)
    {
        int maxL = Math.Max(0, _monitor.BoundsPx.Width - _regionWidth);
        int maxT = Math.Max(0, _monitor.BoundsPx.Height - _regionHeight);
        Volatile.Write(ref _originPacked, Pack(Math.Clamp(left, 0, maxL), Math.Clamp(top, 0, maxT)));
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return; // idempotent, mirrors the Windows ring's own single-Start contract
        }
        _pumpThread = new Thread(PumpLoop)
        {
            IsBackground = true,
            Name = "RoeSnip-PolledRegionCapture",
        };
        _pumpThread.Start();
    }

    private void PumpLoop()
    {
        var schedule = new RecordingSchedule(_targetFps, Stopwatch.Frequency);
        // Poll faster than the target interval so the SCHEDULE (not the poll granularity) decides
        // the effective fps - the same "advance, don't gate on since-last" principle the Windows
        // ring's own OnFrameArrived doc comment explains. A too-coarse poll interval would alias
        // down exactly like a naive since-last-forward gate would.
        int pollIntervalMs = Math.Clamp(250 / Math.Max(1, _targetFps), 2, 50);

        while (Volatile.Read(ref _disposed) == 0)
        {
            try
            {
                if (Paused)
                {
                    Thread.Sleep(pollIntervalMs);
                    continue;
                }

                long now = Stopwatch.GetTimestamp();
                if (!schedule.ShouldAccept(now))
                {
                    Thread.Sleep(pollIntervalMs);
                    continue;
                }

                var frames = _backend.CaptureAll(_monitorList, onlyMonitorIndex: null);
                if (frames.Count == 0)
                {
                    // Backend omitted the monitor this round (transient failure) - try again next
                    // tick, but still yield so a persistently failing backend doesn't spin this
                    // thread at full CPU.
                    Thread.Sleep(pollIntervalMs);
                    continue;
                }
                using var full = frames[0];
                long packed = Volatile.Read(ref _originPacked);
                int left = (int)(packed >> 32), top = (int)(uint)packed;
                var cropped = CropFrame(full, left, top, _regionWidth, _regionHeight);
                if (!_queue.Writer.TryWrite(new RegionCaptureFrame(cropped, now)))
                {
                    cropped.Dispose(); // writer already completed (Stop() raced us) - drop it
                }
            }
            catch (Exception ex)
            {
                Faulted?.Invoke(ex);
                return;
            }
        }
    }

    private static CapturedFrame CropFrame(CapturedFrame src, int left, int top, int width, int height)
    {
        int bpp = src.BytesPerPixel;
        int stride = width * bpp;
        var pixels = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            var srcRow = src.Row(top + y);
            srcRow.Slice(left * bpp, stride).CopyTo(pixels.AsSpan(y * stride, stride));
        }
        return new CapturedFrame(src.Format, width, height, stride, pixels, src.Monitor, src.SdrWhiteInBufferUnits);
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return; // idempotent
        }
        _pumpThread?.Join(TimeSpan.FromSeconds(2));
        _queue.Writer.TryComplete();
    }

    public void Dispose() => Stop();
}
