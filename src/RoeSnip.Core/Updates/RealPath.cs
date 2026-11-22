using System;
using System.Collections.Generic;
using System.IO;

namespace RoeSnip.Core.Updates;

/// <summary>realpath(3) in managed code: every symlinked component of a path resolved, so two
/// spellings of one file compare equal. $APPIMAGE and macOS process paths arrive fully resolved,
/// while paths built from $HOME / $XDG_DATA_HOME may run through a symlink. Missing trailing
/// components are kept as written (the install target may not exist yet).</summary>
public static class RealPath
{
    private const int MaxLinkHops = 40; // Linux MAXSYMLINKS

    /// <summary>The resolved absolute form of <paramref name="path"/>. Throws IOException on a link
    /// loop, like realpath's ELOOP.</summary>
    public static string Resolve(string path)
    {
        var pending = new List<string>(Split(Path.GetFullPath(path), out string current));
        int hops = 0;
        while (pending.Count > 0)
        {
            string next = Path.Combine(current, pending[0]);
            pending.RemoveAt(0);

            string? target = LinkTargetOf(next);
            if (target is null)
            {
                current = next;
                continue;
            }

            if (++hops > MaxLinkHops)
            {
                throw new IOException($"Too many levels of symbolic links resolving '{path}'.");
            }

            // A relative target is relative to the link's (already resolved) directory; restart
            // from the target's root with the unresolved tail appended.
            string joined = Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(current, target));
            pending.InsertRange(0, Split(joined, out current));
        }

        return current;
    }

    /// <summary><see cref="Resolve"/> that never throws: falls back to Path.GetFullPath, then to the
    /// input itself.</summary>
    public static string ResolveOrFull(string path)
    {
        try { return Resolve(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            try { return Path.GetFullPath(path); }
            catch (Exception ex2) when (ex2 is IOException or ArgumentException or NotSupportedException) { return path; }
        }
    }

    /// <summary>True when both paths name the same file after symlink resolution (a trailing
    /// separator ignored).</summary>
    public static bool SameFile(string a, string b, StringComparison comparison) =>
        string.Equals(
            ResolveOrFull(a).TrimEnd(Path.DirectorySeparatorChar),
            ResolveOrFull(b).TrimEnd(Path.DirectorySeparatorChar),
            comparison);

    private static string[] Split(string fullPath, out string root)
    {
        root = Path.GetPathRoot(fullPath) ?? string.Empty;
        return fullPath[root.Length..].Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
    }

    // lstat-based: LinkTarget reads the link itself, so a dangling link still reports its target.
    private static string? LinkTargetOf(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
