using System;
using RoeSnip.Interop;

namespace RoeSnip.App;

/// <summary>Registers the global capture hotkey on a message-only window (HWND_MESSAGE, per
/// DESIGN.md) and dispatches WM_HOTKEY to a caller-supplied callback (wired by TrayApp to
/// <see cref="AppComposition.RunCaptureFlowAsync"/>).
///
/// This class is deliberately prompt-free: the one-time PrintScreen/Snipping-Tool consent flow
/// (the dialog around <c>HKCU\Control Panel\Keyboard\PrintScreenKeyForSnippingEnabled</c>,
/// DESIGN.md §2) lives in <see cref="TrayApp.ResolvePrintScreenConsent"/>, which owns the settings
/// and can persist the user's answer. The settings handed to <see cref="Register"/> are expected
/// to already be consent-resolved — keeping the (modal) prompt out of this class means merely
/// registering a hotkey can never block the tray app's startup.</summary>
public sealed class HotkeyManager : IDisposable
{
    private const int HotkeyId = 1;

    private readonly HotkeyMessageWindow _window;
    private readonly Action _onHotkey;
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

    /// <summary>(Re)registers the hotkey from the given (already consent-resolved) settings,
    /// unregistering any previous registration first. Called once at startup and again by TrayApp
    /// after SettingsWindow saves a hotkey change — in both cases the caller has run
    /// TrayApp.ResolvePrintScreenConsent over the settings first.</summary>
    public void Register(RoeSnipSettings settings)
    {
        Unregister();

        uint modifiers = settings.HotkeyModifiers;
        uint virtualKey = settings.HotkeyVirtualKey;

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
