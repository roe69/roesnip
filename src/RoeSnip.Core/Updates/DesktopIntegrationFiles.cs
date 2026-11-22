using System;
using System.Security;
using System.Text;

namespace RoeSnip.Core.Updates;

/// <summary>Content of the Linux .desktop files (menu entry, XDG autostart) and the macOS
/// LaunchAgent plist the installer writes. Pure strings, unit-tested.</summary>
public static class DesktopIntegrationFiles
{
    /// <summary>One Exec argument per the Desktop Entry spec: always double-quoted, with
    /// '"', '`', '$' and '\' backslash-escaped inside the quotes, then the value-level '\' escape
    /// applied on top, and '%' doubled (field codes).</summary>
    public static string QuoteExecArg(string arg)
    {
        var quoted = new StringBuilder("\"");
        foreach (char c in arg)
        {
            if (c is '"' or '`' or '$' or '\\')
            {
                quoted.Append('\\');
            }
            quoted.Append(c);
        }
        quoted.Append('"');
        return quoted.ToString().Replace("\\", "\\\\").Replace("%", "%%");
    }

    /// <summary>~/.local/share/applications/net.roelite.roesnip.desktop: launches the AppImage; the "Capture"
    /// action runs the `capture` verb (signals the resident, or starts one and captures).</summary>
    public static string LinuxMenuEntry(string appImagePath, string? version)
    {
        string exec = QuoteExecArg(appImagePath);
        var sb = new StringBuilder();
        sb.Append("[Desktop Entry]\n");
        sb.Append("Type=Application\n");
        sb.Append("Name=RoeSnip\n");
        sb.Append("GenericName=Screenshot tool\n");
        sb.Append("Comment=Screenshots and screen recordings\n");
        sb.Append("Exec=").Append(exec).Append('\n');
        sb.Append("Icon=").Append(UpdateInstallLayout.LinuxIconName).Append('\n');
        sb.Append("Terminal=false\n");
        sb.Append("Categories=Graphics;Utility;\n");
        sb.Append("StartupNotify=false\n");
        if (!string.IsNullOrEmpty(version))
        {
            sb.Append("X-AppImage-Version=").Append(version).Append('\n');
        }
        sb.Append("Actions=Capture;\n");
        sb.Append('\n');
        sb.Append("[Desktop Action Capture]\n");
        sb.Append("Name=Capture\n");
        sb.Append("Exec=").Append(exec).Append(" capture\n");
        return sb.ToString();
    }

    /// <summary>The app id the portals know RoeSnip by; the menu entry's file name without ".desktop".</summary>
    public const string LinuxAppId = "net.roelite.roesnip";

