namespace RoeSnip.Core.Updates;

/// <summary>Per-process staging names for an update download ("RoeSnip.AppImage.new-1234"), so two
/// updaters can never write or swap the same file even if the update lock fails, and the age-gated
/// cleanup that never deletes another process's in-flight download.</summary>
public static class UpdateStaging
{
    /// <summary>Downloads older than this are abandoned (a live one finishes in minutes).</summary>
    public static readonly TimeSpan StaleAge = TimeSpan.FromHours(1);

    /// <summary><paramref name="stem"/> + "-" + <paramref name="processId"/>.</summary>
    public static string ForProcess(string stem, int processId) => $"{stem}-{processId}";

    /// <summary>True for the legacy shared names (the stem itself, or stem + ".gz" /
    /// ".app.tar.gz") and every per-process name (stem + "-" + anything, e.g.
    /// "RoeSnip.exe.new-12.gz"). Never matches the stem's siblings such as ".old" or ".restore".</summary>
    public static bool IsStagingName(string fileName, string stemFileName, StringComparison comparison) =>
        string.Equals(fileName, stemFileName, comparison) ||
        (fileName.Length > stemFileName.Length + 1 &&
         (fileName.StartsWith(stemFileName + "-", comparison) || fileName.StartsWith(stemFileName + ".", comparison)));

    /// <summary>Staging leftovers in <paramref name="directory"/> for <paramref name="stem"/>
    /// (files or directories) last written before <paramref name="nowUtc"/> - <paramref name="maxAge"/>.</summary>
    public static List<string> FindStale(string directory, string stem, DateTime nowUtc, TimeSpan maxAge, StringComparison comparison)
    {
        var stale = new List<string>();
        if (!Directory.Exists(directory))
        {
            return stale;
        }

        string stemName = Path.GetFileName(stem);
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if (!IsStagingName(Path.GetFileName(entry), stemName, comparison))
            {
                continue;
            }

            DateTime written = Directory.Exists(entry) ? Directory.GetLastWriteTimeUtc(entry) : File.GetLastWriteTimeUtc(entry);
            if (nowUtc - written > maxAge)
            {
                stale.Add(entry);
            }
        }

        return stale;
    }
}
