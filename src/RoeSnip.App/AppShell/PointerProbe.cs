using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using RoeSnip.Core.Diagnostics;

namespace RoeSnip.App.AppShell;

/// <summary>Wayland sends a client the pointer position only while the pointer is over one of its
/// own surfaces. The probe maps one invisible, non-activating, topmost fullscreen window per
/// monitor for up to <see cref="Timeout"/> and takes the first pointer event any of them gets.
/// Runs a nested dispatcher frame so callers stay synchronous; every window is closed before it
/// returns.</summary>
internal static class PointerProbe
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);

    private static bool _running;

    /// <summary>Returns the pointer in physical virtual-desktop px, or null after the timeout,
    /// on a re-entrant call or on any failure.</summary>
    public static PixelPoint? Run(IReadOnlyList<Screen> screens)
    {
        if (_running || screens.Count == 0)
        {
            return null;
        }
        _running = true;
        var windows = new List<Window>(screens.Count);
        var frame = new DispatcherFrame();
        PixelPoint? result = null;
        string? monitor = null;
        var sw = Stopwatch.StartNew();
        DispatcherTimer? timer = null;
        try
        {
            for (int i = 0; i < screens.Count; i++)
            {
                var screen = screens[i];
                string name = string.IsNullOrEmpty(screen.DisplayName)
                    ? $"monitor {i} ({screen.Bounds.X},{screen.Bounds.Y})"
                    : screen.DisplayName!;
                var w = CreateWindow(screen);
                void Report(object? sender, PointerEventArgs e)
                {
                    if (result is not null || sender is not Window win)
                    {
                        return;
                    }
                    var p = e.GetPosition(win);
                    double s = win.RenderScaling > 0 ? win.RenderScaling : 1.0;
                    result = new PixelPoint(win.Position.X + (int)Math.Round(p.X * s),
                        win.Position.Y + (int)Math.Round(p.Y * s));
                    monitor = name;
                    frame.Continue = false;
                }
                w.PointerEntered += Report;
                w.PointerMoved += Report;
                // A click during the probe ends it at once (the position is still valid).
                w.PointerPressed += Report;
                windows.Add(w);
            }

            timer = new DispatcherTimer { Interval = Timeout };
            timer.Tick += (_, _) => frame.Continue = false;
            timer.Start();
            foreach (var w in windows)
            {
                w.Show();
            }
            if (result is null)
            {
                Dispatcher.UIThread.PushFrame(frame);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: toast cursor probe failed ({ex.Message})");
            result = null;
            monitor = null;
        }
        finally
        {
            timer?.Stop();
            foreach (var w in windows)
            {
                try { w.Close(); } catch { /* already gone */ }
            }
            _running = false;
        }

        if (result is { } r)
        {
            FileLog.Write($"RoeSnip: toast cursor probe: {r.X},{r.Y} on {monitor} in {sw.ElapsedMilliseconds} ms");
        }
        else
        {
            FileLog.Write($"RoeSnip: toast cursor probe: no pointer event in {(int)Timeout.TotalMilliseconds} ms");
        }
        return result;
    }

    /// <summary>Placed like OverlayWindow.TryPlaceOnScreen: physical Position, DIP size, and
    /// fullscreen on Linux so panel struts do not push it.</summary>
    private static Window CreateWindow(Screen screen)
    {
        double scale = screen.Scaling > 0 ? screen.Scaling : 1.0;
        var b = screen.Bounds;
        var w = new Window
        {
            WindowDecorations = WindowDecorations.None,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            Focusable = false,
            CanResize = false,
            // A non-null transparent brush keeps the window hit-testable with no visible pixels.
            Background = Brushes.Transparent,
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent },
            Position = new PixelPoint(b.X, b.Y),
            Width = b.Width / scale,
            Height = b.Height / scale,
        };
        if (OperatingSystem.IsLinux())
        {
            w.WindowState = WindowState.FullScreen;
        }
        return w;
    }
}
