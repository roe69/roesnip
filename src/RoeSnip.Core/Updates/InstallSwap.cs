using System;
using System.IO;

namespace RoeSnip.Core.Updates;

/// <summary>Swap of an install unit (a file - the exe / AppImage - or a directory - the macOS .app
/// bundle) with a ".old" rollback copy. Callers stage <c>staged</c> next to <c>target</c> (same
/// directory) so the renames never cross a volume. Off Windows a file swap is one rename(2) over
/// the target, so the path autostart / the menu entry run always exists; a bundle swap is two
/// renames and <see cref="RepairInterruptedSwap"/> fixes a crash between them at startup.</summary>
public static class InstallSwap
{
    public static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>Deletes a file or a directory tree; no-op when absent. Throws on failure.</summary>
    public static void Delete(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>Renames a file or directory (never overwrites).</summary>
    public static void Move(string from, string to)
    {
        if (Directory.Exists(from))
        {
            Directory.Move(from, to);
        }
        else
        {
            File.Move(from, to);
        }
    }

    /// <summary>Puts <paramref name="staged"/> at <paramref name="target"/>, keeping the previous
    /// unit at <paramref name="old"/> (a stale <paramref name="old"/> is deleted first).
    /// A file off Windows: target is copied to old, then staged renamed over target in one step.
    /// Windows and directories: target is renamed to old, then staged to target (Windows cannot
    /// rename over a running exe); if the second rename fails the previous unit is moved back.
    /// Either way the exception is rethrown and <paramref name="staged"/> is left in place for the
    /// caller to clean up.</summary>
    public static void SwapIn(string staged, string target, string old)
    {
        if (!Exists(staged))
        {
            throw new FileNotFoundException($"Staged update '{staged}' is missing.", staged);
        }

        Delete(old);
        if (!OperatingSystem.IsWindows() && File.Exists(staged))
        {
            if (File.Exists(target))
            {
                File.Copy(target, old);
            }
            File.Move(staged, target, overwrite: true);
            return;
        }

        bool movedCurrent = false;
        if (Exists(target))
        {
            Move(target, old);
            movedCurrent = true;
        }

        try
        {
            Move(staged, target);
        }
        catch
        {
            if (movedCurrent && !Exists(target) && Exists(old))
            {
                Move(old, target);
            }
            throw;
        }
    }

    /// <summary>Startup repair after a crash mid-swap. When <paramref name="target"/> is missing it
    /// moves back <paramref name="restoreStaging"/> (a crash inside <see cref="RestorePrevious"/>
    /// after target -> old; it holds the good unit) or else <paramref name="old"/> (a crash inside
    /// <see cref="SwapIn"/>). When target is present but a crash inside RestorePrevious left the
    /// good unit at restoreStaging with <paramref name="old"/> empty, it moves it back to old so the
    /// rollback can run again. Returns true when it moved something. Throws on failure.</summary>
    public static bool RepairInterruptedSwap(string target, string old, string restoreStaging)
    {
        if (Exists(target))
        {
            if (Exists(restoreStaging) && !Exists(old))
            {
                Move(restoreStaging, old);
                return true;
            }
            return false;
        }

        if (Exists(restoreStaging))
        {
            Move(restoreStaging, target);
            return true;
        }

        if (Exists(old))
        {
            Move(old, target);
            return true;
        }

        return false;
    }

    /// <summary>Crash-loop rollback: puts <paramref name="old"/> back at <paramref name="target"/>
    /// and parks the bad unit at <paramref name="old"/> (deleted by the next clean launch), using
    /// <paramref name="staging"/> as the free slot for the three-way rename. Staging may hold the
    /// only good unit from an earlier interrupted rollback, so it is never deleted: an occupied slot
    /// throws. Throws on failure.</summary>
    public static void RestorePrevious(string target, string old, string staging)
    {
        if (Exists(staging))
        {
            throw new IOException($"Rollback slot '{staging}' is occupied.");
        }
        Move(old, staging);
        if (Exists(target))
        {
            Move(target, old);
        }
        Move(staging, target);
    }
}
