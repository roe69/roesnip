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
using RoeSnip.Core.Diagnostics;

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
/// after <see cref="Start"/>.
///
/// GPU readback is a 3-slot RING (<see cref="_stagingRing"/>), not a single reused staging
/// texture: the original single-texture version did CopySubresourceRegion immediately followed by
/// Map(MapMode.Read) on that SAME texture, every accepted frame (up to the target fps, e.g. 50/s).
/// Map on a staging resource blocks the calling thread until the GPU has actually finished
/// whatever was still in flight against that resource — i.e. that pair was a full CPU/GPU pipeline
/// SYNC STALL, up to 50 times a second, on the process's own D3D device. That doesn't just cost
/// this thread time; a stalled ImmediateContext call can hold up the GPU command queue broadly
/// enough to visibly stutter OTHER apps' rendering (games, video) system-wide while a recording is
/// running — the "PC feels bad while recording" symptom this ring fixes. The ring breaks the
/// stall by never mapping the slot it JUST copied into: it copies into slot N and maps slot N-1
/// (the one copied a full callback ago), which by now has almost certainly finished on the GPU
/// asynchronously, so the Map call returns immediately instead of blocking. See OnFrameArrived's
/// own comment for the exact indexing.</summary>
internal sealed class RegionRecorder : IDisposable
{
    private readonly WgcCapturer.MonitorResources _resources; // via WgcCapturer.CreateResources — never the s_slots cache
    private readonly MonitorInfo _monitor;
    // SIZE is fixed for the whole take (see SetOrigin's own doc for why only position can move).
    // NOT guaranteed even: RecordingController.RoundDownToEven floors the single-recorder/whole-
    // selection case, but multi-monitor recording phase 2's spanning callers (StartSpanningRecorders/
    // RebuildSpanningRecorders, via SpanningCanvasCompositor.BuildSlots) pass a PER-MONITOR crop of
    // whatever width a seam happens to split an even selection into (e.g. an even 1000px selection
    // straddling a seam can split 333+667, both odd) — nothing downstream of this field currently
    // relies on evenness (no SIMD 2-pixel-stride readback, no NV12-adjacent math on the crop), but a
    // future change that assumes it would work in single-monitor mode and only misbehave on an odd
    // spanning split.
    private readonly int _regionWidth;
    private readonly int _regionHeight;
    private long _originPacked;                                 // (left << 32) | (uint)top — Volatile-accessed, see SetOrigin
    private readonly int _targetFps;
    private readonly long _minIntervalTicks;
    private readonly object _gpuLock = new();                  // serializes THIS recorder's own ImmediateContext use only
    private readonly Channel<QueuedFrame> _queue;

    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;

    // Staging ring (kills the sync GPU stall — see the class doc comment above). Sized to the
    // SELECTION, not the monitor; all three textures share the one format decided the first time
    // any of them is created (WGC's frame format never changes mid-session — DisplaySettingsChanged
    // stops the recording rather than reconfiguring it, see RecordingSession's own handler).
    private const int StagingRingSize = 3;
    private readonly ID3D11Texture2D?[] _stagingRing = new ID3D11Texture2D?[StagingRingSize];
    // QPC "now" captured at the moment each slot's CopySubresourceRegion was ISSUED (same value
    // the old single-texture code stamped a QueuedFrame with immediately after its Map) — carried
    // alongside the slot so the frame this ring eventually emits keeps its true CAPTURE timestamp,
    // not the (up to one callback later) time it was actually read back.
    private readonly long[] _stagingTimestamps = new long[StagingRingSize];
    private Format? _stagingFormat;                             // set once, when the ring textures are first created
    private int _ringWriteIndex;                                // next slot CopySubresourceRegion writes into
    private long _copyCount;                                    // total copies issued this session; copy #0 has no prior slot to read (see OnFrameArrived)
    private long _nextDueTimestamp;                            // Stopwatch ticks — schedule-based throttle, see OnFrameArrived
    private int _disposed;

