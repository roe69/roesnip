using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RoeSnip.Platform.Linux;

/// <summary>X11 helper for the overlay window on Linux: sets <c>override_redirect</c> on the
/// Avalonia window's underlying X11 surface so the window manager bypasses its own strut/panel
/// logic entirely. Without this, GNOME's fullscreen animation (triggered by
/// <c>WindowState.FullScreen</c>) rearranges the user's desktop when the overlay opens - the
/// override_redirect path avoids both the animation AND the strut-offset problem that fullscreen
/// was introduced to solve, because an override-redirect window is placed exactly where we ask,
/// with no WM adjustments. Called from OverlayWindow's Opened handler (the platform handle only
/// exists once the window has been shown).</summary>
[SupportedOSPlatform("linux")]
public static class LinuxOverlayHelper
{
    private const string LibX11 = "libX11.so.6";
    private const nuint CwOverrideRedirect = 1 << 9;
    private const int RevertToParent = 2;
    private const nuint CurrentTime = 0;

    [DllImport(LibX11)]
    private static extern int XUnmapWindow(IntPtr display, nuint window);

    [DllImport(LibX11)]
    private static extern int XMapRaised(IntPtr display, nuint window);

    [DllImport(LibX11)]
    private static extern int XMoveResizeWindow(IntPtr display, nuint window,
        int x, int y, uint width, uint height);

    [DllImport(LibX11)]
    private static extern int XChangeWindowAttributes(IntPtr display, nuint window,
        nuint valueMask, ref XSetWindowAttributes attributes);

    [DllImport(LibX11)]
    private static extern int XSetInputFocus(IntPtr display, nuint window,
        int revertTo, nuint time);

    [DllImport(LibX11)]
    private static extern int XFlush(IntPtr display);

    [DllImport(LibX11)]
    private static extern IntPtr XOpenDisplay(IntPtr displayName);

    [DllImport(LibX11)]
    private static extern int XCloseDisplay(IntPtr display);

    // Only the override_redirect field matters (selected by CwOverrideRedirect); the rest of
    // the struct is laid out so the field sits at its correct offset in XSetWindowAttributes.
    [StructLayout(LayoutKind.Sequential)]
    private struct XSetWindowAttributes
    {
        public IntPtr background_pixmap;
        public nuint background_pixel;
        public IntPtr border_pixmap;
        public nuint border_pixel;
        public int bit_gravity;
        public int win_gravity;
        public int backing_store;
        public nuint backing_planes;
        public nuint backing_pixel;
        public int save_under;
        public nuint event_mask;
        public nuint do_not_propagate_mask;
        public int override_redirect;
    }

    /// <summary>Unmaps the window, sets override_redirect, remaps it at the given monitor bounds
    /// and grabs keyboard focus. Returns true on success.</summary>
    public static bool SetOverrideRedirect(IntPtr windowXid, int x, int y, int width, int height)
    {
        if (windowXid == IntPtr.Zero)
            return false;

        IntPtr display = XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero)
            return false;

        try
        {
            nuint xid = (nuint)(ulong)windowXid;

            XUnmapWindow(display, xid);

            var attrs = new XSetWindowAttributes { override_redirect = 1 };
            XChangeWindowAttributes(display, xid, CwOverrideRedirect, ref attrs);

            XMoveResizeWindow(display, xid, x, y, (uint)Math.Max(1, width), (uint)Math.Max(1, height));
            XMapRaised(display, xid);
            XSetInputFocus(display, xid, RevertToParent, CurrentTime);
            XFlush(display);
            return true;
        }
        finally
        {
            XCloseDisplay(display);
        }
    }
}
