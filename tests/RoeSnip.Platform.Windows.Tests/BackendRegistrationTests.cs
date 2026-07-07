using RoeSnip.Core.Capture;
using RoeSnip.Platform.Windows;
using Xunit;

namespace RoeSnip.Platform.Windows.Tests;

/// <summary>PLAN-XPLAT.md §4's explicit "CaptureBackendRegistry selection risk" check: on this
/// Windows machine, <see cref="CaptureBackendRegistry.CreateForCurrentPlatform"/> must return a
/// <see cref="WindowsCaptureBackend"/> specifically (not just "some backend"), proving the
/// [ModuleInitializer] registration in WindowsCaptureBackend.cs actually ran and its IsSupported
/// gate is keyed on the right OS.</summary>
public class BackendRegistrationTests
{
    [Fact]
    public void CreateForCurrentPlatform_OnWindows_ReturnsWindowsCaptureBackend()
    {
        // Touching the WindowsCaptureBackend type above (via the using/typeof below) guarantees the
        // RoeSnip.Platform.Windows assembly is loaded, which is exactly the condition the App shell
        // establishes at startup before asking the registry for a backend.
        var backend = CaptureBackendRegistry.CreateForCurrentPlatform();
        Assert.IsType<WindowsCaptureBackend>(backend);
        Assert.True(backend.SupportsHdrExport);
        Assert.Equal("Windows (Desktop Duplication/WGC)", backend.Name);
    }
}
