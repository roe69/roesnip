using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RoeSnip.Platform.Linux;

/// <summary>X11 helper for the overlay window on Linux: sets <c>override_redirect</c> on the
/// Avalonia window's underlying X11 surface so the window manager bypasses its own strut/panel
/// logic entirely. Without this, GNOME's fullscreen animation (triggered by
/// <c>WindowState.FullScreen</c>) rearranges the user's desktop when the overlay opens - the
/// override_redirect path avoids both the animation AND the strut-offset problem that fullscreen
/// was introduced to solve, because an override-redirect window is placed exactly where we ask,
/// with no WM adjustments.
///
/// Keyboard input: XWayland does not deliver keyboard events to override-redirect windows at all
/// unless an active <c>XGrabKeyboard</c> is held on the SAME display connection. The grab is
/// released when the display is closed, so this helper keeps one static display connection open
/// for the lifetime of the overlay session. <see cref="ReleaseOverlay"/> closes it (and the
/// grab) when the overlay session ends; unmapping the grab window also releases the grab.</summary>
[SupportedOSPlatform("linux")]
public static class LinuxOverlayHelper
{
    private const string LibX11 = "libX11.so.6";
    private const nuint CwOverrideRedirect = 1 << 9;
    private const int RevertToParent = 2;
    private const int GrabModeAsync = 1;
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
    private static extern int XGrabKeyboard(IntPtr display, nuint grabWindow,
        int ownerEvents, int pointerMode, int keyboardMode, nuint time);

    [DllImport(LibX11)]
    private static extern int XUngrabKeyboard(IntPtr display, nuint time);

    [DllImport(LibX11)]
    private static extern int XFlush(IntPtr display);

    [DllImport(LibX11)]
    private static extern IntPtr XOpenDisplay(IntPtr displayName);

    [DllImport(LibX11)]
    private static extern int XCloseDisplay(IntPtr display);

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

    // Kept open so the XGrabKeyboard active grab persists. Closed by ReleaseOverlay().
    private static IntPtr _grabDisplay;

    /// <summary>Unmaps the window, sets override_redirect, remaps it at the given monitor bounds,
    /// and grabs the keyboard on a persistent display connection. Returns true on success.</summary>
    public static bool SetOverrideRedirect(IntPtr windowXid, int x, int y, int width, int height)
    {
        if (windowXid == IntPtr.Zero)
            return false;

        if (_grabDisplay == IntPtr.Zero)
            _grabDisplay = XOpenDisplay(IntPtr.Zero);
        if (_grabDisplay == IntPtr.Zero)
            return false;

        nuint xid = (nuint)(ulong)windowXid;

        XUnmapWindow(_grabDisplay, xid);

        var attrs = new XSetWindowAttributes { override_redirect = 1 };
        XChangeWindowAttributes(_grabDisplay, xid, CwOverrideRedirect, ref attrs);

        XMoveResizeWindow(_grabDisplay, xid, x, y, (uint)Math.Max(1, width), (uint)Math.Max(1, height));
        XMapRaised(_grabDisplay, xid);
        XSetInputFocus(_grabDisplay, xid, RevertToParent, CurrentTime);
        XGrabKeyboard(_grabDisplay, xid, 1, GrabModeAsync, GrabModeAsync, CurrentTime);
        XFlush(_grabDisplay);
        return true;
    }

    /// <summary>Releases the keyboard grab and closes the persistent display connection.
    /// Call once when the overlay session ends (e.g. from the window's Closed handler).</summary>
    public static void ReleaseOverlay()
    {
        if (_grabDisplay == IntPtr.Zero)
            return;
        XUngrabKeyboard(_grabDisplay, CurrentTime);
        XFlush(_grabDisplay);
        XCloseDisplay(_grabDisplay);
        _grabDisplay = IntPtr.Zero;
    }
}
