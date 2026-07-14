using System;
using System.IO;

namespace RoeSnip.Core.Diagnostics;

/// <summary>Field-visible "did this build crash" breadcrumb, written by each app's top-level
/// unhandled-exception handlers (AppDomain.UnhandledException / WinForms Application.ThreadException)
/// alongside the full stack trace FileLog.Write already captures. This file is the small, cheap-to-
/// read counterpart a future crash-loop guard can check without parsing roesnip.log: which version
/// crashed and when. Static and framework-free (no WPF/Avalonia reference), matching FileLog's own
/// placement in Core so both apps share one implementation.
///
/// Deliberately never throws: called from inside an unhandled-exception handler, where the process
/// is already on its way down — an I/O failure here must never mask or replace the real crash.</summary>
public static class CrashMarker
{
    private const string FileName = "crash-marker.txt";

    /// <summary>Writes/overwrites "&lt;directory&gt;/crash-marker.txt" with <paramref name="version"/>
    /// and the current UTC timestamp. Overwrites any previous marker — only the most recent crash
    /// matters for a future crash-loop guard, not a history of every one.</summary>
    public static void Write(string directory, string version)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, FileName);
            string contents = version + Environment.NewLine +
                DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff") + "Z" + Environment.NewLine;
            File.WriteAllText(path, contents);
        }
        catch
        {
            // Best-effort: a handler already unwinding an unhandled exception must never throw a
            // second one on the way down.
        }
    }
}
