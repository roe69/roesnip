using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using RoeSnip.Core.Diagnostics;
using RoeSnip.Interop;

namespace RoeSnip.Capture;

/// <summary>Fallback capture path per DESIGN.md: Windows.Graphics.Capture via
/// IGraphicsCaptureItemInterop.CreateForMonitor, using Direct3D11CaptureFramePool.CreateFreeThreaded
/// (NOT plain Create, which needs a DispatcherQueue and throws from CLI/console contexts). Covers
/// RDP and other Desktop-Duplication-denied contexts. The yellow capture border may appear
/// (unpackaged apps lack the graphicsCaptureWithoutBorder capability) — accepted per DESIGN.md,
/// since Desktop Duplication (the primary path) is borderless.
///
/// Pre-provisioning (r5-latency): the expensive parts of a WGC capture — creating the D3D11
/// device + its WinRT wrapper, and resolving the monitor's GraphicsCaptureItem — are cached
/// per monitor (keyed by GDI device name) and reused across captures. Those two ARE reusable:
/// a GraphicsCaptureItem only describes WHAT to capture and stays valid until the display
/// configuration changes, and a D3D11 device is process-lifetime reusable. The
/// GraphicsCaptureSession and Direct3D11CaptureFramePool are deliberately NOT reused — a session
/// that stays alive keeps the capture (and its yellow border) running permanently, which is
/// forbidden — so they are created and disposed inside every capture. Invalidation: a changed
/// HMONITOR or item size for the same device name recreates the resources up front, and ANY
/// capture failure disposes them; if the failed attempt was using cached resources it is retried
/// once against freshly provisioned ones, so a stale item after a monitor-set/mode change costs
/// one silent retry, never a failed screenshot. <see cref="Prewarm"/> lets TrayApp's startup
/// warmup pay the provisioning (and optionally the first session handshake) before the first
/// hotkey press.</summary>
public sealed class WgcCapturer : IScreenCapturer
{
    /// <summary>Per-monitor cache slot. <see cref="Gate"/> is held for the whole capture: it
    /// protects the cached resources' lifecycle AND serializes use of the shared device's
    /// immediate context (ID3D11Device is thread-safe, ID3D11DeviceContext is not) in case the
    /// startup warmup overlaps a real capture of the same monitor. Different monitors use
    /// different slots (and devices), so CaptureService's per-monitor parallelism is unaffected.</summary>
    private sealed class MonitorSlot
    {
        public readonly object Gate = new();
        public MonitorResources? Resources;

        // r5-latency round 2 (D5): the MonitorInfo last used to (re)provision Resources for this
        // slot — written every time Resources is assigned in Capture/Prewarm. The background
        // keepalive timer (KeepaliveTick below) needs a MonitorInfo to re-provision a dead device
        // proactively; without remembering it here, the timer could only ever null out a dead
        // slot's Resources and wait for the next real capture to supply one, defeating the point of
        // catching the failure ahead of time.
        public MonitorInfo? LastMonitor;
    }

    // internal (not private): Recording/RegionRecorder.cs calls CreateResources directly to get its
    // own dedicated device/item, deliberately bypassing the s_slots cache below — see that class's
    // doc comment for why a recording must never share the cached per-monitor slot the background
    // keepalive timer polls. This is a pure accessibility change; CreateResources itself is a pure
    // factory with no cache interaction (only Capture/Prewarm/KeepaliveTick touch s_slots), so
    // widening it costs nothing here and avoids duplicating ~130 lines of fragile CsWinRT/COM
    // interop (the HSTRING/RoGetActivationFactory dance, the FromAbi-then-Release wrapping).
    internal sealed class MonitorResources : IDisposable
    {
        public required GraphicsCaptureItem Item { get; init; }
        public required ID3D11Device D3dDevice { get; init; }
        public required IDirect3DDevice WinrtDevice { get; init; }
        public required nint HMonitor { get; init; }

