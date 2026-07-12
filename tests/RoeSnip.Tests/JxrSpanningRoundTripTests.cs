using System;
using System.Collections.Generic;
using System.IO;
using RoeSnip.Capture;
using RoeSnip.Imaging;
using Vortice.WIC;
using Xunit;
using WicPixelFormat = Vortice.WIC.PixelFormat;

namespace RoeSnip.Tests;

/// <summary>Cross-monitor selection HDR save (JxrWriter.WriteSpanning): stitching raw scRGB crops
/// from multiple monitors into one .jxr is well-defined because raw scRGB is one absolute linear
/// space (1.0 = 80 nits) regardless of which monitor a pixel came from — see WriteSpanning's own
/// doc comment. These tests build two small synthetic CapturedFrames with distinct, known pixel
/// values, place them at different offsets in a shared virtual canvas via SpanningFrameCrop, and
/// verify the written file's decoded pixels land exactly where expected — including the documented
/// gap fill (opaque linear black) for canvas pixels no crop covers.
///
/// Same WIC-direct decode helper as JxrRoundTripTests (WPF's own JXR decoder flattens float
/// headroom regardless of what's actually in the file — see that class's own doc comment).</summary>
public class JxrSpanningRoundTripTests
{
    private static MonitorInfo MakeMonitor(int index, RectPhysical boundsPx, double sdrWhiteNits = 80.0) =>
        new(
            Index: index,
            DeviceName: $@"\\.\TESTDISPLAY{index}",
            HMonitor: 0,
            BoundsPx: boundsPx,
            DpiX: 96,
            DpiY: 96,
            AdvancedColorActive: false,
            SdrWhiteNits: sdrWhiteNits,
            MaxLuminanceNits: 1000.0,
            IsPrimary: index == 0);

