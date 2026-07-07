using RoeSnip.Core.Capture;

namespace RoeSnip.Platform.Linux;

/// <summary>Linux capture backend (PLAN-XPLAT.md §3.4): composes the xdg-desktop-portal capturer
/// (primary — correct on Wayland and X11-with-portals) and the raw X11 XGetImage capturer
/// (fallback — portal-less X sessions) into Core's <see cref="FallbackCaptureBackend"/>, so the
/// same persisted once-broken-skip-forever memo behavior Windows uses applies here for free.
///
/// Monitor enumeration uses XRandR regardless of which capturer ultimately succeeds (both need the
/// same bounds list, and the portal has no per-monitor handles of its own) — this keeps
/// RoeSnip.Platform.Linux independent of RoeSnip.App/Avalonia like every other Platform.* project.
/// SdrWhiteNits=240 / AdvancedColorActive=false are pinned unconditionally (no ACM/HDR concept on
/// Linux in v1 — PLAN-XPLAT.md §6 flag 6) and every frame is Bgra8Srgb passthrough with the
/// documented SdrWhiteInBufferUnits=1.0 sentinel.</summary>
public sealed class LinuxCaptureBackend : ICaptureBackend
{
    private readonly FallbackCaptureBackend _inner;

    public LinuxCaptureBackend()
    {
        _inner = new FallbackCaptureBackend(
            "Linux (xdg-desktop-portal/X11)",
            supportsHdrExport: false,
            EnumerateMonitorsCore,
            new IScreenCapturer[]
            {
                new PortalScreenshotCapturer(EnumerateMonitorsCore),
                new X11Capturer(),
            },
            CaptureCache.Default);
    }

    public string Name => _inner.Name;
    public bool SupportsHdrExport => _inner.SupportsHdrExport;

    public IReadOnlyList<MonitorInfo> EnumerateMonitors() => _inner.EnumerateMonitors();

    public IReadOnlyList<CapturedFrame> CaptureAll(
        IReadOnlyList<MonitorInfo>? monitors = null, int? onlyMonitorIndex = null)
        => _inner.CaptureAll(monitors, onlyMonitorIndex);

    private static IReadOnlyList<MonitorInfo> EnumerateMonitorsCore()
        => X11Capturer.EnumerateMonitorsViaRandR();
}

file static class ModuleInit
{
    // CA2255 warns against ModuleInitializer in libraries, but cross-assembly self-registration
    // into CaptureBackendRegistry is exactly the pattern PLAN-XPLAT.md §2.3/§3.4 mandates for
    // every Platform.* project (Core/App never name a concrete backend type).
#pragma warning disable CA2255
    [System.Runtime.CompilerServices.ModuleInitializer]
#pragma warning restore CA2255
    internal static void Init() => CaptureBackendRegistry.Register(
        () => OperatingSystem.IsLinux(), () => new LinuxCaptureBackend());
}
