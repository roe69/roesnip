using System;
using System.Threading;
using Avalonia.Threading;
using RoeSnip.App.Overlay;
using RoeSnip.Core.Diagnostics;

namespace RoeSnip.App;

/// <summary>Returns burst memory to the OS once the app is back to its resident-tray idle state.
/// Ported from the WPF app's src/RoeSnip/IdleMemoryTrimmer.cs (item 17) — same mechanics, same
/// blocking-collect trade-off, retargeted onto Avalonia's Dispatcher and this port's own
/// OverlayController/CaptureGate busy signals.
///
/// Why this exists: RoeSnip's allocation profile is short, huge bursts (a multi-monitor HDR snip
/// is tens of MB of FP16 CapturedFrame buffers + SDR previews, all LOH-sized) followed by hours of
/// near-zero allocation. Workstation GC only collects on allocation pressure, so after a burst the
/// dead LOH segments stay committed indefinitely — the process ratchets up to its high-water mark
/// and idles there. One Aggressive collect at the moment the app returns to idle sweeps the
/// burst's garbage, compacts the LOH, and decommits the segments back to the OS.
///
/// Latency contract (see the WPF reference's own note): this must never run on the hot path.
/// Every call site schedules it at DispatcherPriority.ApplicationIdle — Avalonia exposes this
/// priority directly (Avalonia.Threading.DispatcherPriority.ApplicationIdle), same as WPF's — so
/// the trim always dequeues only when the UI thread genuinely has nothing left to do.
///
/// Accepted residual trade-off (kept deliberately, same as the WPF reference): a blocking collect
/// suspends every managed thread, so a hotkey press landing in the brief window while a trim is
/// mid-collect waits it out — the busy-checks below stop a trim from STARTING during a flow,
/// nothing can stop a flow from ARRIVING during a trim. That window only exists for a moment right
/// after a snip/warmup finishes and is the price of not idling at a high-water mark.</summary>
internal static class IdleMemoryTrimmer
{
    // Coalescing flag: several flows finishing back-to-back must not queue several full
    // collections. Interlocked (not a plain bool, unlike the WPF reference) because, unlike WPF's
    // call sites, this port's own StartWarmup calls Schedule() from the background warmup thread
    // rather than always from the UI thread — Dispatcher.UIThread.Post is itself thread-safe, so
    // the only thing that actually needed hardening here was this flag.
    private static int s_scheduled;

    /// <summary>Defers a trim to ApplicationIdle. Safe to call from any thread — every real call
    /// site in this port does (RunCaptureFlowAsync's finally, which may resume on a
    /// threadpool continuation, and TrayApp's background warmup thread).</summary>
    public static void Schedule()
    {
        if (Interlocked.CompareExchange(ref s_scheduled, 1, 0) != 0)
        {
            return;
        }
        Dispatcher.UIThread.Post(RunIfIdle, DispatcherPriority.ApplicationIdle);
    }

    private static void RunIfIdle()
    {
        Interlocked.Exchange(ref s_scheduled, 0);
        TrimNow();
    }

    /// <summary>Runs the collection immediately if the app is genuinely idle; a busy state (live
    /// overlay session or an in-flight capture) just skips — whichever flow is running schedules a
    /// fresh trim when IT finishes, so nothing is lost by not rescheduling here. Once a Recording
    /// subsystem lands (item 20) this must also check its own "active" flag, mirroring the WPF
    /// reference's RecordingController.IsActive check (see AppComposition.IsCaptureBusy's own
    /// doc comment for the same caveat).</summary>
    private static void TrimNow()
    {
        if (OverlayController.IsSessionActive || AppShell.CaptureGate.IsBusy)
        {
            return;
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        // Aggressive = full blocking compacting Gen2 + LOH compaction + decommit unused segments
        // back to the OS — exactly the "give the memory back, we won't need it for hours" hint a
        // resident tray app wants after a burst.
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        // The burst's biggest NATIVE cost hides behind tiny managed shells: every frozen preview
        // bitmap holds backing memory that only a FINALIZER releases. Run the finalizers, then
        // sweep what they just released.
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        // Managed side is now minimal — the other resident chunk is driver-internal scratch memory
        // cached against the permanently-warm per-monitor D3D11 devices (Windows only). Hand that
        // back too; a no-op on platforms with no such cache.
        TrimPlatformCaptureCache();
        FileLog.Write(
            $"RoeSnip: idle memory trim {watch.ElapsedMilliseconds} ms " +
            $"(managed heap now {GC.GetTotalMemory(false) / (1024 * 1024)} MB)");
    }

    /// <summary>Platform trim hook (item 17): releases WgcCapturer's cached per-monitor D3D device
    /// scratch memory on Windows (see WgcCapturer.TrimCachedDeviceMemory's own doc comment) without
    /// dropping the warm device itself. No-op on Linux/macOS — DesktopPortalCaptureBackend and
    /// MacCaptureBackend hold no comparable permanently-warm GPU cache to trim.</summary>
    private static void TrimPlatformCaptureCache()
    {
#if WINDOWS
        try
        {
            RoeSnip.Platform.Windows.WgcCapturer.TrimCachedDeviceMemory();
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: platform capture cache trim failed (non-fatal): {ex.Message}");
        }
#endif
    }
}