    private static CapturedFrame MakeFp16Frame(
        int width, int height, Func<int, int, (float r, float g, float b, float a)> pixelFn, MonitorInfo monitor)
    {
        int stride = width * 8;
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

    /// <summary>Same raw-WIC decode approach as JxrRoundTripTests.DecodePixel.</summary>
    private static (float R, float G, float B) DecodePixel(string path, int x, int y)
    {
        using var factory = new IWICImagingFactory();
        using var decoder = factory.CreateDecoderFromFileName(path, FileAccess.Read, DecodeOptions.CacheOnDemand);
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
    public void WriteSpanning_TwoMonitorsSideBySide_PlacesEachCropAtItsOwnOffsetWithHeadroomIntact()
    {
        // Monitor A (bright, 3.0 headroom) sits left of Monitor B (dim, 0.1) in the virtual desktop;
        // the spanning selection is the full 2x1 canvas straddling both.
        var monitorA = MakeMonitor(0, new RectPhysical(0, 0, 1, 1));
        var monitorB = MakeMonitor(1, new RectPhysical(1, 0, 2, 1));
        var frameA = MakeFp16Frame(1, 1, (_, _) => (3.0f, 3.0f, 3.0f, 1.0f), monitorA);
        var frameB = MakeFp16Frame(1, 1, (_, _) => (0.1f, 0.1f, 0.1f, 1.0f), monitorB);

        var virtualRect = new RectPhysical(0, 0, 2, 1);
        var crops = new List<SpanningFrameCrop>
        {
            new(frameA, new RectPhysical(0, 0, 1, 1), DestX: 0, DestY: 0),
            new(frameB, new RectPhysical(0, 0, 1, 1), DestX: 1, DestY: 0),
        };

        string path = Path.Combine(Path.GetTempPath(), $"roesnip_jxr_spanning_test_{Guid.NewGuid():N}.jxr");
        try
        {
            JxrWriter.WriteSpanning(path, virtualRect, crops);
            Assert.True(File.Exists(path));

            var (r0, _, _) = DecodePixel(path, 0, 0);
            var (r1, _, _) = DecodePixel(path, 1, 0);

            // The acceptance gate (mirrors JxrRoundTripTests): headroom above 1.0 survives, and each
            // monitor's own crop landed at its own destination offset, not swapped or averaged.
            Assert.True(r0 > 1.0f, $"Expected monitor A's bright pixel (headroom) at (0,0), got R={r0}");
            Assert.True(r1 < 0.5f, $"Expected monitor B's dim pixel at (1,0), got R={r1}");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WriteSpanning_GapNoCropCovers_IsWrittenAsOpaqueLinearBlack()
    {
        var monitorA = MakeMonitor(0, new RectPhysical(0, 0, 1, 1));
        var frameA = MakeFp16Frame(1, 1, (_, _) => (3.0f, 3.0f, 3.0f, 1.0f), monitorA);

        // A 3-pixel-wide virtual canvas but only the LEFT pixel has a contributing crop — mirrors a
        // real gap between non-adjacent monitors, or a monitor whose own selection slice didn't
        // reach as far as the composite's own bounding box.
        var virtualRect = new RectPhysical(0, 0, 3, 1);
        var crops = new List<SpanningFrameCrop>
        {
            new(frameA, new RectPhysical(0, 0, 1, 1), DestX: 0, DestY: 0),
        };

        string path = Path.Combine(Path.GetTempPath(), $"roesnip_jxr_spanning_test_{Guid.NewGuid():N}.jxr");
        try
        {
            JxrWriter.WriteSpanning(path, virtualRect, crops);

            // The JXR (WMP) codec is not bit-exact even at its "lossless" setting — a written 0.0
            // can come back as a tiny non-zero value (observed ~1.5e-5, i.e. within a couple of
            // half-precision ULPs of zero). A tight tolerance still distinguishes "gap, left at the
            // documented black fill" from "a real (bright or dark-but-nonzero) pixel".
            var (rGap, gGap, bGap) = DecodePixel(path, 1, 0);
            const float tolerance = 0.001f;
            Assert.True(Math.Abs(rGap) < tolerance, $"Expected ~0 (gap fill), got R={rGap}");
            Assert.True(Math.Abs(gGap) < tolerance, $"Expected ~0 (gap fill), got G={gGap}");
            Assert.True(Math.Abs(bGap) < tolerance, $"Expected ~0 (gap fill), got B={bGap}");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WriteSpanning_Bgra8SrgbCrop_DoesNotThrow_AndProducesFile()
    {
        // Degenerate case: one of the contributing monitors delivered an SDR-only (Bgra8Srgb) frame
        // rather than Fp16 — CapturedFrame.ReadPixelScRgb decodes it uniformly either way, exactly
        // like the single-monitor JxrWriter.Write already handles this (see
        // JxrRoundTripTests.Write_Bgra8SrgbFrame_DoesNotThrow_AndProducesFile).
        var monitor = MakeMonitor(0, new RectPhysical(0, 0, 1, 1), sdrWhiteNits: 240.0);
        var frame = new CapturedFrame(FrameFormat.Bgra8Srgb, 1, 1, 4, new byte[] { 200, 150, 100, 255 }, monitor);

        var virtualRect = new RectPhysical(0, 0, 1, 1);
        var crops = new List<SpanningFrameCrop> { new(frame, new RectPhysical(0, 0, 1, 1), DestX: 0, DestY: 0) };

        string path = Path.Combine(Path.GetTempPath(), $"roesnip_jxr_spanning_test_{Guid.NewGuid():N}.jxr");
        try
        {
            JxrWriter.WriteSpanning(path, virtualRect, crops);
            Assert.True(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WriteSpanning_CropOutOfBoundsForItsOwnFrame_ThrowsRatherThanSilentlyCorrupting()
    {
        var monitor = MakeMonitor(0, new RectPhysical(0, 0, 1, 1));
        var frame = MakeFp16Frame(1, 1, (_, _) => (1f, 1f, 1f, 1f), monitor);
        var badCrop = new SpanningFrameCrop(frame, new RectPhysical(0, 0, 5, 5), DestX: 0, DestY: 0);

        string path = Path.Combine(Path.GetTempPath(), $"roesnip_jxr_spanning_test_{Guid.NewGuid():N}.jxr");
        try
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => JxrWriter.WriteSpanning(path, new RectPhysical(0, 0, 5, 5), new[] { badCrop }));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
