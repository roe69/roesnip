using System;
using System.IO;

namespace RoeSnip.Core.Updates;

/// <summary>Where the cross-platform app installs itself on Linux and macOS, and how a running
/// process finds the unit (AppImage file / .app bundle) it was started from. Pure path math with
/// the environment and home directory passed in, so tests never touch the real user dirs.</summary>
public static class UpdateInstallLayout
{
    public const string LinuxAppImageFileName = "RoeSnip.AppImage";
    public const string LinuxDesktopFileName = "net.roelite.roesnip.desktop";
    public const string LegacyLinuxDesktopFileName = "roesnip.desktop";
    public const string LinuxIconName = "roesnip";
    public const string MacBundleName = "RoeSnip.app";
    public const string MacExecutableName = "RoeSnip";
    public const string MacLaunchAgentLabel = "net.roelite.roesnip";

    /// <summary>XDG base dir: an absolute $<paramref name="variable"/> wins, else ~/<paramref name="fallback"/>
    /// (the spec says a relative value is invalid and must be ignored).</summary>
    public static string XdgDir(Func<string, string?> getEnv, string home, string variable, string fallback)
    {
        string? value = getEnv(variable);
        return !string.IsNullOrEmpty(value) && Path.IsPathRooted(value) ? value : Path.Combine(home, fallback);
    }

    public static string LinuxDataHome(Func<string, string?> getEnv, string home) =>
        XdgDir(getEnv, home, "XDG_DATA_HOME", Path.Combine(".local", "share"));

    public static string LinuxConfigHome(Func<string, string?> getEnv, string home) =>
        XdgDir(getEnv, home, "XDG_CONFIG_HOME", ".config");

    /// <summary>~/.local/share/RoeSnip</summary>
    public static string LinuxInstallDir(Func<string, string?> getEnv, string home) =>
        Path.Combine(LinuxDataHome(getEnv, home), "RoeSnip");

    /// <summary>~/.local/share/RoeSnip/RoeSnip.AppImage</summary>
    public static string LinuxAppImagePath(Func<string, string?> getEnv, string home) =>
        Path.Combine(LinuxInstallDir(getEnv, home), LinuxAppImageFileName);

    /// <summary>~/.local/share/applications/net.roelite.roesnip.desktop (named after the portal app id).</summary>
    public static string LinuxMenuEntryPath(Func<string, string?> getEnv, string home) =>
        Path.Combine(LinuxDataHome(getEnv, home), "applications", LinuxDesktopFileName);

    /// <summary>~/.local/share/icons/hicolor/256x256/apps/roesnip.png</summary>
    public static string LinuxIconPath(Func<string, string?> getEnv, string home) =>
        Path.Combine(LinuxDataHome(getEnv, home), "icons", "hicolor", "256x256", "apps", LinuxIconName + ".png");

    /// <summary>~/.config/autostart/net.roelite.roesnip.desktop</summary>
    public static string LinuxAutostartPath(Func<string, string?> getEnv, string home) =>
        Path.Combine(LinuxConfigHome(getEnv, home), "autostart", LinuxDesktopFileName);

    /// <summary>The AppImage this process runs from ($APPIMAGE, set by the AppImage runtime), or
    /// null for a tar.gz / dev publish folder run.</summary>
    public static string? RunningAppImage(Func<string, string?> getEnv)
    {
        string? value = getEnv("APPIMAGE");
        return !string.IsNullOrEmpty(value) && Path.IsPathRooted(value) ? value : null;
    }

    /// <summary>The APPIMAGE value in a NUL-separated /proc/&lt;pid&gt;/environ block, or null. Lets
    /// replace-on-run match another AppImage instance by the file it runs from (its own exe path is
    /// a per-run /tmp/.mount_* path).</summary>
    public static string? AppImageFromEnviron(string environ)
    {
        foreach (string entry in environ.Split('\0'))
        {
            if (entry.StartsWith("APPIMAGE=", StringComparison.Ordinal))
            {
                string value = entry["APPIMAGE=".Length..];
                return Path.IsPathRooted(value) ? value : null;
            }
        }
        return null;
    }

    /// <summary>~/Applications/RoeSnip.app</summary>
    public static string MacBundlePath(string home) => Path.Combine(home, "Applications", MacBundleName);

    /// <summary>/Applications/RoeSnip.app (where the .dmg's Applications link puts it).</summary>
    public static string MacSystemBundlePath => Path.Combine("/Applications", MacBundleName);

    /// <summary>The bundle macOS self-updates and starts at login: <paramref name="runningBundle"/>
    /// when it sits directly in /Applications or ~/Applications and
    /// <paramref name="isWritableDir"/> says the user can rename entries in that folder, else
    /// ~/Applications/RoeSnip.app (what Install copies a .dmg / Downloads run to).</summary>
    public static string MacInstallTarget(string? runningBundle, string home, Func<string, bool> isWritableDir)
    {
        if (!string.IsNullOrEmpty(runningBundle))
        {
            string bundle = RealPath.ResolveOrFull(runningBundle).TrimEnd(Path.DirectorySeparatorChar);
            string? parent = Path.GetDirectoryName(bundle);
            if (parent is not null &&
                // APFS is case-insensitive by default.
                (RealPath.SameFile(parent, "/Applications", StringComparison.OrdinalIgnoreCase) ||
                 RealPath.SameFile(parent, Path.Combine(home, "Applications"), StringComparison.OrdinalIgnoreCase)) &&
                isWritableDir(parent))
            {
                return bundle;
            }
        }

        return MacBundlePath(home);
    }

    /// <summary>The bundle's executable: &lt;bundle&gt;/Contents/MacOS/RoeSnip.</summary>
    public static string MacBundleExecutable(string bundlePath) =>
        Path.Combine(bundlePath, "Contents", "MacOS", MacExecutableName);

    /// <summary>~/Library/LaunchAgents/net.roelite.roesnip.plist</summary>
    public static string MacLaunchAgentPath(string home) =>
        Path.Combine(home, "Library", "LaunchAgents", MacLaunchAgentLabel + ".plist");

    /// <summary>The .app bundle containing <paramref name="processPath"/> when it is
    /// &lt;x&gt;.app/Contents/MacOS/&lt;exe&gt;, else null (a dev publish folder run).</summary>
    public static string? BundleOf(string? processPath)
    {
        if (string.IsNullOrEmpty(processPath))
        {
            return null;
        }

        string? macOsDir = Path.GetDirectoryName(processPath);
        string? contents = macOsDir is null ? null : Path.GetDirectoryName(macOsDir);
        string? bundle = contents is null ? null : Path.GetDirectoryName(contents);
        if (macOsDir is null || contents is null || bundle is null)
        {
            return null;
        }

        return Path.GetFileName(macOsDir) == "MacOS" &&
               Path.GetFileName(contents) == "Contents" &&
               bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
            ? bundle
            : null;
    }

    /// <summary>True for a file a first-run install may delete after copying it into place: a
    /// "RoeSnip*.AppImage" download that is not the install target itself.</summary>
    public static bool IsDisposableLinuxSource(string sourcePath, string installedPath)
    {
        string name = Path.GetFileName(sourcePath);
        return name.StartsWith("RoeSnip", StringComparison.OrdinalIgnoreCase) &&
               name.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase) &&
               !RealPath.SameFile(sourcePath, installedPath, StringComparison.Ordinal);
    }
}