    /// <summary>Set/cleared by RecordingSession.Pause()/Resume() on the UI thread. While true,
    /// OnFrameArrived drops every incoming frame before any GPU readback — no CopySubresourceRegion/
    /// Map/memcpy work, and nothing enters <see cref="Frames"/> during a pause.</summary>
    public volatile bool Paused;

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
        _regionWidth = selectionPx.Width;
        _regionHeight = selectionPx.Height;
        _originPacked = Pack(selectionPx.Left, selectionPx.Top);
        _targetFps = targetFps;
        _minIntervalTicks = System.Diagnostics.Stopwatch.Frequency / targetFps;
        _queue = Channel.CreateBounded<QueuedFrame>(new BoundedChannelOptions(3)
        {
            FullMode = BoundedChannelFullMode.DropOldest, // never block the WGC callback thread
            SingleReader = true,
            SingleWriter = true,
        }, static dropped => dropped.Frame.Dispose()); // DropOldest discards silently otherwise — pooled buffers must go back
    }

    private static long Pack(int left, int top) => ((long)left << 32) | (uint)top;

    public ChannelReader<QueuedFrame> Frames => _queue.Reader;

    /// <summary>Slides the crop origin while recording (the user dragged the region). Only the
    /// position changes - the width/height, staging texture, and encoder dimensions stay fixed - so
    /// no pipeline rebuild is needed. LOCK-FREE on purpose: this runs on the UI thread inside the
    /// drag's WM_MOUSEMOVE, and taking _gpuLock here parked the UI thread behind a whole in-flight
    /// CopySubresourceRegion+Map+copy readback (several ms per frame, up to 50x/s while dragging).
    /// A single packed volatile write can't tear and needs no lock. Clamped to the monitor so the
    /// CopySubresourceRegion box can never exceed the captured surface.</summary>
    public void SetOrigin(int left, int top)
    {
        int maxL = Math.Max(0, _monitor.BoundsPx.Width - _regionWidth);
        int maxT = Math.Max(0, _monitor.BoundsPx.Height - _regionHeight);
        Volatile.Write(ref _originPacked, Pack(Math.Clamp(left, 0, maxL), Math.Clamp(top, 0, maxT)));
    }

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

            if (Paused)
            {
                return; // paused take — no GPU readback, no frame enters the queue
            }

            using var frame = sender.TryGetNextFrame();
            if (frame is null)
            {
                return;
            }

            // Schedule-based throttle, NOT a since-last-forward gate: WGC delivers on the monitor's
            // refresh cadence (16.7ms at 60Hz), and requiring a full _minIntervalTicks since the
            // LAST forwarded frame beats against that cadence — a 50fps target then skips every
            // other 60Hz frame and records 30fps. Advancing a due-time by the interval per forward
            // (clamped so a quiet stretch can't bank an unbounded burst) hits the target rate on
            // average regardless of the source cadence.
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

                // Issue THIS frame's copy into the NEXT ring slot — GPU-async, no Map here. The
                // slot actually read back below (if any) is the PREVIOUS one: it was copied a
                // whole callback ago, giving the GPU roughly a monitor frame's worth of wall-clock
                // time to finish that earlier copy, so its Map call doesn't block (see the class
                // doc comment for why that's the whole point of this ring).
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
                // process outright. Hand the failure to the owner (RecordingSession) instead, which
                // stops the recording cleanly with whatever was already captured.
                Faulted?.Invoke(ex);
            }
        }
    }

    /// <summary>On-demand fallback for a monitor that delivered its very first WGC frame (copy #0)
    /// and then nothing else. WGC always delivers that first frame quickly even for a fully static
    /// screen (see WgcCapturer.CaptureCore's own "well under 100 ms, even capturing a completely
    /// static screen" doc comment), but <see cref="OnFrameArrived"/> deliberately withholds READING
    /// it back until a SECOND callback arrives (see the class doc's ring-stall rationale) — a second
    /// callback a genuinely unchanging monitor may never actually deliver. Left alone, that monitor's
    /// crop would never enter <see cref="Frames"/> at all until <see cref="Stop"/>'s own end-of-take
    /// ring drain, i.e. it contributes nothing to a live spanning composite for the take's ENTIRE
    /// duration (RecordingController.EncoderLoopSpanning starts writing encoded frames as soon as ANY
    /// OTHER monitor composites, onto a canvas this monitor's sub-rect would otherwise never touch).
    ///
    /// Callable any time after <see cref="Start"/>; safe to call from the encoder thread (or any
    /// thread) since it takes <see cref="_gpuLock"/>, the same lock <see cref="OnFrameArrived"/> and
    /// <see cref="Stop"/> already serialize all GPU-touching work behind, so this can never race a
    /// real callback's own readback of the same slot. No-op (returns false) once a second callback
    /// has actually landed (<see cref="_copyCount"/> &gt;= 2 — the normal path already read slot 0
    /// back at that point) or before the first callback has landed at all (<see cref="_copyCount"/>
    /// == 0 — nothing captured yet). Copy #0 always lands in ring slot 0 (a fresh recorder's
    /// <see cref="_ringWriteIndex"/> starts at 0), so there is no ambiguity about which slot to read.
    ///
    /// Deliberately NOT self-throttling or repeat-safe beyond the count check above: the CALLER
    /// (RecordingController.EncoderLoopSpanning) is responsible for invoking this AT MOST ONCE per
    /// recorder instance — see that loop's own per-slot bookkeeping. Calling it every tick for a
    /// recorder stuck at one copy forever would re-Map and re-enqueue the same unchanged pixels
    /// forever, which is exactly the per-frame hot-path work this codebase's own LOH/Gen2 lesson
    /// forbids; this method itself has no memory of "already tried" because that bookkeeping already
    /// exists one layer up, index-aligned with the rest of that loop's per-slot state.</summary>
    public bool TryFlushPendingInitialFrame()
    {
        lock (_gpuLock)
        {
            if (Volatile.Read(ref _disposed) != 0 || _copyCount != 1 || _stagingFormat is not { } format)
            {
                return false; // disposed, nothing captured yet, or the normal path already handled it
            }
            try
            {
                ReadRingSlot(0, format, _resources.D3dDevice.ImmediateContext);
                return true;
            }
            catch (Exception ex)
            {
                // Best-effort: worst case this monitor's canvas sub-rect just stays at whatever it
                // was for a bit longer, same as before this method was ever called — not worth
                // failing the whole take over a stop-time-style GPU hiccup here either.
                FileLog.Write($"RoeSnip: initial-frame flush failed (non-fatal): {ex.Message}");
                return false;
            }
        }
    }

    // Called from OnFrameArrived (WGC callback thread) and from Stop()'s drain (whichever thread
    // calls Stop) — both callers hold _gpuLock, so this needs no locking of its own. Maps
    // _stagingRing[slot], copies it into a pooled buffer, and enqueues it with that slot's own
    // remembered CAPTURE timestamp (not "now" — the copy that filled this slot happened up to one
    // callback ago, see the class doc comment).
    private void ReadRingSlot(int slot, Format format, ID3D11DeviceContext context)
    {
        var texture = _stagingRing[slot]!;
        var mapped = context.Map(texture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            int w = _regionWidth;
            int h = _regionHeight;
            int stride = (int)mapped.RowPitch;
            // Pooled: a region-sized array is LOH-sized, and a fresh one per accepted frame
            // at up to 50/s is exactly the Gen2-pause churn f7aa9a3 was about. The rented
            // array may be longer than byteCount — CapturedFrame consumers index via Row().
            int byteCount = stride * h;
            var pixels = System.Buffers.ArrayPool<byte>.Shared.Rent(byteCount);
            Marshal.Copy(mapped.DataPointer, pixels, 0, byteCount);

            FrameFormat frameFormat = format == Format.R16G16B16A16_Float
                ? FrameFormat.Fp16ScRgb
                : FrameFormat.Bgra8Srgb;

            // _monitor (not a monitor-sized rect) passed through unchanged — CapturedFrame's
            // own Width/Height/Stride below are authoritative for indexing this cropped
            // buffer; Monitor is only ever consulted for its photometric fields
            // (SdrWhiteNits/MaxLuminanceNits) by the tone-mapper.
            var cropped = new CapturedFrame(frameFormat, w, h, stride, pixels, _monitor, pooledBuffer: true);
            if (!_queue.Writer.TryWrite(new QueuedFrame(cropped, _stagingTimestamps[slot])))
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
            // Ring drain: OnFrameArrived deliberately never reads back the slot it JUST copied
            // into — that slot is only read on the NEXT callback (see the class doc comment). With
            // no next callback coming, the single most recent copy would otherwise be silently
            // discarded, dropping the take's very last frame. Read it here instead, under the same
            // _gpuLock that already serializes this against any in-flight OnFrameArrived, before
            // the session/pool/textures are torn down below.
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
                    FileLog.Write($"RoeSnip: recording ring drain on stop failed (non-fatal): {ex.Message}");
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

internal readonly record struct QueuedFrame(CapturedFrame Frame, long TimestampTicks);
