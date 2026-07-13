using System.Threading;

namespace RoeSnip.App.AppShell;

/// <summary>Single process-wide capture gate for the hotkey/tray/pipe flow
/// (AppComposition.RunCaptureFlowAsync in Program.cs). Ported from the WPF app's identical
/// CaptureGate (src/RoeSnip/Program.cs:816-847) — kept internal (not folded into AppComposition)
/// so a future color-picker pick-mode capture or Recording subsystem can share it the same way the
/// WPF app's OverlayController/RecordingController do, without AppComposition itself growing more
/// gate-shaped state than the one flow it already owns.</summary>
internal static class CaptureGate
{
    private static int s_busy; // 0 = idle, 1 = busy; only touched via Interlocked

    /// <summary>True from just before TrayApp.StartWarmup launches the warmup thread until
    /// RunWarmup's finally clears it on every exit path (success, exception, or a benign
    /// zero-monitors return) — a wider window than <see cref="HeldByWarmup"/>, which only covers
    /// the sub-second stretch WarmupCaptureSessions actually holds the gate for.
    /// RunCaptureFlowAsync polls this BEFORE its TryEnter to close the race where a trigger lands
    /// after StartWarmup but before WarmupCaptureSessions has entered the gate: winning that race
    /// would make the real capture pay first-time D3D/WGC device init itself instead of riding the
    /// warmup's own init (WPF measured 292-1003 ms lost vs. ~20 ms waited). Defaults to false, so
    /// CLI/test callers of RunCaptureFlowAsync that never touch TrayApp are unaffected.</summary>
    public static volatile bool WarmupPending;

    /// <summary>True while the STARTUP WARMUP capture holds the gate (set/cleared by TrayApp's
    /// WarmupCaptureSessions, strictly inside its TryEnter/Exit pair). Lets RunCaptureFlowAsync
    /// tell "busy because the sub-second warmup is running" (a real hotkey press should wait that
    /// out) apart from "busy because another overlay session is live" (drop the trigger).</summary>
    public static volatile bool HeldByWarmup;

    public static bool TryEnter() => Interlocked.CompareExchange(ref s_busy, 1, 0) == 0;
    public static void Exit() => Interlocked.Exchange(ref s_busy, 0);

    /// <summary>Peek for IdleMemoryTrimmer's am-I-actually-idle check — never used for entry
    /// decisions (that's TryEnter's compare-exchange), so a stale read is harmless: the trimmer
    /// skipping or running against a just-changed gate is safe either way.</summary>
    public static bool IsBusy => Volatile.Read(ref s_busy) != 0;
}
