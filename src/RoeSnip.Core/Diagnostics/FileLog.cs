using System;
using System.IO;

namespace RoeSnip.Core.Diagnostics;

/// <summary>Field-visible diagnostics sink. Both apps are WinExe (or WinExe-on-Windows for the
/// Avalonia app — see RoeSnip.App.csproj's own OutputType override comment), so every
/// Console.Error.WriteLine diagnostic in the codebase is discarded on every real launch path (Run
/// key, Start Menu, tray double-click, hotkey) — there is no attached console to receive it and no
/// log file exists on disk. This class is the fix: <see cref="Write"/> is a drop-in replacement for
/// Console.Error.WriteLine that ALSO appends the line to a rotating log file, so a support session
/// can ask a user for one file instead of "please run it from a terminal and paste what you see".
///
/// Static and framework-free (no WPF/Avalonia reference) so it can live in Core and be called from
/// every project, including Platform.* backends that predate either app's UI layer.
///
/// Deliberately never throws and never blocks meaningfully: a logging call must not be able to
/// crash or hang the capture/overlay/update hot paths it instruments. Every I/O op below is wrapped
/// in its own try/catch; a locked, missing, or unwritable file silently degrades to console-only
/// output (which is exactly today's behavior) rather than propagating.</summary>
public static class FileLog
{
    private const string FileName = "roesnip.log";
    private const long MaxBytes = 1L * 1024 * 1024; // 1 MB, per the class's own rotation contract

    // How many Write() calls between each in-session rotation size check (see Write's own comment) -
    // cheap enough (a FileInfo stat, not a read) to run this often without turning every write into
    // extra I/O, frequent enough that a tray resident logging a line every few seconds still gets
    // checked well within a session, long before 1 MB could accumulate unnoticed.
    private const int RotateCheckInterval = 256;

    private static readonly object Lock = new();

    // Null until Initialize succeeds; Write() falls back to console-only output while null (covers
    // both "never initialized" — e.g. a unit test exercising Core code directly — and "Initialize
    // itself failed", e.g. an unwritable config directory).
    private static string? _filePath;

    // Counts Write() calls since the last rotation check (Initialize's own check, or one of these) -
    // see Write's own comment for why this exists.
    private static int _writesSinceRotationCheck;

    /// <summary>Points the sink at "&lt;directory&gt;/roesnip.log" and rotates it if it has grown
    /// past <see cref="MaxBytes"/>. Call once, as early as possible in each app's Main, before any
    /// other startup code that might log. Safe to call more than once (each call re-resolves the
    /// path and re-checks rotation); safe to call with a directory that doesn't exist yet or can't
    /// be created (falls back to console-only logging, matching pre-FileLog behavior).</summary>
    public static void Initialize(string directory)
    {
        lock (Lock)
        {
            try
            {
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, FileName);
                RotateIfOversized(path);
                _filePath = path;
                _writesSinceRotationCheck = 0;
            }
            catch
            {
                // Best-effort: leave the sink console-only rather than let a startup-time I/O
                // failure (read-only config dir, disk full, permissions) take down the app.
                _filePath = null;
            }
        }
    }

    /// <summary>Appends a UTC-timestamped line to the log file (if initialized) and echoes
    /// <paramref name="message"/> to Console.Error unchanged — the same single call every former
    /// Console.Error.WriteLine("RoeSnip: ...") site now makes, preserving the existing dev/terminal
    /// experience while also making the line durable on a field machine.
    ///
    /// Also re-checks rotation every <see cref="RotateCheckInterval"/> calls (never on every single
    /// call — a FileInfo stat on every capture-hot-path log line would be needless overhead for a
    /// cap that only needs to be approximately enforced). Without this, <see cref="Initialize"/>'s
    /// own rotation only ever ran once per process launch, so a tray resident's normal multi-week
    /// uptime could grow the log well past <see cref="MaxBytes"/> before the next relaunch ever
    /// looked at it again — this keeps the "capped at roughly 2x MaxBytes" contract true within a
    /// single long-lived session too, not just across launches.</summary>
    public static void Write(string message)
    {
        try
        {
            Console.Error.WriteLine(message);
        }
        catch
        {
            // No attached/writable console (the common WinExe case) — nothing more to do here,
            // the file append below is the whole point in that case anyway.
        }

        lock (Lock)
        {
            if (_filePath is null)
            {
                return;
            }

            try
            {
                string line = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z] {message}";
                File.AppendAllText(_filePath, line + Environment.NewLine);

                if (++_writesSinceRotationCheck >= RotateCheckInterval)
                {
                    _writesSinceRotationCheck = 0;
                    RotateIfOversized(_filePath);
                }
            }
            catch
            {
                // Locked file (another process, e.g. a support session tailing it, or a rare
                // concurrent-rotation edge), disk full, permissions revoked mid-run, etc. — a
                // dropped log line is always the right tradeoff over crashing the caller.
            }
        }
    }

    /// <summary>Moves an oversized log file to "roesnip.log.1" (overwriting any previous one), so
    /// the field footprint of this sink is capped at roughly 2x <see cref="MaxBytes"/> — one active
    /// file plus at most one rotated-out predecessor. Called only from <see cref="Initialize"/>
    /// (i.e. once per process launch), not on every write, since a tray resident's log grows slowly
    /// enough that per-write size checks would be pure overhead for no real benefit.</summary>
    private static void RotateIfOversized(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length > MaxBytes)
            {
                string rotatedPath = path + ".1";
                File.Move(path, rotatedPath, overwrite: true);
            }
        }
        catch
        {
            // Best-effort rotation: if it fails, Write() just keeps appending to the existing
            // (still-oversized) file rather than losing logging entirely.
        }
    }

    /// <summary>Test-only reset back to the "never initialized" state. Internal (not conditionally
    /// compiled) so release builds still contain it — a static sink's file path would otherwise
    /// leak across FileLogTests methods, which all share this one process-wide static.</summary>
    internal static void ResetForTests()
    {
        lock (Lock)
        {
            _filePath = null;
            _writesSinceRotationCheck = 0;
        }
    }
}
