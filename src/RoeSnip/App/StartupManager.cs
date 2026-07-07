using System;
using Microsoft.Win32;

namespace RoeSnip.App;

/// <summary>Run-at-startup toggle via the per-user HKCU Run key (DESIGN.md §6) — no admin rights
/// needed, no scheduled task, no installer hook. Not covered by an automated test: it mutates the
/// real registry, and the WP-C brief explicitly says this class should be code-reviewed rather
/// than exercised by unit tests (a fake/temp registry key abstraction would test nothing useful —
/// the only thing worth verifying is "does it write the right value under the right key," which
/// is straightforward to eyeball here).</summary>
public static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "RoeSnip";

    public static void SetRunAtStartup(bool enabled)
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

    public static bool IsRunAtStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is not null;
    }
}
