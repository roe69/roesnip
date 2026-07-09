using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using RoeSnip.Capture;

namespace RoeSnip.Recording;

/// <summary>Continuous WGC capture of one monitor, cropped per-frame to a fixed region, delivered
/// as ready-to-encode raw <see cref="CapturedFrame"/>s (still BGRA8/FP16, not yet tone-mapped — the
/// encoder thread owns that step, see RecordingSession) on a bounded queue.
///
/// Deliberately NOT WgcCapturer.Capture()'s cached per-monitor slot (see WgcCapturer.CreateResources's
/// own accessibility-change note). Two independent reasons this recorder must own its own
/// device/item rather than reuse the cache:
///   1. WgcCapturer's background KeepaliveTick (10s interval) locks the SAME MonitorSlot.Gate a
///      cached-resources capture would use, and — on a false-positive DeviceRemovedReason race, or
///      simply a monitor unplug during recording — can Dispose() the cached Item/D3dDevice out from
///      under a session built on it, crashing WriteSample/CopySubresourceRegion mid-recording on a
///      disposed COM object. A private device is immune: nothing else ever touches it.
///   2. WgcCapturer's whole design assumes cheap, ephemeral per-shot sessions ("keeping a session
///      alive would keep the yellow border up permanently... forbidden" — WgcCapturer class doc).
///      Recording is the one legitimate case where a PERSISTENT session for the whole recording IS
///      the requirement, which is architecturally incompatible with that cache's contract.
///
/// Threading model: the WGC callback thread (free-threaded — NOT the UI thread, NOT guaranteed to
/// be the same thread across calls) does the throttle check + GPU readback, serialized by
/// <see cref="_gpuLock"/> (private to THIS recorder's own device — zero contention with WgcCapturer
/// or other monitors). It never touches an encoder. A separate thread (owned by RecordingSession)
/// drains <see cref="Frames"/> and does tone-map + encode. The UI thread never touches this class
/// after <see cref="Start"/>.</summary>
internal sealed class RegionRecorder : IDisposable
{
    private readonly WgcCapturer.MonitorResources _resources; // via WgcCapturer.CreateResources — never the s_slots cache
    private readonly MonitorInfo _monitor;
    private readonly RectPhysical _selectionPx;                // even-WxH already enforced by the caller (RecordingController)
    private readonly int _targetFps;
    private readonly long _minIntervalTicks;
    private readonly object _gpuLock = new();                  // serializes THIS recorder's own ImmediateContext use only
    private readonly Channel<QueuedFrame> _queue;

    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private ID3D11Texture2D? _regionStaging;                   // sized to the SELECTION, not the monitor — reused every frame
    private long _lastForwardedTimestamp;                      // Stopwatch ticks — throttle gate, Volatile-accessed
    private int _disposed;

    /// <summary>Raised (at most once) if the GPU readback throws mid-recording (TDR, monitor
    /// unplug) — WGC's FrameArrived runs on a COM/threadpool thread, so an unhandled exception
    /// there could crash the process outright; the recorder catches it instead and hands the
    /// failure to whoever owns the session so it can stop cleanly with whatever was captured so
    /// far. Raised at most once — a faulted recorder stops trying.</summary>
    public event Action<Exception>? Faulted;

    public RegionRecorder(MonitorInfo monitor, RectPhysical selectionPx, int targetFps)
    {
        _resources = WgcCapturer.CreateResources(monitor); // fresh device/item, never cached
        _monitor = monitor;
        _selectionPx = selectionPx;
        _targetFps = targetFps;
        _minIntervalTicks = System.Diagnostics.Stopwatch.Frequency / targetFps;
        _queue = Channel.CreateBounded<QueuedFrame>(new BoundedChannelOptions(3)
        {
            FullMode = BoundedChannelFullMode.DropOldest, // never block the WGC callback thread
            SingleReader = true,
            SingleWriter = true,
        });
    }

    public ChannelReader<QueuedFrame> Frames => _queue.Reader;

