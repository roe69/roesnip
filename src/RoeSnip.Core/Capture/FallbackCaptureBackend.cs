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
        Parallel.For(0, selected.Count, i => slots[i] = CaptureOneOrNull(selected[i]));

        var results = new List<CapturedFrame>(selected.Count);
        foreach (var frame in slots) if (frame is not null) results.Add(frame);
        return results;
    }

    private CapturedFrame? CaptureOneOrNull(MonitorInfo monitor)
    {
        for (int i = 0; i < _capturersInPriorityOrder.Count; i++)
        {
            string capturerKey = $"{monitor.DeviceName}::{i}"; // per-capturer-slot memo, not just per-monitor
            if (_cache.IsDesktopDuplicationBroken(capturerKey)) continue; // name kept for on-disk compat; see PLAN-XPLAT.md §2.3 note

            try
            {
                return _capturersInPriorityOrder[i].Capture(monitor);
            }
            catch (CaptureException ex)
            {
                bool isLastCapturer = i == _capturersInPriorityOrder.Count - 1;
                _cache.MarkDesktopDuplicationBroken(capturerKey);
                Console.Error.WriteLine(
                    $"RoeSnip: capturer #{i} failed for monitor {monitor.Index} ({monitor.DeviceName}): " +
                    $"{ex.Message}.{(isLastCapturer ? " Omitting this monitor." : " Falling back to the next capturer.")}");
            }
        }
        return null;
    }
}
