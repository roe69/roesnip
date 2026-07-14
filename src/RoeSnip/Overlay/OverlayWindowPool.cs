using System;
using System.Collections.Generic;
using System.Windows.Threading;
using RoeSnip.Capture;
using RoeSnip.Core.Diagnostics;

namespace RoeSnip.Overlay;

/// <summary>The overlay-latency pool (r5-latency round 2, D1/D4): one pre-built, parked, already
/// first-rendered OverlayWindow per monitor, ready for OverlaySession.RunAsync to claim instead of
/// paying the ~80-103 ms construction/Show/first-render cost on the hot path. Mirrors FlashDimmer's
/// own "compare the current set against the requested set, rebuild wholesale on any mismatch"
/// design (see FlashDimmer.EnsureCreated/Matches) almost exactly — the one deliberate difference is
/// that a pool window is SINGLE-USE (see OverlayWindow.IsPooled's doc comment): <see cref="TryTake"/>
/// removes it from the pool permanently, so "a session just consumed some of the pool" naturally
/// shows up as a set-size mismatch the next time <see cref="EnsureBuilt"/> runs — which is exactly
/// when a fresh pool needs building anyway. No separate "invalidate" path is needed for that case.
///
/// Lifecycle: built during TrayApp's startup warmup (right after the flash windows — see
/// OverlayController.PrewarmOverlayPool), rebuilt on SystemEvents.DisplaySettingsChanged (same
/// hook TrayApp already uses for the flash windows), and re-provisioned off the hot path after every
/// session via <see cref="ScheduleReprovision"/> (OverlayController.OverlaySession.Finish), which
/// defers the actual rebuild to DispatcherPriority.ContextIdle so it never contends with the session
/// that just finished — or, if it's still mid-rebuild when the NEXT trigger lands, that session
/// simply falls back to constructing whichever monitor's window on demand (see OverlaySession.
/// RunAsync) — correctness first, the fallback is merely slower. UI thread only throughout.</summary>
internal static class OverlayWindowPool
{
    private static readonly List<OverlayWindow> s_parked = new();

    // Reentrancy guard, same reasoning as FlashDimmer.s_ensuringCreated: EnsureBuilt's own
    // Dispatcher.Invoke(Loaded) flush per window runs a nested message pump that CAN dispatch a
    // hotkey/pipe trigger landing mid-build; without this a reentrant EnsureBuilt call could see a
    // partially-rebuilt s_parked, decide it "doesn't match", and CloseAll()+rebuild out from under
    // the outer call's own in-flight loop.
    private static bool s_ensuringBuilt;

    // Coalescing flag for ScheduleReprovision: a session that finishes while an earlier
    // ContextIdle-deferred reprovision is still pending must not stack a second one.
    private static bool s_reprovisionScheduled;

    /// <summary>Current pool size — purely for OverlaySession's "overlay pool: hit|miss (N pooled)"
    /// diagnostic line (D7).</summary>
    public static int Count => s_parked.Count;

    /// <summary>Pre-builds (or wholesale rebuilds, on any mismatch against the requested monitor
    /// set — see the class doc for why a partially-consumed pool also counts as a mismatch) one
    /// parked OverlayWindow per monitor: constructed via OverlayWindow's MonitorInfo-only
    /// constructor, Show()n (which drives OnSourceInitialized: parks it off-screen, computes its
    /// DPI scale, applies the capture-exclusion + Alt+Tab-hiding styles — see that method), and
    /// flushed through one real render pass so its first-paint cost is already paid. A single
    /// monitor's build failing is logged and simply leaves the pool short one window for that
    /// monitor (TryTake naturally reports a miss for it; OverlaySession falls back to on-demand
    /// construction) rather than aborting the whole pool. Safe to call repeatedly; a no-op when the
    /// current pool already matches. UI thread only.</summary>
    public static void EnsureBuilt(IReadOnlyList<MonitorInfo> monitors)
    {
        if (monitors.Count == 0 || Matches(monitors))
        {
            return;
        }
        if (s_ensuringBuilt)
        {
            // Bail rather than race the in-flight call — see s_ensuringBuilt's doc comment. Like
            // FlashDimmer's own PrewarmFlash, this is a pure perceived-latency optimization: the
            // caller that lost the race simply gets no pool this time, and the next real session
            // falls back to on-demand construction for whatever wasn't ready.
            return;
        }

        s_ensuringBuilt = true;
        try
        {
            CloseAllParked();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            foreach (var monitor in monitors)
            {
                try
                {
                    var window = new OverlayWindow(monitor);
                    window.Show(); // drives OnSourceInitialized: parks off-screen + first-paints the placeholder
                    window.Dispatcher.Invoke(static () => { }, DispatcherPriority.Loaded);
                    s_parked.Add(window);
                }
                catch (Exception ex)
                {
                    FileLog.Write(
                        $"RoeSnip: overlay pool build failed for monitor {monitor.DeviceName} (non-fatal, " +
                        $"that monitor falls back to on-demand construction): {ex.Message}");
                }
            }
            FileLog.Write(
                $"RoeSnip: overlay pool built in {watch.ElapsedMilliseconds} ms ({s_parked.Count}/{monitors.Count} monitors)");
        }
        finally
        {
            s_ensuringBuilt = false;
        }
    }