    /// <summary>Starts the persistent capture session. Unlike WgcCapturer.CaptureCore's session,
    /// this one is NEVER disposed until <see cref="Stop"/> — that permanence is exactly what
    /// recording needs (WgcCapturer's own ephemeral-session design is why it can't be reused here).</summary>
    public void Start()
    {
        DirectXPixelFormat pixelFormat = _monitor.AdvancedColorActive
            ? DirectXPixelFormat.R16G16B16A16Float
            : DirectXPixelFormat.B8G8R8A8UIntNormalized;

        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _resources.WinrtDevice, pixelFormat, 2 /* double-buffer, unlike WgcCapturer's single-shot 1 */, _resources.Item.Size);
        _session = _framePool.CreateCaptureSession(_resources.Item);
        _session.IsCursorCaptureEnabled = true; // per design — the recording should show the cursor
        try { _session.IsBorderRequired = false; }
        catch { /* property may not exist on older Windows builds, or capability denied — accepted, same as WgcCapturer. */ }
        _framePool.FrameArrived += OnFrameArrived;
        _session.StartCapture();
    }

    // WGC callback thread. Everything from the disposed-check through TryGetNextFrame and the GPU
    // work is inside _gpuLock, and Stop() takes that SAME lock before it disposes _session/
    // _framePool (see Stop's own comment) — that is what actually prevents the race: a
    // Volatile.Read(_disposed) check alone only protects callbacks that hadn't started yet, not one
    // already past that check and mid-flight when Stop() runs on another thread. Throttling is
    // still checked first inside the lock, before any GPU work, so a high-refresh monitor delivering
    // far more FrameArrived callbacks per second than the recording's target fps doesn't pay a full
    // CopySubresourceRegion+Map+memcpy for frames it's going to drop anyway.
    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        lock (_gpuLock)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return; // Stop() already disposed the pool/session under this same lock
            }

            using var frame = sender.TryGetNextFrame();
            if (frame is null)
            {
                return;
            }

            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now - Volatile.Read(ref _lastForwardedTimestamp) < _minIntervalTicks)
            {
                return; // throttled — frame.Dispose() via the using above, no GPU work done
            }
            Volatile.Write(ref _lastForwardedTimestamp, now);

            try
            {
                using var surfaceTexture = WgcCapturer.GetTextureForSurface(frame.Surface);
                var srcDesc = surfaceTexture.Description;

                _regionStaging ??= CreateRegionStagingTexture(srcDesc.Format);

                var box = new Box(
                    _selectionPx.Left, _selectionPx.Top, 0,
                    _selectionPx.Right, _selectionPx.Bottom, 1);
                _resources.D3dDevice.ImmediateContext.CopySubresourceRegion(
                    _regionStaging, 0, 0, 0, 0, surfaceTexture, 0, box);

                var mapped = _resources.D3dDevice.ImmediateContext.Map(_regionStaging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    int w = _selectionPx.Width;
                    int h = _selectionPx.Height;
                    int stride = (int)mapped.RowPitch;
                    var pixels = new byte[stride * h];
                    Marshal.Copy(mapped.DataPointer, pixels, 0, pixels.Length);

                    FrameFormat format = srcDesc.Format == Format.R16G16B16A16_Float
                        ? FrameFormat.Fp16ScRgb
                        : FrameFormat.Bgra8Srgb;

                    // _monitor (not a monitor-sized rect) passed through unchanged — CapturedFrame's
                    // own Width/Height/Stride below are authoritative for indexing this cropped
                    // buffer; Monitor is only ever consulted for its photometric fields
                    // (SdrWhiteNits/MaxLuminanceNits) by the tone-mapper.
                    var cropped = new CapturedFrame(format, w, h, stride, pixels, _monitor);
                    if (!_queue.Writer.TryWrite(new QueuedFrame(cropped, now)))
                    {
                        cropped.Dispose(); // writer already completed (Stop() raced us) — drop it
                    }
                }
                finally
                {
                    _resources.D3dDevice.ImmediateContext.Unmap(_regionStaging, 0);
                }
            }
            catch (Exception ex)
            {
                // Belt-and-braces: an unhandled exception on this WGC callback thread can crash the
                // process outright. Hand the failure to the owner (RecordingSession) instead, which
                // stops the recording cleanly with whatever was already captured.
                Faulted?.Invoke(ex);
            }
        }
    }

    private ID3D11Texture2D CreateRegionStagingTexture(Format format)
    {
        var desc = new Texture2DDescription
        {
            Width = (uint)_selectionPx.Width,
            Height = (uint)_selectionPx.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        };
        return _resources.D3dDevice.CreateTexture2D(desc);
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return; // idempotent — Stop()/Dispose() may both call this
        }
        if (_framePool is not null)
        {
            _framePool.FrameArrived -= OnFrameArrived;
        }
        // Unsubscribing does not cancel a callback that had already started executing on the WGC
        // thread before _disposed flipped to 1 above. _gpuLock is the same lock OnFrameArrived holds
        // around its own disposed-check + TryGetNextFrame + GPU work, so acquiring it here blocks
        // until any such in-flight callback has returned — only then is it safe to Dispose() the
        // session/frame pool/staging texture it may still be using.
        lock (_gpuLock)
        {
            _session?.Dispose();
            _framePool?.Dispose();
            _regionStaging?.Dispose();
            _regionStaging = null;
        }
        _queue.Writer.TryComplete();
    }

    public void Dispose()
    {
        Stop();
        _resources.Dispose(); // the recorder's OWN device/item — never touches WgcCapturer's cache
    }
}

internal readonly record struct QueuedFrame(CapturedFrame Frame, long TimestampTicks);
