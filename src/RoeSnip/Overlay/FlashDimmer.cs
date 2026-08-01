using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using RoeSnip.Capture;
using RoeSnip.Core.Diagnostics;
using RoeSnip.Interop;

namespace RoeSnip.Overlay;

// Same aliasing convention as the sibling Overlay/* files (RoeSnip.csproj enables both UseWPF and
// UseWindowsForms, so System.Windows.Forms/System.Drawing collide with WPF names).
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Brush = System.Windows.Media.Brush;

/// <summary>The instant-response dim layer (r5-latency). One ultra-lightweight borderless topmost
/// window per monitor showing ONLY the same dim the real overlay uses (#8A000000 — must match
/// OverlayWindow.xaml's DimPath fill) plus the crosshair cursor: no preview, no toolbar, no
/// per-session state. OverlayController.TryShowFlash shows these within milliseconds of the
/// hotkey, BEFORE the capture+tonemap stretch (which runs on the thread pool with a deadline while
/// this thread keeps pumping — see RunCaptureFlowAsync); because the frozen preview the real
/// overlay then displays equals the live screen, each monitor's flash can be hidden the moment
/// that monitor's real OverlayWindow has rendered (ContentRendered) with no visible seam.
///
/// Input policy: the windows deliberately SWALLOW input rather than click through — while the
/// flash is up the user believes the snip UI is active, so a click must do nothing rather than
/// land in whatever app is underneath. This swallow comes from being topmost + non-click-through
/// for Win32 hit-testing (clicks go to whatever's topmost under the cursor by z-order); it does
/// NOT depend on holding OS foreground/activation — ShowAll no longer stakes any foreground claim
/// at all (2026-08-02, see its own doc comment). Esc is the one key acted on (cancels the pending
/// capture via OverlayController.OnFlashEscape) via a focus-independent WH_KEYBOARD_LL hook
/// (FlashEscapeHook, installed in TryShowFlash) — it fires regardless of which window has OS
/// focus, so it is unaffected by the foreground-claim removal; once the real session opens, its
/// own SessionKeyboardHook covers Esc the same way.
///
/// Lifecycle — park, don't hide (r5-latency, first-trigger fix): AllowsTransparency (layered)
/// windows are expensive to CREATE (~100 ms each) and, it turns out, still pay a measurable
/// first-PRESENTATION cost the first time they're ever re-Show()n after being WPF-Hidden — a
/// genuinely cold first trigger measured 65-90 ms hotkey-to-dim even with S3's create-time priming
/// (see PrepareHidden), versus 18-19 ms on every later trigger. So these windows are never
/// WPF-Hide()n at all past warmup: PrepareHidden Show()s each one exactly ONCE, parked fully off
/// the virtual desktop (x=60000, see FlashWindow.OffScreenX), and it is left PERMANENTLY VISIBLE —
/// resident for the app's whole lifetime (~15 MB of layered-window surface per monitor) — that is
/// the deliberate trade-off for guaranteed sub-frame response. "Show"/"hide" on the hot path are
/// then just a single SetWindowPos moving the already-composited surface onto or off its real
/// monitor bounds; WPF's own Show()/Hide()/IsVisible are never touched again, so FlashWindow tracks
/// on-screen state itself via the explicit IsPresented flag (WPF IsVisible would say "true" for a
/// window that is very much off-screen and invisible to the user). WS_EX_TOOLWINDOW keeps these
/// permanently-visible windows out of Alt+Tab. A changed monitor set — compared by device name +
/// physical bounds on every EnsureCreated — closes (for real; see FlashWindow.CloseFlash) and
/// recreates them (the WM_DISPLAYCHANGE-style path: TrayApp re-prewarms on
/// SystemEvents.DisplaySettingsChanged). UI (dispatcher) thread only.</summary>
internal static class FlashDimmer
{
    private static readonly List<FlashWindow> s_windows = new();

    // Reentrancy guard (review fix, r5-latency S3): PrepareHidden's priming Dispatcher.Invoke (see
    // below) runs a nested Win32 message pump on this thread — it was never started via
    // Dispatcher.Run(), so a blocking Invoke has to pump the OS queue itself to make progress, and a
    // nested GetMessage/DispatchMessage loop dispatches ANY ready message, not just WPF ones,
    // including a queued Control.BeginInvoke callback such as TriggerCapture. Without this guard, a
    // hotkey landing mid-EnsureCreated (classic "trigger right after launch" scenario) could
    // reentrantly call EnsureCreated again while s_windows is only partially rebuilt, see a spurious
    // Matches()==false, and CloseAll()+rebuild out from under the outer call's own in-flight loop —
    // corrupting s_windows with duplicate/orphaned FlashWindow instances. See EnsureCreated below.
    private static bool s_ensuringCreated;

