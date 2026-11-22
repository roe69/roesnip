using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using RoeSnip.Core.Diagnostics;
using RoeSnip.Core.Settings;

namespace RoeSnip.App.AppShell;

/// <summary>GNOME's analogue of the Windows Snipping Tool PrintScreen intercept: gnome-shell grabs
/// Print / Shift+Print / Alt+Print for its own screenshot UI, so neither the GlobalShortcuts portal
/// nor a custom keybinding ever receives them. Detects which org.gnome.shell.keybindings key holds
/// the capture hotkey and, unless <see cref="RoeSnipSettings.TakeOverSystemPrintScreen"/> is off,
/// clears only that accelerator (original value kept in
/// <see cref="RoeSnipSettings.GnomeShellKeybindingBackup"/>), writing it back when the hotkey moves
/// off it or the setting is turned off.</summary>
public sealed class GnomePrintScreenTakeover
{
    public const string ShellSchema = "org.gnome.shell.keybindings";

    public static readonly string[] ShellKeys =
    {
        "show-screenshot-ui", "screenshot", "screenshot-window", "show-screen-recording-ui",
    };

    private readonly Func<string[], string?> _gsettings;

    /// <param name="gsettings">Runs gsettings with these args; stdout on success, null on failure.
    /// Defaults to the real binary.</param>
    public GnomePrintScreenTakeover(Func<string[], string?>? gsettings = null)
    {
        _gsettings = gsettings ?? RunGsettings;
    }

    public static bool IsGnomeSession() =>
        (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "")
            .Split(':').Any(d => string.Equals(d, "GNOME", StringComparison.OrdinalIgnoreCase));

    /// <summary>Applies <see cref="RoeSnipSettings.TakeOverSystemPrintScreen"/> with no prompt. Call
    /// before HotkeyManager.Register (startup, and every Settings save with the previous settings)
    /// so the keybinding sees a free key. Saves the settings when it changed them.</summary>
    public static RoeSnipSettings ApplyTakeover(RoeSnipSettings settings, RoeSnipSettings? previous)
    {
        if (!OperatingSystem.IsLinux() || !IsGnomeSession())
        {
            return settings;
        }

        return new GnomePrintScreenTakeover().Apply(settings, previous, SettingsStore.Save);
    }

    /// <summary>On: clear GNOME's binding for the hotkey (backed up) and restore keys the hotkey
    /// left; re-ticking after an opt-out moves Ctrl+PrintScreen back to PrintScreen. Off: restore
    /// every backed-up key and, when GNOME then holds the PrintScreen hotkey, use Ctrl+PrintScreen.</summary>
    public RoeSnipSettings Apply(RoeSnipSettings settings, RoeSnipSettings? previous, Action<RoeSnipSettings> save)
    {
        try
        {
            var updated = settings;
            if (settings.TakeOverSystemPrintScreen)
            {
                if (previous is { TakeOverSystemPrintScreen: false }
                    && updated.HotkeyVirtualKey == HotkeyManager.VkSnapshot
                    && updated.HotkeyModifiers == HotkeyManager.ModControl)
                {
                    updated = updated with { HotkeyModifiers = 0 };
                }

                updated = RestoreIfMovedOff(updated, updated.HotkeyModifiers, updated.HotkeyVirtualKey);
                var conflicts = Detect(updated.HotkeyModifiers, updated.HotkeyVirtualKey);
                if (conflicts.Count > 0)
                {
                    string accel = LinuxShortcutFormat.ToGtkAccelerator(updated.HotkeyModifiers, updated.HotkeyVirtualKey)!;
                    FileLog.Write($"RoeSnip: GNOME binds {accel} to {string.Join(", ", conflicts.Keys)}; taking it over.");
                    updated = TakeOver(updated, updated.HotkeyModifiers, updated.HotkeyVirtualKey);
                }
            }
            else
            {
                updated = Restore(updated);
                if (updated.HotkeyVirtualKey == HotkeyManager.VkSnapshot
                    && updated.HotkeyModifiers != HotkeyManager.ModControl
                    && Detect(updated.HotkeyModifiers, updated.HotkeyVirtualKey).Count > 0)
                {
                    FileLog.Write("RoeSnip: PrintScreen takeover is off and GNOME holds the hotkey; RoeSnip uses Ctrl+PrintScreen.");
                    updated = updated with { HotkeyModifiers = HotkeyManager.ModControl };
                }
            }

            if (!ReferenceEquals(updated, settings))
            {
                try
                {
                    save(updated);
                }
                catch (Exception ex)
                {
                    FileLog.Write($"RoeSnip: failed to persist the GNOME PrintScreen takeover: {ex.Message}");
                }
            }

            return updated;
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: GNOME PrintScreen takeover failed, leaving GNOME's bindings: {ex.Message}");
            return settings;
        }
    }