        // r5-latency round 2 (D5 follow-up): the size Item was created for, cached at provisioning
        // time from the SAME MonitorInfo.BoundsPx CreateResources used to build it — NOT read back
        // via Item.Size. IsReusable used to compare against a live Item.Size read, which turned out
        // to be a genuine WinRT/COM round-trip into the capture stack (not merely a field read) and
        // was still measured costing tens of ms on a trigger after an idle gap on the DD-broken
        // monitor — the exact GPU-wake-adjacent cost D5 set out to remove from the hot path in the
        // first place, just hiding behind a different property than DeviceRemovedReason. Comparing
        // against these cached ints instead makes IsReusable a pure in-memory check with no live
        // interop call anywhere in it. CaptureCore's own resources.Item.Size read (framepool
        // creation) is unaffected — that one is on the real capture-session path, where a live query
        // is expected and necessary.
        public required int Width { get; init; }
        public required int Height { get; init; }

        public void Dispose()
        {
            // Same disposal contract as the old per-capture cleanup (audit finding H): winrtDevice
            // and item are CsWinRT projections whose native references must be released explicitly;
            // IDirect3DDevice implements IDisposable, GraphicsCaptureItem releases via NativeObject.
            try { WinrtDevice.Dispose(); } catch { /* best-effort */ }
            try { D3dDevice.Dispose(); } catch { /* best-effort */ }
            try
            {
                if (Item is WinRT.IWinRTObject winrtItem)
                {
                    winrtItem.NativeObject?.Dispose();
                }
            }
            catch { /* best-effort */ }
        }
    }

    private static readonly ConcurrentDictionary<string, MonitorSlot> s_slots =
        new(StringComparer.OrdinalIgnoreCase);

    // Upper bound on waiting for a slot's Gate before declaring its holder wedged. Far above every
    // legitimate hold (a full capture worst-cases ~2.9 s of internal timeouts; keepalive/prewarm
    // re-provisions are ~100 ms) but well under the flow-level CaptureDeadline, so a retry after a
    // deadline-abandoned capture gets a FRESH slot instead of stacking forever on the wedged one.
    private const int SlotGateTimeoutMs = 6_000;

    /// <summary>Enters the monitor's slot Gate with a bound (wedge fix — see SlotGateTimeoutMs).
    /// If the wait times out, the current slot's holder is wedged in a driver call: the slot is
    /// ORPHANED (conditionally removed from s_slots, so only this exact instance is dropped) and a
    /// fresh slot is created and entered — retries then actually retry with fresh resources instead
    /// of blocking a thread per attempt behind a gate that will never open. The orphaned slot's
    /// resources are deliberately left to its wedged holder (its own catch disposes them if the
    /// driver call ever returns; if not, they leak once — strictly better than a permanent stall).
    /// Caller MUST Monitor.Exit the returned slot's Gate in a finally.</summary>
    private static MonitorSlot AcquireSlotGate(string deviceName)
    {
        var slot = s_slots.GetOrAdd(deviceName, _ => new MonitorSlot());
        if (Monitor.TryEnter(slot.Gate, SlotGateTimeoutMs))
        {
            return slot;
        }

        FileLog.Write(
            $"RoeSnip: WGC slot gate for {deviceName} still held after {SlotGateTimeoutMs} ms - " +
            "orphaning the wedged slot and provisioning a fresh one.");
        ((System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<string, MonitorSlot>>)s_slots)
            .Remove(new System.Collections.Generic.KeyValuePair<string, MonitorSlot>(deviceName, slot));
        var fresh = s_slots.GetOrAdd(deviceName, _ => new MonitorSlot());
        if (!Monitor.TryEnter(fresh.Gate, SlotGateTimeoutMs))
        {
            // Only possible if the replacement wedged just as fast — give up on this capture; the
            // omit-the-monitor contract (CaptureService) handles it.
            throw new CaptureException($"WGC capture stack is wedged for monitor {deviceName}.");
        }
        return fresh;
    }

