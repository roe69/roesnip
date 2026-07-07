using System;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace RoeSnip.App.AppShell;

/// <summary>Run-at-startup toggle. Windows: the per-user HKCU Run key, ported unchanged from
/// src/RoeSnip/App/StartupManager.cs (PLAN-XPLAT.md §3.2) — no admin rights needed, no scheduled
/// task, no installer hook; code-reviewed rather than unit-tested (it mutates the real registry).
/// macOS/Linux: a documented no-op that logs to stderr — DESIGN-XPLAT.md does not specify
/// run-at-startup behavior there (PLAN-XPLAT.md §6 flag 5: a real implementation needs a
/// LaunchAgents plist / XDG autostart .desktop file, out of scope this pass) — so the Settings
/// toggle is honest about having no effect rather than throwing or silently lying.
///
/// DELIBERATE deviation from the WPF app's value name ("RoeSnip"): both apps ship this cycle and
/// may be installed side by side; sharing the Run value name would make each app's toggle
/// overwrite/delete the other's autostart entry. This app registers as "RoeSnip.App" instead —
/// flagged in the WP-X2 report.</summary>
public static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "RoeSnip.App";

    public static void SetRunAtStartup(bool enabled)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("RoeSnip: run-at-startup is not yet implemented on this OS.");
            return;
        }

        SetRunAtStartupWindows(enabled);
    }

    public static bool IsRunAtStartupEnabled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        return IsRunAtStartupEnabledWindows();
    }

    [SupportedOSPlatform("windows")]
    private static void SetRunAtStartupWindows(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

        if (key is null)
        {
            throw new InvalidOperationException($"Could not open or create HKCU\\{RunKeyPath}.");
        }

        if (enabled)
        {
            string exePath = Environment.ProcessPath
                ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                ?? throw new InvalidOperationException("Could not determine the current executable path.");
            key.SetValue(ValueName, $"\"{exePath}\"", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsRunAtStartupEnabledWindows()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is not null;
    }
}
