using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using RoeSnip.App.AppShell;
using RoeSnip.Core.Capture;
using RoeSnip.Core.Diagnostics;
using RoeSnip.Core.Overlay;

namespace RoeSnip.App.Overlay;

/// <summary>Windows-only instant-response dim layer (item 18 — ported from the WPF app's
/// src/RoeSnip/Overlay/FlashDimmer.cs, which is the tuned source of truth for every timing/ordering
/// decision below; read that file's own doc comment before changing anything here). One borderless,
/// always-topmost, capture-excluded window per monitor showing ONLY the same dim the real
/// OverlayWindow's DimPath uses (#8A000000 — see OverlayWindow.axaml) plus a crosshair cursor: no
/// preview, no toolbar, no per-session state. TrayApp.TriggerCapture calls TryShowFlash within
/// milliseconds of the trigger, BEFORE the capture+tonemap stretch (which runs on the thread pool
/// with a deadline while this thread keeps pumping — see RunCaptureFlowAsync) that otherwise
/// dominates the app's whole "hotkey to overlay" latency; because the frozen preview the real
/// overlay then shows equals the live screen (same dim, same pixels), each monitor's flash is hidden
/// the moment that monitor's real OverlayWindow has been shown, with no visible seam.
///
/// Lifecycle — park, don't hide (ported verbatim from the WPF reference's own design, and just as
/// load-bearing here): these windows are Show()n exactly ONCE, parked fully off the virtual desktop
/// (x = OffScreenX), and then never Avalonia-Hidden again for the rest of the process's life —
/// ShowOnMonitor/HideFlash are a single raw SetWindowPos moving the already-composited surface on-
/// or off-screen. A cold Window.Show() (or a Show() after being framework-Hidden) pays real surface
/// creation/first-presentation cost; a raw SetWindowPos on an already-shown window does not — this
/// is the entire reason a flash trigger can be single-digit milliseconds instead of tens.
/// IsPresented (not any Avalonia-visible notion) is this class's own on-screen bookkeeping.
///
/// Positioning after the initial Show() is done EXCLUSIVELY via raw Win32 SetWindowPos in PHYSICAL
/// pixels — never through Avalonia's own DIP-based Position/Width/Height — matching every other
/// mixed-DPI-sensitive window in this app (see OverlayWindow's own "never reposition/resize
/// post-Show" discipline and its Avalonia #13917/#17834 citation).
///
/// Windows-only by construction: every public entry point below no-ops on non-Windows, so a caller
/// (OverlayController) can use this class unconditionally and rely on the OS gate living here rather
/// than duplicating it at every call site. This is also what makes ROESNIP_NO_FLASH=1's fallback
/// path (direct capture-then-show — see AppShell/TrayApp.TriggerCapture) the PERMANENT behavior on
/// Linux/macOS: Wayland in particular forbids a client positioning its own window at all, so
/// off-screen parking cannot exist there (accepted limitation — see docs/PARITY.md). UI (dispatcher)
/// thread only.
///
/// Input policy: the windows deliberately SWALLOW input rather than click through — while the flash
/// is up the user believes the snip UI is active, so a click must do nothing rather than land in
/// whatever app is underneath. This swallow comes from being topmost + non-click-through for Win32
/// hit-testing (clicks go to whatever's topmost under the cursor by z-order); it does NOT depend on
/// holding OS foreground/activation — ShowAllCore no longer stakes any foreground claim at all
/// (2026-08-02, ported from the WPF reference's own removal — see its doc comment). Esc is the one
/// key acted on via a focus-independent WH_KEYBOARD_LL hook (FlashEscapeHook, installed by
/// OverlayController.TryShowFlash) — it fires regardless of which window has OS focus, so it is
/// unaffected by the foreground-claim removal; each FlashWindow's own OnKeyDown handler is a
/// focus-dependent fallback for the same key, in case the hook failed to install.</summary>
internal static class FlashDimmer
{
    private static readonly List<FlashWindow> s_windows = new();

    // Reentrancy guard (ported from the WPF reference, same rationale): a nested pump inside
    // PrepareHidden's priming Dispatcher.Invoke can dispatch an unrelated queued callback (e.g. a
    // hotkey landing mid-EnsureCreated) that reenters EnsureCreated while s_windows is only
    // partially rebuilt. Bail rather than race the in-flight call — the flash is a best-effort,
    // non-fatal perceived-latency optimization, so simply not flashing on the reentrant call is
    // acceptable.
    private static bool s_ensuringCreated;

