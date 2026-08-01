using System;
using System.Windows;
using System.Windows.Interop;
using RoeSnip.Interop;

namespace RoeSnip.App;

/// <summary>Paints the system caption bar dark on the app's system-chromed windows (Settings,
/// Sharing providers, Configure provider). Everything inside those windows is themed by
/// Theme/RoeSnipTheme.xaml, but the non-client area belongs to DWM, not WPF - without this a pure
/// black window ships with a bright white title bar bolted to the top of it.
///
/// The attribute has to be set on a real HWND, so callers hook it up in their constructor and it
/// applies itself on SourceInitialized (the first moment the handle exists, and still before the
/// window is shown, so the caption never flashes light first).</summary>
internal static class DarkTitleBar
{
    public static void Apply(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            int enabled = 1;
            // Best-effort: a failing HRESULT just means this build of Windows doesn't know the
            // attribute, and a light caption is a cosmetic miss, never a reason to fail a window.
            if (NativeMethods.DwmSetWindowAttribute(
                    hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int)) != 0)
            {
                NativeMethods.DwmSetWindowAttribute(
                    hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY, ref enabled, sizeof(int));
            }
        };
    }
}