    /// <summary>True when the entry's main or action Exec line launches exactly <paramref name="path"/>.</summary>
    public static bool EntryLaunches(string entry, string path)
    {
        string exec = "Exec=" + QuoteExecArg(path);
        foreach (string raw in entry.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line == exec || line.StartsWith(exec + " ", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>What the portal-registration menu entry should launch, or null to leave the file as
    /// it is. An entry for the installed AppImage is never replaced; with an install on disk the
    /// entry points at it; otherwise at <paramref name="launcher"/> ($APPIMAGE, else the process
    /// path), rewritten when that path changes.</summary>
    public static string? RegistrationEntryExec(string? existingEntry, string installedAppImage, bool installExists, string? launcher)
    {
        if (existingEntry is not null && EntryLaunches(existingEntry, installedAppImage))
        {
            return null;
        }
        if (installExists)
        {
            return installedAppImage;
        }
        if (string.IsNullOrEmpty(launcher))
        {
            return null;
        }
        return existingEntry is not null && EntryLaunches(existingEntry, launcher) ? null : launcher;
    }

    /// <summary>The systemd unit (last path segment ending in .scope or .service) of the unified
    /// "0::" line of /proc/self/cgroup, else of the first line that has one; null if none.</summary>
    public static string? SystemdUnitOf(string procSelfCgroup)
    {
        string? fallback = null;
        foreach (string raw in procSelfCgroup.Split('\n'))
        {
            string line = raw.Trim();
            int colon = line.IndexOf(':');
            int second = colon < 0 ? -1 : line.IndexOf(':', colon + 1);
            if (second < 0)
            {
                continue;
            }

            string unit = UnitOfPath(line[(second + 1)..]) ?? "";
            if (unit.Length == 0)
            {
                continue;
            }
            if (line.StartsWith("0::", StringComparison.Ordinal))
            {
                return unit;
            }
            fallback ??= unit;
        }
        return fallback;
    }

    private static string? UnitOfPath(string path)
    {
        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = parts.Length - 1; i >= 0; i--)
        {
            if (parts[i].EndsWith(".scope", StringComparison.Ordinal) || parts[i].EndsWith(".service", StringComparison.Ordinal))
            {
                return parts[i];
            }
        }
        return null;
    }

    /// <summary>The app id in an XDG app unit name, as xdg-desktop-portal reads it for host apps:
    /// app[-launcher]-ID-RANDOM.scope or app[-launcher]-ID[@RANDOM].service, "\x2d" unescaped.
    /// Null for anything else (session-2.scope, a plain service).</summary>
    public static string? AppIdOfUnit(string? unit)
    {
        if (string.IsNullOrEmpty(unit) || !unit.StartsWith("app-", StringComparison.Ordinal))
        {
            return null;
        }

        string body;
        if (unit.EndsWith(".scope", StringComparison.Ordinal))
        {
            body = unit[4..^".scope".Length];
            int dash = body.LastIndexOf('-');
            if (dash <= 0)
            {
                return null;
            }
            body = body[..dash];
        }
        else if (unit.EndsWith(".service", StringComparison.Ordinal))
        {
            body = unit[4..^".service".Length];
            int at = body.IndexOf('@');
            if (at >= 0)
            {
                body = body[..at];
            }
        }
        else
        {
            return null;
        }

        int launcherDash = body.LastIndexOf('-');
        if (launcherDash >= 0)
        {
            body = body[(launcherDash + 1)..];
        }
        body = body.Replace("\\x2d", "-");
        return body.Length == 0 ? null : body;
    }

    /// <summary>The transient scope RoeSnip moves itself into when the portal cannot be told its app id.</summary>
    public static string OwnScopeName(int pid) => $"app-{LinuxAppId}-{pid}.scope";

    /// <summary>~/.config/autostart/net.roelite.roesnip.desktop: starts the tray resident at login.</summary>
    public static string LinuxAutostartEntry(string launchPath)
    {
        var sb = new StringBuilder();
        sb.Append("[Desktop Entry]\n");
        sb.Append("Type=Application\n");
        sb.Append("Name=RoeSnip\n");
        sb.Append("Comment=Start RoeSnip at login\n");
        sb.Append("Exec=").Append(QuoteExecArg(launchPath)).Append('\n');
        sb.Append("Icon=").Append(UpdateInstallLayout.LinuxIconName).Append('\n');
        sb.Append("Terminal=false\n");
        sb.Append("X-GNOME-Autostart-enabled=true\n");
        return sb.ToString();
    }

    /// <summary>An autostart entry counts as enabled unless the desktop disabled it in place
    /// (Hidden=true, or GNOME's X-GNOME-Autostart-enabled=false).</summary>
    public static bool AutostartEntryEnabled(string content)
    {
        foreach (string raw in content.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Equals("Hidden=true", StringComparison.OrdinalIgnoreCase) ||
                line.Equals("X-GNOME-Autostart-enabled=false", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>~/Library/LaunchAgents/net.roelite.roesnip.plist: launchd runs the bundle
    /// executable once at login (RunAtLoad), never restarts it (no KeepAlive).</summary>
    public static string MacLaunchAgent(string bundleExecutablePath)
    {
        string label = SecurityElement.Escape(UpdateInstallLayout.MacLaunchAgentLabel);
        string program = SecurityElement.Escape(bundleExecutablePath);
        return
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
            "<plist version=\"1.0\">\n" +
            "<dict>\n" +
            "  <key>Label</key><string>" + label + "</string>\n" +
            "  <key>ProgramArguments</key>\n" +
            "  <array>\n" +
            "    <string>" + program + "</string>\n" +
            "  </array>\n" +
            "  <key>RunAtLoad</key><true/>\n" +
            "  <key>ProcessType</key><string>Interactive</string>\n" +
            "</dict>\n" +
            "</plist>\n";
    }
}
