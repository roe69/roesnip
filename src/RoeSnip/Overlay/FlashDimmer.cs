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
/// Lifecycle: AllowsTransparency (layered) windows are expensive to CREATE (~100 ms each) but
/// cheap to re-Show, so the windows are pre-created hidden (TrayApp warmup calls
/// OverlayController.PrewarmFlash) and reused across sessions via Show/Hide; they hold no
/// session state. A changed monitor set — compared by device name + physical bounds on every
/// EnsureCreated — closes and recreates them (the WM_DISPLAYCHANGE-style path: TrayApp re-prewarms
/// on SystemEvents.DisplaySettingsChanged). UI (dispatcher) thread only.</summary>
internal static class FlashDimmer
{
    private static readonly List<FlashWindow> s_windows = new();

    /// <summary>True while any flash window is on screen — used by OverlayController to decide
    /// whether a starting session was hotkey-initiated (its latency logs then measure from the
    /// flash timestamp) and to keep a prewarm from recreating windows mid-flash.</summary>
    public static bool AnyVisible
    {
        get
        {
            foreach (var window in s_windows)
            {
                if (window.IsVisible) return true;
            }
            return false;
        }
    }

    /// <summary>Pre-creates (or recreates, when the monitor set changed) one hidden flash window
    /// per monitor. Safe to call repeatedly; a matching set is a no-op.</summary>
    public static void EnsureCreated(IReadOnlyList<MonitorInfo> monitors)
    {
        if (Matches(monitors))
        {
            return;
        }

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
        }
    }

    /// <summary>Shows the dim on every monitor and flushes layout+render before returning: the
    /// capture flow that follows BLOCKS the UI thread for its whole capture+tonemap stretch,
    /// during which no dispatcher work runs, so the dim must actually reach the screen NOW.
    /// (DispatcherPriority.Loaded sits just below Render, so the flush drains every pending
    /// layout/render operation without dispatching lower-priority queued work.)</summary>
    public static void ShowAll(IReadOnlyList<MonitorInfo> monitors)
    {
        EnsureCreated(monitors);

        FlashWindow? first = null;
        foreach (var window in s_windows)
        {
            window.ShowOnMonitor();
            first ??= window;
        }

        // Best-effort activation so an Esc typed during the flash phase reaches our KeyDown
        // handler instead of the previously-focused app. Failure is acceptable: the session
        // keyboard hook takes over the moment the real overlay session opens.
        try { first?.Activate(); }
        catch { /* foreground-lock restrictions — see class doc */ }

        Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.Loaded);
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

    private static bool Matches(IReadOnlyList<MonitorInfo> monitors)
    {
        if (s_windows.Count != monitors.Count)
        {
            return false;
        }
        for (int i = 0; i < monitors.Count; i++)
        {
            var have = s_windows[i].Monitor;
            var want = monitors[i];
            if (!string.Equals(have.DeviceName, want.DeviceName, StringComparison.OrdinalIgnoreCase)
                || have.BoundsPx != want.BoundsPx)
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

        /// <summary>Creates the HWND and positions it on its monitor WITHOUT showing — pre-pays
        /// the expensive layered-window creation during warmup.</summary>
        public void PrepareHidden()
        {
            var hwnd = new WindowInteropHelper(this).EnsureHandle();
            // CRITICAL: the flash is shown BEFORE the capture runs, so without this every
            // screenshot would contain the flash's own 45% dim baked into the pixels (user-reported
            // round-5 bug: "its screenshotting the dimmed screen state instead of the pre-dimmed
            // state"). WDA_EXCLUDEFROMCAPTURE makes the window visible to the user but invisible
            // to WGC/DD/print-screen capture paths (Win10 2004+). Failure is non-fatal but must be
            // loud — a silent failure silently corrupts every screenshot.
            if (!SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE))
            {
                Console.Error.WriteLine(
                    "RoeSnip: SetWindowDisplayAffinity(EXCLUDEFROMCAPTURE) failed on a flash window — " +
                    "captures taken while the flash is up will include the dim!");
            }
            Position(show: false);
        }

        private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

        public void ShowOnMonitor()
        {
            if (IsVisible)
            {
                return;
            }
            Show();
            // Re-assert physical-pixel bounds + topmost on every show (same mixed-DPI pattern as
            // OverlayWindow: position via Win32 in physical pixels, never WPF DIP properties).
            Position(show: true);
        }

        private void Position(bool show)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }
            var b = Monitor.BoundsPx;
            uint flags = NativeMethods.SWP_NOACTIVATE | (show ? NativeMethods.SWP_SHOWWINDOW : 0u);
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, b.Left, b.Top, b.Width, b.Height, flags);
        }

        public void HideFlash()
        {
            if (IsVisible)
            {
                Hide();
            }
        }

        public void CloseFlash()
        {
            _closingForReal = true;
            Close();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // These windows are reused across sessions; nothing external should be able to close
            // one (e.g. Alt+F4 while a flash is focused) — hide it instead. Only FlashDimmer's own
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
