using System;
using System.IO;
using RoeSnip.Core.Diagnostics;
using RoeSnip.Core.Updates;

namespace RoeSnip.App.AppShell;

/// <summary>Linux counterpart of <see cref="StartMenuShortcut"/>: the application-menu entry
/// ~/.local/share/applications/net.roelite.roesnip.desktop (with a "Capture" action) and its icon
/// ~/.local/share/icons/hicolor/256x256/apps/roesnip.png, copied from the running AppImage's
/// $APPDIR/roesnip.png (release.yml puts it there). Called from Install() and on every start of the
/// installed copy, so a deleted entry heals; rewrites only when the content changed. Never throws.</summary>
public static class LinuxDesktopIntegration
{
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name);

    public static void EnsureMenuEntry(string appImagePath, string? version)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        try
        {
            EnsureIcon();

            string entryPath = UpdateInstallLayout.LinuxMenuEntryPath(Env, Home);
            DeleteLegacySibling(entryPath);
            string content = DesktopIntegrationFiles.LinuxMenuEntry(appImagePath, version);
            if (File.Exists(entryPath) && File.ReadAllText(entryPath) == content)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(entryPath)!);
            File.WriteAllText(entryPath, content);
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: could not write the application menu entry (non-fatal): {ex.Message}");
        }
    }

    /// <summary>Removes a "roesnip.desktop" next to <paramref name="entryPath"/>: the entry is named
    /// after the portal app id (net.roelite.roesnip) so the GlobalShortcuts portal can match it.</summary>
    public static void DeleteLegacySibling(string entryPath)
    {
        try
        {
            string legacy = Path.Combine(Path.GetDirectoryName(entryPath)!, UpdateInstallLayout.LegacyLinuxDesktopFileName);
            if (File.Exists(legacy))
            {
                File.Delete(legacy);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FileLog.Write($"RoeSnip: could not remove the old roesnip.desktop (non-fatal): {ex.Message}");
        }
    }

    private static void EnsureIcon()
    {
        string? appDir = Env("APPDIR");
        if (string.IsNullOrEmpty(appDir))
        {
            return;
        }

        string source = Path.Combine(appDir, UpdateInstallLayout.LinuxIconName + ".png");
        if (!File.Exists(source))
        {
            return;
        }

        string target = UpdateInstallLayout.LinuxIconPath(Env, Home);
        if (File.Exists(target) && new FileInfo(target).Length == new FileInfo(source).Length)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target, overwrite: true);
    }
}
