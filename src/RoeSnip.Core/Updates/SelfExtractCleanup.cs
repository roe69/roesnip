using System;
using System.Diagnostics;
using System.IO;
using RoeSnip.Core.Diagnostics;

namespace RoeSnip.Core.Updates;

/// <summary>Hardening item 10: reclaims the unbounded %TEMP%\.net\RoeSnip content-hash folders a
/// PublishSingleFile+IncludeNativeLibrariesForSelfExtract build leaves behind. Every launch of a
/// single-file self-extracting exe unpacks its native/managed payload into a fresh
/// content-hash-named directory under %TEMP%\.net\&lt;AssemblyName&gt; and NEVER deletes it itself
/// (that is entirely a .NET host runtime behavior, not something either app's code controls) - on a
/// long-lived tray resident relaunched by every update this app of its own volition accumulates one
/// ~19-20 MB folder per launch, forever. Verified live on a dev machine: 100+ folders, ~1 GB.
///
/// Both RoeSnip.csproj and RoeSnip.App.csproj set AssemblyName=RoeSnip (deliberately, per each
/// project's own doc comment - unrelated to this class), so BOTH apps' single-file win-x64 builds
/// extract into the SAME %TEMP%\.net\RoeSnip parent directory, side by side. This class treats that
/// as a feature rather than special-casing it: it removes every sibling folder under its own
/// process's extraction parent it can, regardless of which of the two apps produced it. Deleting the
/// OTHER app's non-running extraction folder just costs it one extra ~19 MB re-extract on its next
/// launch - a one-time, self-healing cost, not a functional loss, and simpler than teaching this
/// class to distinguish "my sibling" from "their sibling" (the folder names are opaque content
/// hashes; there is nothing in the path to tell them apart by app anyway).
///
/// Framework-free (System.IO only) so it lives in Core and both TrayApps call it identically from
/// their own existing startup background Task.Run. Best-effort like every other cleanup helper in
/// this codebase (CleanupStaleExeWithRetry, ProcessPendingSourceCleanup): a still-running peer
/// process (either app) holds its own extraction folder's DLLs locked, so Directory.Delete on that
/// one folder simply throws and is skipped - no retry, no special detection needed, the OS lock
/// itself is the "is this one in use" check. Never throws into the caller.</summary>
public static class SelfExtractCleanup
{
    private const string TempNetFolderName = ".net";

    /// <summary>Entry point both TrayApps call from their startup background Task.Run. Resolves this
    /// process's own extraction directory and %TEMP%\.net from the real environment - see the
    /// internal overload below for the testable form.</summary>
    public static void CleanupSiblingExtractionDirs()
    {
        string tempNetRoot = Path.Combine(Path.GetTempPath(), TempNetFolderName);
        string? ownDir = ResolveOwnExtractionDirectory(tempNetRoot);
        if (ownDir is null)
        {
            return; // dev/portable run, or no natively-loaded module found under %TEMP%\.net - nothing to reclaim
        }

        CleanupSiblingExtractionDirs(ownDir, tempNetRoot);
    }

    /// <summary>Finds THIS process's own single-file extraction directory, if any. Deliberately NOT
    /// AppContext.BaseDirectory - verified live (2026-07) that for a single-file apphost, BaseDirectory
    /// resolves to the directory the apphost EXE itself lives in (its install/publish directory),
    /// not the %TEMP%\.net\... folder its bundled native libraries actually get extracted into. The
    /// only reliable way to find the real extraction directory is to look at where the OS actually
    /// loaded a bundled native DLL from - every single-file build here has at least one (WPF ships its
    /// own PresentationNative_cor3.dll/wpfgfx_cor3.dll etc.; the Avalonia app ships libSkiaSharp.dll,
    /// uiohook.dll, and friends), so the first loaded module whose on-disk path sits under
    /// <paramref name="tempNetRoot"/> IS this process's extraction directory - its containing folder,
    /// since every native DLL observed here sits directly in that folder with no further
    /// nesting.</summary>
    private static string? ResolveOwnExtractionDirectory(string tempNetRoot)
    {
        string prefix = NormalizeDirectory(tempNetRoot) + Path.DirectorySeparatorChar;
        try
        {
            using Process current = Process.GetCurrentProcess();
            foreach (ProcessModule module in current.Modules)
            {
                using (module)
                {
                    string? fileName = module.FileName;
                    if (fileName != null && fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        return Path.GetDirectoryName(fileName);
                    }
                }
            }
        }
        catch
        {
            // Best-effort: module enumeration can fail in unusual/sandboxed environments - treat
            // exactly like "nothing found", the safe no-op default.
        }

        return null;
    }

