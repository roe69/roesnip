using RoeSnip.Core.Capture;
using RoeSnip.Core.Recording;
using Xunit;

namespace RoeSnip.Core.Tests;

/// <summary>RawFrameEquality.RawFramesEqual — the encoder-thread pre-tonemap dedupe check, ported
/// verbatim from the WPF app's RecordingController.RawFramesEqual.</summary>
public class RawFrameEqualityTests
{
    private static MonitorInfo Monitor() => new(
        Index: 0, DeviceName: @"\\.\DISPLAY1", BackendKey: "0x0",
        BoundsPx: RectPhysical.FromSize(0, 0, 100, 100),
        DpiX: 96, DpiY: 96, Scale: 1.0, AdvancedColorActive: false,
        SdrWhiteNits: 240.0, MaxLuminanceNits: 1000.0, IsPrimary: true);

    private static CapturedFrame Frame(byte[] pixels, int width = 2, int height = 2, FrameFormat format = FrameFormat.Bgra8Srgb) =>
        new(format, width, height, width * 4, pixels, Monitor(), sdrWhiteInBufferUnits: 1.0);

    [Fact]
    public void IdenticalPixels_AreEqual()
    {
        byte[] pixels = { 1, 2, 3, 255, 4, 5, 6, 255, 7, 8, 9, 255, 10, 11, 12, 255 };
        var a = Frame((byte[])pixels.Clone());
        var b = Frame((byte[])pixels.Clone());
        Assert.True(RawFrameEquality.RawFramesEqual(a, b));
    }

    [Fact]
    public void OneDifferentByte_AreNotEqual()
    {
        byte[] pixelsA = { 1, 2, 3, 255, 4, 5, 6, 255, 7, 8, 9, 255, 10, 11, 12, 255 };
        byte[] pixelsB = (byte[])pixelsA.Clone();
        pixelsB[15] = 200; // last row's last byte
        var a = Frame(pixelsA);
        var b = Frame(pixelsB);
        Assert.False(RawFrameEquality.RawFramesEqual(a, b));
    }

    [Fact]
    public void DifferentDimensions_AreNotEqual()
    {
        var a = Frame(new byte[16], width: 2, height: 2);
        var b = Frame(new byte[32], width: 4, height: 2);
        Assert.False(RawFrameEquality.RawFramesEqual(a, b));
    }

    [Fact]
    public void DifferentFormats_AreNotEqual()
    {
        var a = Frame(new byte[16], format: FrameFormat.Bgra8Srgb);
        var b = Frame(new byte[16], format: FrameFormat.Fp16ScRgb); // same byte length coincidentally, still counts as changed
        Assert.False(RawFrameEquality.RawFramesEqual(a, b));
    }
}
