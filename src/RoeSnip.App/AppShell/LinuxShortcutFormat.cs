using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RoeSnip.App.AppShell;

/// <summary>Pure conversions for the Wayland capture hotkey (LinuxGlobalShortcuts): the stored
/// Windows VK/MOD_* hotkey to the GlobalShortcuts portal's xdg shortcut trigger ("CTRL+SHIFT+s",
/// "Print") and to a GTK accelerator for a GNOME custom keybinding ("&lt;Control&gt;&lt;Shift&gt;s"), plus
/// the gsettings string-array text format. Portable so the Windows-TFM test project covers it.</summary>
public static class LinuxShortcutFormat
{
    public const string GnomeMediaKeysSchema = "org.gnome.settings-daemon.plugins.media-keys";
    public const string GnomeCustomKeybindingSchema = "org.gnome.settings-daemon.plugins.media-keys.custom-keybinding";
    public const string GnomeRoeSnipPath = "/org/gnome/settings-daemon/plugins/media-keys/custom-keybindings/roesnip/";

    /// <summary>xkb keysym name for a stored virtual key, or null when unmapped. Covers the same
    /// set as HotkeyManager.VirtualKeyToKeyCode. Letters are lowercase (keysym "s", not "S").</summary>
    public static string? KeysymName(uint vk) => vk switch
    {
        HotkeyManager.VkSnapshot => "Print",
        0x13 => "Pause",
        0x20 => "space",
        0x21 => "Page_Up",
        0x22 => "Page_Down",
        0x23 => "End",
        0x24 => "Home",
        0x25 => "Left",
        0x26 => "Up",
        0x27 => "Right",
        0x28 => "Down",
        0x2D => "Insert",
        0x2E => "Delete",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => char.ToLowerInvariant((char)vk).ToString(),
        >= 0x60 and <= 0x69 => "KP_" + (vk - 0x60),
        >= 0x70 and <= 0x87 => "F" + (vk - 0x70 + 1),
        _ => null,
    };

    /// <summary>The portal's preferred_trigger (XDG shortcuts spec: CTRL, ALT, SHIFT, LOGO, then
    /// the keysym, joined by "+").</summary>
    public static string? ToPortalTrigger(uint modifiers, uint vk)
    {
        string? key = KeysymName(vk);
        if (key is null)
        {
            return null;
        }

        var parts = new List<string>();
        if ((modifiers & HotkeyManager.ModControl) != 0) parts.Add("CTRL");
        if ((modifiers & HotkeyManager.ModAlt) != 0) parts.Add("ALT");
        if ((modifiers & HotkeyManager.ModShift) != 0) parts.Add("SHIFT");
        if ((modifiers & HotkeyManager.ModWin) != 0) parts.Add("LOGO");
        parts.Add(key);
        return string.Join("+", parts);
    }

    /// <summary>GTK accelerator text for a GNOME custom keybinding's "binding" key.</summary>
    public static string? ToGtkAccelerator(uint modifiers, uint vk)
    {
        string? key = KeysymName(vk);
        if (key is null)
        {
            return null;
        }

        var sb = new StringBuilder();
        if ((modifiers & HotkeyManager.ModControl) != 0) sb.Append("<Control>");
        if ((modifiers & HotkeyManager.ModAlt) != 0) sb.Append("<Alt>");
        if ((modifiers & HotkeyManager.ModShift) != 0) sb.Append("<Shift>");
        if ((modifiers & HotkeyManager.ModWin) != 0) sb.Append("<Super>");
        sb.Append(key);
        return sb.ToString();
    }

    /// <summary>True when two GTK accelerators name the same combination (modifier order, case
    /// and the Ctrl/Primary/Control and Super/Mod4 aliases ignored).</summary>
    public static bool AcceleratorsEqual(string? a, string? b)
    {
        var na = NormalizeAccelerator(a);
        var nb = NormalizeAccelerator(b);
        return na is not null && na == nb;
    }

    private static string? NormalizeAccelerator(string? accel)
    {
        if (string.IsNullOrWhiteSpace(accel))
        {
            return null;
        }

        var mods = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(accel, "<([^>]+)>"))
        {
            string mod = m.Groups[1].Value.ToLowerInvariant() switch
            {
                "ctrl" or "control" or "primary" => "control",
                "super" or "mod4" => "super",
                "alt" or "mod1" => "alt",
                var other => other,
            };
            mods.Add(mod);
        }