    // Foreground-claim epoch — NO LONGER LOAD-BEARING (2026-08-02): this used to guard ShowAll's
    // own best-effort background-thread SetForegroundWindow call against racing the real overlay
    // session's later, more robust ForegroundActivator.Activate("session-start") claim. That
    // ShowAll-side claim has since been deleted outright (it was racing ahead of CaptureAll()
    // actually reading pixels and dismissing tooltips/hover UI that was on screen at hotkey-press
    // time — see ShowAll's own comment at the removal site) — the flash phase now stakes NO
    // foreground claim of any kind. s_foregroundClaimEpoch, InvalidateForegroundClaim,
    // s_foregroundBeforeClaim and TryRestoreForegroundFromFlash are all kept anyway, deliberately,
    // as a no-op safety net: with nothing ever queuing a claim, TryRestoreForegroundFromFlash's own
    // "is the foreground currently one of our flash windows" check simply never trips in the normal
    // case, so it costs nothing to leave wired up as cheap insurance against some future change
    // reintroducing a claim here without reintroducing this guard alongside it.
    private static int s_foregroundClaimEpoch;

    // The HWND that was foreground just before ShowAll's best-effort claim — the restore target for
    // TryRestoreForegroundFromFlash. UI thread writes (ShowAll), UI thread reads.
    private static IntPtr s_foregroundBeforeClaim;

    /// <summary>Invalidates any in-flight best-effort foreground claim queued by ShowAll (see
    /// <see cref="s_foregroundClaimEpoch"/>'s doc comment) — call this immediately before staking
    /// a real foreground claim of your own (OverlaySession's session-start activation) or before
    /// tearing everything down (OverlaySession.Finish's HideAll), so a slow flash-activation call
    /// can no longer steal focus back afterwards. Safe to call even when no flash claim is
    /// outstanding.</summary>
    public static void InvalidateForegroundClaim() =>
        System.Threading.Interlocked.Increment(ref s_foregroundClaimEpoch);

    /// <summary>True while any flash window is genuinely on-screen (IsPresented — see FlashWindow;
    /// NOT WPF's IsVisible, which is meaningless here now that every window is permanently
    /// WPF-Show()n and parked/moved via raw SetWindowPos) — used by OverlayController to decide
    /// whether a starting session was hotkey-initiated (its latency logs then measure from the
    /// flash timestamp) and to keep a prewarm from recreating windows mid-flash.</summary>
    public static bool AnyVisible
    {
        get
        {
            foreach (var window in s_windows)
            {
                if (window.IsPresented) return true;
            }
            return false;
        }
    }

    /// <summary>Pre-creates (or recreates, when the monitor set changed) one flash window per
    /// monitor, parked off-screen (see FlashWindow.PrepareHidden — "hidden" in the park-don't-hide
    /// sense: WPF-Show()n once, permanently, but positioned where nothing can see it). Safe to call
    /// repeatedly; a matching set is a no-op. <paramref name="presentAsBuilt"/> (ALSO item,
    /// r5-latency): when true, each monitor's window is moved on-screen the INSTANT its own build
    /// finishes rather than only after every monitor in the list has been built — used only by
    /// ShowAll's cold-build path (a real trigger landing before PrewarmFlash ever ran), paired with
    /// that path's cursor-monitor-first ordering, so the monitor the user is actually looking at
    /// dims as early as possible instead of waiting out every other monitor's own ~100 ms build
    /// first. Defaults to false: PrewarmFlash's own warmup call must never present anything — it has
    /// no real trigger to respond to.</summary>
    public static void EnsureCreated(IReadOnlyList<MonitorInfo> monitors, bool presentAsBuilt = false)
    {
        if (Matches(monitors))
        {
            return;
        }

        if (s_ensuringCreated)
        {
            // Bail rather than race the in-flight call (see s_ensuringCreated's doc comment): the
            // outer call will finish shortly and this reentrant call's own flash simply doesn't show
            // this once — acceptable, since the flash is a best-effort/non-fatal perceived-latency
            // optimization (TrayApp.TriggerCapture proceeds with the real capture regardless of
            // whether TryShowFlash succeeded).
            return;
        }

        s_ensuringCreated = true;
        try
        {
            CloseAll();
            foreach (var monitor in monitors)
            {
                var window = new FlashWindow(monitor);
                try
                {
                    window.PrepareHidden();
                }
                catch
                {
                    try { window.CloseFlash(); } catch { /* best-effort */ }
                    throw;
                }
                lock (s_watchdogGate) // the watchdog snapshots s_windows from its timer thread
                {
                    s_windows.Add(window);
                }
                if (presentAsBuilt)
                {
                    window.ShowOnMonitor();
                }
            }
        }
        finally
        {
            s_ensuringCreated = false;
        }
    }

