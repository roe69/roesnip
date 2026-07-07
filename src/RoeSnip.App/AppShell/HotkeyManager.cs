using System;
using System.Threading.Tasks;
using RoeSnip.Core.Settings;
using SharpHook;
using SharpHook.Data;

namespace RoeSnip.App.AppShell;

/// <summary>Global capture hotkey via SharpHook's <see cref="TaskPoolGlobalHook"/>
/// (PLAN-XPLAT.md §3.2/§5). The settings' HotkeyModifiers/HotkeyVirtualKey keep their Windows
/// VK/MOD_* shapes (PLAN-XPLAT.md §2.6); this class translates them to SharpHook's OS-independent
/// key codes, so the same stored combination works on Windows, macOS (Accessibility permission
/// required — surfaced by the failed-start log, not assumed) and X11 Linux. On Wayland the hook is
/// never started at all (libuiohook is X11-only): the documented primary Linux activation is a DE
/// keyboard shortcut bound to <c>RoeSnip capture</c>.
///
/// This class is deliberately prompt-free, like the WPF original: the one-time
/// PrintScreen/Snipping-Tool consent flow lives in TrayApp (which owns settings persistence); the
/// settings handed to <see cref="Register"/> are expected to already be consent-resolved.
///
/// Unlike the WPF app's RegisterHotKey, a low-level hook observes rather than claims the key:
/// registration "succeeding" is even weaker evidence of end-to-end delivery than RegisterHotKey
/// was (nothing stops another app from also acting on the key), and the keystroke is NOT consumed.
/// The only real verification is a human pressing the key and observing the overlay appear.</summary>
public sealed class HotkeyManager : IDisposable
{
    public const uint ModAlt = 0x0001, ModControl = 0x0002, ModShift = 0x0004, ModWin = 0x0008;
    public const uint VkSnapshot = 0x2C;

    private readonly Action _onHotkey;
    private readonly object _gate = new();

    private TaskPoolGlobalHook? _hook;
    private bool? _hookRunning; // null = not attempted yet; false = unavailable on this session
    private volatile bool _armed;
    private KeyCode _keyCode;
    private uint _modifiers;

    public HotkeyManager(Action onHotkey)
    {
        _onHotkey = onHotkey;
    }

    /// <summary>Best-effort "the hook is up and a combination is armed". Same caveat as the WPF
    /// version's IsRegistered, but weaker still — see the class doc comment.</summary>
    public bool IsRegistered => _armed && _hookRunning == true;

    /// <summary>(Re)arms the hotkey from the given (already consent-resolved) settings. Called
    /// once at startup and again after SettingsWindow saves a hotkey change.</summary>
    public void Register(RoeSnipSettings settings)
    {
        lock (_gate)
        {
            _armed = false;

            KeyCode? keyCode = VirtualKeyToKeyCode(settings.HotkeyVirtualKey);
            if (keyCode is null)
            {
                Console.Error.WriteLine(
                    $"RoeSnip: hotkey virtual key 0x{settings.HotkeyVirtualKey:X} has no global-hook mapping; " +
                    "no hotkey is active. Pick a different key in Settings.");
                return;
            }

            _keyCode = keyCode.Value;
            _modifiers = settings.HotkeyModifiers;

            if (!EnsureHookRunning())
            {
                return;
            }

            _armed = true;
        }
    }

    public void Unregister() => _armed = false;

