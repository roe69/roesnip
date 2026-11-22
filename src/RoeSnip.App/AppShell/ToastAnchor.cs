using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using RoeSnip.Core.Capture;
using RoeSnip.Core.Diagnostics;
using RoeSnip.Core.Notifications;

namespace RoeSnip.App.AppShell;

/// <summary>Places toast windows next to the mouse cursor (Core ToastPlacement), in Avalonia's
/// physical-pixel Position space. Cursor source per OS: Windows GetCursorPos, macOS
/// NSEvent.mouseLocation, X11 XQueryPointer. Wayland gives clients no global pointer, so it uses
/// a pointer position a RoeSnip window saw in the last 2 s, then a PointerProbe, then XQueryPointer
/// through XWayland (stale while the pointer is over a native Wayland window), then the older
/// last-seen position, then the bottom-centre of a monitor.</summary>
internal static class ToastAnchor
{
    private static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(2);

    private static PixelPoint? _lastPointer;
    private static long _lastPointerAt;
    private static bool _lastPointerFromProbe;

    /// <summary>Records a screen-space (physical px) pointer position a RoeSnip window saw.</summary>
    public static void NotePointer(PixelPoint screenPx) => Remember(screenPx, false);

    private static void Remember(PixelPoint screenPx, bool fromProbe)
    {
        _lastPointer = screenPx;
        _lastPointerAt = System.Diagnostics.Stopwatch.GetTimestamp();
        _lastPointerFromProbe = fromProbe;
    }

    private static bool LastPointerFresh =>
        _lastPointer is not null && System.Diagnostics.Stopwatch.GetElapsedTime(_lastPointerAt) <= FreshFor;

    /// <summary>Sets <paramref name="window"/>.Position (before Show) for a toast of the given DIP
    /// size and logs the source used. <paramref name="fallbackMonitor"/> is the monitor to use when
    /// no pointer position is known (else the primary). On Linux the position is re-applied once
    /// after the window opens if the window manager moved it.</summary>
    public static void Place(Window window, double widthDip, double heightDip, RectPhysical? fallbackMonitor = null)
    {
        var screens = window.Screens.All;
        var bounds = new List<RectPhysical>(screens.Count);
        foreach (var s in screens)
        {
            bounds.Add(RectPhysical.FromSize(s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height));
        }

        PixelPoint? pointer = ResolvePointer(screens, out string source);
        int index = pointer is { } p ? ToastPlacement.MonitorIndexAt(bounds, p.X, p.Y) : -1;
        PixelPoint target;
        if (pointer is { } cursor && index >= 0)
        {
            var screen = screens[index];
            var (w, h, wa) = Measure(screen, widthDip, heightDip);
            var (x, y) = ToastPlacement.NextToCursor(cursor.X, cursor.Y, w, h, wa,
                (int)Math.Round(ToastPlacement.CursorOffsetDip * screen.Scaling));
            target = new PixelPoint(x, y);
        }
        else
        {
            Screen? screen = null;
            if (_lastPointer is { } last && ToastPlacement.MonitorIndexAt(bounds, last.X, last.Y) is var li && li >= 0)
            {
                screen = screens[li];
            }
            else if (fallbackMonitor is { } fm)
            {
                int fi = bounds.IndexOf(fm);
                if (fi >= 0) screen = screens[fi];
            }
            screen ??= window.Screens.Primary ?? (screens.Count > 0 ? screens[0] : null);
            if (screen is null)
            {
                return;
            }
            var (w, h, wa) = Measure(screen, widthDip, heightDip);
            var (x, y) = ToastPlacement.BottomCentre(w, h, wa,
                (int)Math.Round(ToastPlacement.EdgeMarginDip * screen.Scaling));
            target = new PixelPoint(x, y);
            source = "monitor fallback";
        }

        window.Position = target;
        FileLog.Write($"RoeSnip: toast placed at {target.X},{target.Y} ({source})");

        if (OperatingSystem.IsLinux())
        {
            // A plain borderless window is placed by the window manager on X11/XWayland; put it
            // back once if it was moved (the target is already inside the work area).
            window.Opened += (_, _) => DispatcherTimer.RunOnce(() =>
            {
                if (window.IsVisible && window.Position != target)
                {
                    FileLog.Write($"RoeSnip: toast moved by the window manager to {window.Position.X},{window.Position.Y}, placing it back at {target.X},{target.Y}");
                    window.Position = target;
                }
            }, TimeSpan.FromMilliseconds(150));
        }
    }

    private static (int W, int H, RectPhysical WorkArea) Measure(Screen screen, double widthDip, double heightDip)
    {
        var wa = screen.WorkingArea;
        return ((int)Math.Round(widthDip * screen.Scaling), (int)Math.Round(heightDip * screen.Scaling),
            RectPhysical.FromSize(wa.X, wa.Y, wa.Width, wa.Height));
    }

    private static PixelPoint? ResolvePointer(IReadOnlyList<Screen> screens, out string source)
    {
        source = "cursor";
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (GetCursorPos(out var pt)) return new PixelPoint(pt.X, pt.Y);
            }
            else if (OperatingSystem.IsMacOS())
            {
                if (TryMacCursor(screens) is { } mac) return mac;
            }
#if LINUX_BACKEND
            else if (OperatingSystem.IsLinux())
            {
                bool wayland = string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase);
                if (wayland && LastPointerFresh && _lastPointer is { } seen)
                {
                    // Also reuses a probe result, so a toast and the share window probe once.
                    source = _lastPointerFromProbe ? "cursor (probe)" : "last-known";
                    return seen;
                }
                if (wayland && PointerProbe.Run(screens) is { } probed)
                {
                    Remember(probed, true);
                    source = "cursor (probe)";
                    return probed;
                }
                if (RoeSnip.Platform.Linux.X11Capturer.TryQueryPointer(out int x, out int y))
                {
                    source = wayland ? "cursor via XWayland" : "cursor";
                    return new PixelPoint(x, y);
                }
            }
#endif
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: toast cursor query failed ({ex.Message})");
        }

        source = "last-known";
        return _lastPointer;
    }

    /// <summary>NSEvent.mouseLocation is in points with a bottom-left origin on the primary
    /// screen; flip it and scale by the scaling of the screen it falls on.</summary>
    private static PixelPoint? TryMacCursor(IReadOnlyList<Screen> screens)
    {
        IntPtr nsEvent = objc_getClass("NSEvent");
        if (nsEvent == IntPtr.Zero) return null;
        var loc = MsgSendPoint(nsEvent, sel_registerName("mouseLocation"));
        Screen? primary = null;
        foreach (var s in screens)
        {
            if (s.Bounds.X == 0 && s.Bounds.Y == 0) { primary = s; break; }
        }
        if (primary is null) return null;
        double topY = primary.Bounds.Height / primary.Scaling - loc.Y;
        foreach (var s in screens)
        {
            int px = (int)Math.Round(loc.X * s.Scaling), py = (int)Math.Round(topY * s.Scaling);
            var b = s.Bounds;
            if (px >= b.X && px < b.Right && py >= b.Y && py < b.Bottom) return new PixelPoint(px, py);
        }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32 { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NSPoint { public double X; public double Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point32 lpPoint);

    private const string LibObjC = "/usr/lib/libobjc.A.dylib";
    [DllImport(LibObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(LibObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern NSPoint MsgSendPoint(IntPtr receiver, IntPtr selector);
}
