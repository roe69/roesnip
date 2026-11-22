using System;
using System.IO;

namespace RoeSnip.Core.Updates;

/// <summary>Rename-based swap of an install unit (a file - the exe / AppImage - or a directory -
/// the macOS .app bundle) with a ".old" rollback copy. Every step is a same-volume rename, so the
/// target is either the old unit or the new one, never half-written. Callers stage
/// <c>staged</c> next to <c>target</c> (same directory) so the renames never cross a volume.</summary>
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

    /// <summary>Moves <paramref name="target"/> (when present) to <paramref name="old"/>, then
    /// <paramref name="staged"/> to <paramref name="target"/>. A stale <paramref name="old"/> is
    /// deleted first. If the second rename fails the previous unit is moved back and the exception
    /// rethrown; <paramref name="staged"/> is left in place for the caller to clean up.</summary>
    public static void SwapIn(string staged, string target, string old)
    {
        if (!Exists(staged))
        {
            throw new FileNotFoundException($"Staged update '{staged}' is missing.", staged);
        }

        Delete(old);
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

    /// <summary>Crash-loop rollback: puts <paramref name="old"/> back at <paramref name="target"/>
    /// and parks the bad unit at <paramref name="old"/> (deleted by the next clean launch), using
    /// <paramref name="staging"/> as the free slot for the three-way rename. Throws on failure.</summary>
    public static void RestorePrevious(string target, string old, string staging)
    {
        Delete(staging);
        Move(old, staging);
        if (Exists(target))
        {
            Move(target, old);
        }
        Move(staging, target);
    }
}
