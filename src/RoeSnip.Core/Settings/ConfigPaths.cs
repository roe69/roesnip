namespace RoeSnip.Core.Settings;

/// <summary>Per-OS config directory (DESIGN-XPLAT.md "portable config dirs"). Shared by
/// SettingsStore (settings.json) and CaptureCache (capture-cache.json, PLAN-XPLAT.md §2.4) so both
/// files live side by side per OS.
///
/// P5 audit fix: this directory is deliberately DISTINCT from the frozen WPF app's
/// %APPDATA%\RoeSnip this cycle, not a "RoeSnip" convergence with it. Both apps can be resident at
/// once (they are separate deliverables this cycle — DESIGN-XPLAT.md "Strategy"); sharing one
/// config directory meant they'd load the same settings.json, including the same
/// HotkeyVirtualKey/HotkeyModifiers — so a single PrintScreen press fired BOTH SharpHook's global
/// hook (this app) and RegisterHotKey (the WPF app), producing two overlay stacks plus concurrent
/// capture-cache.json rewrites from two processes. Splitting the directory removes the collision
/// entirely. Convergence to one shared config directory is a later cleanup, once the WPF app
/// retires (DESIGN-XPLAT.md "Convergence to a single app is a later cleanup").</summary>
public static class ConfigPaths
{
    public const string AppName = "RoeSnip.App";       // Windows/macOS directory name
    public const string AppNameLower = "roesnip-app";  // Linux directory name (XDG convention)

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
            // Linux and any other POSIX host. Per the XDG Base Directory spec, a RELATIVE
            // XDG_CONFIG_HOME must be treated as unset/invalid (C2 audit fix), not resolved
            // relative to some ambient current directory (which is meaningless for a config path
            // and would silently scatter config files depending on the process's launch cwd).
            string? xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            string baseDir = !string.IsNullOrEmpty(xdg) && System.IO.Path.IsPathRooted(xdg)
                ? xdg
                : System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return System.IO.Path.Combine(baseDir, AppNameLower);
        }
    }
}
