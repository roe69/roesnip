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
