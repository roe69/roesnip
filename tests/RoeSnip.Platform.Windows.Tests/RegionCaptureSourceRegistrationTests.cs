using RoeSnip.Core.Capture;
using RoeSnip.Core.Recording;
using Xunit;

namespace RoeSnip.Platform.Windows.Tests;

/// <summary>Same "did the [ModuleInitializer] actually run and gate on the right OS" check
/// BackendRegistrationTests/RecordingSeamsRegistrationTests already establish, extended to item 20's
/// own new registry: on this Windows machine, RegionCaptureSourceRegistry must return a genuine
/// <see cref="WindowsRegionCaptureSource"/>, never falling through to the portable polling
/// fallback.</summary>
public class RegionCaptureSourceRegistrationTests
{
    [Fact]
    public void Create_OnWindows_ReturnsWindowsRegionCaptureSource()
    {
        // Unlike CaptureBackendRegistry.CreateForCurrentPlatform (a cheap wrapper construction),
        // WindowsRegionCaptureSource's own constructor eagerly calls WgcCapturer.CreateResources
        // (a real D3D11 device + GraphicsCaptureItem, see that class's own doc comment for why it
        // is not lazy) - a genuine, currently-enumerated monitor is required, not a synthetic one.
        var monitor = MonitorEnumerator.Enumerate()[0];
        using var source = RegionCaptureSourceRegistry.Create(monitor, RectPhysical.FromSize(0, 0, 64, 64), targetFps: 30);
        Assert.IsType<WindowsRegionCaptureSource>(source);
    }
}
