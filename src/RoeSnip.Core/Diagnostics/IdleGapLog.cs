namespace RoeSnip.Core.Diagnostics;

/// <summary>Tiny, pure formatting helper for the trigger-timing log lines' idle-gap suffix (2026-08
/// idle-latency investigation). Each app's own AppComposition.RunCaptureFlowAsync (Program.cs) owns
/// the actual Environment.TickCount64 bookkeeping — tracking how long it has been since the
/// previous trigger won CaptureGate — because that bookkeeping is tangled up with each app's own
/// gate/dispatcher plumbing and isn't worth extracting; this class only knows how to render an
/// already-computed gap, which IS pure and worth sharing rather than duplicating (unlike the
/// CaptureDeadline/s_lastBusyNoticeTick fields next to it, which the codebase already duplicates
/// per app on purpose).
///
/// Why this exists at all: field logs showed a "the hotkey is sometimes just slow" complaint
/// correlating tightly with how long the app had been idle before the press — the capture pipeline
/// and the monitor itself both pay a real wake cost that grows with idle time (GPU/display
/// power-state ramp-up; WgcCapturer's IsReusable/TrimCachedDeviceMemory doc comments describe the
/// same phenomenon from the capture side). That correlation was only visible after manually
/// cross-referencing timestamps across thousands of log lines; logging the idle gap directly
/// alongside each trigger's own timing makes the next slow occurrence diagnosable from the log
/// alone, without that reconstruction.
///
/// Framework-free (matching FileLog/CrashMarker's own placement in Core) so both apps share one
/// implementation and it is unit-testable without either app's UI/capture dependencies.</summary>
public static class IdleGapLog
{
    /// <summary>Raw milliseconds, not a "3m45s" rendering — this is grep/awk/script fodder for
    /// correlating a slow or abandoned trigger with how long the app had been idle beforehand, not
    /// prose for a human to read in isolation. Empty string when <paramref name="idleMs"/> is
    /// negative (the sentinel each app's caller uses for "no previous trigger this process"), so the
    /// log line it's appended to reads exactly as it did before this existed.</summary>
    public static string FormatSuffix(long idleMs) =>
        idleMs < 0 ? "" : $" [idle {idleMs} ms since previous trigger]";
}
