using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using RoeSnip.Core.Diagnostics;

namespace RoeSnip.App.Overlay;

/// <summary>Flash-phase Esc coverage (post-sleep stall fix, ported from the WPF app's
/// FlashEscapeHook): a minimal Esc-ONLY WH_KEYBOARD_LL hook alive from a successful TryShowFlash
/// until either the flow ends (ReleaseFlash), the flash phase is cancelled (OnFlashEscape), or the
/// real OverlaySession takes over (its windows then own the keyboard via their ordinary Avalonia
/// KeyDown handlers — this port has no session-wide LL hook the way the WPF app does, so the
/// hand-off is simply disposing this one). Before this, cancelling during the flash phase depended
/// on the flash window actually winning the best-effort SetForegroundWindow negotiation — under
/// foreground-lock restrictions it often didn't, leaving Esc dead exactly while the screen sat
/// dimmed waiting on a slow (post-resume) capture. Only Esc keydowns are acted on and swallowed;
/// every other key passes through untouched via CallNextHookEx — this phase has no text editing or
/// session commands to worry about. Install failure is non-fatal (logged; the focus-dependent
/// FlashWindow KeyDown handler remains as the fallback), the delegate is rooted for the hook's
/// lifetime, Dispose is idempotent. UI (hook-installing) thread only — with the capture now off
/// the UI thread, that thread pumps during the flash phase, so callbacks are actually delivered
/// (a LL hook on a non-pumping thread is silently skipped by Windows). Windows-only by
/// construction: only ever created inside OverlayController.TryShowFlash's own
/// OperatingSystem.IsWindows()-guarded path. Local P/Invoke per the per-file convention
/// (WindowCaptureExclusion, FlashDimmer.NativeMethods).</summary>
[SupportedOSPlatform("windows")]
internal sealed class FlashEscapeHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeydown = 0x0100;
    private const int WmSyskeydown = 0x0104;
    private const uint VkEscape = 0x1B;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    private readonly Action _onEscape;
    private readonly LowLevelKeyboardProc _proc; // rooted for the hook's lifetime
    private IntPtr _hookHandle = IntPtr.Zero;
    private int _disposed;

    public FlashEscapeHook(Action onEscape)
    {
        _onEscape = onEscape;
        _proc = HookProc;
        try
        {
            using var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
            using var mainModule = currentProcess.MainModule;
            IntPtr hMod = mainModule is not null
                ? GetModuleHandle(mainModule.ModuleName)
                : IntPtr.Zero;

            _hookHandle = SetWindowsHookEx(WhKeyboardLl, _proc, hMod, 0);
            if (_hookHandle == IntPtr.Zero)
            {
                FileLog.Write(
                    $"RoeSnip: failed to install the flash Esc hook (error 0x{Marshal.GetLastWin32Error():X}); " +
                    "Esc during the flash phase will only work while a flash window has focus.");
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: failed to install the flash Esc hook: {ex.Message}");
            _hookHandle = IntPtr.Zero;
        }
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0
            && (wParam == (IntPtr)WmKeydown || wParam == (IntPtr)WmSyskeydown))
        {
            try
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if (data.vkCode == VkEscape)
                {
                    _onEscape();
                    return (IntPtr)1; // swallow — the cancel must not also leak into the app underneath
                }
            }
            catch (Exception ex)
            {
                // Never crash the hook chain (would break keyboard input system-wide).
                FileLog.Write($"RoeSnip: flash Esc hook callback error: {ex.Message}");
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

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);
}
