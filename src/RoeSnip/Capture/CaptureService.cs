using System;
using System.Collections.Generic;

namespace RoeSnip.Capture;

public sealed class CaptureService
{
    private readonly IScreenCapturer _primary;
    private readonly IScreenCapturer _fallback;

    public CaptureService() : this(new DesktopDuplicationCapturer(), new WgcCapturer()) { }

    internal CaptureService(IScreenCapturer primary, IScreenCapturer fallback)
    {
        _primary = primary;
        _fallback = fallback;
    }

    /// <summary>Captures every monitor in <paramref name="monitors"/> (or all enumerated monitors
    /// if null). Per monitor: try DesktopDuplicationCapturer; on CaptureException, try WgcCapturer;
    /// on CaptureException from both, log to stderr and OMIT that monitor from the result (does not
    /// throw). If <paramref name="onlyMonitorIndex"/> is set, only that monitor is attempted.
    /// Returns frames in the same order as the input monitor list. Empty result means every
    /// monitor failed — callers (CLI / AppComposition) treat that as a hard failure.</summary>
    public IReadOnlyList<CapturedFrame> CaptureAll(
        IReadOnlyList<MonitorInfo>? monitors = null,
        int? onlyMonitorIndex = null)
    {
        var targets = monitors ?? MonitorEnumerator.Enumerate();
        var results = new List<CapturedFrame>();

        foreach (var monitor in targets)
        {
            if (onlyMonitorIndex is int idx && monitor.Index != idx) continue;

            try
            {
                results.Add(_primary.Capture(monitor));
                continue;
            }
            catch (CaptureException primaryEx)
            {
                Console.Error.WriteLine(
                    $"RoeSnip: Desktop Duplication capture failed for monitor {monitor.Index} " +
                    $"({monitor.DeviceName}): {primaryEx.Message}. Falling back to WGC.");
            }

            try
            {
                results.Add(_fallback.Capture(monitor));
            }
            catch (CaptureException fallbackEx)
            {
                Console.Error.WriteLine(
                    $"RoeSnip: WGC fallback capture also failed for monitor {monitor.Index} " +
                    $"({monitor.DeviceName}): {fallbackEx.Message}. Omitting this monitor.");
            }
        }

        return results;
    }
}
