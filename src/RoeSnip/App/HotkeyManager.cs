using System;
using Microsoft.Win32;
using RoeSnip.Interop;

namespace RoeSnip.App;

/// <summary>Registers the global capture hotkey on a message-only window (HWND_MESSAGE, per
/// DESIGN.md) and dispatches WM_HOTKEY to a caller-supplied callback (wired by TrayApp to
/// <see cref="AppComposition.RunCaptureFlowAsync"/>).
///
/// PrintScreen/Snipping-Tool consent flow (DESIGN.md): on Windows 11, a bare PrintScreen is
/// intercepted for Snipping Tool whenever
/// <c>HKCU\Control Panel\Keyboard\PrintScreenKeyForSnippingEnabled</c> is non-zero. The first time
/// this process registers the PrintScreen-alone default hotkey, this class checks that value and,
/// if set, shows a one-time dialog offering to disable it (writes 0 to that key) or instead
/// register Ctrl+PrintScreen. That registry write happens ONLY as the direct result of an
/// interactive "Yes" click on that dialog — this path must never run unattended/automated (no
/// unit test exercises it; see PLAN.md §3.3 and the WP-C brief).</summary>
public sealed class HotkeyManager : IDisposable
{
    private const int HotkeyId = 1;
    private const string PrintScreenRegistryKeyPath = @"Control Panel\Keyboard";
    private const string PrintScreenValueName = "PrintScreenKeyForSnippingEnabled";

    private readonly HotkeyMessageWindow _window;
    private readonly Action _onHotkey;
    private bool _consentAsked;
    private bool _registered;

    public HotkeyManager(Action onHotkey)
    {
        _onHotkey = onHotkey;
        _window = new HotkeyMessageWindow(HandleHotkeyMessage);
    }

    /// <summary>True if the last <see cref="Register"/> call's RegisterHotKey succeeded.
    /// IMPORTANT: this is NOT proof of end-to-end delivery. RegisterHotKey succeeding only means
    /// no other top-level window on this desktop already registered the same combination via
    /// RegisterHotKey; it does NOT mean some other mechanism (a global keyboard hook, Snipping
    /// Tool's own PrtScr interception, a game running in exclusive fullscreen, etc.) won't still
    /// steal the keystroke before Windows ever routes it here. The only real verification is a
    /// human pressing the key and observing the overlay appear — this class has no way to detect
    /// that itself and does not attempt to fake one.</summary>
    public bool IsRegistered => _registered;

    /// <summary>(Re)registers the hotkey from the given settings, unregistering any previous
    /// registration first. Called once at startup and again by SettingsWindow after the user
    /// changes the hotkey.</summary>
    public void Register(RoeSnipSettings settings)
    {
        Unregister();

        uint modifiers = settings.HotkeyModifiers;
        uint virtualKey = settings.HotkeyVirtualKey;

        if (modifiers == 0 && virtualKey == NativeMethods.VK_SNAPSHOT && !_consentAsked)
        {
            _consentAsked = true;
            modifiers = ResolvePrintScreenConsent(modifiers);
        }

        _registered = NativeMethods.RegisterHotKey(
            _window.Handle, HotkeyId, modifiers | NativeMethods.MOD_NOREPEAT, virtualKey);

        if (!_registered)
        {
            Console.Error.WriteLine(
                $"RoeSnip: RegisterHotKey failed (modifiers=0x{modifiers:X}, vk=0x{virtualKey:X}); " +
                "another application likely already owns this key combination.");
        }
    }

    public void Unregister()
    {
        if (_registered)
        {
            NativeMethods.UnregisterHotKey(_window.Handle, HotkeyId);
            _registered = false;
        }
    }

    private void HandleHotkeyMessage(int id)
    {
        if (id == HotkeyId)
        {
            _onHotkey();
        }
    }

    /// <summary>Reads PrintScreenKeyForSnippingEnabled and, if non-zero, asks the user (via a
    /// blocking dialog) whether to disable it or fall back to Ctrl+PrintScreen. Returns the
    /// modifiers RoeSnip should actually register with. Any registry or dialog failure logs and
    /// falls back to keeping the PrintScreen-alone default (fail open on the UX, not silently
    /// register nothing).</summary>
    private static uint ResolvePrintScreenConsent(uint modifiers)
    {
        try
        {
            int value;
            using (var key = Registry.CurrentUser.OpenSubKey(PrintScreenRegistryKeyPath, writable: false))
            {
                value = key?.GetValue(PrintScreenValueName) switch
                {
                    int i => i,
                    string s when int.TryParse(s, out int parsed) => parsed,
                    _ => 0,
                };
            }

            if (value == 0)
            {
                return modifiers; // Windows isn't intercepting a bare PrtScr; nothing to do.
            }

            var result = System.Windows.Forms.MessageBox.Show(
                "Windows is currently set to open Snipping Tool when you press PrintScreen, which " +
                "would prevent RoeSnip's screenshot hotkey from receiving it.\n\n" +
                "Disable that Windows setting so PrintScreen triggers RoeSnip directly?\n\n" +
                "Choosing \"No\" leaves the Windows setting alone and makes RoeSnip use " +
                "Ctrl+PrintScreen instead.",
                "RoeSnip - PrintScreen hotkey",
                System.Windows.Forms.MessageBoxButtons.YesNo,
                System.Windows.Forms.MessageBoxIcon.Question);

            if (result == System.Windows.Forms.DialogResult.Yes)
            {
                using var writableKey = Registry.CurrentUser.OpenSubKey(PrintScreenRegistryKeyPath, writable: true);
                writableKey?.SetValue(PrintScreenValueName, 0, RegistryValueKind.DWord);
                return modifiers;
            }

            return NativeMethods.MOD_CONTROL;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"RoeSnip: PrintScreen consent check failed, keeping PrintScreen-alone: {ex.Message}");
            return modifiers;
        }
    }

    public void Dispose()
    {
        Unregister();
        _window.Dispose();
    }

    /// <summary>A message-only window (HWND_MESSAGE parent, never shown) that exists purely to
    /// receive WM_HOTKEY. Relies on WinForms' NativeWindow for the Win32 window-class/WndProc
    /// plumbing; the actual message pump is whatever's already running on this thread (TrayApp's
    /// WinForms Application.Run).</summary>
    private sealed class HotkeyMessageWindow : System.Windows.Forms.NativeWindow, IDisposable
    {
        private readonly Action<int> _onHotkeyId;

        public HotkeyMessageWindow(Action<int> onHotkeyId)
        {
            _onHotkeyId = onHotkeyId;
            var cp = new System.Windows.Forms.CreateParams
            {
                Parent = new IntPtr(-3), // HWND_MESSAGE
            };
            CreateHandle(cp);
        }

        protected override void WndProc(ref System.Windows.Forms.Message m)
        {
            if (m.Msg == NativeMethods.WM_HOTKEY)
            {
                _onHotkeyId((int)m.WParam);
            }
            base.WndProc(ref m);
        }

        public void Dispose() => DestroyHandle();
    }
}
