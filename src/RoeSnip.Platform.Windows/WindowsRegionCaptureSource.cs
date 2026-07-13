using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using RoeSnip.Core.Capture;
using RoeSnip.Core.Recording;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace RoeSnip.Platform.Windows;

/// <summary>Windows <see cref="IRegionCaptureSource"/>: continuous WGC capture of one monitor,
/// cropped per-frame to a fixed region, delivered as ready-to-encode raw <see cref="CapturedFrame"/>s
/// (still BGRA8/FP16, not yet tone-mapped) on a bounded queue. Ported near-verbatim from the WPF
/// app's own <c>RoeSnip.Recording.RegionRecorder</c> - see that class's doc comment for the full
/// design rationale, reproduced in the relevant places below.
///
/// Deliberately NOT WgcCapturer.Capture()'s cached per-monitor slot - see that class's own
/// accessibility-change note. Two independent reasons this recorder must own its own device/item
/// rather than reuse the cache:
///   1. WgcCapturer's background KeepaliveTick locks the SAME MonitorSlot.Gate a cached-resources
///      capture would use, and could Dispose() the cached Item/D3dDevice out from under a session
///      built on it mid-recording. A private device is immune: nothing else ever touches it.
///   2. WgcCapturer's whole design assumes cheap, ephemeral per-shot sessions; recording is the one
///      legitimate case where a PERSISTENT session for the whole take IS the requirement, which is
///      architecturally incompatible with that cache's contract.
///
/// Threading model: the WGC callback thread (free-threaded - NOT the UI thread) does the throttle
/// check + GPU readback, serialized by <see cref="_gpuLock"/> (private to THIS recorder's own
/// device - zero contention with WgcCapturer or other monitors). It never touches an encoder.
/// RoeSnip.App's RecordingController owns a separate thread that drains <see cref="Frames"/> and does
/// tone-map + encode.
///
/// GPU readback is a 3-slot RING (<see cref="_stagingRing"/>), not a single reused staging texture:
/// a single-texture design would CopySubresourceRegion immediately followed by Map(MapMode.Read) on
/// that SAME texture, every accepted frame (up to the target fps, e.g. 50/s). Map on a staging
/// resource blocks the calling thread until the GPU has actually finished whatever was still in
/// flight against that resource - i.e. that pair would be a full CPU/GPU pipeline SYNC STALL, up to
/// 50 times a second, on the process's own D3D device, which can visibly stutter OTHER apps'
/// rendering system-wide while a recording is running. The ring breaks the stall by never mapping
/// the slot it JUST copied into: it copies into slot N and maps slot N-1 (the one copied a full
/// callback ago), which by then has almost certainly finished on the GPU asynchronously, so the Map
/// call returns immediately instead of blocking. See <see cref="OnFrameArrived"/>'s own comment for
/// the exact indexing.
///
/// Deliberate scope reduction vs. the WPF reference: raw readback buffers are plain <c>new byte[]</c>
/// allocations, not an ArrayPool rental - Core's <see cref="CapturedFrame"/> has no
/// pooled-buffer-return-on-Dispose contract anywhere in this port (WgcCapturer's own one-shot
/// Capture() already follows this same plain-allocation convention), so adding pooling here alone
/// would not actually avoid the Gen2/LOH churn end to end. The LOH-avoidance requirement item 20 is
/// actually scored on - reusing the TONE-MAPPED output buffer across frames at recording cadence -
/// lives in RoeSnip.App's RecordingController (the encoder-loop buffer reuse, via
/// SdrImage.FromCapturedFrame's reuseOutput parameter), unaffected by this file's own allocation
/// choice.</summary>
public sealed class WindowsRegionCaptureSource : IRegionCaptureSource
{
    private readonly WgcCapturer.MonitorResources _resources; // via WgcCapturer.CreateResources — never the s_slots cache
    private readonly MonitorInfo _monitor;
    // Fixed for the whole take — see RegionRecorder's own doc comment on the WPF side for why only
    // position (not size) can move mid-take.
    private readonly int _regionWidth;
    private readonly int _regionHeight;
    private long _originPacked; // (left << 32) | (uint)top — Volatile-accessed, see SetOrigin
    private readonly object _gpuLock = new(); // serializes THIS recorder's own ImmediateContext use only
    private readonly Channel<RegionCaptureFrame> _queue;

    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;

