namespace RoeSnip.Core.Settings;

/// <summary>Per-OS config directory (DESIGN-XPLAT.md "portable config dirs"). Shared by
/// SettingsStore (settings.json) and CaptureCache (capture-cache.json, PLAN-XPLAT.md §2.4) so both
/// files live side by side per OS, matching the WPF app's %APPDATA%\RoeSnip convention on Windows.</summary>
public static class ConfigPaths
{
    public const string AppName = "RoeSnip";       // Windows/macOS directory name
    public const string AppNameLower = "roesnip";  // Linux directory name (XDG convention)

    public static string ConfigDirectory
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                return System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);
            }
            if (OperatingSystem.IsMacOS())
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return System.IO.Path.Combine(home, "Library", "Application Support", AppName);
            }
            // Linux and any other POSIX host.
            string? xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            string baseDir = !string.IsNullOrEmpty(xdg)
                ? xdg
                : System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return System.IO.Path.Combine(baseDir, AppNameLower);
        }
    }
}