        string key = Regex.Replace(accel, "<[^>]+>", "").Trim().ToLowerInvariant();
        return key.Length == 0 ? null : string.Join("", mods.Select(x => "<" + x + ">")) + key;
    }

    /// <summary>Parses gsettings' printed string array: "@as []" or "['/a/', '/b/']".</summary>
    public static IReadOnlyList<string> ParseStringArray(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        foreach (Match m in Regex.Matches(text, @"'((?:[^'\\]|\\.)*)'|""((?:[^""\\]|\\.)*)"""))
        {
            string raw = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            result.Add(Regex.Replace(raw, @"\\(.)", "$1"));
        }

        return result;
    }

    /// <summary>GVariant text for a string array, as gsettings set expects.</summary>
    public static string FormatStringArray(IEnumerable<string> items)
    {
        var list = items.Select(GVariantString).ToList();
        return list.Count == 0 ? "@as []" : "[" + string.Join(", ", list) + "]";
    }

    /// <summary>The custom-keybindings list with our entry appended, or null when it is already
    /// present (nothing to write). Existing entries keep their order.</summary>
    public static IReadOnlyList<string>? MergeKeybindingList(IReadOnlyList<string> existing, string ourPath)
    {
        if (existing.Any(p => string.Equals(p.TrimEnd('/'), ourPath.TrimEnd('/'), StringComparison.Ordinal)))
        {
            return null;
        }

        return existing.Concat(new[] { ourPath }).ToList();
    }

    /// <summary>GVariant single-quoted string literal.</summary>
    public static string GVariantString(string value) =>
        "'" + value.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

    /// <summary>Quotes a path for a command line GLib splits with g_shell_parse_argv.</summary>
    public static string ShellQuote(string path)
    {
        if (path.Length > 0 && Regex.IsMatch(path, @"^[A-Za-z0-9_@%+=:,./-]+$"))
        {
            return path;
        }

        return "'" + path.Replace("'", "'\\''") + "'";
    }

    /// <summary>The custom keybinding command: the installed executable and the capture verb.</summary>
    public static string CaptureCommand(string executablePath) => ShellQuote(executablePath) + " capture";

    /// <summary>The binary a keybinding should run: the installed AppImage when it exists (a
    /// scratch or Downloads copy can be deleted), else $APPIMAGE, else the process path.</summary>
    public static string? PreferredCaptureExecutable(string? installedAppImage, bool installExists, string? appImage, string? processPath)
    {
        if (installExists && !string.IsNullOrEmpty(installedAppImage))
        {
            return installedAppImage;
        }
        return !string.IsNullOrEmpty(appImage) ? appImage : processPath;
    }

    /// <summary>The first word of a command, with POSIX single quotes (as <see cref="ShellQuote"/>
    /// writes them) and backslash escapes undone; null when empty.</summary>
    public static string? CommandExecutable(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var word = new StringBuilder();
        string s = command.TrimStart();
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\'')
            {
                int end = s.IndexOf('\'', i + 1);
                if (end < 0)
                {
                    end = s.Length;
                }
                word.Append(s, i + 1, end - i - 1);
                i = end;
            }
            else if (c == '\\' && i + 1 < s.Length)
            {
                word.Append(s[++i]);
            }
            else if (char.IsWhiteSpace(c))
            {
                break;
            }
            else
            {
                word.Append(c);
            }
        }
        return word.Length > 0 ? word.ToString() : null;
    }

    /// <summary>True when the command runs an absolute path that <paramref name="exists"/> says is
    /// gone. A bare name (found via PATH) is never stale.</summary>
    public static bool CommandBinaryMissing(string? command, Func<string, bool> exists)
    {
        string? exe = CommandExecutable(command);
        return exe is not null && exe.StartsWith('/') && !exists(exe);
    }

    /// <summary>True for a command that already runs a RoeSnip capture (a shortcut the user made).</summary>
    public static bool IsRoeSnipCaptureCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        string trimmed = command.Trim();
        return trimmed.Contains("roesnip", StringComparison.OrdinalIgnoreCase)
            && trimmed.EndsWith(" capture", StringComparison.Ordinal);
    }
}
