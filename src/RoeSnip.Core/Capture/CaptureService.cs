namespace RoeSnip.Core.Capture;

/// <summary>Thin, backend-agnostic facade so CLI/AppComposition code looks almost identical to the
/// WPF app's (PLAN.md §2.3) even though the fallback logic now lives behind whichever
/// ICaptureBackend the registry selects.</summary>
public sealed class CaptureService
{
    private readonly ICaptureBackend _backend;

    public CaptureService() : this(CaptureBackendRegistry.CreateForCurrentPlatform()) { }
    public CaptureService(ICaptureBackend backend) { _backend = backend; }

    public bool SupportsHdrExport => _backend.SupportsHdrExport;
    public string BackendName => _backend.Name;

    /// <summary>The last capturer's failure message per monitor omitted by the most recent
    /// <see cref="CaptureAll"/> call — only <see cref="FallbackCaptureBackend"/> tracks this (Windows
    /// and Linux both compose it), so backends that implement <see cref="ICaptureBackend"/> directly
    /// (MacCaptureBackend, which throws rather than returning empty — a different contract) report
    /// empty here rather than growing a new interface member for one caller (RunCaptureFlowAsync's
    /// error toast).</summary>
    public IReadOnlyList<string> LastCaptureFailureMessages =>
        _backend is FallbackCaptureBackend fallback ? fallback.LastCaptureFailureMessages : Array.Empty<string>();

    public IReadOnlyList<MonitorInfo> EnumerateMonitors() => _backend.EnumerateMonitors();

    public IReadOnlyList<CapturedFrame> CaptureAll(
        IReadOnlyList<MonitorInfo>? monitors = null, int? onlyMonitorIndex = null)
        => _backend.CaptureAll(monitors, onlyMonitorIndex);
}
