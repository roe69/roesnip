namespace RoeSnip.Core.Capture;

/// <summary>The per-OS entry point: monitor enumeration + capture-all for one platform. This is the
/// NEW abstraction (docs/architecture.md: "ICaptureBackend (new: monitor enumeration +
/// capture-all)") - it replaces the WPF app's static <c>MonitorEnumerator</c> class (monitor
/// enumeration is now backend-specific: DXGI+DisplayConfig on Windows, CGDirectDisplayID/scksnap on
/// macOS, RandR/portal on Linux) and generalizes <c>CaptureService</c> from "always DD-then-WGC" to
/// "whatever this OS's backend does."</summary>
public interface ICaptureBackend
{
    /// <summary>Human-readable name for --diag / error messages, e.g. "Windows (Desktop
    /// Duplication/WGC)", "Linux (xdg-desktop-portal)".</summary>
    string Name { get; }

    /// <summary>True if this backend can produce an untouched HDR original suitable for the "Save
    /// HDR" / --jxr-equivalent export path: Windows (WIC), Linux and macOS (the bundled JxrEncApp,
    /// <c>JxrExport</c>) - the capability flag that hides the button elsewhere.</summary>
    bool SupportsHdrExport { get; }

    /// <summary>Enumerates all active monitors for this session. Never throws for a single bad
    /// monitor entry - logs to stderr and omits it. Empty list only if enumeration itself fails
    /// entirely.</summary>
    IReadOnlyList<MonitorInfo> EnumerateMonitors();

    /// <summary>Captures every monitor in <paramref name="monitors"/> (or all enumerated monitors if
    /// null). Per monitor: try this backend's own fallback policy; on total failure, log to stderr
    /// and OMIT that monitor (never throw). If <paramref name="onlyMonitorIndex"/> is set, only that
    /// monitor is attempted. Returns frames in the same order as the input monitor list.</summary>
    IReadOnlyList<CapturedFrame> CaptureAll(
        IReadOnlyList<MonitorInfo>? monitors = null, int? onlyMonitorIndex = null);
}
