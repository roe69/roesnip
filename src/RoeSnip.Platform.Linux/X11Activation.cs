using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RoeSnip.Platform.Linux;

/// <summary>Gives an X11 window keyboard focus under mutter. Mutter never focuses a UTILITY window
/// on map, and refuses an application-source <c>_NET_ACTIVE_WINDOW</c> whose timestamp is older than
/// the last user input - Avalonia stamps it with the resident's own last input, older than the
/// PrintScreen press that started the snip, so the key stayed with the previous app. A pager-source
/// request (2) skips that check; CurrentTime makes mutter use the server time.</summary>
[SupportedOSPlatform("linux")]
public static class X11Activation
{
    private const string LibX11 = "libX11.so.6";
    private const int ClientMessage = 33;
    private const long SubstructureNotifyMask = 1L << 19;
    private const long SubstructureRedirectMask = 1L << 20;
    private const int SourcePager = 2;

    [DllImport(LibX11)]
    private static extern IntPtr XOpenDisplay(IntPtr displayName);

    [DllImport(LibX11)]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport(LibX11)]
    private static extern nuint XInternAtom(IntPtr display, string atomName, bool onlyIfExists);

    [DllImport(LibX11)]
    private static extern nuint XDefaultRootWindow(IntPtr display);

    [DllImport(LibX11)]
    private static extern int XSendEvent(IntPtr display, nuint window, bool propagate, nint eventMask, nint[] eventSend);

    [DllImport(LibX11)]
    private static extern int XFlush(IntPtr display);

    /// <summary>Returns false when no X display is reachable.</summary>
    public static bool Activate(IntPtr window)
    {
        IntPtr display = XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero)
        {
            return false;
        }
        try
        {
            // XClientMessageEvent inside a 24-long XEvent (LP64: every field takes one 8-byte slot).
            var ev = new nint[24];
            ev[0] = ClientMessage;
            ev[2] = 1; // send_event
            ev[3] = display;
            ev[4] = window;
            ev[5] = (nint)XInternAtom(display, "_NET_ACTIVE_WINDOW", false);
            ev[6] = 32; // format
            ev[7] = SourcePager;
            ev[8] = 0; // CurrentTime
            ev[9] = 0; // requestor's currently active window: none
            XSendEvent(display, XDefaultRootWindow(display), false,
                (nint)(SubstructureNotifyMask | SubstructureRedirectMask), ev);
            XFlush(display);
            return true;
        }
        finally
        {
            XCloseDisplay(display);
        }
    }
}