    // Foreground-claim epoch — NO LONGER LOAD-BEARING (2026-08-02, ported from the WPF reference):
    // this used to guard ShowAllCore's own best-effort background-thread SetForegroundWindow call
    // against racing the real overlay session's later, more robust
    // ForegroundActivator.Activate("session-start") claim. That ShowAllCore-side claim has since
    // been deleted outright (it was racing ahead of CaptureAll() actually reading pixels and
    // dismissing tooltips/hover UI that was on screen at hotkey-press time — see ShowAllCore's own
    // comment at the removal site) — the flash phase now stakes NO foreground claim of any kind.
    // s_foregroundClaimEpoch, InvalidateForegroundClaim, s_foregroundBeforeClaim and
    // TryRestoreForegroundFromFlash are all kept anyway, deliberately, as a no-op safety net: with
    // nothing ever queuing a claim, TryRestoreForegroundFromFlash's own "is the foreground currently
    // one of our flash windows" check simply never trips in the normal case, so it costs nothing to
    // leave wired up as cheap insurance against some future change reintroducing a claim here
    // without reintroducing this guard alongside it.
    private static int s_foregroundClaimEpoch;

    // The HWND that was foreground just before ShowAll's best-effort claim — the restore target for
    // TryRestoreForegroundFromFlash. UI thread writes (ShowAllCore), UI thread reads.
    private static IntPtr s_foregroundBeforeClaim;