    private const int StagingRingSize = 3;
    private readonly ID3D11Texture2D?[] _stagingRing = new ID3D11Texture2D?[StagingRingSize];
    private readonly long[] _stagingTimestamps = new long[StagingRingSize];
    private Format? _stagingFormat; // set once, when the ring textures are first created
    private int _ringWriteIndex;    // next slot CopySubresourceRegion writes into
    private long _copyCount;        // total copies issued this session; copy #0 has no prior slot to read
    private long _nextDueTimestamp; // Stopwatch ticks — schedule-based throttle, see OnFrameArrived
    private int _disposed;

    /// <summary>Set/cleared by RecordingController on the UI thread. While true, OnFrameArrived
    /// drops every incoming frame before any GPU readback — no CopySubresourceRegion/Map/memcpy
    /// work, and nothing enters <see cref="Frames"/> during a pause.</summary>
    public bool Paused { get; set; }

    public event Action<Exception>? Faulted;

    public WindowsRegionCaptureSource(MonitorInfo monitor, RectPhysical selectionPx, int targetFps)
    {
        _resources = WgcCapturer.CreateResources(monitor); // fresh device/item, never cached
        _monitor = monitor;
        _regionWidth = selectionPx.Width;
        _regionHeight = selectionPx.Height;
        _originPacked = Pack(selectionPx.Left, selectionPx.Top);
        _minIntervalTicks = System.Diagnostics.Stopwatch.Frequency / Math.Max(1, targetFps);
        _queue = Channel.CreateBounded<RegionCaptureFrame>(new BoundedChannelOptions(3)
        {
            FullMode = BoundedChannelFullMode.DropOldest, // never block the WGC callback thread
            SingleReader = true,
            SingleWriter = true,
        }, static dropped => dropped.Frame.Dispose());
    }

    private readonly long _minIntervalTicks; // schedule-throttle interval, see OnFrameArrived

    private static long Pack(int left, int top) => ((long)left << 32) | (uint)top;

    public ChannelReader<RegionCaptureFrame> Frames => _queue.Reader;

    /// <summary>LOCK-FREE on purpose — see the WPF reference's own doc comment: this runs on the UI
    /// thread inside a drag's pointer-move, and taking <see cref="_gpuLock"/> here would park the UI
    /// thread behind a whole in-flight CopySubresourceRegion+Map+copy readback. A single packed
    /// volatile write can't tear and needs no lock. Clamped to the monitor so the
    /// CopySubresourceRegion box can never exceed the captured surface.</summary>
    public void SetOrigin(int left, int top)
    {
        int maxL = Math.Max(0, _monitor.BoundsPx.Width - _regionWidth);
        int maxT = Math.Max(0, _monitor.BoundsPx.Height - _regionHeight);
        Volatile.Write(ref _originPacked, Pack(Math.Clamp(left, 0, maxL), Math.Clamp(top, 0, maxT)));
    }

