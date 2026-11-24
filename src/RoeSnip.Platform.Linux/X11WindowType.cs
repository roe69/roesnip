using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RoeSnip.Platform.Linux;

/// <summary>Sets an X11 window's <c>_NET_WM_WINDOW_TYPE</c> before it is first mapped.
/// GNOME Shell plays its open/close zoom and its fullscreen resize animation only for NORMAL and
/// DIALOG windows (windowManager.js <c>_mapWindow</c> / <c>_sizeChangeWindow</c>), so the snip
/// overlay is typed so it appears and goes fullscreen in one frame, like the built-in screenshot
/// UI. Must run before the first map: mutter reads the type when it manages the window.</summary>
[SupportedOSPlatform("linux")]
public static class X11WindowType
{
    private const string LibX11 = "libX11.so.6";
    private const int PropModeReplace = 0;
    private const nuint XaAtom = 4;

    [DllImport(LibX11)]
    private static extern IntPtr XOpenDisplay(IntPtr displayName);

    [DllImport(LibX11)]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport(LibX11)]
    private static extern nuint XInternAtom(IntPtr display, string atomName, bool onlyIfExists);

    [DllImport(LibX11)]
    private static extern int XChangeProperty(IntPtr display, nuint window, nuint property, nuint type,
        int format, int mode, nuint[] data, int nelements);

    [DllImport(LibX11)]
    private static extern int XFlush(IntPtr display);

    /// <summary><paramref name="typeAtom"/> is the full atom name, e.g.
    /// <c>_NET_WM_WINDOW_TYPE_UTILITY</c>. Returns false when no X display is reachable.</summary>
    public static bool Set(IntPtr window, string typeAtom)
    {
        IntPtr display = XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero)
        {
            return false;
        }
        try
        {
            nuint prop = XInternAtom(display, "_NET_WM_WINDOW_TYPE", false);
            nuint value = XInternAtom(display, typeAtom, false);
            XChangeProperty(display, (nuint)window, prop, XaAtom, 32, PropModeReplace, new[] { value }, 1);
            XFlush(display);
            return true;
        }
        finally
        {
            // Properties live on the server; closing this connection does not undo them.
            XCloseDisplay(display);
        }
    }
}
