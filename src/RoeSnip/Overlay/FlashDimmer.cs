using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using RoeSnip.Capture;
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
/// hotkey, BEFORE the UI-thread-blocking capture+tonemap stretch; because the frozen preview the
/// real overlay then displays equals the live screen, each monitor's flash can be hidden the
/// moment that monitor's real OverlayWindow has rendered (ContentRendered) with no visible seam.
///
/// Input policy: the windows deliberately SWALLOW input rather than click through — while the
/// flash is up the user believes the snip UI is active, so a click must do nothing rather than
/// land in whatever app is underneath. Esc is the one key acted on (cancels the pending capture
/// via OverlayController.OnFlashEscape); it can only be delivered if the best-effort Activate in
/// ShowAll won (foreground-lock restrictions apply — once the real session opens, its
/// SessionKeyboardHook covers Esc regardless of focus).
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

    // Foreground-claim epoch (review fix, r5-latency S3 follow-up): ShowAll's best-effort
    // background-thread SetForegroundWindow call (see below) is deliberately unsynchronized with
    // the rest of the flow so it can run CONCURRENTLY with the UI thread's blocking capture+
    // tonemap stretch. Nothing else about it is synchronized, though: OverlaySession.RunAsync
    // separately calls its own ForegroundActivator.Activate for the real overlay window once
    // capture finishes, on the UI thread. Both calls target the process's OWN windows, so if the
    // flash's call is delayed (thread-pool cold start, AV/driver interference, a slower-than-usual
    // negotiation) it can complete AFTER the overlay has already won the foreground — flipping OS
    // keyboard focus back onto the (still on-screen at that point; flash-hide is itself deferred a
    // beat past the overlay's activation, see OnOverlayContentRendered) flash window, silently
    // stealing input away from the visible, focused-looking overlay. InvalidateForegroundClaim is
    // called right before every place that stakes a REAL foreground claim of its own (the overlay
    // session's own activation, and session teardown) so a flash call still in flight at that
    // point sees a bumped epoch and skips its own SetForegroundWindow — shrinking the race from
    // "the whole capture+construction stretch" to a negligible instant. Not a full mutex (two
    // native SetForegroundWindow calls from different threads can't be made atomic against each
    // other short of one), but a proportionate mitigation matching this call's existing
    // best-effort/non-fatal contract.
    private static int s_foregroundClaimEpoch;

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
                s_windows.Add(window);
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

    /// <summary>Shows the dim on every monitor and flushes layout+render before returning: the
    /// capture flow that follows BLOCKS the UI thread for its whole capture+tonemap stretch,
    /// during which no dispatcher work runs, so the dim must actually reach the screen NOW.
    /// (DispatcherPriority.Loaded sits just below Render, so the flush drains every pending
    /// layout/render operation without dispatching lower-priority queued work.)</summary>
    public static void ShowAll(IReadOnlyList<MonitorInfo> monitors)
    {
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
        // and is otherwise irrelevant now that Matches() is order-independent. This is also what
        // guarantees `first` (the best-effort foreground target below) is genuinely the cursor's
        // own monitor window, not just whatever happens to be s_windows[0].
        var presentationOrder = OrderCursorMonitorFirst(monitors);
        FlashWindow? first = null;
        foreach (var monitor in presentationOrder)
        {
            var window = FindWindow(monitor.DeviceName);
            if (window is null)
            {
                continue; // shouldn't happen post-EnsureCreated, but never crash the flash path over it
            }
            window.ShowOnMonitor(); // no-op for any window the cold-build path already presented
            first ??= window;
        }

        // Best-effort activation, off the UI THREAD (r5-latency, first-trigger fix): instrumenting
        // ShowAll found that with parking eliminating the WPF Show()/Hide() cost, a synchronous
        // first?.Activate() call right here was the entire remaining first-trigger outlier — 63 ms
        // on a genuinely cold SetForegroundWindow negotiation (Windows' foreground-lock machinery
        // costs real time to resolve the first time a background process asks for it) versus 4-9 ms
        // on every later trigger, once Windows has already granted this process the foreground once.
        // A first attempt deferred this via Dispatcher.BeginInvoke(Background) — WRONG: the capture
        // flow that TriggerCapture starts right after ShowAll returns runs its whole capture+tonemap
        // stretch SYNCHRONOUSLY on this same UI thread with no intervening await (confirmed by
        // reading RunCaptureFlowAsync), so a Background-priority dispatcher item can't run until
        // that entire stretch finishes — by which point the real overlay session has already opened
        // and grabbed its OWN foreground activation, making the flash's activation pointless and
        // silently breaking "Esc during the flash phase" reaching the flash window at all. Fixed by
        // calling the raw Win32 SetForegroundWindow directly on a background THREAD instead of the
        // UI thread's dispatcher queue: unlike WPF's Window.Activate() (which is dispatcher-thread-
        // affine), a raw HWND-based Win32 call has no thread affinity, so it runs CONCURRENTLY with
        // the UI thread's blocking capture+tonemap stretch rather than waiting behind it — the ~60ms
        // negotiation on a cold call comfortably finishes within that ~100-150 ms stretch, so the
        // flash window has already won focus by the time a same-phase Esc could be typed, same as
        // the original synchronous call achieved (when it was fast). Getting the HWND itself must
        // stay on the UI thread (WindowInteropHelper touches the WPF Window), but that's a cheap
        // field read, not the slow part. Failure is still best-effort/non-fatal, per the class doc.
        if (first is not null)
        {
            var hwnd = new WindowInteropHelper(first).Handle;
            if (hwnd != IntPtr.Zero)
            {
                // Snapshot the epoch now; the background task only fires SetForegroundWindow if
                // nothing has invalidated this claim by the time it actually runs — see
                // s_foregroundClaimEpoch's doc comment for the race this closes.
                int claimEpoch = System.Threading.Volatile.Read(ref s_foregroundClaimEpoch);
                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        if (System.Threading.Volatile.Read(ref s_foregroundClaimEpoch) != claimEpoch)
                        {
                            return; // superseded by a real activation claim — see class doc
                        }
                        NativeMethods.SetForegroundWindow(hwnd);
                    }
                    catch { /* foreground-lock restrictions — see class doc */ }
                });
            }
        }

        Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.Loaded);
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
    /// ContentRendered so the swap is per-monitor and zero-gap.</summary>
    public static void HideForMonitor(string deviceName)
    {
        foreach (var window in s_windows)
        {
            if (string.Equals(window.Monitor.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                window.HideFlash();
            }
        }
    }


    public static void HideAll()
    {
        foreach (var window in s_windows)
        {
            window.HideFlash();
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
                Console.Error.WriteLine($"RoeSnip: closing a flash dimmer window failed: {ex.Message}");
            }
        }
        s_windows.Clear();
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
            ShowActivated = false; // activation is explicit (ShowAll activates only the first)
            WindowStartupLocation = WindowStartupLocation.Manual;
            Focusable = true;
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

            // WS_EX_TOOLWINDOW (park-don't-hide): this window is about to become permanently
            // OS-visible for the app's entire lifetime (see below) — without this extended style it
            // would show up as its own Alt+Tab entry despite always living either parked at
            // x=60000 or dimming a monitor. ShowInTaskbar=false alone only keeps it off the
            // taskbar, not Alt+Tab.
            long exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            NativeMethods.SetWindowLongPtr(
                hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle | NativeMethods.WS_EX_TOOLWINDOW));

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
                Console.Error.WriteLine(
                    "RoeSnip: SetWindowDisplayAffinity(EXCLUDEFROMCAPTURE) failed on a flash window — " +
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
            IsPresented = onScreen;
        }

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
