using System;
using System.Runtime.InteropServices;
using RoeSnip.Core.Diagnostics;

namespace RoeSnip.App.Recording;

/// <summary>Ctrl+C while a finished take sits in Reviewing: copies the recording to the clipboard.
///
/// Windows only, and a low-level keyboard hook rather than an ordinary key handler, because
/// RecordingChrome is deliberately ShowActivated=false with Focusable=false buttons so it never
/// steals focus from whatever is being recorded - which also means it never receives a keystroke.
/// Ported from the WPF app's Recording/ReviewCopyHook.cs; the DllImports compile everywhere but
/// <see cref="TryInstall"/> returns null off Windows, where the chrome's Copy button is the only
/// way in (X11/Wayland/macOS each need their own global-shortcut mechanism, and neither has one
/// wired in this port - documented in docs/PARITY.md).
///
/// The keystroke PASSES THROUGH (CallNextHookEx) rather than being swallowed - see the WPF twin's
/// own doc comment. The hook lives only from entering Reviewing to leaving it.</summary>
internal sealed class ReviewCopyHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const uint VkC = 0x43;
    private const int VkControl = 0x11;
    private const int VkShift = 0x10;
    private const int VkMenu = 0x12;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    // GetAsyncKeyState, NOT GetKeyState: inside a low-level hook callback the modifier check has to
    // read the PHYSICAL key state. GetKeyState reports the calling thread's own input-queue state,
    // which for a hook thread is never updated for keys headed to another thread's queue.
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private readonly Action _onCopy;
    private readonly LowLevelKeyboardProc _proc; // rooted for the hook's lifetime
    private IntPtr _hookHandle = IntPtr.Zero;
    private int _disposed;

    /// <summary>Returns null (no hook, non-fatal) off Windows or if the hook cannot be installed.</summary>
    public static ReviewCopyHook? TryInstall(Action onCopy)
        => OperatingSystem.IsWindows() ? new ReviewCopyHook(onCopy) : null;

    private ReviewCopyHook(Action onCopy)
    {
        _onCopy = onCopy;
        _proc = HookProc;
        try
        {
            using var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
            using var mainModule = currentProcess.MainModule;
            IntPtr hMod = mainModule is not null ? GetModuleHandle(mainModule.ModuleName) : IntPtr.Zero;

            _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, hMod, 0);
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
        if (nCode >= 0 && wParam == (IntPtr)WM_KEYDOWN)
        {
            try
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                // Ctrl+C only. Shift/Alt held too means some other app's shortcut, so those pass
                // through untouched rather than being hijacked.
                if (data.vkCode == VkC
                    && (GetAsyncKeyState(VkControl) & 0x8000) != 0
                    && (GetAsyncKeyState(VkShift) & 0x8000) == 0
                    && (GetAsyncKeyState(VkMenu) & 0x8000) == 0)
                {
                    _onCopy();
                    // Deliberately NOT swallowed - see the WPF twin's own note.
                }
            }
            catch (Exception ex)
            {
                // Never crash the hook chain (would break keyboard input system-wide).
                FileLog.Write($"RoeSnip: review Ctrl+C hook callback error: {ex.Message}");
            }
        }
        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
    }
}