    /// <summary>Testable core: <paramref name="baseDirectory"/> stands in for this process's own
    /// resolved extraction directory (see <see cref="ResolveOwnExtractionDirectory"/>) and
    /// <paramref name="tempNetRoot"/> for %TEMP%\.net, so tests can point both at an isolated temp
    /// tree instead of the real, machine-wide %TEMP%. Internal (not conditionally compiled) so release
    /// builds still contain it, matching FileLog.ResetForTests' own precedent for exposing a
    /// test-only seam via InternalsVisibleTo rather than a public API surface nobody else should
    /// call.</summary>
    internal static void CleanupSiblingExtractionDirs(string baseDirectory, string tempNetRoot)
    {
        try
        {
            string ownDir = NormalizeDirectory(baseDirectory);
            string root = NormalizeDirectory(tempNetRoot);

            // Acts ONLY when this process's own extraction directory actually sits under %TEMP%\.net -
            // ResolveOwnExtractionDirectory already guarantees this for the real entry point above, but
            // this internal core is also driven directly by tests, so the guard stays here too.
            if (!ownDir.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string? parent = Path.GetDirectoryName(ownDir);
            if (parent is null || !Directory.Exists(parent))
            {
                return;
            }

            int removedCount = 0;
            long bytesFreed = 0;
            foreach (string siblingDir in Directory.EnumerateDirectories(parent))
            {
                if (string.Equals(NormalizeDirectory(siblingDir), ownDir, StringComparison.OrdinalIgnoreCase))
                {
                    continue; // never delete the folder this very process is executing out of
                }

                try
                {
                    long size = DirectorySize(siblingDir);
                    Directory.Delete(siblingDir, recursive: true);
                    removedCount++;
                    bytesFreed += size;
                }
                catch
                {
                    // A running peer process (this app or the other RoeSnip app - see the class doc
                    // comment) has this folder's DLLs loaded and locked; skip it and let the next
                    // launch's sweep try again once that process has exited. No retry here - unlike
                    // CleanupStaleExeWithRetry's single install-dir target, this loops over an
                    // unbounded, unpredictable set of sibling folders, so a bounded retry per folder
                    // would just multiply a already-O(n) sweep for no real benefit.
                }
            }

            if (removedCount > 0)
            {
                double megabytesFreed = bytesFreed / (1024.0 * 1024.0);
                FileLog.Write($"RoeSnip: self-extraction cleanup removed {removedCount} stale folder(s) under {parent}, freeing {megabytesFreed:F1} MB");
            }
        }
        catch (Exception ex)
        {
            // Best-effort like every other startup cleanup in this codebase - a failure here must
            // never be allowed to affect the app's own startup.
            FileLog.Write($"RoeSnip: self-extraction cleanup failed (non-fatal): {ex.Message}");
        }
    }

    /// <summary>Full path with any trailing separator stripped, so a straight ordinal comparison
    /// (used both for the root prefix check and the own-directory skip) can't be fooled by one side
    /// having a trailing backslash and the other not.</summary>
    private static string NormalizeDirectory(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>Sums file lengths recursively for the summary log line only - never blocks the delete
    /// on it. A file that vanishes or is locked mid-enumeration just drops out of the byte count, not
    /// out of the directory being removed.</summary>
    private static long DirectorySize(string directory)
    {
        long total = 0;
        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch
            {
                // Best-effort byte count only - see summary above.
            }
        }

        return total;
    }
}
