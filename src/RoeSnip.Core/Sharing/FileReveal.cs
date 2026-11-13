using System.Diagnostics;
using RoeSnip.Core.Diagnostics;

namespace RoeSnip.Core.Sharing;

/// <summary>"Show in folder" for a kept take: selects the file in Explorer / Finder, or opens its
/// directory on Linux. Never throws.</summary>
public static class FileReveal
{
    public static void Show(string filePath)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = true });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo("open", $"-R \"{filePath}\""));
            }
            else if (Path.GetDirectoryName(filePath) is { } dir)
            {
                Process.Start(new ProcessStartInfo("xdg-open", $"\"{dir}\""));
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: failed to open folder for {filePath}: {ex.Message}");
        }
    }
}
