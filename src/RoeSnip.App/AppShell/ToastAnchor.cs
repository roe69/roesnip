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
/// the snapshot screencast's cursor metadata (ScreenCastCursor), then a pointer position a RoeSnip
/// window saw in the last 2 s, then a PointerProbe, then XQueryPointer
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
    /// after the window opens if the window manager moved it. While open the window follows the
    /// pointer (<see cref="Follow"/>).</summary>
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

        var placedFor = pointer is { } pf && index >= 0 ? (pf.X, pf.Y) : ((int, int)?)null;
        new Follow(window, widthDip, heightDip, target, placedFor).Attach();
    }

    /// <summary>Moves a visible toast with the pointer (Core ToastFollower) at ~60 Hz, only when
    /// the pointer moved, and freezes it once the pointer heads into it so it stays clickable.
    /// Pointer sources: GetCursorPos, NSEvent, XQueryPointer on X11, the screencast cursor
    /// metadata on Wayland (no probes: they would flash windows under the pointer).</summary>
    private sealed class Follow
    {
        private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(16);
        private static readonly TimeSpan ScreensFor = TimeSpan.FromSeconds(1);

        private readonly Window _window;
        private readonly double _widthDip;
        private readonly double _heightDip;
        private readonly ToastFollower _follower;
        private readonly DispatcherTimer _timer;
        private PixelPoint _target;
        private IReadOnlyList<Screen>? _screens;
        private List<RectPhysical>? _bounds;
        private long _screensAt;
        private int _moves;
        private string _source = "";

        public Follow(Window window, double widthDip, double heightDip, PixelPoint target, (int X, int Y)? placedFor)
        {
            _window = window;
            _widthDip = widthDip;
            _heightDip = heightDip;
            _target = target;
            double scale = window.Screens.ScreenFromPoint(target)?.Scaling ?? 1.0;
            _follower = new ToastFollower(placedFor, RectPhysical.FromSize(target.X, target.Y,
                (int)Math.Round(widthDip * scale), (int)Math.Round(heightDip * scale)));
            _timer = new DispatcherTimer { Interval = Tick };
            _timer.Tick += (_, _) => OnTick();
        }

        public void Attach()
        {
            _window.Opened += (_, _) =>
            {
                if (OperatingSystem.IsLinux())
                {
                    // A plain borderless window is placed by the window manager on X11/XWayland; put
                    // it back once if it was moved (the target is already inside the work area).
                    DispatcherTimer.RunOnce(() =>
                    {
                        if (_window.IsVisible && _window.Position != _target)
                        {
                            FileLog.Write($"RoeSnip: toast moved by the window manager to {_window.Position.X},{_window.Position.Y}, placing it back at {_target.X},{_target.Y}");
                            _window.Position = _target;
                        }
                    }, TimeSpan.FromMilliseconds(150));
                }
                _timer.Start();
            };
            _window.PointerEntered += (_, _) => _follower.Freeze();
            _window.Closed += (_, _) =>
            {
                _timer.Stop();
                if (_moves > 0)
                {
                    FileLog.Write($"RoeSnip: toast followed the pointer ({_moves} moves, {(_follower.Frozen ? "frozen when approached" : "never approached")}, {_source})");
                }
            };
        }

        private void OnTick()
        {
            try
            {
                if (!_window.IsVisible || _follower.Frozen) return;
                if (_screens is null || System.Diagnostics.Stopwatch.GetElapsedTime(_screensAt) > ScreensFor)
                {
                    _screens = _window.Screens.All;
                    _bounds = new List<RectPhysical>(_screens.Count);
                    foreach (var s in _screens)
                    {
                        _bounds.Add(RectPhysical.FromSize(s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height));
                    }
                    _screensAt = System.Diagnostics.Stopwatch.GetTimestamp();
                }
                var screens = _screens;
                if (!TryLivePointer(screens, out var p, out _source)) return;
                int index = ToastPlacement.MonitorIndexAt(_bounds!, p.X, p.Y);
                if (index < 0) return;
                var screen = screens[index];
                var (w, h, wa) = Measure(screen, _widthDip, _heightDip);
                double own = _window.RenderScaling > 0 ? _window.RenderScaling : screen.Scaling;
                var pos = _window.Position;
                var current = RectPhysical.FromSize(pos.X, pos.Y,
                    (int)Math.Round(_widthDip * own), (int)Math.Round(_heightDip * own));
                if (_follower.Step(p.X, p.Y, current, wa, w, h, screen.Scaling) is { } next)
                {
                    _target = new PixelPoint(next.X, next.Y);
                    _window.Position = _target;
                    _moves++;
                }
            }
            catch (Exception ex)
            {
                _timer.Stop();
                FileLog.Write($"RoeSnip: toast follow stopped ({ex.Message})");
            }
        }
    }

    /// <summary>The pointer from a source cheap enough to poll; false when none answers.</summary>
    private static bool TryLivePointer(IReadOnlyList<Screen> screens, out PixelPoint pointer, out string source)
    {
        pointer = default;
        source = "cursor";
        if (OperatingSystem.IsWindows())
        {
            if (!GetCursorPos(out var pt)) return false;
            pointer = new PixelPoint(pt.X, pt.Y);
            return true;
        }
        if (OperatingSystem.IsMacOS())
        {
            if (TryMacCursor(screens) is not { } mac) return false;
            pointer = mac;
            return true;
        }
#if LINUX_BACKEND
        if (OperatingSystem.IsLinux())
        {
            if (IsWayland)
            {
                source = "cursor (screencast)";
                if (!RoeSnip.Platform.Linux.ScreenCastCursor.TryGetDesktopPosition(out int wx, out int wy)) return false;
                pointer = new PixelPoint(wx, wy);
                return true;
            }
            if (!RoeSnip.Platform.Linux.X11Capturer.TryQueryPointer(out int x, out int y)) return false;
            pointer = new PixelPoint(x, y);
            return true;
        }
#endif
        return false;
    }

    private static bool IsWayland =>
        string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase);

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
                bool wayland = IsWayland;
                if (wayland && RoeSnip.Platform.Linux.ScreenCastCursor.TryGetDesktopPosition(out int cx, out int cy))
                {
                    source = "cursor (screencast)";
                    return new PixelPoint(cx, cy);
                }
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