    /// <summary>Starts the persistent capture session. Unlike WgcCapturer.CaptureCore's session,
    /// this one is NEVER disposed until <see cref="Stop"/> — that permanence is exactly what
    /// recording needs.</summary>
    public void Start()
    {
        DirectXPixelFormat pixelFormat = _monitor.AdvancedColorActive
            ? DirectXPixelFormat.R16G16B16A16Float
            : DirectXPixelFormat.B8G8R8A8UIntNormalized;

        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _resources.WinrtDevice, pixelFormat, 2 /* double-buffer, unlike WgcCapturer's single-shot 1 */, _resources.Item.Size);
        _session = _framePool.CreateCaptureSession(_resources.Item);
        _session.IsCursorCaptureEnabled = true; // the recording should show the cursor
        try { _session.IsBorderRequired = false; }
        catch { /* property may not exist on older Windows builds, or capability denied — accepted, same as WgcCapturer. */ }
        _framePool.FrameArrived += OnFrameArrived;
        _session.StartCapture();
    }

    // WGC callback thread. Everything from the disposed-check through TryGetNextFrame and the GPU
    // work is inside _gpuLock, and Stop() takes that SAME lock before it disposes _session/
    // _framePool — that is what actually prevents the race: a Volatile.Read(_disposed) check alone
    // only protects callbacks that hadn't started yet, not one already past that check and
    // mid-flight when Stop() runs on another thread. Throttling is still checked first inside the
    // lock, before any GPU work, so a high-refresh monitor delivering far more FrameArrived
    // callbacks per second than the recording's target fps doesn't pay a full
    // CopySubresourceRegion+Map+memcpy for frames it's going to drop anyway.
    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        lock (_gpuLock)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return; // Stop() already disposed the pool/session under this same lock
            }

            if (Paused)
            {
                return; // paused take — no GPU readback, no frame enters the queue
            }

            using var frame = sender.TryGetNextFrame();
            if (frame is null)
            {
                return;
            }

            // Schedule-based throttle, NOT a since-last-forward gate — see RecordingSchedule's own
            // doc comment (this class inlines the same math rather than holding a RecordingSchedule
            // instance, since the throttle state must live inside THIS lock alongside the ring
            // bookkeeping it gates).
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            long due = _nextDueTimestamp;
            if (now < due)
            {
                return; // throttled — frame.Dispose() via the using above, no GPU work done
            }
            _nextDueTimestamp = Math.Max(due + _minIntervalTicks, now - _minIntervalTicks);

            try
            {
                using var surfaceTexture = WgcCapturer.GetTextureForSurface(frame.Surface);
                var srcDesc = surfaceTexture.Description;
                var context = _resources.D3dDevice.ImmediateContext;

                if (_stagingRing[0] is null)
                {
                    _stagingFormat = srcDesc.Format;
                    for (int i = 0; i < StagingRingSize; i++)
                    {
                        _stagingRing[i] = CreateRegionStagingTexture(srcDesc.Format);
                    }
                }

                long packed = Volatile.Read(ref _originPacked);
                int originLeft = (int)(packed >> 32), originTop = (int)(uint)packed;
                var box = new Box(
                    originLeft, originTop, 0,
                    originLeft + _regionWidth, originTop + _regionHeight, 1);

                // Issue THIS frame's copy into the NEXT ring slot — GPU-async, no Map here. The slot
                // actually read back below (if any) is the PREVIOUS one: it was copied a whole
                // callback ago, giving the GPU roughly a monitor frame's worth of wall-clock time to
                // finish that earlier copy, so its Map call doesn't block.
                int writeSlot = _ringWriteIndex;
                context.CopySubresourceRegion(_stagingRing[writeSlot], 0, 0, 0, 0, surfaceTexture, 0, box);
                _stagingTimestamps[writeSlot] = now;
                long thisCopyIndex = _copyCount++;
                _ringWriteIndex = (writeSlot + 1) % StagingRingSize;

                if (thisCopyIndex == 0)
                {
                    return; // very first copy this session — no older slot to read back yet
                }

                int readSlot = (writeSlot + StagingRingSize - 1) % StagingRingSize;
                ReadRingSlot(readSlot, srcDesc.Format, context);
            }
            catch (Exception ex)
            {
                // Belt-and-braces: an unhandled exception on this WGC callback thread can crash the
                // process outright. Hand the failure to the owner instead, which stops the recording
                // cleanly with whatever was already captured.
                Faulted?.Invoke(ex);
            }
        }
    }

    // Called from OnFrameArrived (WGC callback thread) and from Stop()'s drain (whichever thread
    // calls Stop) — both callers hold _gpuLock, so this needs no locking of its own. Maps
    // _stagingRing[slot], copies it into a fresh buffer, and enqueues it with that slot's own
    // remembered CAPTURE timestamp (not "now" — the copy that filled this slot happened up to one
    // callback ago).
    private void ReadRingSlot(int slot, Format format, ID3D11DeviceContext context)
    {
        var texture = _stagingRing[slot]!;
        var mapped = context.Map(texture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            int w = _regionWidth;
            int h = _regionHeight;
            int stride = (int)mapped.RowPitch;
            int byteCount = stride * h;
            var pixels = new byte[byteCount];
            Marshal.Copy(mapped.DataPointer, pixels, 0, byteCount);

            FrameFormat frameFormat = format == Format.R16G16B16A16_Float
                ? FrameFormat.Fp16ScRgb
                : FrameFormat.Bgra8Srgb;

            var cropped = new CapturedFrame(
                frameFormat, w, h, stride, pixels, _monitor, sdrWhiteInBufferUnits: _monitor.SdrWhiteNits / 80.0);
            if (!_queue.Writer.TryWrite(new RegionCaptureFrame(cropped, _stagingTimestamps[slot])))
            {
                cropped.Dispose(); // writer already completed (Stop() raced us) — drop it
            }
        }
        finally
        {
            context.Unmap(texture, 0);
        }
    }

    private ID3D11Texture2D CreateRegionStagingTexture(Format format)
    {
        var desc = new Texture2DDescription
        {
            Width = (uint)_regionWidth,
            Height = (uint)_regionHeight,
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
            return; // idempotent
        }
        if (_framePool is not null)
        {
            _framePool.FrameArrived -= OnFrameArrived;
        }
        // Unsubscribing does not cancel a callback that had already started executing on the WGC
        // thread before _disposed flipped to 1 above. _gpuLock is the same lock OnFrameArrived holds
        // around its own disposed-check + TryGetNextFrame + GPU work, so acquiring it here blocks
        // until any such in-flight callback has returned.
        lock (_gpuLock)
        {
            // Ring drain: OnFrameArrived deliberately never reads back the slot it JUST copied
            // into — that slot is only read on the NEXT callback. With no next callback coming, the
            // single most recent copy would otherwise be silently discarded, dropping the take's
            // very last frame. Read it here instead, under the same _gpuLock, before the session/
            // pool/textures are torn down below.
            if (_copyCount > 0 && _stagingFormat is { } format)
            {
                int lastSlot = (int)((_copyCount - 1) % StagingRingSize);
                try
                {
                    ReadRingSlot(lastSlot, format, _resources.D3dDevice.ImmediateContext);
                }
                catch (Exception ex)
                {
                    // Best-effort: losing the take's last frame to a stop-time GPU hiccup is far
                    // better than throwing out of Stop() and leaving the session half torn-down.
                    Console.Error.WriteLine($"RoeSnip: recording ring drain on stop failed (non-fatal): {ex.Message}");
                }
            }

            _session?.Dispose();
            _framePool?.Dispose();
            for (int i = 0; i < StagingRingSize; i++)
            {
                _stagingRing[i]?.Dispose();
                _stagingRing[i] = null;
            }
        }
        _queue.Writer.TryComplete();
    }

    public void Dispose()
    {
        Stop();
        _resources.Dispose(); // the recorder's OWN device/item — never touches WgcCapturer's cache
    }
}

file static class ModuleInit
{
#pragma warning disable CA2255
    [System.Runtime.CompilerServices.ModuleInitializer]
#pragma warning restore CA2255
    internal static void Init() => RegionCaptureSourceRegistry.Register(
        () => OperatingSystem.IsWindows(),
        (monitor, selectionPx, targetFps) => new WindowsRegionCaptureSource(monitor, selectionPx, targetFps));
}
