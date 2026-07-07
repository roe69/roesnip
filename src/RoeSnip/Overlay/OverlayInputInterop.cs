using System;
using System.Runtime.InteropServices;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace RoeSnip.Overlay;

// Not in Interop/NativeMethods.cs — added locally in this file per the existing convention
// (Imaging/ClipboardService.cs's own P/Invoke block documents the same allowance): these
// declarations are used only by the overlay session's keyboard-reliability layers below and have
// no reason to be visible to the rest of the app.
internal static class OverlayInputInterop
{
    internal delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    internal static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    internal static extern short GetKeyState(int nVirtKey);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [StructLayout(LayoutKind.Sequential)]
    internal struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    internal const int WH_KEYBOARD_LL = 13;
    internal const int WM_KEYDOWN = 0x0100;
    internal const int WM_SYSKEYDOWN = 0x0104;
    internal const int VK_CONTROL = 0x11;
}

/// <summary>Session-scoped low-level keyboard hook (WH_KEYBOARD_LL) — the fix for "Esc doesn't
/// cancel". Root cause (confirmed): OverlayController.RunAsync's Activate() is not proof of actual
/// foreground delivery — a background tray process cannot reliably steal foreground focus
/// (SetForegroundWindow restrictions), so the hotkey's foreground grant can be stale by the time
/// the overlay appears, and the second-instance pipe trigger never had a foreground grant at all.
/// When that happens keyboard focus stays in whatever app the user was in, and Esc/Enter/Ctrl+C/
/// Ctrl+S/Ctrl+Z never reach the overlay's WPF key handlers no matter how correct the two-stage Esc
/// logic is.
///
/// A WH_KEYBOARD_LL hook intercepts these keys at the OS level regardless of which window has
/// focus, so the overlay's session keys work with zero dependence on foreground/focus. It only
/// intercepts (and swallows, so the keystroke doesn't leak through to the background app) the five
/// keys the overlay cares about; everything else passes through untouched via CallNextHookEx.
///
/// EXCEPTION (per spec): while a text annotation edit is active, only Esc is intercepted here — the
/// rest (typing, Enter-to-commit, Ctrl+C-to-copy-text) must reach the real focused control (the
/// in-progress TextBox, via OverlayWindow's own best-effort activation — see TryActivateForTextInput
/// in OverlayWindow.xaml.cs) through the normal WPF input pipeline, not through this hook.
///
/// Lifecycle: installed once when the owning OverlaySession is constructed ("the session opens"),
/// disposed exactly once from OverlaySession.Finish() — the single terminal point every session exit
/// path (Cancel, CancelStage-to-empty, Confirm/Copy/Save/SaveHdr, an exception during Show(), or an
/// externally-closed window such as Alt+F4) funnels through, wrapped in try/finally there so the
/// hook is removed unconditionally. Dispose() itself is idempotent (Interlocked-guarded) as a second
/// safety net. The hook delegate is kept alive in <see cref="_proc"/> for the hook's entire lifetime
/// — a hook whose delegate gets garbage-collected while still installed is a classic crash.</summary>
internal sealed class SessionKeyboardHook : IDisposable
{
    private readonly Func<OverlayWindow?> _getActiveWindow;
    private readonly OverlayInputInterop.LowLevelKeyboardProc _proc; // rooted for the hook's lifetime
    private IntPtr _hookHandle = IntPtr.Zero;
    private int _disposed;

    public SessionKeyboardHook(Func<OverlayWindow?> getActiveWindow)
    {
        _getActiveWindow = getActiveWindow;
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
                int error = Marshal.GetLastWin32Error();
                Console.Error.WriteLine(
                    $"RoeSnip: failed to install the session keyboard hook (error 0x{error:X}); " +
                    "Esc/Enter/Ctrl+C/Ctrl+S/Ctrl+Z will only work while the overlay actually has focus.");
            }
        }
        catch (Exception ex)
        {
            // Never let hook installation itself take down the overlay session — this is a
            // reliability layer on top of normal WPF key handling, not a hard requirement.
            Console.Error.WriteLine($"RoeSnip: failed to install the session keyboard hook: {ex.Message}");
            _hookHandle = IntPtr.Zero;
        }
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // Per MSDN: a hook procedure MUST call CallNextHookEx without further processing whenever
        // nCode < 0, and should generally do so for any message it doesn't act on.
        if (nCode < 0)
        {
            return OverlayInputInterop.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        if (wParam == (IntPtr)OverlayInputInterop.WM_KEYDOWN || wParam == (IntPtr)OverlayInputInterop.WM_SYSKEYDOWN)
        {
            try
            {
                var data = Marshal.PtrToStructure<OverlayInputInterop.KBDLLHOOKSTRUCT>(lParam);
                Key key = KeyInterop.KeyFromVirtualKey((int)data.vkCode);
                bool ctrl = (OverlayInputInterop.GetKeyState(OverlayInputInterop.VK_CONTROL) & 0x8000) != 0;

                bool isSessionKey = key == Key.Escape || key == Key.Enter
                    || (ctrl && (key == Key.C || key == Key.S || key == Key.Z));

                if (isSessionKey)
                {
                    var activeWindow = _getActiveWindow();
                    bool textEditing = activeWindow?.IsTextEditingActive == true;

                    // While typing, only Esc is ours to steal — everything else must reach the real
                    // focused TextBox through the normal (focus-dependent) WPF pipeline.
                    if (!textEditing || key == Key.Escape)
                    {
                        if (activeWindow is not null)
                        {
                            var modifiers = ctrl ? ModifierKeys.Control : ModifierKeys.None;
                            // Marshal to the UI thread's dispatcher queue rather than calling
                            // straight through: we're on the hook's call stack here (synchronous,
                            // system-wide), and ProcessKeyCommand can close windows (Cancel) —
                            // doing that reentrantly from inside the hook callback itself would be
                            // fragile. BeginInvoke defers it to run right after this hook returns.
                            activeWindow.Dispatcher.BeginInvoke(new Action(
                                () => activeWindow.ProcessKeyCommand(key, modifiers)));
                        }
                        return (IntPtr)1; // swallow — never let it leak through to the background app
                    }
                }
            }
            catch (Exception ex)
            {
                // A malformed hook struct or KeyInterop failure must never crash the hook chain
                // (which would break keyboard input system-wide) — fall through to CallNextHookEx.
                Console.Error.WriteLine($"RoeSnip: session keyboard hook callback error: {ex.Message}");
            }
        }

        return OverlayInputInterop.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return; // already unhooked — Dispose is safe to call more than once
        }

        if (_hookHandle != IntPtr.Zero)
        {
            OverlayInputInterop.UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
    }
}
