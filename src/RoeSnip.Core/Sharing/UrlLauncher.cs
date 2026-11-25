using System.Diagnostics;
using RoeSnip.Core.Diagnostics;
using RoeSnip.Core.Updates;

namespace RoeSnip.Core.Sharing;

/// <summary>Opens a web page in the default browser and logs the outcome. Linux runs xdg-open then
/// gio open with the AppImage's environment stripped and no inherited descriptors; macOS runs
/// open; Windows uses the shell. Never throws, never blocks the caller.</summary>
public static class UrlLauncher
{
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(5);

    /// <summary>Only http(s) pages are ever handed to the OS opener.</summary>
    public static bool IsOpenable(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    /// <summary>The Linux openers tried in order: program and its leading arguments.</summary>
    public static IReadOnlyList<string[]> LinuxOpeners { get; } = new[]
    {
        new[] { "xdg-open" },
        new[] { "gio", "open" },
    };

    /// <summary>Starts the open on a worker thread and returns at once.</summary>
    public static void Open(string url, string source)
    {
        _ = Task.Run(() => OpenNow(url, source));
    }

    /// <summary>Synchronous form; returns true when an opener started and did not fail.</summary>
    public static bool OpenNow(string url, string source)
    {
        if (!IsOpenable(url))
        {
            FileLog.Write($"RoeSnip: open url ({source}) refused, not an http(s) address: {url}");
            return false;
        }
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var p = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                FileLog.Write($"RoeSnip: open url ({source}) {url}: started via the shell");
                return true;
            }
            if (OperatingSystem.IsMacOS())
            {
                return RunOpener(new[] { "/usr/bin/open" }, url, source, sanitise: false);
            }
            InheritedDescriptors.MarkCloseOnExec();
            foreach (var opener in LinuxOpeners)
            {
                if (RunOpener(opener, url, source, sanitise: true))
                {
                    return true;
                }
            }
            FileLog.Write($"RoeSnip: open url ({source}) {url}: no opener worked");
            return false;
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: open url ({source}) {url}: failed ({ex.GetType().Name}: {ex.Message})");
            return false;
        }
    }

    private static bool RunOpener(string[] opener, string url, string source, bool sanitise)
    {
        string name = string.Join(' ', opener);
        try
        {
            // sh redirects the opener's output to /dev/null: a pipe we close later would kill a
            // browser that writes to it. exec keeps the opener's exit code ours.
            var psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("exec \"$@\" >/dev/null 2>&1");
            psi.ArgumentList.Add("roesnip-open");
            foreach (string part in opener) psi.ArgumentList.Add(part);
            psi.ArgumentList.Add(url);
            if (sanitise)
            {
                ApplyEnvironment(psi);
            }
            using var process = Process.Start(psi);
            if (process is null)
            {
                FileLog.Write($"RoeSnip: open url ({source}) {url}: {name} did not start");
                return false;
            }
            if (!process.WaitForExit(ExitWait))
            {
                FileLog.Write($"RoeSnip: open url ({source}) {url}: {name} still running after {ExitWait.TotalSeconds:0}s (taken as opened)");
                return true;
            }
            if (process.ExitCode == 0)
            {
                FileLog.Write($"RoeSnip: open url ({source}) {url}: {name} exit 0");
                return true;
            }
            FileLog.Write($"RoeSnip: open url ({source}) {url}: {name} exit {process.ExitCode}");
            return false;
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: open url ({source}) {url}: {name} failed ({ex.GetType().Name}: {ex.Message})");
            return false;
        }
    }

    private static void ApplyEnvironment(ProcessStartInfo psi)
    {
        var env = new Dictionary<string, string?>();
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            env[(string)e.Key] = e.Value as string;
        }
        UrlLauncherEnvironment.Sanitize(env);
        psi.Environment.Clear();
        foreach (var (key, value) in env)
        {
            if (value is not null) psi.Environment[key] = value;
        }
    }
}

/// <summary>Strips what an AppImage launch leaks into the environment so the desktop's opener and
/// the browser it starts run as if started from the desktop.</summary>
public static class UrlLauncherEnvironment
{
    private static readonly string[] Dropped =
    {
        "APPDIR", "APPIMAGE", "ARGV0", "OWD", "LD_PRELOAD", "PYTHONHOME", "PYTHONPATH", "PERLLIB",
        "GSETTINGS_SCHEMA_DIR", "GIO_EXTRA_MODULES", "QT_PLUGIN_PATH", "GTK_PATH", "GTK_EXE_PREFIX",
        "GTK_DATA_PREFIX", "GDK_PIXBUF_MODULE_FILE", "DOTNET_BUNDLE_EXTRACT_BASE_DIR",
    };

    private static readonly string[] PathLists =
    {
        "PATH", "LD_LIBRARY_PATH", "XDG_DATA_DIRS", "XDG_CONFIG_DIRS", "GI_TYPELIB_PATH",
    };

    /// <summary>Edits <paramref name="env"/> in place: drops the AppImage markers and removes every
    /// path-list entry that lies under the mount directory (APPDIR, or any /tmp/.mount_ entry).
    /// A list left empty is removed.</summary>
    public static void Sanitize(IDictionary<string, string?> env)
    {
        env.TryGetValue("APPDIR", out string? appDir);
        foreach (string key in PathLists)
        {
            if (!env.TryGetValue(key, out string? value) || value is null) continue;
            var kept = value.Split(':').Where(p => p.Length > 0 && !IsUnderMount(p, appDir)).ToArray();
            if (kept.Length == 0) env.Remove(key);
            else env[key] = string.Join(':', kept);
        }
        foreach (string key in Dropped) env.Remove(key);
        foreach (string key in env.Keys.Where(k => k.StartsWith("GST_", StringComparison.Ordinal)).ToList())
        {
            env.Remove(key);
        }
    }

    private static bool IsUnderMount(string path, string? appDir) =>
        (!string.IsNullOrEmpty(appDir) && (path == appDir || path.StartsWith(appDir.TrimEnd('/') + "/", StringComparison.Ordinal)))
        || path.Contains("/.mount_", StringComparison.Ordinal);
}
