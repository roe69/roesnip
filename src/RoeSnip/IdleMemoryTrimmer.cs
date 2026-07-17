using System;
using System.Windows.Threading;
using RoeSnip.Core.Diagnostics;

namespace RoeSnip;

/// <summary>Returns burst memory to the OS once the app is back to its resident-tray idle state.
///
/// Why this exists: RoeSnip's allocation profile is short, huge bursts (a 3-monitor HDR snip is
/// ~84 MB of FP16 CapturedFrame buffers + ~42 MB of SDR previews, all LOH-sized; a 60 s GIF
/// recording can peak at 1-1.5 GB — see GifEncoder) followed by hours of near-zero allocation.
/// Workstation GC only collects on allocation pressure, so after a burst the dead LOH segments
/// stay committed indefinitely — the process ratchets up to its high-water mark and idles there
/// (observed: ~1 GB private bytes after two days of tray residence). One Aggressive collect at
/// the moment the app returns to idle sweeps the burst's garbage, compacts the LOH, and decommits
/// the segments back to the OS.
///
/// Latency contract (see DESIGN/PLAN latency notes): this must never run on the hot path. Every
/// call site schedules it at DispatcherPriority.ApplicationIdle — one step BELOW the ContextIdle
/// priority OverlayWindowPool.ScheduleReprovision uses — so the trim always dequeues after the
/// next trigger's pool has been rebuilt and only when the UI thread genuinely has nothing to do.
/// Freeing the burst memory does not regress the warm 51-60 ms first-overlay-visible budget: the
/// capture path allocates fresh byte[]s per snip either way (there is no buffer pooling), and the
/// verified latency numbers were measured on fresh launches, i.e. with nothing pre-committed.
///
/// Accepted residual trade-off (review-flagged, kept deliberately): a blocking collect suspends
/// every managed thread, so a hotkey press landing in the brief window while a trim is mid-collect
/// waits it out — the busy-checks below stop a trim from STARTING during a flow, nothing can stop
/// a flow from ARRIVING during a trim. That window only exists for a moment right after a
/// snip/recording/warmup finishes, delays one dim by the collect's duration (tens of ms at
/// steady-state heap sizes) at worst, and is the price of not idling at a 1 GB high-water mark.</summary>
internal static class IdleMemoryTrimmer
{
    // Coalescing flag, same pattern/reasoning as OverlayWindowPool.s_reprovisionScheduled: several
    // flows finishing back-to-back must not queue several full collections. UI thread only.
    private static bool s_scheduled;

    /// <summary>Defers a <see cref="TrimNow"/> to ApplicationIdle. Call from the UI thread once a
    /// capture flow, pick-mode capture, or recording has fully finished (gate released).</summary>
    public static void Schedule(Dispatcher dispatcher)
    {
        if (s_scheduled)
        {
            return;
        }
        s_scheduled = true;
        try
        {
            dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                s_scheduled = false;
                TrimNow();
            }));
        }
        catch
        {
            // BeginInvoke can throw synchronously on a shutting-down dispatcher (see
            // ScheduleReprovision's identical guard) — don't wedge the coalescing flag.
            s_scheduled = false;
            throw;
        }
    }

    // One background trim at a time (UI-thread s_scheduled coalescing can't see the background
    // hop's lifetime): a second sweep arriving while one is mid-collect is dropped — the running
    // one is already doing the work.
    private static int s_trimRunning;

    /// <summary>Checks idleness on the UI thread, then runs the collection on a BACKGROUND thread;
    /// a busy state (live overlay session, in-flight capture, active recording) just skips —
    /// whichever flow is running schedules a fresh trim when IT finishes, so nothing is lost by not
    /// rescheduling here.
    ///
    /// Off the UI thread (post-sleep stuck-UI fix): GC.WaitForPendingFinalizers is UNBOUNDED — a
    /// finalizer wedged on a dead post-resume D3D/WIC COM object used to hang the whole pump (tray
    /// menu stuck open, hotkey dead) from a sweep that dequeued at ApplicationIdle, including idle
    /// moments inside the tray menu's modal tracking loop. The blocking collects themselves still
    /// suspend all managed threads briefly (process-wide, unavoidable from any thread — the class
    /// doc's accepted trade-off), but a wedged finalizer now strands only this throwaway thread.</summary>
    private static void TrimNow()
    {
        if (Overlay.OverlayController.IsSessionActive
            || Recording.RecordingController.IsActive
            || CaptureGate.IsBusy)
        {
            return;
        }
        if (System.Threading.Interlocked.CompareExchange(ref s_trimRunning, 1, 0) != 0)
        {
            return;
        }

        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                // Aggressive = full blocking compacting Gen2 + LOH compaction + decommit unused
                // segments back to the OS — exactly the "give the memory back, we won't need it for
                // hours" hint a resident tray app wants after a burst.
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                // The burst's biggest NATIVE cost hides behind tiny managed shells: every frozen
                // BitmapSource preview holds ~15 MB of WIC memory that only a FINALIZER releases
                // (measured: private bytes kept climbing ~50-160 MB/snip with the managed heap
                // already at 2 MB before this pass existed). Run the finalizers, then sweep what
                // they just released.
                GC.WaitForPendingFinalizers();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                // Managed side is now minimal — the other resident chunk is driver-internal scratch
                // memory cached against the permanently-warm per-monitor D3D11 devices. Hand that
                // back too.
                Capture.WgcCapturer.TrimCachedDeviceMemory();
                FileLog.Write(
                    $"RoeSnip: idle memory trim {watch.ElapsedMilliseconds} ms " +
                    $"(managed heap now {GC.GetTotalMemory(false) / (1024 * 1024)} MB)");
            }
            catch (Exception ex)
            {
                FileLog.Write($"RoeSnip: idle memory trim failed (non-fatal): {ex.Message}");
            }
            finally
            {
                System.Threading.Volatile.Write(ref s_trimRunning, 0);
            }
        });
    }
}
