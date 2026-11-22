using System;
using System.IO;
using System.Runtime.Versioning;
using Microsoft.Win32;
using RoeSnip.Core.Diagnostics;
using RoeSnip.Core.Updates;

namespace RoeSnip.App.AppShell;

/// <summary>Run-at-login toggle, per OS:
/// - Windows: the per-user HKCU Run value "RoeSnip.App" (ported from src/RoeSnip/App/StartupManager.cs;
///   distinct from the WPF app's "RoeSnip" so the two apps never overwrite each other's entry).
/// - Linux: an XDG autostart entry, $XDG_CONFIG_HOME/autostart/net.roelite.roesnip.desktop.
/// - macOS: a LaunchAgent, ~/Library/LaunchAgents/net.roelite.roesnip.plist (RunAtLoad; launchd
///   picks it up at the next login).
/// The entry points at <see cref="UpdateManager.CurrentLaunchPath"/> (the AppImage on Linux, never
/// its per-run mount path). Mutates the real registry / user dirs, so it is reviewed by eye; the
/// file contents come from DesktopIntegrationFiles (unit-tested).</summary>
public static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "RoeSnip.App";

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name);

    private static string LinuxAutostartPath => UpdateInstallLayout.LinuxAutostartPath(Env, Home);

    private static string MacLaunchAgentPath => UpdateInstallLayout.MacLaunchAgentPath(Home);

    public static void SetRunAtStartup(bool enabled)
    {
        if (OperatingSystem.IsWindows())
        {
            SetRunAtStartupWindows(enabled);
            return;
        }

        // Prefer the installed copy when it exists, so a portable run never repoints login
        // startup away from the install.
        string? path = UpdateManager.InstallExists ? UpdateManager.InstalledLaunchPath : UpdateManager.CurrentLaunchPath;
        SetRunAtStartupFor(enabled, path);
    }

    /// <summary>Linux / macOS: writes (or removes) the login entry for <paramref name="launchPath"/>.</summary>
    public static void SetRunAtStartupFor(bool enabled, string? launchPath)
    {
        if (OperatingSystem.IsWindows())
        {
            SetRunAtStartupWindows(enabled);
            return;
        }

        string entryPath;
        if (OperatingSystem.IsLinux())
        {
            entryPath = LinuxAutostartPath;
        }
        else if (OperatingSystem.IsMacOS())
        {
            entryPath = MacLaunchAgentPath;
        }
        else
        {
            FileLog.Write("RoeSnip: run-at-startup is not implemented on this OS.");
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            LinuxDesktopIntegration.DeleteLegacySibling(entryPath);
        }

        if (!enabled)
        {
            if (File.Exists(entryPath))
            {
                File.Delete(entryPath);
            }
            return;
        }

        if (string.IsNullOrEmpty(launchPath))
        {
            throw new InvalidOperationException("Could not determine the current executable path.");
        }

        string content = OperatingSystem.IsLinux()
            ? DesktopIntegrationFiles.LinuxAutostartEntry(launchPath)
            : DesktopIntegrationFiles.MacLaunchAgent(launchPath);
        Directory.CreateDirectory(Path.GetDirectoryName(entryPath)!);
        File.WriteAllText(entryPath, content);
    }

    /// <summary>macOS: an existing LaunchAgent is rewritten to start <paramref name="launchPath"/>
    /// when it names another bundle (the user moved to a /Applications copy, or back). Never
    /// creates one: start-at-login stays the user's choice.</summary>
    public static void RepointMacLaunchAgent(string launchPath)
    {
        string path = MacLaunchAgentPath;
        if (!OperatingSystem.IsMacOS() || !File.Exists(path))
        {
            return;
        }

        string wanted = DesktopIntegrationFiles.MacLaunchAgent(launchPath);
        if (File.ReadAllText(path) != wanted)
        {
            File.WriteAllText(path, wanted);
            FileLog.Write($"RoeSnip: start-at-login now runs {launchPath}");
        }
    }

    public static bool IsRunAtStartupEnabled()
    {
        if (OperatingSystem.IsWindows())
        {
            return IsRunAtStartupEnabledWindows();
        }

        if (OperatingSystem.IsLinux())
        {
            string path = LinuxAutostartPath;
            return File.Exists(path) && DesktopIntegrationFiles.AutostartEntryEnabled(File.ReadAllText(path));
        }

        return OperatingSystem.IsMacOS() && File.Exists(MacLaunchAgentPath);
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