    /// <summary>Shows the dim on every monitor and flushes layout+render before returning, so the
    /// dim is guaranteed on-screen when ShowAll returns — hotkey-to-dim latency stays deterministic
    /// instead of depending on when the pump next renders. (DispatcherPriority.Loaded sits just
    /// below Render, so the flush drains every pending layout/render operation without dispatching
    /// lower-priority queued work.) NOTE (post-sleep stall fix): the capture+tonemap stretch that
    /// follows now runs on the THREAD POOL — this UI thread keeps pumping during it, so dispatcher
    /// work (another trigger, display-change handlers, queued idle items) CAN interleave with a
    /// capture in flight; guards that assume "nothing runs mid-capture" are wrong now (see
    /// PrewarmOverlayPool / ScheduleReprovision's in-flight-flow guards).</summary>
    public static void ShowAll(IReadOnlyList<MonitorInfo> monitors)
    {
        // Snapshot the pre-claim foreground FIRST (before anything moves on-screen or negotiates
        // focus) so a flash-phase exit that never opens a session can hand focus back — see
        // TryRestoreForegroundFromFlash. Skipped when the current foreground is already one of OUR
        // flash windows (review-caught): the UI thread pumps during the capture wait now, so a
        // repeat trigger's ShowAll can run after the FIRST trigger's claim already won — snapshotting
        // then would record a flash window as the "restore target" and the restore would park focus
        // right back on the invisible key-swallowing window (the exact dead-keyboard bug the restore
        // exists to fix). Keeping the earlier snapshot preserves the genuine pre-claim app; on
        // exception the previous value is likewise kept rather than zeroed.
        try
        {
            IntPtr fg = OverlayInputInterop.GetForegroundWindow();
            bool fgIsFlash = false;
            foreach (var window in s_windows)
            {
                if (window.CachedHwnd == fg)
                {
                    fgIsFlash = true;
                    break;
                }
            }
            if (!fgIsFlash)
            {
                s_foregroundBeforeClaim = fg;
            }
        }
        catch { /* keep the previous snapshot */ }

        bool coldBuild = !Matches(monitors);
        if (coldBuild)
        {
            // ALSO item (r5-latency): this trigger landed before PrewarmFlash ever built these
            // windows (or a display change invalidated them) — EnsureCreated is about to pay the
            // full ~100 ms/monitor build cost with nothing dimmed until it returns. Build+present
            // the CURSOR monitor's window first (the monitor the user is actually looking at) so it
            // dims as early as possible instead of waiting behind every other monitor's build.
            monitors = OrderCursorMonitorFirst(monitors);
        }
        EnsureCreated(monitors, presentAsBuilt: coldBuild);

        // Presentation order is recomputed cursor-first on EVERY call (not just cold builds) and
        // resolved against s_windows BY NAME, never by relying on s_windows' own storage order —
        // that order is whatever the last cold build happened to use (see Matches' doc comment)
        // and is otherwise irrelevant now that Matches() is order-independent.
        var presentationOrder = OrderCursorMonitorFirst(monitors);
        foreach (var monitor in presentationOrder)
        {
            var window = FindWindow(monitor.DeviceName);
            if (window is null)
            {
                continue; // shouldn't happen post-EnsureCreated, but never crash the flash path over it
            }
            window.ShowOnMonitor(); // no-op for any window the cold-build path already presented
        }

        // Foreground claim REMOVED here (capture-fidelity fix, 2026-08-02). ShowAll used to fire a
        // best-effort SetForegroundWindow off a background thread at this exact point (targeting
        // the cursor monitor's flash window, "first" in the removed code) — instrumentation showed
        // that call was the entire remaining first-trigger latency outlier, so it was moved off the
        // UI thread rather than removed outright at the time. Re-reading the whole flow turned up
        // the real problem with keeping it at all: SetForegroundWindow reassigns OS foreground
        // activation, which is exactly the Win32 mechanism comctl32 tooltips and most custom hover
        // popups use to self-dismiss (WM_ACTIVATE/WM_ACTIVATEAPP) — so anything on screen at
        // hotkey-press time (a tooltip, a hover menu) was being silently dismissed by THIS call,
        // racing ahead of CaptureAll() ever reading pixels, before the user's own capture had a
        // chance to see it. It is not load-bearing for anything this flow still needs:
        //   - Input-swallow comes from being topmost + non-click-through (Position()'s
        //     SWP_NOACTIVATE below never activated anything and never needed to).
        //   - Esc during the flash phase is covered focus-independently by FlashEscapeHook (a
        //     WH_KEYBOARD_LL hook installed in TryShowFlash) — it does not depend on this claim.
        //   - The real overlay session's own ForegroundActivator.Activate("session-start") — a
        //     proper 3-tier ladder, strictly more robust than this single best-effort call ever
        //     was — already claims foreground once the session opens, which is always after
        //     CaptureAll() has returned frames.
        // Removing it costs zero on every latency number this codebase logs (hotkey-to-dim,
        // capture-to-overlay): the deleted call was fire-and-forget on a Task.Run queued BEFORE
        // TrayApp.TriggerCapture reads its own flashWatch stopwatch, so it was never on that clock
        // to begin with. s_foregroundClaimEpoch / InvalidateForegroundClaim /
        // s_foregroundBeforeClaim / TryRestoreForegroundFromFlash are all deliberately KEPT (see
        // s_foregroundClaimEpoch's own doc comment) as a no-op safety net, not because anything
        // here still queues a claim for them to guard.
        //
        // Scope caveat (do not overclaim this as "all tooltips now survive"): this closes the
        // ACTIVATION-triggered dismissal path only. The ShowOnMonitor loop just above still places a
        // topmost, non-click-through window over whatever was under the cursor, with SWP_NOACTIVATE —
        // that never claims foreground, but it DOES change what WindowFromPoint(cursor) resolves to,
        // which can independently make an already-hovering tooltip's own hover-tracking
        // (TrackMouseEvent) self-dismiss via WM_MOUSELEAVE. That residual path is not touched by this
        // removal; see docs/CAPTURE-FIDELITY-SPEC.md item 1.

        Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.Loaded);

