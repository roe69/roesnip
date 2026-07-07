using System;
using System.IO;
using RoeSnip.Capture;
using RoeSnip.Imaging;
using Vortice.WIC;
using Xunit;
using WicPixelFormat = Vortice.WIC.PixelFormat;

namespace RoeSnip.Tests;

/// <summary>The acceptance gate DESIGN.md requires before trusting an encoder for HDR export:
/// encode an FP16 scRGB buffer containing a value of 3.0, decode it back, and assert a value
/// &gt; 1.0 survives (i.e. the encoder did not flatten/clip to an 8-bit [0,1] SDR format).
///
/// This test was actually run against both candidates (not guessed): WPF's
/// <c>WmpBitmapEncoder</c>/<c>BitmapDecoder</c> pair FAILS — WPF flattens JXR to 32bpp Pbgra32 on
/// both encode and decode regardless of the source pixel format supplied to it (a 3.0 value came
/// back as ~0.78). Talking to WIC directly (namespace <see cref="Vortice.WIC"/>, from the
/// <c>Vortice.Direct2D1</c> package which bundles the WIC bindings — see PLAN.md's "Plan-time
/// flags" §1) with pixel format <see cref="WicPixelFormat.Format128bppRGBAFloat"/> DOES preserve
/// the float headroom, so <see cref="JxrWriter"/> and this test's decode helper both bypass WPF's
/// imaging stack entirely.</summary>
public class JxrRoundTripTests
{
    private static MonitorInfo MakeMonitor(double sdrWhiteNits = 240.0, double maxLuminanceNits = 1000.0) =>
        new(
            Index: 0,
            DeviceName: @"\\.\TESTDISPLAY",
            HMonitor: 0,
            BoundsPx: new RectPhysical(0, 0, 1, 1),
            DpiX: 96,
            DpiY: 96,
            AdvancedColorActive: false,
            SdrWhiteNits: sdrWhiteNits,
            MaxLuminanceNits: maxLuminanceNits,
            IsPrimary: true);