    public CapturedFrame Capture(MonitorInfo monitor)
    {
        // Latency instrumentation (r5-latency): splits the device-liveness recheck (IsReusable) from
        // the actual capture session. This used to include a DeviceRemovedReason TDR query that a
        // trigger-1-coldness investigation measured costing ~30-90 ms — after an idle gap, not just
        // on the very first call — on an otherwise fully warmed-up cached device; that check has
        // since moved off this hot path entirely onto a background keepalive timer (r5-latency round
        // 2, D5 — see KeepaliveTick below and IsReusable's own doc comment), so deviceCheckMs below
        // should now read ~0 ms on every capture. The split itself is kept as the observability hook
        // for verifying that.
        var deviceCheckWatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var slot = AcquireSlotGate(monitor.DeviceName);
            try
            {
                bool fromCache = IsReusable(slot.Resources, monitor);
                long deviceCheckMs = deviceCheckWatch.ElapsedMilliseconds;
                if (!fromCache)
                {
                    slot.Resources?.Dispose();
                    slot.Resources = null;
                    slot.Resources = CreateResources(monitor);
                    slot.LastMonitor = monitor;
                }
                MonitorResources resources = slot.Resources!;

                try
                {
                    var sessionWatch = System.Diagnostics.Stopwatch.StartNew();
                    var frame = CaptureCore(monitor, resources);
                    FileLog.Write(
                        $"RoeSnip: WGC capture {monitor.DeviceName} deviceCheck={deviceCheckMs}ms " +
                        $"session={sessionWatch.ElapsedMilliseconds}ms");
                    return frame;
                }
                catch (Exception ex)
                {
                    resources.Dispose();
                    slot.Resources = null;
                    if (!fromCache)
                    {
                        throw;
                    }
                    FileLog.Write(
                        $"RoeSnip: WGC capture with cached resources failed for monitor {monitor.DeviceName} " +
                        $"({ex.Message}); re-provisioning and retrying once.");
                }

                slot.Resources = CreateResources(monitor);
                slot.LastMonitor = monitor;
                try
                {
                    return CaptureCore(monitor, slot.Resources);
                }
                catch
                {
                    slot.Resources.Dispose();
                    slot.Resources = null;
                    throw;
                }
            }
            finally
            {
                Monitor.Exit(slot.Gate);
            }
        }
        catch (CaptureException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new CaptureException($"WGC capture failed for monitor {monitor.DeviceName}: {ex.Message}", ex);
        }
    }

    /// <summary>Startup-warmup hook (TrayApp): provisions the cached item/device for the monitor
    /// so the first hotkey press skips that cost. With <paramref name="throwawayFrame"/> it also
    /// performs one full single-frame capture and discards it — that pays the session handshake
    /// (and briefly shows the OS capture border, accepted at tray startup per the r5-latency
    /// brief; the session is disposed immediately, so no border persists).</summary>
    public static void Prewarm(MonitorInfo monitor, bool throwawayFrame)
    {
        if (throwawayFrame)
        {
            new WgcCapturer().Capture(monitor).Dispose();
            return;
        }

        var slot = s_slots.GetOrAdd(monitor.DeviceName, _ => new MonitorSlot());
        lock (slot.Gate)
        {
            if (!IsReusable(slot.Resources, monitor))
            {
                slot.Resources?.Dispose();
                slot.Resources = null;
                slot.Resources = CreateResources(monitor);
                slot.LastMonitor = monitor;
            }
        }
    }

    /// <summary>Idle-memory hook (IdleMemoryTrimmer): asks the driver to release the internal
    /// allocations it has cached against each permanently-warm per-monitor device —
    /// IDXGIDevice3::Trim, the API Windows documents for exactly this "app went idle, give the
    /// driver scratch memory back, keep the device" situation (measured here: the driver retains
    /// on the order of 150-200 MB of process-private allocations after capture sessions). The
    /// device object itself stays cached and warm, so the r5-latency pre-provisioning design is
    /// untouched; the next capture after a trim just lets the driver re-grow its scratch buffers,
    /// a cost that lands on the same first-capture-after-idle path that already pays the GPU wake.
    /// Never blocks: a slot whose Gate is held (capture/keepalive in flight) is skipped — the app
    /// isn't idle for that monitor anyway, and the next trim catches it.</summary>
    public static void TrimCachedDeviceMemory()
    {
        foreach (var slot in s_slots.Values)
        {
            if (!Monitor.TryEnter(slot.Gate))
            {
                continue;
            }
            try
            {
                var device = slot.Resources?.D3dDevice;
                if (device is null)
                {
                    continue;
                }
                using var dxgiDevice = device.QueryInterfaceOrNull<IDXGIDevice3>();
                dxgiDevice?.Trim();
            }
            catch (Exception ex)
            {
                FileLog.Write($"RoeSnip: DXGI device trim failed (non-fatal): {ex.Message}");
            }
            finally
            {
                Monitor.Exit(slot.Gate);
            }
        }
    }

    /// <summary>Sleep/resume hook (TrayApp.OnSystemResumed): drops every cached per-monitor
    /// device/item/framepool outright. After a suspend cycle the cached stack is routinely stale
    /// (DWM restarts, DP links retrain, the GraphicsCaptureItem's monitor association can be dead)
    /// in ways DeviceRemovedReason does NOT report — the first post-wake capture then discovered
    /// the staleness on the hot path by burning its 1200 ms frame wait, re-provisioning, and often
    /// burning a second wait. Invalidating at the RESUME EVENT (and re-prewarming right after — see
    /// the caller) moves that discovery to the moment the machine wakes, while the user is still
    /// looking at the lock screen. Takes each slot's Gate for real (never a TryEnter-skip): a
    /// capture somehow mid-flight must finish before its resources are yanked. Background thread
    /// only — never call on the UI thread, the Gate waits are unbounded by design.</summary>
    public static void InvalidateAll()
    {
        foreach (var entry in s_slots)
        {
            var slot = entry.Value;
            if (Monitor.TryEnter(slot.Gate, SlotGateTimeoutMs))
            {
                try
                {
                    slot.Resources?.Dispose();
                    slot.Resources = null;
                }
                finally
                {
                    Monitor.Exit(slot.Gate);
                }
            }
            else
            {
                // The holder is wedged (post-resume, exactly when wedges happen) — orphan the slot
                // rather than blocking the whole re-warm behind it; see AcquireSlotGate's doc for
                // the orphaning contract.
                FileLog.Write(
                    $"RoeSnip: WGC invalidate: slot gate for {entry.Key} held past {SlotGateTimeoutMs} ms - " +
                    "orphaning the wedged slot.");
                ((System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<string, MonitorSlot>>)s_slots)
                    .Remove(new System.Collections.Generic.KeyValuePair<string, MonitorSlot>(entry.Key, slot));
            }
        }
    }

    /// <summary>Background WGC device-health keepalive (r5-latency round 2, D5). A driver
    /// timeout/reset (TDR) leaves a cached ID3D11Device object alive as a .NET reference but
    /// internally dead; this used to be discovered synchronously on the capture hot path (the
    /// DeviceRemovedReason check IsReusable used to make — see its doc comment), costing a measured
    /// ~30-90 ms on the first capture after an idle gap. Moving that check here, onto a periodic
    /// background timer, means a dead device is found and proactively re-provisioned while the app
    /// is merely sitting in the tray, so the very next real capture never pays for the discovery —
    /// only a genuinely-just-died device (dead within the last KeepaliveIntervalMs) can still reach
    /// the hot path, where the existing cached-resources retry-once path in Capture already handles
    /// it exactly like any other capture failure. Started once, runs for the process's whole
    /// lifetime — this is a static cache with no shutdown hook, so a simple always-running
    /// System.Threading.Timer (thread-pool backed, no dispatcher/UI-thread dependency) is
    /// sufficient; see the class doc for the general "static, process-lifetime cache" pattern this
    /// already follows.</summary>
    private const int KeepaliveIntervalMs = 10_000;

    private static readonly System.Threading.Timer s_keepaliveTimer =
        new(KeepaliveTick, null, KeepaliveIntervalMs, KeepaliveIntervalMs);

    // Guards against overlapping ticks (e.g. a slow device re-provision still running when the next
    // 10-second tick fires) — never lets two ticks touch the same slot's Gate concurrently, and
    // never lets the timer itself pile up work.
    private static int s_keepaliveRunning;

    private static void KeepaliveTick(object? state)
    {
        if (System.Threading.Interlocked.CompareExchange(ref s_keepaliveRunning, 1, 0) != 0)
        {
            return; // a previous tick is still running — skip this one rather than overlap it
        }
        try
        {
            foreach (var slot in s_slots.Values)
            {
                // Same Gate a real capture (or Prewarm) holds for its whole duration (class doc).
                // TryEnter-skip, never a blocking lock (wedge fix): a capture wedged in a driver
                // call holds its Gate indefinitely, and a blocking wait here — with
                // s_keepaliveRunning latched — would permanently disable the keepalive for EVERY
                // monitor. A slot that is mid-capture needs no health check this tick anyway.
                if (!Monitor.TryEnter(slot.Gate))
                {
                    continue;
                }
                try
                {
                    var resources = slot.Resources;
                    if (resources is null)
                    {
                        continue; // nothing provisioned for this monitor yet — nothing to keep alive
                    }

                    bool alive;
                    try
                    {
                        alive = resources.D3dDevice.DeviceRemovedReason.Success;
                    }
                    catch (Exception ex)
                    {
                        FileLog.Write($"RoeSnip: WGC keepalive health check failed (non-fatal): {ex.Message}");
                        continue;
                    }
                    if (alive)
                    {
                        continue;
                    }

                    FileLog.Write(
                        "RoeSnip: WGC keepalive found a dead cached device; re-provisioning proactively.");
                    resources.Dispose();
                    slot.Resources = null;
                    if (slot.LastMonitor is { } monitor)
                    {
                        try
                        {
                            slot.Resources = CreateResources(monitor);
                        }
                        catch (Exception ex)
                        {
                            FileLog.Write(
                                $"RoeSnip: WGC keepalive re-provisioning failed (non-fatal; the next real " +
                                $"capture will retry): {ex.Message}");
                        }
                    }
                }
                finally
                {
                    Monitor.Exit(slot.Gate);
                }
            }
        }
        finally
        {
            System.Threading.Volatile.Write(ref s_keepaliveRunning, 0);
        }
    }

    private static bool IsReusable(MonitorResources? resources, MonitorInfo monitor) =>
        resources is not null
        && resources.HMonitor == monitor.HMonitor
        && resources.Width == monitor.BoundsPx.Width
        && resources.Height == monitor.BoundsPx.Height;
        // NVIDIA/TDR health check (originally r5-latency S8; moved off this hot path by r5-latency
        // round 2's D5): a driver timeout/reset (TDR — e.g. a GPU driver crash, hang, or a
        // long-running compute workload elsewhere on the machine) leaves a cached ID3D11Device
        // object alive as a .NET reference but internally dead — every real call on it fails. This
        // method used to also check resources.D3dDevice.DeviceRemovedReason.Success right here,
        // which caught that case up front but was measured costing ~30-90 ms on the first capture
        // after an idle gap — a real cost paid on the hot path for a rare failure mode. That check
        // now runs in the background instead (see KeepaliveTick above), so this method TRUSTS the
        // cached resources unconditionally once the cheap identity/size checks above pass. A device
        // that died more recently than the keepalive's last tick still fails fast inside CaptureCore
        // (framepool creation throws against a removed device), which Capture's existing
        // cached-resources retry-once path already recovers from exactly like any other capture
        // failure — so correctness is unchanged, only WHERE the common case's health check runs.

    // internal (not private): see the MonitorResources accessibility note above — RegionRecorder
    // calls this directly for a private, never-cached device/item pair.
    internal static MonitorResources CreateResources(MonitorInfo monitor)
    {
        GraphicsCaptureItem item = CreateItemForMonitor(monitor.HMonitor);
        ID3D11Device? d3dDevice = null;
        IDirect3DDevice? winrtDevice = null;
        try
        {
            FeatureLevel[] levels =
            {
                FeatureLevel.Level_11_1, FeatureLevel.Level_11_0,
                FeatureLevel.Level_10_1, FeatureLevel.Level_10_0,
            };
            d3dDevice = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels);
            using var dxgiDevice = d3dDevice.QueryInterface<IDXGIDevice>();

            // Deliberately NOT using Vortice's generic CreateDirect3D11DeviceFromDXGIDevice<T> helper
            // here: it returns a classic .NET COM RCW (System.__ComObject), and CsWinRT's projection
            // marshaler cannot build a CCW for that object when it's later passed into
            // Direct3D11CaptureFramePool.CreateFreeThreaded ("Failed to create a CCW ... the
            // specified cast is not valid"). Call the native factory directly and wrap the raw ABI
            // pointer with WinRT.MarshalInterface<T>.FromAbi instead, exactly like CreateItemForMonitor
            // does below, so the resulting object is a proper CsWinRT-projected IDirect3DDevice.
            // FromAbi AddRefs internally (verified empirically: refcount 1 -> 3) and does NOT
            // consume the caller's reference, so we must Release the raw pointer we own after
            // wrapping — otherwise the device leaks once per (re)provisioning.
            CreateDirect3D11DeviceFromDXGIDeviceNative(dxgiDevice.NativePointer, out IntPtr winrtDevicePtr);
            try
            {
                winrtDevice = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(winrtDevicePtr);
            }
            finally
            {
                Marshal.Release(winrtDevicePtr);
            }

            return new MonitorResources
            {
                Item = item,
                D3dDevice = d3dDevice,
                WinrtDevice = winrtDevice,
                HMonitor = monitor.HMonitor,
                Width = monitor.BoundsPx.Width,
                Height = monitor.BoundsPx.Height,
            };
        }
        catch
        {
            winrtDevice?.Dispose();
            d3dDevice?.Dispose();
            if (item is WinRT.IWinRTObject winrtItem)
            {
                winrtItem.NativeObject?.Dispose();
            }
            throw;
        }
    }

    /// <summary>Bound on the FrameArrived wait (r5-latency, S8). A healthy WGC session delivers its
    /// first frame within a frame or two — well under 100 ms — even capturing a completely static
    /// screen; a wait this long stalling means the session is stale or wedged (e.g. a TDR'd device
    /// IsReusable's health check above didn't catch, or a monitor that just went to sleep), not that
    /// a real frame is merely "running a little late". Failing fast here lets Capture's own
    /// cached-resources retry (one re-provision + retry) or CaptureService's per-monitor
    /// omit-the-monitor contract take over, instead of hanging the hotkey. The old TimeSpan.FromSeconds(5)
    /// meant the worst case — a cached-resources attempt that times out, then its retry against
    /// freshly provisioned resources ALSO timing out — was up to 2x5s = ~10s frozen on the hotkey
    /// path.</summary>
    private const int FrameWaitMs = 1200;

    private static CapturedFrame CaptureCore(MonitorInfo monitor, MonitorResources resources)
    {
        Direct3D11CaptureFramePool? framePool = null;
        GraphicsCaptureSession? session = null;
        try
        {
            // WGC doesn't expose a "delivered format" the way Desktop Duplication does — request
            // FP16 for advanced-color displays, BGRA8 otherwise (DESIGN.md's one legitimate use of
            // AdvancedColorActive for branching).
            DirectXPixelFormat pixelFormat = monitor.AdvancedColorActive
                ? DirectXPixelFormat.R16G16B16A16Float
                : DirectXPixelFormat.B8G8R8A8UIntNormalized;

            framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                resources.WinrtDevice, pixelFormat, 1, resources.Item.Size);
            session = framePool.CreateCaptureSession(resources.Item);
            session.IsCursorCaptureEnabled = false;
            try { session.IsBorderRequired = false; }
            catch { /* property may not exist on older Windows builds, or capability denied — accepted. */ }

            using var frameReady = new ManualResetEventSlim(false);
            Direct3D11CaptureFrame? capturedFrame = null;
            Exception? callbackError = null;

            void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
            {
                try
                {
                    capturedFrame = sender.TryGetNextFrame();
                }
                catch (Exception ex)
                {
                    callbackError = ex;
                }
                finally
                {
                    frameReady.Set();
                }
            }

            framePool.FrameArrived += OnFrameArrived;
            try
            {
                session.StartCapture();
                if (!frameReady.Wait(FrameWaitMs))
                {
                    throw new CaptureException($"WGC frame wait timed out for monitor {monitor.DeviceName}.");
                }
            }
            finally
            {
                framePool.FrameArrived -= OnFrameArrived;
            }

            if (callbackError is not null)
            {
                throw new CaptureException($"WGC FrameArrived callback failed for monitor {monitor.DeviceName}: {callbackError.Message}", callbackError);
            }
            if (capturedFrame is null)
            {
                throw new CaptureException($"WGC delivered no frame for monitor {monitor.DeviceName}.");
            }

            using (capturedFrame)
            {
                using var surfaceTexture = GetTextureForSurface(capturedFrame.Surface);
                var srcDesc = surfaceTexture.Description;

                var stagingDesc = new Texture2DDescription
                {
                    Width = srcDesc.Width,
                    Height = srcDesc.Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = srcDesc.Format,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    BindFlags = BindFlags.None,
                    CPUAccessFlags = CpuAccessFlags.Read,
                    MiscFlags = ResourceOptionFlags.None,
                };
                using var staging = resources.D3dDevice.CreateTexture2D(stagingDesc);
                resources.D3dDevice.ImmediateContext.CopyResource(staging, surfaceTexture);

                var mapped = resources.D3dDevice.ImmediateContext.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                byte[] pixels;
                try
                {
                    int stride = (int)mapped.RowPitch;
                    pixels = new byte[stride * (int)srcDesc.Height];
                    Marshal.Copy(mapped.DataPointer, pixels, 0, pixels.Length);
                }
                finally
                {
                    resources.D3dDevice.ImmediateContext.Unmap(staging, 0);
                }

                FrameFormat format = srcDesc.Format == Format.R16G16B16A16_Float
                    ? FrameFormat.Fp16ScRgb
                    : FrameFormat.Bgra8Srgb;

                return new CapturedFrame(format, (int)srcDesc.Width, (int)srcDesc.Height, (int)mapped.RowPitch, pixels, monitor);
            }
        }
        finally
        {
            // Session/pool are strictly per-capture (see class doc: keeping a session alive would
            // keep the yellow border up permanently). The cached item/device are NOT disposed here
            // — their lifecycle is owned by the MonitorSlot in Capture/Prewarm.
            session?.Dispose();
            framePool?.Dispose();
        }
    }

    // internal (not private): RegionRecorder's own FrameArrived handler needs this same
    // CsWinRT-interop-to-classic-COM bridge for its own captured frames — deliberately widened
    // rather than duplicated (unlike the ~15-line accessibility candidates elsewhere in this file,
    // this one has a subtle ownership contract in its doc comment below that's easy to get wrong a
    // second time).
    internal static ID3D11Texture2D GetTextureForSurface(IDirect3DSurface surface)
    {
        // surface is a CsWinRT-projected object, not a classic COM RCW — a direct cast to our
        // ComImport interface fails ("Invalid cast from WinRT.IInspectable"). Query the interface
        // through CsWinRT's own IWinRTObject/IObjectReference machinery instead, then hand the
        // resulting raw pointer to classic COM interop for the actual GetInterface call.
        var winrtObj = (WinRT.IWinRTObject)surface;
        using var accessRef = winrtObj.NativeObject.As(typeof(IDirect3DDxgiInterfaceAccess).GUID);
        var access = (IDirect3DDxgiInterfaceAccess)Marshal.GetTypedObjectForIUnknown(
            accessRef.ThisPtr, typeof(IDirect3DDxgiInterfaceAccess));

        // GetInterface returns an AddRef'd pointer that we own. SharpGen's ComObject(IntPtr)
        // constructor ATTACHES to the pointer (no AddRef of its own) and releases it on Dispose —
        // so ownership transfers to the wrapper here and there must be no Marshal.Release of our
        // own (an extra Release over-frees a texture the WinRT surface still references, which
        // crashes later in WinRT.IObjectReference.Finalize once the GC runs).
        IntPtr texturePtr = access.GetInterface(typeof(ID3D11Texture2D).GUID);
        return new ID3D11Texture2D(texturePtr);
    }

    private static GraphicsCaptureItem CreateItemForMonitor(nint hmonitor)
    {
        // .NET Core's P/Invoke marshaler does not support UnmanagedType.HString directly (that
        // relies on the old .NET Framework WinRT-metadata interop path) — build the HSTRING by
        // hand via WindowsCreateString/WindowsDeleteString instead, which is what CsWinRT itself
        // does internally.
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        WindowsCreateString(className, (uint)className.Length, out IntPtr classNameHandle);
        try
        {
            Guid interopIid = typeof(NativeMethods.IGraphicsCaptureItemInterop).GUID;
            RoGetActivationFactory(classNameHandle, ref interopIid, out IntPtr factoryPtr);
            try
            {
                var interop = (NativeMethods.IGraphicsCaptureItemInterop)Marshal.GetTypedObjectForIUnknown(
                    factoryPtr, typeof(NativeMethods.IGraphicsCaptureItemInterop));
                Guid itemIid = ResolveGraphicsCaptureItemIid();
                // FromAbi AddRefs internally and does NOT consume this reference — Release the
                // pointer we own after wrapping (same semantics as the device wrap above).
                IntPtr itemPtr = interop.CreateForMonitor(hmonitor, ref itemIid);
                try
                {
                    return WinRT.MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPtr);
                }
                finally
                {
                    Marshal.Release(itemPtr);
                }
            }
            finally
            {
                Marshal.Release(factoryPtr);
            }
        }
        finally
        {
            WindowsDeleteString(classNameHandle);
        }
    }

    /// <summary>The default-interface IID of GraphicsCaptureItem (IGraphicsCaptureItem), resolved
    /// from the projection assembly at runtime; falls back to the well-known published GUID if the
    /// internal ABI type can't be found (projection implementation detail, per PLAN.md flag on
    /// IGraphicsCaptureItemInterop's stability).</summary>
    private static Guid ResolveGraphicsCaptureItemIid()
    {
        var t = typeof(GraphicsCaptureItem).Assembly.GetType("Windows.Graphics.Capture.IGraphicsCaptureItem");
        return t?.GUID ?? new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    }

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", PreserveSig = false)]
    private static extern void CreateDirect3D11DeviceFromDXGIDeviceNative(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("combase.dll", PreserveSig = false)]
    private static extern void RoGetActivationFactory(
        IntPtr activatableClassId,
        [In] ref Guid iid,
        out IntPtr factory);

    [DllImport("combase.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void WindowsCreateString(
        string sourceString, uint length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] Guid iid);
    }
}
