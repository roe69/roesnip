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
/// milliseconds of the trigger, BEFORE the capture+tonemap stretch that otherwise blocks the UI
/// thread for the app's whole "hotkey to overlay" latency; because the frozen preview the real
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
/// thread only.</summary>
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

    // Foreground-claim epoch (ported from the WPF reference): ShowAll's best-effort background-
    // thread SetForegroundWindow call is deliberately unsynchronized with the rest of the flow so it
    // can run CONCURRENTLY with the UI thread's blocking capture+tonemap stretch. If that call is
    // delayed past the point the real overlay session claims its OWN foreground, it must not steal
    // focus back — InvalidateForegroundClaim bumps this epoch right before any real foreground
    // claim, and the background task checks it hasn't moved before actually calling
    // SetForegroundWindow.
    private static int s_foregroundClaimEpoch;

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
    /// capture flow that follows BLOCKS the UI thread for its whole capture+tonemap stretch, during
    /// which no dispatcher work runs, so the dim must actually reach the screen NOW.</summary>
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
        bool coldBuild = !Matches(monitors);
        var buildOrder = coldBuild ? OrderCursorMonitorFirst(monitors) : monitors;
        EnsureCreated(buildOrder, presentAsBuilt: coldBuild);

        // Presentation order is recomputed cursor-first on EVERY call (not just cold builds) and
        // resolved against s_windows BY NAME — see MonitorPresentationOrder.SetsMatch's own doc
        // comment for why a positional assumption here would be wrong.
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

        // Best-effort activation, off the UI thread (ported from the WPF reference): a raw HWND
        // SetForegroundWindow has no dispatcher-thread affinity, so it runs CONCURRENTLY with the UI
        // thread's blocking capture+tonemap stretch that follows rather than waiting behind it.
        if (first is not null)
        {
            var handle = first.TryGetPlatformHandle();
            if (handle is not null && handle.Handle != IntPtr.Zero)
            {
                IntPtr hwnd = handle.Handle;
                int claimEpoch = Volatile.Read(ref s_foregroundClaimEpoch);
                Task.Run(() =>
                {
                    try
                    {
                        if (Volatile.Read(ref s_foregroundClaimEpoch) != claimEpoch)
                        {
                            return; // superseded by a real activation claim — see the field's doc comment
                        }
                        SetForegroundWindow(hwnd);
                    }
                    catch { /* foreground-lock restrictions — best-effort */ }
                });
            }
        }

        // DispatcherPriority.Loaded sits just below Render, so this flush drains every pending
        // layout/render operation without dispatching lower-priority queued work (matches the WPF
        // reference's own Dispatcher.CurrentDispatcher.Invoke(..., DispatcherPriority.Loaded)).
        Dispatcher.UIThread.Invoke(static () => { }, DispatcherPriority.Loaded);
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

    [SupportedOSPlatform("windows")]
    private static void HideForMonitorCore(string deviceName)
    {
        foreach (var window in s_windows)
        {
            if (string.Equals(window.Monitor.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                window.HideFlash();
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
            window.HideFlash();
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
        s_windows.Clear();
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
            ShowActivated = false; // activation is explicit (ShowAll activates only the first)
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
            long exStyle = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GwlExStyle).ToInt64();
            NativeMethods.SetWindowLongPtr(
                _hwnd, NativeMethods.GwlExStyle, new IntPtr(exStyle | NativeMethods.WsExToolWindow));

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
            IsPresented = onScreen;
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

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        public const int GwlExStyle = -20;
        public const int WsExToolWindow = 0x00000080;
    }
}