    private static CapturedFrame MakeFp16Frame(
        int width, int height, Func<int, int, (float r, float g, float b, float a)> pixelFn,
        MonitorInfo monitor, int? strideOverride = null)
    {
        int tightStride = width * 8;
        int stride = strideOverride ?? tightStride;
        var bytes = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var (r, g, b, a) = pixelFn(x, y);
                int o = y * stride + x * 8;
                BitConverter.GetBytes((Half)r).CopyTo(bytes, o);
                BitConverter.GetBytes((Half)g).CopyTo(bytes, o + 2);
                BitConverter.GetBytes((Half)b).CopyTo(bytes, o + 4);
                BitConverter.GetBytes((Half)a).CopyTo(bytes, o + 6);
            }
        }
        return new CapturedFrame(FrameFormat.Fp16ScRgb, width, height, stride, bytes, monitor);
    }

    /// <summary>Decodes a .jxr file back to a float RGBA buffer via raw WIC (the same imaging
    /// stack <see cref="JxrWriter"/> uses to encode) — WPF's decoder cannot be used here; it
    /// flattens JXR float content to 32bpp regardless of what's actually in the file.</summary>
    private static (float R, float G, float B) DecodePixel(string path, int x, int y)
    {
        using var factory = new IWICImagingFactory();
        using var decoder = factory.CreateDecoderFromFileName(path, System.IO.FileAccess.Read, DecodeOptions.CacheOnDemand);
        using var frame = decoder.GetFrame(0);
        frame.GetSize(out uint width, out uint height);

        using var converter = factory.CreateFormatConverter();
        converter.Initialize(frame, WicPixelFormat.Format128bppRGBAFloat, BitmapDitherType.None, null, 0.0, BitmapPaletteType.Custom);

        int stride = (int)width * 16;
        var buffer = new byte[stride * (int)height];
        converter.CopyPixels((uint)stride, buffer);

        int o = y * stride + x * 16;
        float r = BitConverter.ToSingle(buffer, o);
        float g = BitConverter.ToSingle(buffer, o + 4);
        float b = BitConverter.ToSingle(buffer, o + 8);
        return (r, g, b);
    }

    [Fact]
    public void Write_Fp16FrameWithHeadroom_SurvivesRoundTripAboveOne()
    {
        var monitor = MakeMonitor(sdrWhiteNits: 80.0); // scale = 1.0, so scRGB value == ReadPixelScRgb value
        var frame = MakeFp16Frame(1, 1, (_, _) => (3.0f, 3.0f, 3.0f, 1.0f), monitor);

        string path = Path.Combine(Path.GetTempPath(), $"roesnip_jxr_test_{Guid.NewGuid():N}.jxr");
        try
        {
            JxrWriter.Write(path, frame, new RectPhysical(0, 0, 1, 1));

            Assert.True(File.Exists(path));
            var (r, g, b) = DecodePixel(path, 0, 0);

            // The acceptance gate: a value clearly above 1.0 must survive (not clipped/flattened).
            Assert.True(r > 1.0f, $"Expected decoded R > 1.0, got {r}");
            Assert.True(g > 1.0f, $"Expected decoded G > 1.0, got {g}");
            Assert.True(b > 1.0f, $"Expected decoded B > 1.0, got {b}");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Write_RespectsCropRect_OnlyWritesSelectedRegion()
    {
        var monitor = MakeMonitor(sdrWhiteNits: 80.0);
        // 2x2 frame: top-left pixel is bright (3.0), the rest are dark (0.1). Crop to just the
        // bottom-right pixel and verify the written file reflects the crop, not the whole frame.
        var frame = MakeFp16Frame(2, 2, (x, y) => (x == 0 && y == 0) ? (3.0f, 3.0f, 3.0f, 1.0f) : (0.1f, 0.1f, 0.1f, 1.0f), monitor);

        string path = Path.Combine(Path.GetTempPath(), $"roesnip_jxr_test_{Guid.NewGuid():N}.jxr");
        try
        {
            JxrWriter.Write(path, frame, RectPhysical.FromSize(1, 1, 1, 1));

            var (r, _, _) = DecodePixel(path, 0, 0);
            Assert.True(r < 1.0f, $"Expected the cropped (dark) pixel, got R={r}");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Write_RespectsNonTightStride_DoesNotCorruptPixels()
    {
        var monitor = MakeMonitor(sdrWhiteNits: 80.0);
        int paddedStride = 2 * 8 + 32; // extra padding beyond width*8
        var frame = MakeFp16Frame(2, 2, (x, y) => (x == 0 && y == 0) ? (3.0f, 3.0f, 3.0f, 1.0f) : (0f, 0f, 0f, 1.0f),
            monitor, strideOverride: paddedStride);

        string path = Path.Combine(Path.GetTempPath(), $"roesnip_jxr_test_{Guid.NewGuid():N}.jxr");
        try
        {
            JxrWriter.Write(path, frame, new RectPhysical(0, 0, 2, 2));

            var (r00, _, _) = DecodePixel(path, 0, 0);
            var (r10, _, _) = DecodePixel(path, 1, 0);
            var (r01, _, _) = DecodePixel(path, 0, 1);

            Assert.True(r00 > 1.0f);
            Assert.True(r10 < 0.5f);
            Assert.True(r01 < 0.5f);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Write_Bgra8SrgbFrame_DoesNotThrow_AndProducesFile()
    {
        var monitor = MakeMonitor(sdrWhiteNits: 240.0);
        var frame = new CapturedFrame(FrameFormat.Bgra8Srgb, 1, 1, 4, new byte[] { 200, 150, 100, 255 }, monitor);

        string path = Path.Combine(Path.GetTempPath(), $"roesnip_jxr_test_{Guid.NewGuid():N}.jxr");
        try
        {
            JxrWriter.Write(path, frame, new RectPhysical(0, 0, 1, 1));
            Assert.True(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
