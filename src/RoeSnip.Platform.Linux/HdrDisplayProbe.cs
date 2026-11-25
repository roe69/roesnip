using System.Collections.Concurrent;
using System.Diagnostics;
using RoeSnip.Core.Color;

namespace RoeSnip.Platform.Linux;

/// <summary>Cheap "is any monitor in HDR mode" check and the monitors' EDID peak luminance, so the
/// EGL context and the HDR PipeWire reader are only created on a machine that can use them.</summary>
internal static class HdrDisplayProbe
{
    private const double FallbackPeakNits = 1000.0;
    private static readonly ConcurrentDictionary<string, double> s_peaks = new();

    /// <summary>False only when mutter says no monitor uses color-mode bt2100 (1). When it cannot be
    /// asked (not GNOME, no gdbus) the answer is true: the stream probe then decides.</summary>
    public static bool ShouldTry(out string note)
    {
        note = "";
        if (Environment.GetEnvironmentVariable("ROESNIP_NO_HDR_CAPTURE") == "1")
        {
            note = "disabled by ROESNIP_NO_HDR_CAPTURE";
            return false;
        }
        string? state = GnomeDisplayState();
        if (state is null)
        {
            note = "display color mode unknown, probing the streams";
            return true;
        }
        if (state.Contains("'color-mode': <uint32 1>"))
        {
            note = "a monitor is in HDR mode (color-mode bt2100)";
            return true;
        }
        note = "no monitor is in HDR mode (color-mode)";
        return false;
    }

    private static string? GnomeDisplayState()
    {
        try
        {
            var psi = new ProcessStartInfo("gdbus")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in new[]
            {
                "call", "--session", "--dest", "org.gnome.Mutter.DisplayConfig",
                "--object-path", "/org/gnome/Mutter/DisplayConfig",
                "--method", "org.gnome.Mutter.DisplayConfig.GetCurrentState",
            })
            {
                psi.ArgumentList.Add(a);
            }
            using var p = Process.Start(psi);
            if (p is null) return null;
            var text = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(3000))
            {
                try { p.Kill(true); } catch { /* gone */ }
                return null;
            }
            return p.ExitCode == 0 ? text.GetAwaiter().GetResult() : null;
        }
        catch
        {
            return null; // gdbus not installed
        }
    }

    /// <summary>The DRM device files (card and render node) of every GPU with a connected monitor.</summary>
    public static IReadOnlyList<string> ActiveDrmFiles()
    {
        var files = new List<string>();
        try
        {
            foreach (var connector in Directory.EnumerateDirectories("/sys/class/drm", "card*-*"))
            {
                string status = Path.Combine(connector, "status");
                if (!File.Exists(status) || File.ReadAllText(status).Trim() != "connected") continue;
                string card = Path.GetFileName(connector);
                card = card[..card.IndexOf('-')];
                files.Add("/dev/dri/" + card);
                string drm = $"/sys/class/drm/{card}/device/drm";
                if (!Directory.Exists(drm)) continue;
                foreach (var node in Directory.EnumerateDirectories(drm, "renderD*"))
                {
                    files.Add("/dev/dri/" + Path.GetFileName(node));
                }
            }
        }
        catch
        {
            // no sysfs: the first working EGL display is used
        }
        return files;
    }

    /// <summary>Peak luminance of the named connector (RandR name, e.g. DP-3) from its EDID's HDR
    /// static metadata; 1000 nits when the EDID is unreadable or has none.</summary>
    public static double PeakNits(string connector)
        => s_peaks.GetOrAdd(connector, ReadPeak);

    private static double ReadPeak(string connector)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories("/sys/class/drm", "card*-" + connector))
            {
                string edid = Path.Combine(dir, "edid");
                if (!File.Exists(edid)) continue;
                if (EdidHdr.TryReadMaxLuminance(File.ReadAllBytes(edid), out double nits)) return nits;
            }
        }
        catch
        {
            // unreadable sysfs: fall through
        }
        return FallbackPeakNits;
    }
}