    private bool EnsureHookRunning()
    {
        if (_hookRunning is bool alreadyDecided)
        {
            return alreadyDecided;
        }

        // P8 audit fix: some compositors leave XDG_SESSION_TYPE unset/wrong but always set
        // WAYLAND_DISPLAY on a Wayland session — treat either signal as Wayland.
        bool isWayland = string.Equals(
                Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland",
                StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
        if (OperatingSystem.IsLinux() && isWayland)
        {
            // libuiohook is X11-only (PLAN-XPLAT.md §5) — never start the hook on Wayland;
            // starting-then-failing and never-starting both end in "no global hotkey", but
            // never-starting avoids a confusing failure log on every launch.
            Console.Error.WriteLine(
                "RoeSnip: global hotkeys are not available on Wayland. Bind a desktop-environment " +
                "keyboard shortcut to `RoeSnip capture` instead (the primary activation path on Wayland).");
            _hookRunning = false;
            return false;
        }

        try
        {
            _hook = new TaskPoolGlobalHook();
            _hook.KeyPressed += OnKeyPressed;
            _ = _hook.RunAsync().ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    lock (_gate)
                    {
                        // Covers e.g. macOS without the Accessibility permission, or a dead X display.
                        Console.Error.WriteLine(
                            $"RoeSnip: the global keyboard hook stopped/failed to start: " +
                            $"{t.Exception?.GetBaseException().Message}. The hotkey is inactive; the app " +
                            "remains operable via the tray menu and `RoeSnip capture`.");

                        // P7 audit fix: dispose the dead hook and reset _hookRunning to null (not
                        // false) so the NEXT Register() call — e.g. after the user re-saves
                        // Settings once Accessibility has been granted — tries a FRESH
                        // TaskPoolGlobalHook instead of EnsureHookRunning's "already decided" fast
                        // path permanently short-circuiting to false for the rest of the process.
                        _hook?.Dispose();
                        _hook = null;
                        _hookRunning = null;
                        _armed = false;
                    }
                }
            }, TaskScheduler.Default);
            _hookRunning = true;
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: failed to create the global keyboard hook: {ex.Message}");
            _hook?.Dispose();
            _hook = null;
            _hookRunning = false;
            return false;
        }
    }

    private void OnKeyPressed(object? sender, KeyboardHookEventArgs e)
    {
        if (!_armed || e.Data.KeyCode != _keyCode)
        {
            return;
        }

        if (!ModifiersMatch(e.RawEvent.Mask))
        {
            return;
        }

        _onHotkey();
    }

    /// <summary>Exact-match semantics, like RegisterHotKey's: a bare-PrintScreen hotkey must NOT
    /// fire on Ctrl+PrintScreen. Lock masks (Num/Caps/Scroll) are ignored.</summary>
    private bool ModifiersMatch(EventMask mask)
    {
        bool ctrl = (mask & EventMask.Ctrl) != 0;
        bool alt = (mask & EventMask.Alt) != 0;
        bool shift = (mask & EventMask.Shift) != 0;
        bool meta = (mask & EventMask.Meta) != 0;

        return ctrl == ((_modifiers & ModControl) != 0)
            && alt == ((_modifiers & ModAlt) != 0)
            && shift == ((_modifiers & ModShift) != 0)
            && meta == ((_modifiers & ModWin) != 0);
    }

    /// <summary>Maps a Windows virtual-key code (the settings' storage shape) to SharpHook's
    /// OS-independent <see cref="KeyCode"/>. Covers the keys the SettingsWindow hotkey capture can
    /// produce (letters, digits, numpad digits, F1-F24, PrintScreen, navigation keys); anything
    /// else returns null and Register logs it.</summary>
    internal static KeyCode? VirtualKeyToKeyCode(uint vk) => vk switch
    {
        VkSnapshot => KeyCode.VcPrintScreen,
        0x13 => KeyCode.VcPause,
        0x20 => KeyCode.VcSpace,
        0x21 => KeyCode.VcPageUp,
        0x22 => KeyCode.VcPageDown,
        0x23 => KeyCode.VcEnd,
        0x24 => KeyCode.VcHome,
        0x25 => KeyCode.VcLeft,
        0x26 => KeyCode.VcUp,
        0x27 => KeyCode.VcRight,
        0x28 => KeyCode.VcDown,
        0x2D => KeyCode.VcInsert,
        0x2E => KeyCode.VcDelete,
        >= 0x30 and <= 0x39 => ParseNamed("Vc" + (char)vk),          // digits 0-9
        >= 0x41 and <= 0x5A => ParseNamed("Vc" + (char)vk),          // letters A-Z
        >= 0x60 and <= 0x69 => ParseNamed("VcNumPad" + (vk - 0x60)), // numpad 0-9
        >= 0x70 and <= 0x87 => ParseNamed("VcF" + (vk - 0x70 + 1)),  // F1-F24
        _ => null,
    };

    private static KeyCode? ParseNamed(string name)
        => Enum.TryParse<KeyCode>(name, out var keyCode) ? keyCode : null;

    public void Dispose()
    {
        lock (_gate)
        {
            _armed = false;
            _hook?.Dispose(); // stops the hook loop; RunAsync's task completes
            _hook = null;
        }
    }
}