    /// <summary>Claims (and removes) the parked window pre-built for this exact monitor
    /// (DeviceName + BoundsPx) — or null on a pool miss (rapid re-trigger before re-provisioning
    /// finished, a display change, warmup not yet complete, or that monitor's build having failed).
    /// The caller falls back to constructing that monitor's window on demand — see
    /// OverlayController.OverlaySession.RunAsync.</summary>
    public static OverlayWindow? TryTake(MonitorInfo monitor)
    {
        for (int i = 0; i < s_parked.Count; i++)
        {
            var window = s_parked[i];
            if (string.Equals(window.Monitor.DeviceName, monitor.DeviceName, StringComparison.OrdinalIgnoreCase)
                && window.Monitor.BoundsPx == monitor.BoundsPx)
            {
                s_parked.RemoveAt(i);
                return window;
            }
        }
        return null;
    }

    /// <summary>Defers a fresh EnsureBuilt to DispatcherPriority.ContextIdle (D4) — called from
    /// OverlaySession.Finish() so re-provisioning a pool for the NEXT trigger never lands on the hot
    /// path of the session that's finishing right now, only once the UI thread genuinely has nothing
    /// better to do. Coalesced via s_reprovisionScheduled so back-to-back sessions (G3) don't stack
    /// redundant rebuilds.</summary>
    public static void ScheduleReprovision(Dispatcher dispatcher, IReadOnlyList<MonitorInfo> monitors)
    {
        if (monitors.Count == 0 || s_reprovisionScheduled)
        {
            return;
        }
        s_reprovisionScheduled = true;
        try
        {
            dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                s_reprovisionScheduled = false;
                try
                {
                    // Review fix (G3-adjacent race): a session that opened between this reprovision
                    // being scheduled and the ContextIdle callback actually running must not have its
                    // UI thread stolen for a full pool rebuild (see PrewarmOverlayPool's own guard,
                    // which this deferred callback lacks entirely since it calls EnsureBuilt
                    // directly). Re-schedule instead of dropping the reprovision on the floor, so the
                    // pool still gets rebuilt once the live session actually finishes.
                    if (OverlayController.IsSessionActive)
                    {
                        ScheduleReprovision(dispatcher, monitors);
                        return;
                    }
                    EnsureBuilt(monitors);
                }
                catch (Exception ex)
                {
                    FileLog.Write($"RoeSnip: overlay pool reprovision failed (non-fatal): {ex.Message}");
                }
            }));
        }
        catch
        {
            // Review fix: BeginInvoke can throw synchronously (e.g. the Dispatcher has begun
            // shutting down). If it does, the queued callback above — the only place that resets
            // s_reprovisionScheduled — never runs, which would otherwise wedge this flag at true
            // forever and permanently disable pool re-provisioning for the rest of the process's
            // life. Reset it here before rethrowing so the caller's own non-fatal catch/log doesn't
            // leave the coalescing flag stuck.
            s_reprovisionScheduled = false;
            throw;
        }
    }

    /// <summary>Order-independent set comparison against DeviceName+BoundsPx — same rationale as
    /// FlashDimmer.Matches (see its doc comment): a monitor set in any order is a match, so only a
    /// genuine change (a real monitor add/remove/move, OR a session having taken some windows out —
    /// see the class doc) triggers a rebuild.</summary>
    private static bool Matches(IReadOnlyList<MonitorInfo> monitors)
    {
        if (s_parked.Count != monitors.Count)
        {
            return false;
        }
        foreach (var window in s_parked)
        {
            bool found = false;
            foreach (var want in monitors)
            {
                if (string.Equals(window.Monitor.DeviceName, want.DeviceName, StringComparison.OrdinalIgnoreCase)
                    && window.Monitor.BoundsPx == want.BoundsPx)
                {
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                return false;
            }
        }
        return true;
    }

    private static void CloseAllParked()
    {
        foreach (var window in s_parked)
        {
            try
            {
                window.Close(); // OverlayWindow.OnClosed disposes its own placeholder frame — see that method
            }
            catch (Exception ex)
            {
                FileLog.Write($"RoeSnip: closing a pooled overlay window failed: {ex.Message}");
            }
        }
        s_parked.Clear();
    }
}
