using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using RoeSnip.Core.Diagnostics;

namespace RoeSnip.App.Overlay;

/// <summary>Swallows mouse BUTTONS (not movement) for the flash phase, ported from the WPF app's
/// FlashMouseSwallowHook.
///
/// The flash windows used to swallow input simply by being topmost and hit-testable. That is also
/// what dismissed tooltips: taking hit testing away from the window under the cursor drops its
/// hover tracking, so its tooltip vanished before the capture ever read the screen. The windows are
/// now WS_EX_TRANSPARENT (click-through), and this hook restores the swallow the same
/// focus-independent way <see cref="FlashEscapeHook"/> already handles Esc: a click landing between
/// the dim appearing and the overlay taking over must not reach the app underneath, because the
/// user pressed a capture hotkey, not a mouse button.
///
/// Movement is deliberately PASSED THROUGH: swallowing it would freeze the cursor and would defeat
/// the point of going click-through, since the window under the cursor needs its hover state.
///
/// Same lifecycle and failure rules as FlashEscapeHook (non-fatal install failure, rooted delegate,
/// idempotent Dispose, UI thread only). Windows-only by construction: only ever created inside
/// OverlayController.TryShowFlash's own OperatingSystem.IsWindows()-guarded path.</summary>
[SupportedOSPlatform("windows")]
internal sealed class FlashMouseSwallowHook : IDisposable
{
    private const int WhMouseLl = 14;
    private const int WmLButtonDown = 0x0201, WmLButtonUp = 0x0202;
    private const int WmRButtonDown = 0x0204, WmRButtonUp = 0x0205;
    private const int WmMButtonDown = 0x0207, WmMButtonUp = 0x0208;
    private const int WmXButtonDown = 0x020B, WmXButtonUp = 0x020C;
    private const int WmMouseWheel = 0x020A, WmMouseHWheel = 0x020E;

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    private readonly LowLevelMouseProc _proc; // rooted for the hook's lifetime
    private IntPtr _hookHandle = IntPtr.Zero;
    private int _disposed;

    public FlashMouseSwallowHook()
    {
        _proc = HookProc;
        try
        {
            using var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
            using var mainModule = currentProcess.MainModule;
            IntPtr hMod = mainModule is not null ? GetModuleHandle(mainModule.ModuleName) : IntPtr.Zero;

            _hookHandle = SetWindowsHookEx(WhMouseLl, _proc, hMod, 0);
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
            if (msg is WmLButtonDown or WmLButtonUp
                or WmRButtonDown or WmRButtonUp
                or WmMButtonDown or WmMButtonUp
                or WmXButtonDown or WmXButtonUp
                or WmMouseWheel or WmMouseHWheel)
            {
                return (IntPtr)1; // swallowed: the screen is dimmed, this click was not aimed at anything
            }
        }
        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