    /// <summary>org.gnome.shell.keybindings keys whose bindings include the hotkey, mapped to their
    /// current gsettings value text.</summary>
    public IReadOnlyDictionary<string, string> Detect(uint modifiers, uint virtualKey)
    {
        var result = new Dictionary<string, string>();
        string? accel = LinuxShortcutFormat.ToGtkAccelerator(modifiers, virtualKey);
        if (accel is null)
        {
            return result;
        }

        foreach (string key in ShellKeys)
        {
            string? text = _gsettings(new[] { "get", ShellSchema, key });
            if (text is not null
                && LinuxShortcutFormat.ParseStringArray(text).Any(b => LinuxShortcutFormat.AcceleratorsEqual(b, accel)))
            {
                result[key] = text;
            }
        }

        return result;
    }

    /// <summary>Removes the hotkey's accelerator from every GNOME key that holds it (other
    /// accelerators on that key stay) and records each key's original value once.</summary>
    public RoeSnipSettings TakeOver(RoeSnipSettings settings, uint modifiers, uint virtualKey)
    {
        string? accel = LinuxShortcutFormat.ToGtkAccelerator(modifiers, virtualKey);
        var conflicts = Detect(modifiers, virtualKey);
        if (accel is null || conflicts.Count == 0)
        {
            return settings;
        }

        var backup = new Dictionary<string, string>(settings.GnomeShellKeybindingBackup);
        foreach (var (key, text) in conflicts)
        {
            var remaining = LinuxShortcutFormat.ParseStringArray(text)
                .Where(b => !LinuxShortcutFormat.AcceleratorsEqual(b, accel));
            if (_gsettings(new[] { "set", ShellSchema, key, LinuxShortcutFormat.FormatStringArray(remaining) }) is null)
            {
                FileLog.Write($"RoeSnip: GNOME {key} could not be changed; {accel} stays with GNOME.");
                continue;
            }

            backup.TryAdd(key, text); // keep the oldest original across re-takes
            FileLog.Write($"RoeSnip: GNOME {key} cleared (was {text}) so {accel} reaches RoeSnip.");
        }

        return settings with { GnomeShellKeybindingBackup = backup };
    }

    /// <summary>Writes backed-up GNOME keys back (all of them, or only <paramref name="keys"/>) and
    /// drops them from the backup. Also for uninstall.</summary>
    public RoeSnipSettings Restore(RoeSnipSettings settings, IEnumerable<string>? keys = null)
    {
        var targets = (keys ?? settings.GnomeShellKeybindingBackup.Keys).ToList();
        if (targets.Count == 0)
        {
            return settings;
        }

        var backup = new Dictionary<string, string>(settings.GnomeShellKeybindingBackup);
        foreach (string key in targets)
        {
            if (!backup.TryGetValue(key, out string? value))
            {
                continue;
            }

            if (_gsettings(new[] { "set", ShellSchema, key, value }) is null)
            {
                FileLog.Write($"RoeSnip: GNOME {key} could not be restored to {value}.");
                continue;
            }

            backup.Remove(key);
            FileLog.Write($"RoeSnip: GNOME {key} restored to {value}.");
        }

        return backup.Count == settings.GnomeShellKeybindingBackup.Count
            ? settings
            : settings with { GnomeShellKeybindingBackup = backup };
    }

    /// <summary>Restores backed-up keys whose original value does not include the current hotkey.
    /// Returns the same instance when nothing changed.</summary>
    public RoeSnipSettings RestoreIfMovedOff(RoeSnipSettings settings, uint modifiers, uint virtualKey)
    {
        string? accel = LinuxShortcutFormat.ToGtkAccelerator(modifiers, virtualKey);
        var movedOff = settings.GnomeShellKeybindingBackup
            .Where(kv => accel is null
                || !LinuxShortcutFormat.ParseStringArray(kv.Value).Any(b => LinuxShortcutFormat.AcceleratorsEqual(b, accel)))
            .Select(kv => kv.Key)
            .ToList();
        return movedOff.Count == 0 ? settings : Restore(settings, movedOff);
    }

    /// <summary>Runs gsettings; stdout (trimmed) on exit 0, else null.</summary>
    public static string? RunGsettings(string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("gsettings")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(3000))
            {
                try { process.Kill(); } catch { /* already gone */ }
                return null;
            }

            if (process.ExitCode != 0)
            {
                FileLog.Write($"RoeSnip: gsettings {string.Join(" ", args.Take(3))} failed: {stderr.Result.Trim()}");
                return null;
            }

            return stdout.Result.Trim();
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: gsettings unavailable: {ex.Message}");
            return null;
        }
    }
}
