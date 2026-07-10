using System;
using System.Windows;
using System.Windows.Interop;

namespace RoeSnip.Overlay;

/// <summary>The escalating three-tier foreground/focus-stealing ladder (item 3a of UX round 3),
/// used both when a text annotation edit is about to accept input and at overlay session start —
/// Activate() alone is not proof of real OS foreground delivery for a background tray process (see
/// OverlayInputInterop.cs's SessionKeyboardHook doc comment for the underlying Win32 restriction),
/// and typing specifically needs *real* keyboard/text-services focus in a way the session's
/// low-level keyboard hook deliberately does not fake.
///
/// Tier 1 — a plain SetForegroundWindow call (after WPF's own Activate()).
/// Tier 2 — AttachThreadInput to whichever thread currently owns the foreground window (this lifts
/// the usual SetForegroundWindow restriction between threads that share an input queue), then
/// BringWindowToTop + SetForegroundWindow + SetFocus, then detach.
/// Tier 3 — the documented SendInput Alt-tap "unlock": injecting a synthetic VK_MENU down/up resets
/// the foreground-lock timeout that otherwise makes SetForegroundWindow silently fail for a process
/// that didn't originate the input that's supposed to grant it focus, then a second
/// SetForegroundWindow attempt.
///
/// Every tier is best-effort — a failure anywhere never throws — and whichever tier actually won
/// (or that all three failed) is logged to stderr so interactive verification can see exactly what
/// happened, alongside the real foreground-window ownership before/after the whole ladder ran.</summary>
internal static class ForegroundActivator
{
    public static void Activate(Window window, string context)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            Console.Error.WriteLine($"RoeSnip: {context} activation skipped, window has no HWND yet.");
            return;
        }

        IntPtr before = OverlayInputInterop.GetForegroundWindow();
        Console.Error.WriteLine(
            $"RoeSnip: {context} activation starting; foreground before = 0x{before.ToInt64():X} (ours = 0x{hwnd.ToInt64():X}).");

        try
        {
            window.Activate();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: {context} activation: Window.Activate() threw: {ex.Message}");
        }

        if (TryTier1(hwnd)) { LogResult(context, 1); return; }
        if (TryTier2(hwnd)) { LogResult(context, 2); return; }
        if (TryTier3(hwnd)) { LogResult(context, 3); return; }

        Console.Error.WriteLine(
            $"RoeSnip: {context} activation: all tiers failed; foreground remains 0x{OverlayInputInterop.GetForegroundWindow().ToInt64():X}.");
    }

    private static bool IsForeground(IntPtr hwnd) => OverlayInputInterop.GetForegroundWindow() == hwnd;

    private static bool TryTier1(IntPtr hwnd)
    {
        try
        {
            if (IsForeground(hwnd))
            {
                return true; // Activate() alone already won
            }
            OverlayInputInterop.SetForegroundWindow(hwnd);
            return IsForeground(hwnd);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: tier-1 activation failed: {ex.Message}");
            return false;
        }
    }

    private static bool TryTier2(IntPtr hwnd)
    {
        try
        {
            IntPtr foregroundHwnd = OverlayInputInterop.GetForegroundWindow();
            uint foregroundThreadId = OverlayInputInterop.GetWindowThreadProcessId(foregroundHwnd, IntPtr.Zero);
            uint thisThreadId = OverlayInputInterop.GetCurrentThreadId();

            if (foregroundThreadId == 0 || foregroundThreadId == thisThreadId)
            {
                return false; // nothing to attach to (or we already are it, and tier 1 already failed)
            }

            bool attached = OverlayInputInterop.AttachThreadInput(foregroundThreadId, thisThreadId, true);
            try
            {
                OverlayInputInterop.BringWindowToTop(hwnd);
                OverlayInputInterop.SetForegroundWindow(hwnd);
                OverlayInputInterop.SetFocus(hwnd);
            }
            finally
            {
                if (attached)
                {
                    OverlayInputInterop.AttachThreadInput(foregroundThreadId, thisThreadId, false);
                }
            }
            return IsForeground(hwnd);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: tier-2 (AttachThreadInput) activation failed: {ex.Message}");
            return false;
        }
    }

    private static bool TryTier3(IntPtr hwnd)
    {
        try
        {
            OverlayInputInterop.SendAltTapUnlock();
            OverlayInputInterop.SetForegroundWindow(hwnd);
            OverlayInputInterop.SetFocus(hwnd);
            return IsForeground(hwnd);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: tier-3 (SendInput Alt-tap) activation failed: {ex.Message}");
            return false;
        }
    }

    private static void LogResult(string context, int tier)
    {
        Console.Error.WriteLine(
            $"RoeSnip: {context} activation via tier {tier} (foreground now = 0x{OverlayInputInterop.GetForegroundWindow().ToInt64():X}).");
    }
}
