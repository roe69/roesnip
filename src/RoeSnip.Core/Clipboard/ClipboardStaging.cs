using RoeSnip.Core.Diagnostics;

namespace RoeSnip.Core.Clipboard;

/// <summary>Where a recording goes when it is copied to the clipboard.
///
/// The clipboard carries a PATH, not the bytes (see <see cref="DropFilesPayload"/>), so the file has
/// to outlive the recording session that produced it - handing over a temp path the session is about
/// to delete would leave a clipboard entry that pastes nothing. Staged files therefore live in their
/// own directory that no teardown touches, and each new copy prunes entries older than
/// <see cref="MaxAge"/> so it cannot grow without bound. A paste after that window has passed is the
/// accepted trade-off; the alternative is keeping every copied recording forever.</summary>
public static class ClipboardStaging
{
    /// <summary>Long enough that a paste minutes or hours later still works, short enough that the
    /// temp directory does not accumulate recordings indefinitely.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);

    public static string DirectoryPath => Path.Combine(Path.GetTempPath(), "RoeSnip", "clipboard");

    /// <summary>Moves a finished take out of its session temp path into the staging directory and
    /// returns the new path. Pruning is best-effort and never blocks a copy.</summary>
    public static string Stage(string tempPath, DateTime timestampLocal)
    {
        string dir = DirectoryPath;
        Directory.CreateDirectory(dir);
        Prune(dir);

        string staged = Path.Combine(dir, $"roesnip_{timestampLocal:yyyyMMdd_HHmmss}{Path.GetExtension(tempPath)}");
        File.Move(tempPath, staged, overwrite: true);
        return staged;
    }

    private static void Prune(string dir)
    {
        try
        {
            DateTime cutoff = DateTime.UtcNow - MaxAge;
            foreach (string old in Directory.EnumerateFiles(dir))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(old) < cutoff) File.Delete(old);
                }
                catch { /* best-effort: a file still held by a paste target is simply left alone */ }
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: pruning the clipboard staging directory failed (non-fatal): {ex.Message}");
        }
    }
}
