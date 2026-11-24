using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using RoeSnip.Core.Diagnostics;
using RoeSnip.Interop;

namespace RoeSnip.Overlay;

/// <summary>Keeps a parked monitor-sized window at its intended physical bounds through a DPI change.
/// A window keeps the DPI of the monitor it was created on while parked off-screen; the move onto a
/// monitor with another scale then sends WM_DPICHANGED with a rect Windows scaled by the DPI ratio,
/// and WPF applies it (a 125% window moved to 175% grows 1.4x and shows the preview zoomed). The hook
/// rewrites that rect to the bounds the caller is moving the window to, before WPF reads it.</summary>
internal static class DpiRectPin
{
    private const int WM_DPICHANGED = 0x02E0;

    /// <summary>Hooks <paramref name="hwnd"/>; <paramref name="intended"/> returns the physical rect the
    /// window is being moved to. The returned hook must be kept referenced by the window.</summary>
    public static HwndSourceHook? Attach(IntPtr hwnd, Func<NativeMethods.RECT> intended, string what)
    {
        var source = HwndSource.FromHwnd(hwnd);
        if (source is null)
        {
            return null;
        }
        HwndSourceHook hook = (IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg == WM_DPICHANGED && lParam != IntPtr.Zero)
            {
                var rect = intended();
                Marshal.StructureToPtr(rect, lParam, false);
                FileLog.Write(
                    $"RoeSnip: {what} DPI now {(int)wParam & 0xFFFF}; kept " +
                    $"{rect.Right - rect.Left}x{rect.Bottom - rect.Top} at {rect.Left},{rect.Top}");
            }
            return IntPtr.Zero;
        };
        source.AddHook(hook);
        return hook;
    }

    public static NativeMethods.RECT Rect(int x, int y, int width, int height) =>
        new() { Left = x, Top = y, Right = x + width, Bottom = y + height };
}