        // Something is presented now — start the stuck-dim dead-man (see the watchdog block below).
        ArmWatchdog();
    }

    private static FlashWindow? FindWindow(string deviceName)
    {
        foreach (var window in s_windows)
        {
            if (string.Equals(window.Monitor.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                return window;
            }
        }
        return null;
    }

    /// <summary>Best-effort cursor-monitor-first reordering for a cold EnsureCreated build (ALSO
    /// item, r5-latency) — mirrors OverlayController.OrderCursorMonitorFirst's reasoning but works
    /// on plain MonitorInfo (no captured frame exists yet at this point in the flow).</summary>
    private static IReadOnlyList<MonitorInfo> OrderCursorMonitorFirst(IReadOnlyList<MonitorInfo> monitors)
    {
        if (monitors.Count < 2 || !TryGetCursorPos(out int cx, out int cy))
        {
            return monitors;
        }
        int cursorIndex = -1;
        for (int i = 0; i < monitors.Count; i++)
        {
            var b = monitors[i].BoundsPx;
            if (cx >= b.Left && cx < b.Right && cy >= b.Top && cy < b.Bottom)
            {
                cursorIndex = i;
                break;
            }
        }
        if (cursorIndex <= 0)
        {
            return monitors;
        }
        var reordered = new List<MonitorInfo>(monitors);
        var cursorMonitor = reordered[cursorIndex];
        reordered.RemoveAt(cursorIndex);
        reordered.Insert(0, cursorMonitor);
        return reordered;
    }

    /// <summary>True while a genuinely on-screen (IsPresented) flash window covers the given
    /// monitor — the overlay show path uses this to start that monitor's real window with its own
    /// dim layer hidden (anti-double-dim handoff; see OverlayController).</summary>
    public static bool IsCoveringMonitor(string deviceName)
    {
        foreach (var window in s_windows)
        {
            if (window.IsPresented
                && string.Equals(window.Monitor.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Hides the flash on one monitor — called from the real overlay window's
    /// ContentRendered so the swap is per-monitor and zero-gap. Per-window exception isolation
    /// (stuck-dim fix, here and in <see cref="HideAll"/>): one failed SetWindowPos must not abort
    /// hiding the remaining monitors — the callers all swallow the exception, so before this a
    /// single throw stranded every later window dimmed with s_flashUsers already at zero and
    /// AnyVisible then blocking every future prewarm rebuild until process restart.</summary>
    public static void HideForMonitor(string deviceName)
    {
        foreach (var window in s_windows)
        {
            if (string.Equals(window.Monitor.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                try { window.HideFlash(); }
                catch (Exception ex) { FileLog.Write($"RoeSnip: hiding a flash window failed: {ex.Message}"); }
            }
        }
    }

    public static void HideAll()
    {
        foreach (var window in s_windows)
        {
            try { window.HideFlash(); }
            catch (Exception ex) { FileLog.Write($"RoeSnip: hiding a flash window failed: {ex.Message}"); }
        }
    }

    /// <summary>Focus hygiene for the flash-phase exits that never open a session (Esc during the
    /// flash, capture failed/timed out). NO-OP SAFETY NET as of 2026-08-02 (see
    /// s_foregroundClaimEpoch's doc comment): ShowAll no longer stakes any foreground claim, so the
    /// "is the foreground one of our flash windows" check below never trips in the normal case —
    /// this is kept purely as cheap insurance in case some future change reintroduces a claim
    /// without reintroducing this restore alongside it. Bumps the claim epoch first so an in-flight
    /// background claim (if one ever existed again) can't re-steal afterwards. UI thread;
    /// best-effort.</summary>
    public static void TryRestoreForegroundFromFlash()
    {
        InvalidateForegroundClaim();
        try
        {
            IntPtr foreground = OverlayInputInterop.GetForegroundWindow();
            if (foreground == IntPtr.Zero)
            {
                return;
            }
            bool foregroundIsFlash = false;
            foreach (var window in s_windows)
            {
                if (window.CachedHwnd == foreground)
                {
                    foregroundIsFlash = true;
                    break;
                }
            }
            if (!foregroundIsFlash)
            {
                return; // focus is somewhere legitimate — never yank it
            }
            IntPtr previous = s_foregroundBeforeClaim;
            if (previous == IntPtr.Zero)
            {
                return;
            }
            // Defense-in-depth against a poisoned snapshot: never "restore" onto one of our own
            // flash windows (see ShowAll's snapshot guard for how that could otherwise happen).
            foreach (var window in s_windows)
            {
                if (window.CachedHwnd == previous)
                {
                    return;
                }
            }
            NativeMethods.SetForegroundWindow(previous);
            s_foregroundBeforeClaim = IntPtr.Zero; // one restore per snapshot — never reuse across flows
        }
        catch { /* best-effort, same contract as the claim itself */ }
    }

    // ---------- Dead-man watchdog (stuck-dim backstop) ----------
    //
    // Belt-and-braces behind every architectural fix above: if a flash window has been presented
    // continuously for far longer than any legitimate flow can keep it (the capture deadline plus
    // overlay construction is well under half of this), force-park it from a background thread.
    // The park is a raw async SetWindowPos (SWP_ASYNCWINDOWPOS: POSTS the move rather than blocking
    // on the window's possibly-wedged owner thread) — never a WPF Hide(), which would destroy the
    // warm composited surface the park-don't-hide design depends on. Zero cost while parked: the
    // timer only runs while something is presented and disarms itself when nothing is.
    private const int WatchdogMaxPresentedMs = 30_000;
    private static readonly object s_watchdogGate = new();
    private static System.Threading.Timer? s_watchdogTimer;
    // Bumped by every ArmWatchdog (under the gate): a tick that concluded "nothing presented" from
    // a snapshot taken BEFORE a fresh arm must not disarm the timer that arm just scheduled — the
    // disarm below is generation-checked (same epoch pattern as s_foregroundClaimEpoch).
    private static int s_watchdogGeneration;

    private static void ArmWatchdog()
    {
        lock (s_watchdogGate)
        {
            s_watchdogGeneration++;
            s_watchdogTimer ??= new System.Threading.Timer(WatchdogTick, null,
                System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            s_watchdogTimer.Change(5_000, 5_000);
        }
    }

    private static void WatchdogTick(object? state)
    {
        try
        {
            FlashWindow[] snapshot;
            int generation;
            lock (s_watchdogGate)
            {
                snapshot = s_windows.ToArray();
                generation = s_watchdogGeneration;
            }
            bool anyPresented = false;
            long now = Environment.TickCount64;
            foreach (var window in snapshot)
            {
                if (!window.IsPresented)
                {
                    continue;
                }
                if (now - window.PresentedSinceTick > WatchdogMaxPresentedMs)
                {
                    FileLog.Write(
                        $"RoeSnip: flash watchdog force-parking a dim window stuck on " +
                        $"{window.Monitor.DeviceName} for over {WatchdogMaxPresentedMs / 1000} s.");
                    window.ForceParkFromWatchdogThread();
                }
                else
                {
                    anyPresented = true;
                }
            }
            if (!anyPresented)
            {
                lock (s_watchdogGate)
                {
                    // Only disarm if no ShowAll re-armed since this tick's snapshot (see
                    // s_watchdogGeneration) — a stale conclusion must not cancel a fresh arm.
                    if (generation == s_watchdogGeneration)
                    {
                        s_watchdogTimer?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: flash watchdog tick failed (non-fatal): {ex.Message}");
        }
    }

    /// <summary>Order-independent by design (review fix, r5-latency S3 follow-up): compares the
    /// built window SET against the requested monitor SET keyed by (DeviceName, BoundsPx), never
    /// by list position. ShowAll's cold-build path stores <see cref="s_windows"/> in whatever
    /// order it was handed (cursor-monitor-first — see OrderCursorMonitorFirst), which need not
    /// match a later caller's own (e.g. TrayApp's cached natural-enumeration) order. A positional
    /// comparison here would then report "changed" purely because of ordering, not because the
    /// monitor set actually changed — forcing a full CloseAll()+rebuild (~100 ms/monitor) on every
    /// later ShowAll call whose caller-supplied order differs from whatever order the last cold
    /// build happened to store, permanently defeating the park-don't-hide design, and letting a
    /// reentrant double-trigger (key-repeat, tray click racing a hotkey) tear down windows that
    /// are genuinely mid-flash. Comparing as a set fixes both: the same monitor set in ANY order
    /// is a match, so only a real monitor add/remove/move triggers a rebuild.</summary>
    private static bool Matches(IReadOnlyList<MonitorInfo> monitors)
    {
        if (s_windows.Count != monitors.Count)
        {
            return false;
        }
        foreach (var window in s_windows)
        {
            var have = window.Monitor;
            bool found = false;
            foreach (var want in monitors)
            {
                if (string.Equals(have.DeviceName, want.DeviceName, StringComparison.OrdinalIgnoreCase)
                    && have.BoundsPx == want.BoundsPx)
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

    private static void CloseAll()
    {
        foreach (var window in s_windows)
        {
            try
            {
                window.CloseFlash();
            }
            catch (Exception ex)
            {
                FileLog.Write($"RoeSnip: closing a flash dimmer window failed: {ex.Message}");
            }
        }
        lock (s_watchdogGate) // the watchdog snapshots s_windows from its timer thread
        {
            s_windows.Clear();
        }
    }

    // Local P/Invoke per the OverlayInputInterop convention (used only by the overlay package).
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    /// <summary>Physical-pixel cursor position (virtual-desktop coordinates) — used by
    /// OverlayController's show-the-cursor-monitor-first ordering.</summary>
    public static bool TryGetCursorPos(out int x, out int y)
    {
        if (GetCursorPos(out POINT p))
        {
            x = p.X;
            y = p.Y;
            return true;
        }
        x = 0;
        y = 0;
        return false;
    }

    private sealed class FlashWindow : Window
    {
        public MonitorInfo Monitor { get; }

        /// <summary>Explicit on-screen tracking (park-don't-hide design, r5-latency first-trigger
        /// fix): this window is WPF-Show()n exactly once, permanently — "hidden" is really "parked
        /// off the virtual desktop at x=60000", a distinction WPF's own IsVisible has no notion of
        /// (it would report true even while parked). True only while <see cref="Position"/> has
        /// actually moved this window onto its real monitor bounds.</summary>
        public bool IsPresented { get; private set; }

        /// <summary>Environment.TickCount64 of the most recent move on-screen — the watchdog's
        /// "how long has this dim been up" clock. Only meaningful while IsPresented.</summary>
        public long PresentedSinceTick { get; private set; }

        /// <summary>The HWND, cached at PrepareHidden so the watchdog's timer thread can address
        /// this window without touching WPF (WindowInteropHelper is UI-thread-affine in spirit;
        /// an IntPtr field read is not).</summary>
        public IntPtr CachedHwnd { get; private set; }

        private bool _closingForReal;

        public FlashWindow(MonitorInfo monitor)
        {
            Monitor = monitor;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = DimBrush;
            Topmost = true;
            ShowInTaskbar = false;
            // Nothing in ShowAll ever OS-activates a flash window anymore (2026-08-02, the
            // background SetForegroundWindow call was deleted outright — see ShowAll's own comment
            // at the removal site). ShowActivated=false just suppresses WPF's own implicit
            // activate-on-Show(), which would otherwise fire once at PrepareHidden's one-time Show()
            // call even though this window is parked off-screen at that point.
            ShowActivated = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Focusable = true;
            // Glyph swap only, NOT a cursor hide/clip/capture: Windows' actual OS cursor is never
            // hidden, clipped, or captured anywhere in this flow (no Cursor.Hide/ShowCursor/
            // ClipCursor/SetCapture call exists on this path). The perceived "cursor stopped
            // working" effect users report is this glyph changing to the same crosshair the real
            // overlay's select tool uses (signaling "snip mode is active") COMBINED with clicks
            // being swallowed for the flash phase - see CAPTURE-FIDELITY-SPEC.md item 2. Note this
            // glyph no longer actually applies now that the window is WS_EX_TRANSPARENT: a
            // click-through window is not hit-tested, so the cursor underneath keeps its own shape
            // until the real overlay takes over a few tens of ms later. Kept for the
            // ROESNIP_DIAG_NOEXCLUDE diagnostic path and for the window's own (unfocused) sake.
            Cursor = Cursors.Cross;
            KeyDown += OnKeyDown;
        }

        // Must equal OverlayWindow.xaml's DimPath fill (#8A000000) so the flash-to-overlay swap
        // is invisible on the undimmed... dimmed area (i.e. everywhere, pre-selection).
        private static readonly Brush DimBrush = CreateDimBrush();

        private static Brush CreateDimBrush()
        {
            var brush = new SolidColorBrush(Color.FromArgb(0x8A, 0x00, 0x00, 0x00));
            brush.Freeze();
            return brush;
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                e.Handled = true;
                OverlayController.OnFlashEscape();
            }
            // Every other key is swallowed implicitly: this window (when focused) has no other
            // handlers, so nothing leaks through to the app underneath.
        }

        /// <summary>Creates the HWND, parks it off-screen, and Show()s it exactly ONCE — the window
        /// stays WPF-visible (parked) for the rest of the process's life; see the class doc's
        /// "Lifecycle — park, don't hide" section for why.</summary>
        public void PrepareHidden()
        {
            var hwnd = new WindowInteropHelper(this).EnsureHandle();
            CachedHwnd = hwnd;

            // WS_EX_TOOLWINDOW (park-don't-hide): this window is about to become permanently
            // OS-visible for the app's entire lifetime (see below) — without this extended style it
            // would show up as its own Alt+Tab entry despite always living either parked at
            // x=60000 or dimming a monitor. ShowInTaskbar=false alone only keeps it off the
            // taskbar, not Alt+Tab.
            //
            // WS_EX_TRANSPARENT (click-through) is what lets a tooltip survive into the capture.
            // This window is shown BEFORE the frame is read, and a topmost window that takes part
            // in hit testing steals WindowFromPoint from whatever the cursor was over - which drops
            // that window's TrackMouseEvent hover state (WM_MOUSELEAVE) and dismisses its tooltip,
            // with no dependence on focus or activation at all. Removing the flash's foreground
            // claim fixed only the activation half of that; this fixes the hover half, which is the
            // one that actually loses ordinary hover tooltips. Input is still swallowed for the
            // flash phase, just by FlashMouseSwallowHook (a focus-independent low-level hook,
            // exactly like FlashEscapeHook already does for Esc) instead of by hit testing, so
            // nothing leaks into the app underneath while the screen is dimmed.
            long exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            NativeMethods.SetWindowLongPtr(
                hwnd,
                NativeMethods.GWL_EXSTYLE,
                new IntPtr(exStyle | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TRANSPARENT));

            // CRITICAL: the flash is shown BEFORE the capture runs, so without this every
            // screenshot would contain the flash's own 45% dim baked into the pixels (user-reported
            // round-5 bug: "its screenshotting the dimmed screen state instead of the pre-dimmed
            // state"). WDA_EXCLUDEFROMCAPTURE makes the window visible to the user but invisible
            // to WGC/DD/print-screen capture paths (Win10 2004+). Failure is non-fatal but must be
            // loud — a silent failure silently corrupts every screenshot.
            // Diagnostic escape hatch: with ROESNIP_DIAG_NOEXCLUDE=1 the flash is left capturable
            // so an external luma sampler can observe the flash-to-overlay handoff. Never set in
            // normal use (it would let the flash dim leak into screenshots).
            if (Environment.GetEnvironmentVariable("ROESNIP_DIAG_NOEXCLUDE") != "1"
                && !SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE))
            {
                FileLog.Write(
                    "RoeSnip: SetWindowDisplayAffinity(EXCLUDEFROMCAPTURE) failed on a flash window; " +
                    "captures taken while the flash is up will include the dim!");
            }

            // Park, don't hide (r5-latency, first-trigger fix): the previous design (create-time
            // priming: Show -> flush -> Hide, still measured 65-90 ms on a genuinely cold first
            // trigger) re-Show()d a WPF-Hidden layered window on the hot path — WPF/DWM treat that
            // as a new surface handoff, not a cheap re-composite, so the "warm" 18-19 ms path was
            // never actually reached on the very first trigger. Fix: Show() this window exactly
            // ONCE, here, fully off the virtual desktop at its real monitor SIZE, and never call
            // Hide() again for the rest of the process's life. ShowOnMonitor/HideFlash below then
            // become a single SetWindowPos moving this same live, already-composited surface on- or
            // off-screen — well under a frame, and no WPF Show()/Hide() call is ever on the hot path
            // again. ShowActivated is already false and Position uses SWP_NOACTIVATE, so nothing
            // here steals focus. Accepted trade-off: this window's ~15 MB layered-window surface
            // stays resident for the app's lifetime instead of being freed between captures.
            Position(onScreen: false);
            Show();
            Dispatcher.Invoke(static () => { }, DispatcherPriority.Loaded);
            // Deliberately no Hide() here — see above. IsPresented is already false (Position(false)
            // just set it): the window is OS-visible but parked, i.e. not presented to the user.
        }

        // Same trick, same value as OverlayWindow.OffScreenX (that constant is private to that
        // class, hence the duplicate here): far off the virtual desktop (monitors span roughly
        // x:[-1440, 2560]) — this window's permanent "not dimming anything" position.
        private const int OffScreenX = 60000;

        private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

        public void ShowOnMonitor()
        {
            if (IsPresented)
            {
                return;
            }
            Position(onScreen: true);
        }

        /// <summary>Moves this already-Show()n, already-composited window onto its real monitor
        /// bounds (onScreen: true) or fully off-screen to its parked position (onScreen: false) —
        /// the ONLY thing ShowOnMonitor/HideFlash do now (park-don't-hide design); no WPF
        /// Show()/Hide() call after PrepareHidden's one-time Show(). Re-asserts physical-pixel
        /// bounds + topmost on every on-screen move (same mixed-DPI pattern as OverlayWindow:
        /// position via Win32 in physical pixels, never WPF DIP properties).</summary>
        private void Position(bool onScreen)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }
            var b = Monitor.BoundsPx;
            int x = onScreen ? b.Left : OffScreenX;
            NativeMethods.SetWindowPos(
                hwnd, NativeMethods.HWND_TOPMOST, x, b.Top, b.Width, b.Height, NativeMethods.SWP_NOACTIVATE);
            if (onScreen)
            {
                PresentedSinceTick = Environment.TickCount64;
            }
            IsPresented = onScreen;
        }

        /// <summary>The dead-man watchdog's force-park — the ONLY member of this class that runs
        /// off the UI thread. Raw SetWindowPos with SWP_ASYNCWINDOWPOS: the move request is POSTED
        /// to the window's owner thread rather than delivered synchronously, so this can never
        /// deadlock on that thread's (possibly wedged) message queue; a raw Win32 call on an HWND
        /// has no managed thread affinity. IsPresented is a plain bool write — benign cross-thread
        /// worst case is one redundant park.</summary>
        public void ForceParkFromWatchdogThread()
        {
            var hwnd = CachedHwnd;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }
            var b = Monitor.BoundsPx;
            NativeMethods.SetWindowPos(
                hwnd, NativeMethods.HWND_TOPMOST, OffScreenX, b.Top, b.Width, b.Height,
                NativeMethods.SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
            IsPresented = false;
        }

        private const uint SWP_ASYNCWINDOWPOS = 0x4000;

        public void HideFlash()
        {
            if (!IsPresented)
            {
                return;
            }
            Position(onScreen: false);
        }

        public void CloseFlash()
        {
            _closingForReal = true;
            Close();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // These windows are reused across sessions; nothing external should be able to close
            // one (e.g. Alt+F4 while a flash is focused) — park it instead (HideFlash — see the
            // park-don't-hide class doc; this is no longer a WPF Hide()). Only FlashDimmer's own
            // CloseFlash (monitor-set recreation) really closes.
            if (!_closingForReal)
            {
                e.Cancel = true;
                HideFlash();
            }
            base.OnClosing(e);
        }
    }
}
