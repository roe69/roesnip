using System;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
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

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool BringWindowToTop(IntPtr hWnd);

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
    internal const int VK_SHIFT = 0x10;
    internal const int VK_CAPITAL = 0x14;
    internal const int VK_MENU = 0x12;

    // ---------- SendInput (ForegroundActivator's tier-3 Alt-tap "unlock", item 3a) ----------

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    internal const uint INPUT_KEYBOARD = 1;
    internal const uint KEYEVENTF_KEYUP = 0x0002;

    /// <summary>Injects a synthetic Alt down+up — the documented workaround for Windows' foreground-
    /// lock timeout: SendInput is treated as "real" input for the purposes of resetting that timeout
    /// (unlike calling SetForegroundWindow directly, which is exactly the operation the timeout
    /// restricts), so a SetForegroundWindow attempt immediately afterward is far more likely to
    /// succeed. Used only as ForegroundActivator's last-resort tier 3.</summary>
    internal static void SendAltTapUnlock()
    {
        var inputs = new[]
        {
            new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_MENU } } },
            new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_MENU, dwFlags = KEYEVENTF_KEYUP } } },
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    // ---------- ToUnicodeEx (TextEditKeyForwarder's printable-key translation, item 3b) ----------

    [DllImport("user32.dll")]
    internal static extern int ToUnicodeEx(
        uint wVirtKey, uint wScanCode, byte[] lpKeyState,
        [Out] System.Text.StringBuilder pwszBuff, int cchBuff, uint wFlags, IntPtr dwhkl);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetKeyboardLayout(uint idThread);
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
/// EXCEPTION (per spec): while a text annotation edit is active AND the overlay window genuinely
/// holds OS foreground/focus (via ForegroundActivator's activation ladder), only Esc is intercepted
/// here — the rest (typing, Enter-to-commit, Ctrl+C-to-copy-text) must reach the real focused
/// control (the in-progress TextBox) through the normal WPF input pipeline, not through this hook.
/// If the window does NOT hold real foreground/focus while text-editing (every ForegroundActivator
/// tier failed), this hook instead routes every relevant keydown to TextEditKeyForwarder — the
/// guaranteed fallback tier (item 3b, UX round 3) that applies the keystroke directly against the
/// TextBox, since nothing typed could otherwise ever reach it.
///
/// SECOND EXCEPTION (UX round 5, item 2): while any OTHER text input in the overlay window (today:
/// the toolbar size ComboBox's editable TextBox — see OverlayWindow.IsTextInputFocused) holds WPF
/// keyboard focus and the window is genuinely OS-foreground, this hook intercepts NOTHING — all
/// keys, Enter included, flow through the normal pipeline so typing a size can never trigger a
/// session command. See the inline comment at that branch for why no forwarder tier is needed.
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

                var activeWindow = _getActiveWindow();
                bool textEditing = activeWindow?.IsTextEditingActive == true;

                // Trust the normal WPF pipeline only when BOTH hold: the window is really OS-
                // foreground AND the editor really holds WPF keyboard focus. "Foreground but focus
                // landed elsewhere" (activation succeeded, focus lost during it) previously left an
                // uncovered gap where typing went to the window and silently did nothing.
                if (textEditing && activeWindow is not null
                    && !(IsWindowForeground(activeWindow) && activeWindow.TextEditorHasKeyboardFocus))
                {
                    // Guaranteed fallback tier (item 3b, UX round 3): the overlay does not currently
                    // hold real OS foreground/focus (every ForegroundActivator tier failed), so
                    // nothing typed would ever reach the in-progress TextBox through the normal WPF
                    // input pipeline — forward it directly instead of letting it leak through to
                    // whatever background app actually has focus. Swallowed unconditionally, even
                    // for keys TextEditKeyForwarder doesn't itself act on, since the alternative is
                    // leaking a keystroke the user believes is going into RoeSnip.
                    bool shift = (OverlayInputInterop.GetKeyState(OverlayInputInterop.VK_SHIFT) & 0x8000) != 0;
                    bool capsLockOn = (OverlayInputInterop.GetKeyState(OverlayInputInterop.VK_CAPITAL) & 0x0001) != 0;
                    TextEditKeyForwarder.Forward(activeWindow, key, data.vkCode, data.scanCode, shift, capsLockOn, ctrl);
                    return (IntPtr)1;
                }

                // Generalized text-input guard (UX round 5, item 2): the toolbar's size ComboBox
                // has an editable TextBox — the one non-annotation text input in the overlay.
                // While it genuinely holds WPF keyboard focus AND the window is really OS-
                // foreground (same double condition as the annotation-editor branch above, for the
                // same reason: only then can the normal focus-dependent WPF pipeline be trusted),
                // EVERY key passes through untouched — crucially including Enter, which the
                // ComboBox handles itself (commit the typed size, hand focus back to the window)
                // and which must NOT be swallowed here as confirm-the-snip mid-typing. Unlike the
                // annotation editor there is no forwarder fallback tier: without real focus the
                // user cannot be typing into the box in the first place (it only ever gains focus
                // by a real click on a foreground window), so the normal session-key handling
                // below stays correct in that state.
                if (!textEditing && activeWindow is not null
                    && activeWindow.IsTextInputFocused && IsWindowForeground(activeWindow))
                {
                    return OverlayInputInterop.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
                }

                bool isSessionKey = key == Key.Escape || key == Key.Enter
                    || (ctrl && (key == Key.C || key == Key.S || key == Key.Z));

                // While typing (and the window does hold real focus, per the branch above), only Esc
                // is ours to steal — everything else must reach the real focused TextBox through the
                // normal (focus-dependent) WPF pipeline.
                if (isSessionKey && (!textEditing || key == Key.Escape))
                {
                    if (activeWindow is not null)
                    {
                        var modifiers = ctrl ? ModifierKeys.Control : ModifierKeys.None;
                        // Marshal to the UI thread's dispatcher queue rather than calling straight
                        // through: we're on the hook's call stack here (synchronous, system-wide),
                        // and ProcessKeyCommand can close windows (Cancel) — doing that reentrantly
                        // from inside the hook callback itself would be fragile. BeginInvoke defers
                        // it to run right after this hook returns.
                        activeWindow.Dispatcher.BeginInvoke(new Action(
                            () => activeWindow.ProcessKeyCommand(key, modifiers)));
                    }
                    return (IntPtr)1; // swallow — never let it leak through to the background app
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

    private static bool IsWindowForeground(OverlayWindow window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        return hwnd != IntPtr.Zero && OverlayInputInterop.GetForegroundWindow() == hwnd;
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