    public static void InvalidateForegroundClaim()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        Interlocked.Increment(ref s_foregroundClaimEpoch);
    }

    /// <summary>True while any flash window is genuinely on-screen — used by OverlayController to
    /// decide whether a starting session was hotkey-initiated (its latency logs then measure from
    /// the flash timestamp).</summary>
    public static bool AnyVisible
    {
        get
        {
            foreach (var window in s_windows)
            {
                if (window.IsPresented)
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>Pre-creates (or recreates, when the monitor set changed) one flash window per
    /// monitor, parked off-screen. Safe to call repeatedly; a matching set is a no-op. See ShowAll's
    /// own doc comment for <paramref name="presentAsBuilt"/>.</summary>
    public static void EnsureCreated(IReadOnlyList<MonitorInfo> monitors, bool presentAsBuilt = false)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        EnsureCreatedCore(monitors, presentAsBuilt);
    }

    [SupportedOSPlatform("windows")]
    private static void EnsureCreatedCore(IReadOnlyList<MonitorInfo> monitors, bool presentAsBuilt)
    {
        if (Matches(monitors))
        {
            return;
        }
        if (s_ensuringCreated)
        {
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
    /// instead of depending on when the pump next renders. NOTE (post-sleep stall fix): the
    /// capture+tonemap stretch that follows now runs on the THREAD POOL — this UI thread keeps
    /// pumping during it, so dispatcher work (another trigger, display-change handlers, queued idle
    /// items) CAN interleave with a capture in flight; guards that assume "nothing runs mid-capture"
    /// are wrong now (see the foreground-snapshot guard just below, and OverlayController's
    /// in-flight-flow reasoning where applicable).</summary>
    public static void ShowAll(IReadOnlyList<MonitorInfo> monitors)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        ShowAllCore(monitors);
    }

    [SupportedOSPlatform("windows")]
    private static void ShowAllCore(IReadOnlyList<MonitorInfo> monitors)
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
            IntPtr fg = NativeMethods.GetForegroundWindow();
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
        var buildOrder = coldBuild ? OrderCursorMonitorFirst(monitors) : monitors;
        EnsureCreated(buildOrder, presentAsBuilt: coldBuild);

        // Presentation order is recomputed cursor-first on EVERY call (not just cold builds) and
        // resolved against s_windows BY NAME — see MonitorPresentationOrder.SetsMatch's own doc
        // comment for why a positional assumption here would be wrong.
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

        // Foreground claim REMOVED here (capture-fidelity fix, 2026-08-02 — ported from the WPF
        // reference, src/RoeSnip/Overlay/FlashDimmer.cs's own ShowAll). This used to fire a
        // best-effort SetForegroundWindow off a background thread at this exact point (targeting
        // the cursor monitor's flash window, "first" in the removed code): SetForegroundWindow
        // reassigns OS foreground activation, which is exactly the Win32 mechanism comctl32
        // tooltips and most custom hover popups use to self-dismiss (WM_ACTIVATE/WM_ACTIVATEAPP) —
        // so anything on screen at hotkey-press time (a tooltip, a hover menu) was being silently
        // dismissed by THIS call, racing ahead of CaptureAll() ever reading pixels, before the
        // user's own capture had a chance to see it. It is not load-bearing for anything this flow
        // still needs:
        //   - Input-swallow comes from being topmost + non-click-through (Reposition's
        //     SWP_NOACTIVATE never activated anything and never needed to).
        //   - Esc during the flash phase is covered focus-independently by FlashEscapeHook (a
        //     WH_KEYBOARD_LL hook installed in TryShowFlash) — it does not depend on this claim.
        //   - The real overlay session already claims foreground once it opens (OverlayController's
        //     own _activeWindow.Activate() call, always preceded by InvalidateForegroundClaim() —
        //     see item 18's comment at that call site), which is always after CaptureAll() has
        //     returned frames.
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
        // topmost, non-click-through window over whatever was under the cursor, with SwpNoActivate —
        // that never claims foreground, but it DOES change what WindowFromPoint(cursor) resolves to,
        // which can independently make an already-hovering tooltip's own hover-tracking
        // (TrackMouseEvent) self-dismiss via WM_MOUSELEAVE. That residual path is not touched by this
        // removal; see docs/CAPTURE-FIDELITY-SPEC.md item 1.

        // DispatcherPriority.Loaded sits just below Render, so this flush drains every pending
        // layout/render operation without dispatching lower-priority queued work (matches the WPF
        // reference's own Dispatcher.CurrentDispatcher.Invoke(..., DispatcherPriority.Loaded)).
        Dispatcher.UIThread.Invoke(static () => { }, DispatcherPriority.Loaded);

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

    private static IReadOnlyList<MonitorInfo> OrderCursorMonitorFirst(IReadOnlyList<MonitorInfo> monitors)
    {
        if (monitors.Count < 2 || !TryGetCursorPos(out int cx, out int cy))
        {
            return monitors;
        }
        return MonitorPresentationOrder.OrderCursorMonitorFirst(monitors, cx, cy);
    }

    /// <summary>True while a genuinely on-screen flash window covers the given monitor.</summary>
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

    /// <summary>Hides the flash on one monitor — called once that monitor's real overlay window has
    /// been shown, so the swap is per-monitor and zero-gap. No-ops on non-Windows (there is never
    /// anything to hide there).</summary>
    public static void HideForMonitor(string deviceName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        HideForMonitorCore(deviceName);
    }

    // Per-window exception isolation in the two Hide*Core methods (stuck-dim fix, ported from the
    // WPF app): one failed SetWindowPos must not abort hiding the remaining monitors — the callers
    // all swallow the exception, so before this a single throw stranded every later window dimmed
    // with s_flashUsers already at zero and AnyVisible then blocking every future prewarm rebuild
    // until process restart.
    [SupportedOSPlatform("windows")]
    private static void HideForMonitorCore(string deviceName)
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

    /// <summary>No-ops on non-Windows (there is never anything to hide there) — callers (e.g.
    /// OverlayController's Finish/ReleaseFlash/OnFlashEscape) call this unconditionally, matching
    /// FlashDimmer's own "every public entry point self-guards" convention.</summary>
    public static void HideAll()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        HideAllCore();
    }

    [SupportedOSPlatform("windows")]
    private static void HideAllCore()
    {
        foreach (var window in s_windows)
        {
            try { window.HideFlash(); }
            catch (Exception ex) { FileLog.Write($"RoeSnip: hiding a flash window failed: {ex.Message}"); }
        }
    }

    /// <summary>Focus hygiene for the flash-phase exits that never open a session (Esc during the
    /// flash, capture failed/timed out). NO-OP SAFETY NET as of 2026-08-02 (see
    /// s_foregroundClaimEpoch's doc comment): ShowAllCore no longer stakes any foreground claim, so
    /// the "is the foreground one of our flash windows" check below never trips in the normal case —
    /// this is kept purely as cheap insurance in case some future change reintroduces a claim
    /// without reintroducing this restore alongside it. Bumps the claim epoch first so an in-flight
    /// background claim (if one ever existed again) can't re-steal afterwards. UI thread;
    /// best-effort. No-ops on non-Windows per this class's convention.</summary>
    public static void TryRestoreForegroundFromFlash()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        InvalidateForegroundClaim();
        try
        {
            IntPtr foreground = NativeMethods.GetForegroundWindow();
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
            SetForegroundWindow(previous);
            s_foregroundBeforeClaim = IntPtr.Zero; // one restore per snapshot — never reuse across flows
        }
        catch { /* best-effort, same contract as the claim itself */ }
    }

    // ---------- Dead-man watchdog (stuck-dim backstop, ported from the WPF app) ----------
    //
    // Belt-and-braces behind every architectural fix above: if a flash window has been presented
    // continuously for far longer than any legitimate flow can keep it (the capture deadline plus
    // overlay construction is well under half of this), force-park it from a background thread.
    // The park is a raw async SetWindowPos (SwpAsyncWindowPos: POSTS the move rather than blocking
    // on the window's possibly-wedged owner thread) — never an Avalonia Hide(), which would destroy
    // the warm composited surface the park-don't-hide design depends on. Zero cost while parked:
    // the timer only runs while something is presented and disarms itself when nothing is.
    private const int WatchdogMaxPresentedMs = 30_000;
    private static readonly object s_watchdogGate = new();
    private static Timer? s_watchdogTimer;
    // Bumped by every ArmWatchdog (under the gate): a tick that concluded "nothing presented" from
    // a snapshot taken BEFORE a fresh arm must not disarm the timer that arm just scheduled — the
    // disarm below is generation-checked (same epoch pattern as s_foregroundClaimEpoch).
    private static int s_watchdogGeneration;

    private static void ArmWatchdog()
    {
        lock (s_watchdogGate)
        {
            s_watchdogGeneration++;
            s_watchdogTimer ??= new Timer(WatchdogTick, null, Timeout.Infinite, Timeout.Infinite);
            s_watchdogTimer.Change(5_000, 5_000);
        }
    }

    private static void WatchdogTick(object? state)
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // unreachable (ArmWatchdog only runs from ShowAllCore) — analyzer guard
        }
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
                        s_watchdogTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: flash watchdog tick failed (non-fatal): {ex.Message}");
        }
    }

    /// <summary>Order-independent by design — see MonitorPresentationOrder.SetsMatch's own doc
    /// comment.</summary>
    private static bool Matches(IReadOnlyList<MonitorInfo> monitors)
    {
        var have = new List<MonitorInfo>(s_windows.Count);
        foreach (var window in s_windows)
        {
            have.Add(window.Monitor);
        }
        return MonitorPresentationOrder.SetsMatch(have, monitors);
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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    /// <summary>Physical-pixel cursor position (virtual-desktop coordinates).</summary>
    private static bool TryGetCursorPos(out int x, out int y)
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

    /// <summary>One monitor's parked flash window. See the class doc comment for the "park, don't
    /// hide" lifecycle these methods implement.</summary>
    private sealed class FlashWindow : Window
    {
        public MonitorInfo Monitor { get; }

        /// <summary>Explicit on-screen tracking — "hidden" really means "parked off the virtual
        /// desktop at x = OffScreenX", a distinction Avalonia's own visibility notion has no concept
        /// of once this window is permanently Show()n. True only while this window's real HWND has
        /// actually been moved onto its monitor's bounds.</summary>
        public bool IsPresented { get; private set; }

        /// <summary>Environment.TickCount64 of the most recent move on-screen — the watchdog's
        /// "how long has this dim been up" clock. Only meaningful while IsPresented.</summary>
        public long PresentedSinceTick { get; private set; }

        /// <summary>The HWND cached at PrepareHidden — lets the watchdog's timer thread (and
        /// TryRestoreForegroundFromFlash's identity check) address this window without touching any
        /// Avalonia API (TryGetPlatformHandle is UI-thread territory in spirit; a plain IntPtr
        /// field read is not).</summary>
        public IntPtr CachedHwnd => _hwnd;

        private bool _closingForReal;
        private IntPtr _hwnd;

        // Same value as OverlayWindow's own off-screen park constant (private to that class, hence
        // the duplicate here) — far off the virtual desktop (monitors span roughly x:[-1440, 2560]
        // on this machine; any multi-monitor rig stays well inside a much larger margin).
        private const int OffScreenX = 60000;

        // Must equal OverlayWindow.axaml's DimPath fill (#8A000000) so the flash-to-overlay swap is
        // invisible on the (undimmed... dimmed, i.e. everywhere pre-selection) area underneath.
        private static readonly Color DimColor = Color.FromArgb(0x8A, 0x00, 0x00, 0x00);

        public FlashWindow(MonitorInfo monitor)
        {
            Monitor = monitor;
            WindowDecorations = WindowDecorations.None;
            CanResize = false;
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            // Nothing in ShowAllCore ever OS-activates a flash window anymore (2026-08-02, the
            // background SetForegroundWindow call was deleted outright — see ShowAllCore's own
            // comment at the removal site). ShowActivated=false just suppresses Avalonia's own
            // implicit activate-on-Show(), which would otherwise fire once at PrepareHidden's
            // one-time Show() call even though this window is parked off-screen at that point.
            ShowActivated = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Focusable = true;
            Cursor = new Cursor(StandardCursorType.Cross);
            Content = new Border { Background = new SolidColorBrush(DimColor) };

            var bounds = monitor.BoundsPx;
            double scale = monitor.Scale > 0 ? monitor.Scale : 1.0;
            Position = new PixelPoint(OffScreenX, bounds.Top);
            Width = bounds.Width / scale;
            Height = bounds.Height / scale;

            KeyDown += OnKeyDown;
        }

        private static void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                RoeSnip.App.Overlay.OverlayController.OnFlashEscape();
            }
            // Every other key is swallowed implicitly: this window (when focused) has no other
            // handlers, so nothing leaks through to the app underneath.
        }

        /// <summary>Creates the HWND, parks it off-screen, and Show()s it exactly ONCE — the window
        /// stays presented (parked) for the rest of the process's life; see the class doc's "park,
        /// don't hide" section for why. Only ever constructed/called from FlashDimmer's own
        /// Windows-gated Core methods (see e.g. EnsureCreatedCore), hence the attribute here rather
        /// than another internal OperatingSystem.IsWindows() check.</summary>
        [SupportedOSPlatform("windows")]
        public void PrepareHidden()
        {
            Show();
            _hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (_hwnd == IntPtr.Zero)
            {
                FileLog.Write("RoeSnip: flash dimmer window has no native handle after Show(); skipping it.");
                return;
            }

            // WS_EX_TOOLWINDOW: this window is about to become permanently OS-visible for the app's
            // entire lifetime (parked off-screen or dimming a monitor) — without this it would show
            // up as its own Alt+Tab entry. ShowInTaskbar=false alone only keeps it off the taskbar.
            //
            // WS_EX_TRANSPARENT (click-through) is what lets a hovered tooltip survive into the
            // capture, and it is the half of that fix the foreground-claim removal did not cover:
            // this window is shown BEFORE the frame is read, and a topmost window that takes part in
            // hit testing steals WindowFromPoint from whatever the cursor was over, dropping that
            // window's hover tracking (WM_MOUSELEAVE) and dismissing its tooltip with no dependence
            // on focus at all. Verified end to end on Windows: a hover tooltip that is up when the
            // hotkey is pressed now appears in the captured frame. Input is still swallowed for the
            // flash phase, by FlashMouseSwallowHook rather than by hit testing.
            long exStyle = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GwlExStyle).ToInt64();
            NativeMethods.SetWindowLongPtr(
                _hwnd,
                NativeMethods.GwlExStyle,
                new IntPtr(exStyle | NativeMethods.WsExToolWindow | NativeMethods.WsExTransparent));

            // CRITICAL: the flash is shown BEFORE the capture runs, so without this every screenshot
            // would contain the flash's own dim baked into the pixels. See
            // WindowCaptureExclusion's own doc comment for the ROESNIP_DIAG_NOEXCLUDE=1 escape hatch.
            WindowCaptureExclusion.Apply(this);

            // Explicit pixel-exact re-assert (Avalonia's own DIP Position/Width/Height above is only
            // approximate under a non-integer scale) — see the class doc's positioning discipline.
            Reposition(onScreen: false);
            Dispatcher.UIThread.Invoke(static () => { }, DispatcherPriority.Loaded);
            // Deliberately no Hide() here — see the class doc's "park, don't hide" section.
            // IsPresented is already false (Reposition(false) just set it).
        }

        [SupportedOSPlatform("windows")]
        public void ShowOnMonitor()
        {
            if (IsPresented)
            {
                return;
            }
            Reposition(onScreen: true);
        }

        /// <summary>Moves this already-Show()n, already-composited window onto its real monitor
        /// bounds (onScreen: true) or fully off-screen to its parked position (onScreen: false) — the
        /// ONLY thing ShowOnMonitor/HideFlash do (park-don't-hide design); no Avalonia Show()/Hide()
        /// call after PrepareHidden's one-time Show(). Re-asserts physical-pixel bounds + topmost on
        /// every on-screen move. Named to avoid colliding with the base Window.Position property.</summary>
        [SupportedOSPlatform("windows")]
        private void Reposition(bool onScreen)
        {
            if (_hwnd == IntPtr.Zero)
            {
                return;
            }
            var b = Monitor.BoundsPx;
            int x = onScreen ? b.Left : OffScreenX;
            NativeMethods.SetWindowPos(
                _hwnd, NativeMethods.HwndTopmost, x, b.Top, b.Width, b.Height, NativeMethods.SwpNoActivate);
            if (onScreen)
            {
                PresentedSinceTick = Environment.TickCount64;
            }
            IsPresented = onScreen;
        }

        /// <summary>The dead-man watchdog's force-park — the ONLY member of this class that runs
        /// off the UI thread. Raw SetWindowPos with SwpAsyncWindowPos: the move request is POSTED
        /// to the window's owner thread rather than delivered synchronously, so this can never
        /// deadlock on that thread's (possibly wedged) message queue; a raw Win32 call on a cached
        /// HWND has no managed/Avalonia thread affinity, which is exactly why this deliberately
        /// avoids Avalonia's Position property (that would need a Dispatcher.UIThread.Post — which
        /// only helps when the UI thread pumps; the raw call works even when it doesn't).
        /// IsPresented is a plain bool write — benign cross-thread worst case is one redundant
        /// park.</summary>
        [SupportedOSPlatform("windows")]
        public void ForceParkFromWatchdogThread()
        {
            var hwnd = _hwnd;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }
            var b = Monitor.BoundsPx;
            NativeMethods.SetWindowPos(
                hwnd, NativeMethods.HwndTopmost, OffScreenX, b.Top, b.Width, b.Height,
                NativeMethods.SwpNoActivate | NativeMethods.SwpAsyncWindowPos);
            IsPresented = false;
        }

        [SupportedOSPlatform("windows")]
        public void HideFlash()
        {
            if (!IsPresented)
            {
                return;
            }
            Reposition(onScreen: false);
        }

        public void CloseFlash()
        {
            _closingForReal = true;
            Close();
        }

        [SupportedOSPlatform("windows")]
        protected override void OnClosing(WindowClosingEventArgs e)
        {
            // These windows are reused across sessions; nothing external should be able to close one
            // — park it instead. Only FlashDimmer's own CloseFlash (monitor-set recreation) really
            // closes.
            if (!_closingForReal)
            {
                e.Cancel = true;
                HideFlash();
                return;
            }
            base.OnClosing(e);
        }
    }

    /// <summary>Local P/Invoke, scoped to what this file needs (matches WindowCaptureExclusion's own
    /// per-file convention rather than a shared NativeMethods class).</summary>
    [SupportedOSPlatform("windows")]
    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(
            IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        public static readonly IntPtr HwndTopmost = new(-1);
        public const uint SwpNoActivate = 0x0010;
        public const uint SwpAsyncWindowPos = 0x4000;

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        public const int GwlExStyle = -20;
        public const int WsExToolWindow = 0x00000080;
        public const int WsExTransparent = 0x00000020; // click-through: invisible to hit testing
    }
}
