using RoeSnip.Core.Capture;
using Xunit;

namespace RoeSnip.Core.Tests;

public class LinuxCapturePolicyTests
{
    [Theory]
    [InlineData("GNOME", true)]
    [InlineData("ubuntu:GNOME", true)]
    [InlineData("GNOME-Classic:GNOME", true)]
    [InlineData("gnome", true)]
    [InlineData("KDE", false)]
    [InlineData("sway", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsGnome_ReadsTheColonList(string? desktop, bool expected)
        => Assert.Equal(expected, LinuxCapturePolicy.IsGnome(desktop));

    [Theory]
    [InlineData(ScreenCastState.Idle)]
    [InlineData(ScreenCastState.Opening)]
    [InlineData(ScreenCastState.Live)]
    [InlineData(ScreenCastState.Denied)]
    [InlineData(ScreenCastState.Failed)]
    [InlineData(ScreenCastState.Unsupported)]
    public void Gnome_NeverUsesTheScreenshotPortal(ScreenCastState state)
        => Assert.False(LinuxCapturePolicy.ScreenshotPortalAllowed(isGnome: true, isBackground: false, state, out _));

    [Theory]
    [InlineData(ScreenCastState.Idle)]
    [InlineData(ScreenCastState.Opening)]
    [InlineData(ScreenCastState.Live)]
    [InlineData(ScreenCastState.Denied)]
    [InlineData(ScreenCastState.Failed)]
    public void OtherDesktops_RefuseWhileTheScreencastCanWork(ScreenCastState state)
        => Assert.False(LinuxCapturePolicy.ScreenshotPortalAllowed(isGnome: false, isBackground: false, state, out _));

    [Fact]
    public void OtherDesktops_UseThePortalOnlyWhenScreencastIsUnsupported()
        => Assert.True(LinuxCapturePolicy.ScreenshotPortalAllowed(
            isGnome: false, isBackground: false, ScreenCastState.Unsupported, out _));

    [Fact]
    public void BackgroundCapture_NeverUsesThePortal()
        => Assert.False(LinuxCapturePolicy.ScreenshotPortalAllowed(
            isGnome: false, isBackground: true, ScreenCastState.Unsupported, out _));

    [Theory]
    [InlineData(ScreenCastState.Idle, true)]
    [InlineData(ScreenCastState.Denied, true)]  // pressing PrintScreen again retries
    [InlineData(ScreenCastState.Failed, true)]
    [InlineData(ScreenCastState.Opening, true)]
    [InlineData(ScreenCastState.Live, false)]
    [InlineData(ScreenCastState.Unsupported, false)]
    public void WaylandNotAllowed_AsksForTheShareFirst(ScreenCastState state, bool expected)
        => Assert.Equal(expected, LinuxCapturePolicy.AskForScreenShareFirst(isWayland: true, instantCaptureAllowed: false, state));

    [Fact]
    public void Allowed_CapturesAtOnce()
        => Assert.False(LinuxCapturePolicy.AskForScreenShareFirst(isWayland: true, instantCaptureAllowed: true, ScreenCastState.Idle));

    [Fact]
    public void X11_NeverAsks()
        => Assert.False(LinuxCapturePolicy.AskForScreenShareFirst(isWayland: false, instantCaptureAllowed: false, ScreenCastState.Unsupported));
}
