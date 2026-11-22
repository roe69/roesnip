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

    /// <summary>Takes the lock without waiting; null only when another holder has it. Any other
    /// I/O failure (unwritable folder, full disk) is rethrown with its own message, so it never
    /// reads as "another process is updating". A lock file this user cannot write is locked through a
    /// read-only handle instead.</summary>
    public static UpdateLock? TryAcquire(string lockPath)
    {
        string? dir = System.IO.Path.GetDirectoryName(lockPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        try
        {
            return Open(lockPath);
        }
        catch (UnauthorizedAccessException) when (File.Exists(lockPath))
        {
            // A lock file this user cannot write (root-owned, left by a sudo run): lock it through a
            // read-only handle. FileShare.None still takes flock(LOCK_EX) on a read-only fd (checked
            // on .NET 8 Linux), so a live root holder still reads as busy. Never delete it: two
            // processes recreating it would lock two different inodes.
            return Open(lockPath, FileAccess.Read);
        }
    }

    private static UpdateLock? Open(string lockPath, FileAccess access = FileAccess.ReadWrite)
    {
        try
        {
            var stream = new FileStream(lockPath, access == FileAccess.Read ? FileMode.Open : FileMode.OpenOrCreate,
                access, FileShare.None, 1, FileOptions.None);
            return new UpdateLock(stream, lockPath);
        }
        catch (IOException ex) when (IsHeldElsewhere(ex, OperatingSystem.IsWindows(), OperatingSystem.IsMacOS()))
        {
            return null;
        }
    }

    private const int WindowsSharingViolation = unchecked((int)0x80070020);
    private const int WindowsLockViolation = unchecked((int)0x80070021);
    private const int LinuxEagain = 11;
    private const int MacEagain = 35;

    /// <summary>True when <paramref name="ex"/> from the FileShare.None open means another handle
    /// holds the lock: a Windows sharing / lock violation, or on Unix the EWOULDBLOCK (= EAGAIN) of
    /// the non-blocking flock, which .NET 8 throws as a plain IOException whose HResult is the raw
    /// errno (11 on Linux, checked on .NET 8.0.428; 35 on macOS).</summary>
    public static bool IsHeldElsewhere(Exception ex, bool isWindows, bool isMacOS)
    {
        if (ex.GetType() != typeof(IOException))
        {
            return false; // FileNotFound, DirectoryNotFound, PathTooLong, ... are real failures
        }

        if (isWindows)
        {
            return ex.HResult == WindowsSharingViolation || ex.HResult == WindowsLockViolation;
        }

        return ex.HResult == (isMacOS ? MacEagain : LinuxEagain);
    }

    /// <summary>Polls <see cref="TryAcquire"/> until <paramref name="timeout"/>. Contended is true
    /// when the first try failed, i.e. another updater held the install meanwhile. Throws what
    /// TryAcquire throws.</summary>
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
