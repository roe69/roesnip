namespace RoeSnip.Core.Updates;

/// <summary>One update at a time per install, across processes: an exclusive handle on a lock file
/// next to the install target. FileShare.None is a sharing lock on Windows and a flock(LOCK_EX) on
/// Unix (.NET's FileShare emulation), so a second open fails even inside the same process. The
/// kernel drops it when the holder dies; the file itself is never deleted (deleting a lock file
/// lets two holders lock two different inodes).</summary>
public sealed class UpdateLock : IDisposable
{
    private readonly FileStream _stream;

    private UpdateLock(FileStream stream, string path)
    {
        _stream = stream;
        Path = path;
    }

    public string Path { get; }

    /// <summary>The lock file for an install target ("RoeSnip.AppImage.update.lock" next to it).</summary>
    public static string PathFor(string installTargetPath) =>
        installTargetPath.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar) + ".update.lock";

    /// <summary>Takes the lock without waiting; null when another holder has it (or the folder
    /// cannot be written). Never throws on I/O.</summary>
    public static UpdateLock? TryAcquire(string lockPath)
    {
        try
        {
            string? dir = System.IO.Path.GetDirectoryName(lockPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            return new UpdateLock(stream, lockPath);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Polls <see cref="TryAcquire"/> until <paramref name="timeout"/>. Contended is true
    /// when the first try failed, i.e. another updater held the install meanwhile.</summary>
    public static async Task<(UpdateLock? Lock, bool Contended)> AcquireAsync(
        string lockPath, TimeSpan timeout, TimeSpan pollInterval, CancellationToken cancellationToken = default)
    {
        UpdateLock? first = TryAcquire(lockPath);
        if (first is not null)
        {
            return (first, false);
        }

        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            TimeSpan left = deadline - DateTime.UtcNow;
            await Task.Delay(left < pollInterval ? left : pollInterval, cancellationToken).ConfigureAwait(false);
            UpdateLock? acquired = TryAcquire(lockPath);
            if (acquired is not null)
            {
                return (acquired, true);
            }
        }

        return (null, true);
    }

    public void Dispose() => _stream.Dispose();
}
