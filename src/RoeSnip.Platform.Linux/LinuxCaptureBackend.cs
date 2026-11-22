using RoeSnip.Core.Capture;

namespace RoeSnip.Platform.Linux;

/// <summary>Linux capture backend: an ordered capturer list in Core's
/// <see cref="FallbackCaptureBackend"/>.
///   Wayland: the kept-open ScreenCast stream (<see cref="ScreenCastCapturer"/>), then the
///     Screenshot portal, which refuses unless the desktop cannot screencast at all and never runs
///     on GNOME (shutter sound and flash, <see cref="LinuxCapturePolicy"/>). No X11 capturer:
///     XGetImage refuses on Wayland.
///   X11: XGetImage first, the Screenshot portal last (not on GNOME).
/// Monitor enumeration is XRandR (XWayland on Wayland) for every capturer.
/// SdrWhiteNits=240 / AdvancedColorActive=false are pinned (no HDR concept on Linux in v1) and
/// every frame is Bgra8Srgb with the SdrWhiteInBufferUnits=1.0 sentinel.</summary>
public sealed class LinuxCaptureBackend : ICaptureBackend
{
    private readonly FallbackCaptureBackend _inner;

    public LinuxCaptureBackend()
    {
        IScreenCapturer[] capturers = ScreenCastSession.IsWaylandSession
            ? new IScreenCapturer[]
            {
                new ScreenCastCapturer(EnumerateMonitorsCore),
                new PortalScreenshotCapturer(EnumerateMonitorsCore),
            }
            : new IScreenCapturer[]
            {
                new X11Capturer(),
                new PortalScreenshotCapturer(EnumerateMonitorsCore),
            };
        _inner = new FallbackCaptureBackend(
            ScreenCastSession.IsWaylandSession ? "Linux (ScreenCast portal/Screenshot portal)" : "Linux (X11/Screenshot portal)",
            supportsHdrExport: false,
            EnumerateMonitorsCore,
            capturers,
            CaptureCache.Default);
    }

    public string Name => _inner.Name;
    public bool SupportsHdrExport => _inner.SupportsHdrExport;

    public IReadOnlyList<MonitorInfo> EnumerateMonitors() => _inner.EnumerateMonitors();

    public IReadOnlyList<CapturedFrame> CaptureAll(
        IReadOnlyList<MonitorInfo>? monitors = null, int? onlyMonitorIndex = null)
    {
        // The tray warmup thread (TrayApp.StartWarmup names it) captures for nobody: never let it
        // reach the Screenshot portal's shutter sound, even when the screencast cannot open.
        using var background = Thread.CurrentThread.Name == "RoeSnip-Warmup"
            ? CaptureContext.BeginBackground()
            : null;
        return _inner.CaptureAll(monitors, onlyMonitorIndex);
    }

    private static IReadOnlyList<MonitorInfo> EnumerateMonitorsCore()
        => X11Capturer.EnumerateMonitorsViaRandR();
}

file static class ModuleInit
{
    // CA2255 warns against ModuleInitializer in libraries, but cross-assembly self-registration
    // into CaptureBackendRegistry is the pattern every Platform.* project uses.
#pragma warning disable CA2255
    [System.Runtime.CompilerServices.ModuleInitializer]
#pragma warning restore CA2255
    internal static void Init() => CaptureBackendRegistry.Register(
        () => OperatingSystem.IsLinux(), () => new LinuxCaptureBackend());
}
