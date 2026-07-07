using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace RoeSnip.Capture;

public sealed class CaptureService
{
    private readonly IScreenCapturer _primary;
    private readonly IScreenCapturer _fallback;
    private readonly CaptureCache _cache;

    public CaptureService() : this(new DesktopDuplicationCapturer(), new WgcCapturer(), CaptureCache.Default) { }

    /// <summary>Overload used by tests to inject fake capturers and an isolated CaptureCache path
    /// instead of the real %APPDATA% file. Public (rather than internal) so the test project
    /// doesn't need an InternalsVisibleTo edit, same pattern as SettingsStore's path overloads.</summary>
    public CaptureService(IScreenCapturer primary, IScreenCapturer fallback, CaptureCache cache)
    {
        _primary = primary;
        _fallback = fallback;
        _cache = cache;
    }

    /// <summary>Captures every monitor in <paramref name="monitors"/> (or all enumerated monitors
    /// if null). Per monitor: try DesktopDuplicationCapturer; on CaptureException, try WgcCapturer;
    /// on CaptureException from both, log to stderr and OMIT that monitor from the result (does not
    /// throw). If <paramref name="onlyMonitorIndex"/> is set, only that monitor is attempted.
    /// Monitors are captured in PARALLEL — each capture is fully independent (its own D3D device;
    /// the WGC frame pool is CreateFreeThreaded) and on a 3-monitor machine this cuts the hotkey
    /// latency roughly to the slowest single monitor instead of the sum of all three.
    /// Returns frames in the same order as the input monitor list. Empty result means every
    /// monitor failed — callers (CLI / AppComposition) treat that as a hard failure.</summary>
    public IReadOnlyList<CapturedFrame> CaptureAll(
        IReadOnlyList<MonitorInfo>? monitors = null,
        int? onlyMonitorIndex = null)
    {
        var targets = monitors ?? MonitorEnumerator.Enumerate();

        var selected = new List<MonitorInfo>();
        foreach (var monitor in targets)
        {
            if (onlyMonitorIndex is int idx && monitor.Index != idx) continue;
            selected.Add(monitor);
        }

        // Fixed slot per input monitor preserves input ordering regardless of completion order;
        // a failed monitor leaves a null slot, which is filtered out below (the "omit, don't
        // throw" error contract).
        var slots = new CapturedFrame?[selected.Count];
        Parallel.For(0, selected.Count, i => slots[i] = CaptureOneOrNull(selected[i]));

        var results = new List<CapturedFrame>(selected.Count);
        foreach (var frame in slots)
        {
            if (frame is not null) results.Add(frame);
        }

        return results;
    }

    /// <summary>One monitor's capture with the DD→WGC fallback policy. Returns null (after logging)
    /// if both paths failed. Consults the persisted CaptureCache memo (DESIGN.md "Failure modes",
    /// DD black-frame quirk): once DD is deemed broken for a monitor, every later capture — in this
    /// process AND in future launches — goes straight to WGC instead of paying the doomed DD
    /// attempt (device creation + retry budget) again.</summary>
    private CapturedFrame? CaptureOneOrNull(MonitorInfo monitor)
    {
        if (!_cache.IsDesktopDuplicationBroken(monitor.DeviceName))
        {
            try
            {
                return _primary.Capture(monitor);
            }
            catch (CaptureException primaryEx)
            {
                // First failure for this monitor: log it and persist the memo so later captures
                // (including after an app relaunch) skip the doomed primary attempt entirely.
                _cache.MarkDesktopDuplicationBroken(monitor.DeviceName);
                Console.Error.WriteLine(
                    $"RoeSnip: Desktop Duplication capture failed for monitor {monitor.Index} " +
                    $"({monitor.DeviceName}): {primaryEx.Message}. Falling back to WGC " +
                    "(and skipping Desktop Duplication for this monitor from now on; memo persisted to capture-cache.json).");
            }
        }

        try
        {
            return _fallback.Capture(monitor);
        }
        catch (CaptureException fallbackEx)
        {
            Console.Error.WriteLine(
                $"RoeSnip: WGC fallback capture also failed for monitor {monitor.Index} " +
                $"({monitor.DeviceName}): {fallbackEx.Message}. Omitting this monitor.");
            return null;
        }
    }
}
