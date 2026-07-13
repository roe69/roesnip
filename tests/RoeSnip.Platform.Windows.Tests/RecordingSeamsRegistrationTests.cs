using RoeSnip.Core.Recording;
using RoeSnip.Core.Recording.Gif;
using Xunit;

namespace RoeSnip.Platform.Windows.Tests;

/// <summary>Same "did the [ModuleInitializer] actually run and gate on the right OS" check
/// BackendRegistrationTests already does for <c>CaptureBackendRegistry</c>, extended to this item's
/// three new registries: on this Windows machine, all three must report a live Windows registrant,
/// not fall through to the graceful-degrade defaults Linux/macOS get.</summary>
public class RecordingSeamsRegistrationTests
{
    [Fact]
    public void RecordingCapabilities_OnWindows_ReportsAllThreeCapabilitiesTrue()
    {
        var capabilities = RecordingCapabilitiesRegistry.ForCurrentPlatform();

        Assert.True(capabilities.SupportsMp4);
        Assert.True(capabilities.SupportsMicrophone);
        Assert.True(capabilities.SupportsLoopback);
    }

    [Fact]
    public void Mp4VideoEncoderRegistry_OnWindows_CreatesAConcreteMp4Encoder()
    {
        string tempPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"roesnip-mp4registry-{System.Guid.NewGuid():N}.mp4");
        try
        {
            using IVideoEncoder encoder = Mp4VideoEncoderRegistry.Create(
                tempPath, width: 64, height: 64, fps: 30, withAudio: false, GifSizePreset.Quality);

            Assert.IsType<Mp4Encoder>(encoder);
            Assert.False(encoder.HasAudio); // withAudio: false above
        }
        finally
        {
            if (System.IO.File.Exists(tempPath))
            {
                System.IO.File.Delete(tempPath);
            }
        }
    }

    [Fact]
    public void AudioCaptureDeviceRegistry_OnWindows_BothFlagsFalse_ReturnsNull()
    {
        // Mirrors AudioCaptureEngine.TryStart's own "nothing to capture" short circuit — must not
        // even attempt device activation when neither source was requested.
        IAudioCaptureDevice? device = AudioCaptureDeviceRegistry.TryStart(microphone: false, systemAudio: false);
        Assert.Null(device);
    }
}
