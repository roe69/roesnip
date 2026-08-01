using System;
using System.Runtime.InteropServices;
using RoeSnip.Core.Diagnostics;
using RoeSnip.Overlay;

namespace RoeSnip.Recording;

/// <summary>Ctrl+C while a finished take sits in Reviewing: copies the recording to the clipboard.
///
/// This has to be a WH_KEYBOARD_LL hook rather than an ordinary key handler for the same reason the
/// overlay needs one - RecordingChrome is deliberately ShowActivated=false with Focusable=false
/// buttons so it never steals focus from whatever is being recorded, which also means it never
/// receives a keystroke. Modelled directly on <see cref="FlashEscapeHook"/>: single purpose, alive
/// only for the phase that needs it, install failure is non-fatal (the chrome's own Copy button
/// remains), the delegate is rooted for the hook's lifetime, Dispose is idempotent.
///
/// The keystroke is SWALLOWED. While a take is waiting on a decision, Ctrl+C means "copy the
/// recording", and letting it through as well would leave two writers racing for the clipboard with
/// whichever finished last winning - a nondeterministic result is worse than a decided one. The
/// hook lives only from entering Reviewing to leaving it, so a plain Ctrl+C elsewhere is unaffected.
/// </summary>
internal sealed class ReviewCopyHook : IDisposable
{
    private const uint VkC = 0x43;
    private const int VkControl = 0x11;

    // GetAsyncKeyState, NOT GetKeyState: inside a low-level hook callback the modifier check has to
    // read the PHYSICAL key state. GetKeyState reports the calling thread's own input-queue state,
    // which for a hook thread is never updated for keys headed to another thread's queue - it
    // silently reported "Ctrl is up" for every real Ctrl+C (caught by a live keystroke test, after
    // the automation path had already passed).
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private readonly Action _onCopy;
    private readonly OverlayInputInterop.LowLevelKeyboardProc _proc; // rooted for the hook's lifetime
    private IntPtr _hookHandle = IntPtr.Zero;
    private int _disposed;

    public ReviewCopyHook(Action onCopy)
    {
        _onCopy = onCopy;
        _proc = HookProc;
        try
        {
            using var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
            using var mainModule = currentProcess.MainModule;
            IntPtr hMod = mainModule is not null
                ? OverlayInputInterop.GetModuleHandle(mainModule.ModuleName)
                : IntPtr.Zero;

            _hookHandle = OverlayInputInterop.SetWindowsHookEx(OverlayInputInterop.WH_KEYBOARD_LL, _proc, hMod, 0);
            if (_hookHandle == IntPtr.Zero)
            {
                FileLog.Write(
                    $"RoeSnip: failed to install the review Ctrl+C hook (error 0x{Marshal.GetLastWin32Error():X}); " +
                    "use the chrome's Copy button instead.");
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: failed to install the review Ctrl+C hook: {ex.Message}");
            _hookHandle = IntPtr.Zero;
        }
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)OverlayInputInterop.WM_KEYDOWN)
        {
            try
            {
                var data = Marshal.PtrToStructure<OverlayInputInterop.KBDLLHOOKSTRUCT>(lParam);
                // Ctrl+C only. Shift/Alt held too means the user meant some other app's shortcut, so
                // those pass through untouched rather than being hijacked.
                if (data.vkCode == VkC
                    && (GetAsyncKeyState(VkControl) & 0x8000) != 0
                    && (GetAsyncKeyState(0x10) & 0x8000) == 0  // VK_SHIFT
                    && (GetAsyncKeyState(0x12) & 0x8000) == 0) // VK_MENU (Alt)
                {
                    _onCopy();
                    return (IntPtr)1; // swallow - see the class doc comment
                }
            }
            catch (Exception ex)
            {
                // Never crash the hook chain (would break keyboard input system-wide).
                FileLog.Write($"RoeSnip: review Ctrl+C hook callback error: {ex.Message}");
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
