namespace RoeSnip.Core.Capture;

/// <summary>Generalizes the WPF app's CaptureService fallback/cache/parallel-capture orchestration
/// (PLAN.md §2.3 + the Phase-A CaptureCache addition) into reusable infrastructure for ANY platform
/// whose capture strategy is itself an ordered list of capturers with a "once broken, skip forever"
/// memo — Windows (DD then WGC) and Linux (portal then X11) both fit this shape; macOS does not
/// (exactly one capturer) and implements ICaptureBackend directly instead (PLAN-XPLAT.md §3.5).
///
/// Behavior, copied from the WPF app's CaptureService.CaptureAll/CaptureOneOrNull (unchanged):
/// monitors are captured in PARALLEL (each capturer call is independent); per monitor, try
/// capturers in the given priority order, skipping any this monitor's <see cref="CaptureCache"/>
/// entry says is already known-broken; on the first success, return it; on total failure across all
/// capturers, log to stderr and omit the monitor. The FIRST failure of a given capturer for a given
/// monitor persists a cache entry so future captures (including after a relaunch) skip straight past
/// it.</summary>
public sealed class FallbackCaptureBackend : ICaptureBackend
{
    private readonly Func<IReadOnlyList<MonitorInfo>> _enumerate;
    private readonly IReadOnlyList<IScreenCapturer> _capturersInPriorityOrder;
    private readonly CaptureCache _cache;

    public string Name { get; }
    public bool SupportsHdrExport { get; }

    /// <summary>The last capturer's <see cref="CaptureException.Message"/> for each monitor that was
    /// omitted by the most recent <see cref="CaptureAll"/> call (rebuilt from scratch every call, not
    /// accumulated) — this is the most specific reason available (e.g. the portal's "permission
    /// denied or dialog dismissed" text) for a caller that wants to surface something more useful
    /// than "capture failed on every monitor" to the user. Populated after the Parallel.For in
    /// CaptureAll completes, from a pre-sized per-monitor slot array (no lock needed: each slot is
    /// written by exactly one worker, same discipline as the frame slots array above it).</summary>
    public IReadOnlyList<string> LastCaptureFailureMessages { get; private set; } = Array.Empty<string>();

    public FallbackCaptureBackend(
        string name, bool supportsHdrExport,
        Func<IReadOnlyList<MonitorInfo>> enumerate,
        IReadOnlyList<IScreenCapturer> capturersInPriorityOrder,
        CaptureCache cache)
    {
        Name = name;
        SupportsHdrExport = supportsHdrExport;
        _enumerate = enumerate;
        _capturersInPriorityOrder = capturersInPriorityOrder;
        _cache = cache;
    }

    public IReadOnlyList<MonitorInfo> EnumerateMonitors() => _enumerate();

    public IReadOnlyList<CapturedFrame> CaptureAll(
        IReadOnlyList<MonitorInfo>? monitors = null, int? onlyMonitorIndex = null)
    {
        var targets = monitors ?? EnumerateMonitors();
        var selected = new List<MonitorInfo>();
        foreach (var monitor in targets)
        {
            if (onlyMonitorIndex is int idx && monitor.Index != idx) continue;
            selected.Add(monitor);
        }

        var slots = new CapturedFrame?[selected.Count];
        var failureSlots = new string?[selected.Count];
        Parallel.For(0, selected.Count, i => slots[i] = CaptureOneOrNull(selected[i], out failureSlots[i]));

        var results = new List<CapturedFrame>(selected.Count);
        foreach (var frame in slots) if (frame is not null) results.Add(frame);

        var lastFailureMessages = new List<string>(selected.Count);
        for (int i = 0; i < selected.Count; i++)
        {
            if (slots[i] is null && failureSlots[i] is string message) lastFailureMessages.Add(message);
        }
        LastCaptureFailureMessages = lastFailureMessages;

        return results;
    }

    private CapturedFrame? CaptureOneOrNull(MonitorInfo monitor, out string? lastCapturerFailureMessage)
    {
        var frame = CaptureOneAttempt(monitor, out bool skippedAnyByMemo, out lastCapturerFailureMessage);
        if (frame is null && skippedAnyByMemo)
        {
            // Everything failed while memoized capturers were skipped — the memo may be stale
            // (environment changed since it was recorded, e.g. a portal got installed or a
            // one-off X error got persisted by an older build). Clear this monitor's memos and
            // retry once from scratch so a poisoned cache can never permanently kill capture.
            Console.Error.WriteLine(
                $"RoeSnip: all capturers failed for monitor {monitor.Index} ({monitor.DeviceName}) " +
                "while some were skipped by the persisted memo — clearing the memo and retrying once.");
            for (int i = 0; i < _capturersInPriorityOrder.Count; i++)
            {
                _cache.Unmark($"{monitor.DeviceName}::{i}");
            }
            frame = CaptureOneAttempt(monitor, out _, out lastCapturerFailureMessage);
        }
        return frame;
    }

    private CapturedFrame? CaptureOneAttempt(
        MonitorInfo monitor, out bool skippedAnyByMemo, out string? lastCapturerFailureMessage)
    {
        skippedAnyByMemo = false;
        lastCapturerFailureMessage = null;
        var failedSlots = new List<int>();
        for (int i = 0; i < _capturersInPriorityOrder.Count; i++)
        {
            string capturerKey = $"{monitor.DeviceName}::{i}"; // per-capturer-slot memo, not just per-monitor
            bool isLastCapturer = i == _capturersInPriorityOrder.Count - 1;

            // Never memo-skip the last resort — skipping it guarantees failure, which is
            // strictly worse than trying it. (Memo name kept for on-disk compat; §2.3 note.)
            if (!isLastCapturer && _cache.IsDesktopDuplicationBroken(capturerKey))
            {
                skippedAnyByMemo = true;
                continue;
            }

            try
            {
                var frame = _capturersInPriorityOrder[i].Capture(monitor);
                // Persist "skip capturer #j" memos ONLY now that a later capturer has actually
                // succeeded: the skip provably loses nothing (the Windows DD-black-frame case).
                // Failures with no working alternative are treated as transient and not recorded,
                // so a bad day (missing portal, X hiccup) can never poison future sessions.
                foreach (int j in failedSlots)
                {
                    _cache.MarkDesktopDuplicationBroken($"{monitor.DeviceName}::{j}");
                }
                return frame;
            }
            catch (CaptureException ex)
            {
                failedSlots.Add(i);
                if (isLastCapturer) lastCapturerFailureMessage = ex.Message;
                Console.Error.WriteLine(
                    $"RoeSnip: capturer #{i} failed for monitor {monitor.Index} ({monitor.DeviceName}): " +
                    $"{ex.Message}.{(isLastCapturer ? " Omitting this monitor." : " Falling back to the next capturer.")}");
            }
        }
        return null;
    }
}
