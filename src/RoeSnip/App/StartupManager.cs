using System;
using Microsoft.Win32;

namespace RoeSnip.App;

/// <summary>Run-at-startup toggle via the per-user HKCU Run key (DESIGN.md §6) — no admin rights
/// needed, no scheduled task, no installer hook. Not covered by an automated test: it mutates the
/// real registry, and the WP-C brief explicitly says this class should be code-reviewed rather
/// than exercised by unit tests (a fake/temp registry key abstraction would test nothing useful —
/// the only thing worth verifying is "does it write the right value under the right key," which
/// is straightforward to eyeball here).
///
/// Interplay with ElevationManager's scheduled task: the two startup mechanisms are mutually
/// exclusive, not additive. When the "RoeSnip" elevated task is installed, its own onlogon trigger
/// is what starts RoeSnip at login, so this Run key is kept cleared (see
/// ElevationManager.RunEnableElevatedStartupCli / SettingsWindow's elevated-toggle handler and
/// SaveButton_Click, which all call SetRunAtStartup(false) whenever the task is present rather than
/// writing the checkbox's raw value). When the task is removed, the Run key is restored to match
/// RunAtStartup again (ElevationManager.RunDisableElevatedStartupCli).</summary>
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
