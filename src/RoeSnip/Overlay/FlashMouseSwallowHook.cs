using System;
using System.Runtime.InteropServices;
using RoeSnip.Core.Diagnostics;

namespace RoeSnip.Overlay;

/// <summary>Swallows mouse BUTTONS (not movement) for the flash phase.
///
/// The flash windows used to swallow input simply by being topmost and hit-testable. That is also
/// what dismissed tooltips: taking hit testing away from the window under the cursor drops its
/// TrackMouseEvent hover state, so its tooltip vanished before the capture ever read the screen.
/// The windows are now WS_EX_TRANSPARENT (click-through), and this hook restores the swallow the
/// same focus-independent way <see cref="FlashEscapeHook"/> already handles Esc: a click that lands
/// in the ~50 ms (cold: several hundred ms) between the dim appearing and the overlay taking over
/// must not reach the app underneath, because the user pressed a capture hotkey, not a mouse button.
///
/// Movement is deliberately PASSED THROUGH. Swallowing WM_MOUSEMOVE would freeze the cursor - the
/// very "the cursor stops working" complaint this pass is trying to remove - and would defeat the
/// point of going click-through, since the window under the cursor needs to keep its hover state.
///
/// Same lifecycle rules as FlashEscapeHook: install failure is non-fatal (worst case a stray click
/// reaches the app underneath, which is what happened before this existed), the delegate is rooted
/// for the hook's lifetime, and Dispose is idempotent. UI (hook-installing) thread only.</summary>
internal sealed class FlashMouseSwallowHook : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;
    private const int WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208;
    private const int WM_XBUTTONDOWN = 0x020B, WM_XBUTTONUP = 0x020C;
    private const int WM_MOUSEWHEEL = 0x020A, WM_MOUSEHWHEEL = 0x020E;

    private readonly OverlayInputInterop.LowLevelKeyboardProc _proc; // same delegate shape as the keyboard hook
    private IntPtr _hookHandle = IntPtr.Zero;
    private int _disposed;

    public FlashMouseSwallowHook()
    {
        _proc = HookProc;
        try
        {
            using var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
            using var mainModule = currentProcess.MainModule;
            IntPtr hMod = mainModule is not null
                ? OverlayInputInterop.GetModuleHandle(mainModule.ModuleName)
                : IntPtr.Zero;

            _hookHandle = OverlayInputInterop.SetWindowsHookEx(WH_MOUSE_LL, _proc, hMod, 0);
            if (_hookHandle == IntPtr.Zero)
            {
                FileLog.Write(
                    $"RoeSnip: failed to install the flash mouse-swallow hook (error 0x{Marshal.GetLastWin32Error():X}); " +
                    "a click during the flash phase may reach the app underneath.");
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: failed to install the flash mouse-swallow hook: {ex.Message}");
            _hookHandle = IntPtr.Zero;
        }
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            if (msg is WM_LBUTTONDOWN or WM_LBUTTONUP
                or WM_RBUTTONDOWN or WM_RBUTTONUP
                or WM_MBUTTONDOWN or WM_MBUTTONUP
                or WM_XBUTTONDOWN or WM_XBUTTONUP
                or WM_MOUSEWHEEL or WM_MOUSEHWHEEL)
            {
                return (IntPtr)1; // swallowed: the screen is dimmed, this click was not aimed at anything
            }
        }
        return OverlayInputInterop.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        if (_hookHandle != IntPtr.Zero)
        {
            OverlayInputInterop.UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
    }
}
