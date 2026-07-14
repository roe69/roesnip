using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;
using RoeSnip.Core.Diagnostics;

namespace RoeSnip.App.AppShell;

/// <summary>Windows-only capture-exclusion seam: applies WDA_EXCLUDEFROMCAPTURE to a window's
/// HWND so it stays visible to the user but invisible to WGC/DD/print-screen capture paths
/// (Win10 2004+). Ported from four WPF call sites that each did this inline — src/RoeSnip/
/// Overlay/OverlayWindow.xaml.cs:536-555 (every overlay window, belt-and-braces for pooled
/// windows plus a genuine need for on-demand ones), FlashDimmer.cs:489-499 (the flash windows —
/// CRITICAL there, since the flash is shown BEFORE the capture runs and would otherwise bake its
/// own dim into every screenshot), ColorPickerWindow.xaml.cs:65-79 (a "Pick" re-capture freezes
/// the screen with this window still up), and AutomationServer.cs:697-745 (the clear/restore
/// pair backing the automation screenshot command's `includeExcluded` flag). Centralized here so
/// every Avalonia window (overlay now; flash dimmer/recording chrome/color picker in later
/// items) gets identical behavior from one place instead of four copies drifting apart.
///
/// Same invariant as the WPF app's OverlayWindow comment: this app never captures the screen
/// while any of its own windows are on it (the capture flow always runs to completion strictly
/// BEFORE a window is ever constructed or shown), so applying the exclusion permanently for a
/// window's whole lifetime is simpler and just as safe as toggling it on/off per capture — there
/// is no path where clearing it would matter for THAT window's own correctness. The one case
/// that legitimately needs the flag off temporarily is the automation screenshot command's
/// `includeExcluded` flag (item 03), which is why <see cref="ClearOnOwnWindows"/> and
/// <see cref="Restore"/> exist: they let that one caller flip the flag off/back-on for exactly
/// the windows THIS process itself excluded, without guessing or forcing every window.
///
/// Non-Windows: a documented no-op. X11/Wayland compositors expose no per-window
/// capture-exclusion API, and Avalonia does not surface NSWindow.sharingType on macOS — recorded
/// as an accepted limitation in docs/PARITY.md. Overlay (and later flash/recording-chrome/
/// color-picker) windows may appear in re-captures on those platforms.</summary>
public static class WindowCaptureExclusion
{
    private const uint WdaNone = 0x00000000;
    private const uint WdaExcludeFromCapture = 0x00000011;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(IntPtr hWnd, out uint pdwAffinity);

    // Every HWND this helper has successfully excluded, tracked so ClearOnOwnWindows/Restore can
    // toggle exactly the windows THIS process itself excluded (never a window's own concern to
    // track — a single process-wide set mirrors the WPF AutomationServer's approach of enumerating
    // Application.Current.Windows, but without needing an Avalonia equivalent of that enumeration).
    private static readonly HashSet<IntPtr> AppliedHandles = new();
    private static readonly object Gate = new();

    /// <summary>Applies WDA_EXCLUDEFROMCAPTURE to <paramref name="window"/>'s native handle. Call
    /// this once the window's platform handle exists — for a normal Window that's the Opened
    /// event (TryGetPlatformHandle() returns null before the HWND is created). No-ops on
    /// non-Windows, and honors ROESNIP_DIAG_NOEXCLUDE=1 exactly like all four WPF call sites, so
    /// the external luma-sampler / screenshot harnesses that rely on that escape hatch keep
    /// working unmodified against this app too. Failure is logged but non-fatal (matches WPF).</summary>
    public static void Apply(Window window)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        ApplyWindows(window);
    }

    [SupportedOSPlatform("windows")]
    private static void ApplyWindows(Window window)
    {
        if (Environment.GetEnvironmentVariable("ROESNIP_DIAG_NOEXCLUDE") == "1")
        {
            return; // diagnostic escape hatch — lets an external screenshot harness see this window
        }

        var handle = window.TryGetPlatformHandle();
        if (handle is null || handle.Handle == IntPtr.Zero)
        {
            FileLog.Write(
                "RoeSnip: could not resolve a native handle to apply capture exclusion (non-fatal).");
            return;
        }

        IntPtr hwnd = handle.Handle;
        if (!SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture))
        {
            FileLog.Write(
                "RoeSnip: SetWindowDisplayAffinity(EXCLUDEFROMCAPTURE) failed on a window (non-fatal).");
            return;
        }

        lock (Gate)
        {
            AppliedHandles.Add(hwnd);
        }
        window.Closed += (_, _) =>
        {
            lock (Gate)
            {
                AppliedHandles.Remove(hwnd);
            }
        };
    }

    /// <summary>Clears WDA_EXCLUDEFROMCAPTURE on every HWND this process has currently excluded
    /// and returns the ones actually cleared, so <see cref="Restore"/> can put the flag back on
    /// exactly those — not force it onto handles that were never excluded (WPF's
    /// AutomationServer.ClearCaptureExclusionOnOwnWindows/RestoreCaptureExclusion pair). Returns
    /// an empty list on non-Windows.</summary>
    public static List<IntPtr> ClearOnOwnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new List<IntPtr>();
        }

        return ClearOnOwnWindowsCore();
    }

    [SupportedOSPlatform("windows")]
    private static List<IntPtr> ClearOnOwnWindowsCore()
    {
        List<IntPtr> snapshot;
        lock (Gate)
        {
            snapshot = new List<IntPtr>(AppliedHandles);
        }

        var cleared = new List<IntPtr>();
        foreach (var hwnd in snapshot)
        {
            try
            {
                if (GetWindowDisplayAffinity(hwnd, out uint affinity)
                    && affinity == WdaExcludeFromCapture
                    && SetWindowDisplayAffinity(hwnd, WdaNone))
                {
                    cleared.Add(hwnd);
                }
            }
            catch (Exception ex)
            {
                FileLog.Write(
                    $"RoeSnip: could not inspect a window's capture affinity (non-fatal): {ex.Message}");
            }
        }
        return cleared;
    }

    /// <summary>Restores WDA_EXCLUDEFROMCAPTURE on exactly the handles previously returned by
    /// <see cref="ClearOnOwnWindows"/>. No-ops on non-Windows.</summary>
    public static void Restore(List<IntPtr> handles)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RestoreCore(handles);
    }

    [SupportedOSPlatform("windows")]
    private static void RestoreCore(List<IntPtr> handles)
    {
        foreach (var hwnd in handles)
        {
            try
            {
                SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture);
            }
            catch (Exception ex)
            {
                FileLog.Write(
                    $"RoeSnip: failed to restore capture exclusion (non-fatal): {ex.Message}");
            }
        }
    }
}
