using RoeSnip.Capture;
using RoeSnip.Color;
using RoeSnip.Imaging;
using Xunit;

namespace RoeSnip.Tests;

public class ToneMapperTests
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
        int width, int height, Func<int, int, (float r, float g, float b, float a)> pixelFn, MonitorInfo monitor, int? strideOverride = null)
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

    private static (byte B, byte G, byte R, byte A) ReadOutputPixel(SdrImage image, int x, int y)
    {
        int o = y * image.Stride + x * 4;
        return (image.Pixels[o + 0], image.Pixels[o + 1], image.Pixels[o + 2], image.Pixels[o + 3]);
    }

    [Fact]
    public void ScRgb3_0_At240Nits_MapsToByte255()
    {
        var monitor = MakeMonitor(sdrWhiteNits: 240.0);
        var frame = MakeFp16Frame(1, 1, (_, _) => (3.0f, 3.0f, 3.0f, 1.0f), monitor);

        var image = ToneMapper.MapToSdr(frame, new ToneMapOptions());
        var (b, g, r, a) = ReadOutputPixel(image, 0, 0);

        Assert.Equal(255, r);
        Assert.Equal(255, g);
        Assert.Equal(255, b);
        Assert.Equal(255, a);
    }

    [Fact]
    public void ScRgb1_5_At240Nits_MapsToByte188()
    {
        var monitor = MakeMonitor(sdrWhiteNits: 240.0);
        var frame = MakeFp16Frame(1, 1, (_, _) => (1.5f, 1.5f, 1.5f, 1.0f), monitor);

        var image = ToneMapper.MapToSdr(frame, new ToneMapOptions());
        var (b, g, r, _) = ReadOutputPixel(image, 0, 0);

        Assert.Equal(188, r);
        Assert.Equal(188, g);
        Assert.Equal(188, b);
    }

    [Fact]
    public void Zero_MapsToByteZero()
    {
        var monitor = MakeMonitor();
        var frame = MakeFp16Frame(1, 1, (_, _) => (0f, 0f, 0f, 1.0f), monitor);

        var image = ToneMapper.MapToSdr(frame, new ToneMapOptions());
        var (b, g, r, _) = ReadOutputPixel(image, 0, 0);

        Assert.Equal(0, r);
        Assert.Equal(0, g);
        Assert.Equal(0, b);
    }

    [Fact]
    public void NegativeScRgb_ClampsToZero()
    {
        var monitor = MakeMonitor();
        var frame = MakeFp16Frame(1, 1, (_, _) => (-1.0f, -5.0f, -0.5f, 1.0f), monitor);

        var image = ToneMapper.MapToSdr(frame, new ToneMapOptions());
        var (b, g, r, _) = ReadOutputPixel(image, 0, 0);

        Assert.Equal(0, r);
        Assert.Equal(0, g);
        Assert.Equal(0, b);
    }

    [Fact]
    public void SdrParity_WholeFrameAtOrBelowOne_MatchesIndependentReference()
    {
        var monitor = MakeMonitor(sdrWhiteNits: 240.0);
        const int width = 5, height = 5;
        float scale = (float)(80.0 / monitor.SdrWhiteNits);

        // Values chosen so that after scaling, every pixel channel is in [0, 1].
        float[,] rIn = new float[width, height];
        float[,] gIn = new float[width, height];
        float[,] bIn = new float[width, height];
        var rnd = new Random(12345);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                rIn[x, y] = (float)rnd.NextDouble() * 3.0f; // *scale (1/3) => up to 1.0
                gIn[x, y] = (float)rnd.NextDouble() * 3.0f;
                bIn[x, y] = (float)rnd.NextDouble() * 3.0f;
            }
        }

        var frame = MakeFp16Frame(width, height, (x, y) => (rIn[x, y], gIn[x, y], bIn[x, y], 1.0f), monitor);
        var image = ToneMapper.MapToSdr(frame, new ToneMapOptions());

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                // Round-trip through Half first: that's the precision the pipeline actually reads
                // back from the FP16 buffer, so the independent reference must match it exactly.
                float rHalf = (float)(Half)rIn[x, y];
                float gHalf = (float)(Half)gIn[x, y];
                float bHalf = (float)(Half)bIn[x, y];

                byte expectedR = ColorMath.QuantizeRoundNearest(ColorMath.SrgbEncode(Math.Clamp(rHalf * scale, 0f, 1f)));
                byte expectedG = ColorMath.QuantizeRoundNearest(ColorMath.SrgbEncode(Math.Clamp(gHalf * scale, 0f, 1f)));
                byte expectedB = ColorMath.QuantizeRoundNearest(ColorMath.SrgbEncode(Math.Clamp(bHalf * scale, 0f, 1f)));

                var (b, g, r, _) = ReadOutputPixel(image, x, y);
                Assert.Equal(expectedR, r);
                Assert.Equal(expectedG, g);
                Assert.Equal(expectedB, b);
            }
        }
    }

    [Fact]
    public void ShoulderContinuity_AroundKnee_ProducesAdjacentBytes()
    {
        const double knee = 0.90;
        var monitor = MakeMonitor(sdrWhiteNits: 80.0, maxLuminanceNits: 1000.0); // scale = 1.0, so scRGB == post-scale linear

        // 3 pixels: [0] forces M well above 1+eps (shoulder mode active for the whole frame),
        // [1] just below the knee (pass-through), [2] just above the knee (shoulder-mapped).
        float justBelow = (float)(knee - 1e-6);
        float justAbove = (float)(knee + 1e-6);
        var frame = MakeFp16Frame(3, 1, (x, _) => x switch
        {
            0 => (2.0f, 2.0f, 2.0f, 1.0f),
            1 => (justBelow, justBelow, justBelow, 1.0f),
            _ => (justAbove, justAbove, justAbove, 1.0f),
        }, monitor);

        var image = ToneMapper.MapToSdr(frame, new ToneMapOptions());
        var (_, _, rBelow, _) = ReadOutputPixel(image, 1, 0);
        var (_, _, rAbove, _) = ReadOutputPixel(image, 2, 0);

        Assert.True(Math.Abs(rAbove - rBelow) <= 1,
            $"Expected adjacent bytes at the knee boundary, got {rBelow} and {rAbove}.");
    }

    [Fact]
    public void Shoulder_AtPeak_MapsToByte255_AndClampsAbove()
    {
        var monitor = MakeMonitor(sdrWhiteNits: 80.0, maxLuminanceNits: 100000.0);
        const double peak = 8.0; // large enough to be unambiguous; MaxLuminanceNits/SdrWhiteNits is huge so peak = M itself.

        var frameAtPeak = MakeFp16Frame(1, 1, (_, _) => ((float)peak, (float)peak, (float)peak, 1.0f), monitor);
        var imageAtPeak = ToneMapper.MapToSdr(frameAtPeak, new ToneMapOptions(PeakOverride: peak));
        var (_, _, rAtPeak, _) = ReadOutputPixel(imageAtPeak, 0, 0);
        Assert.Equal(255, rAtPeak);

        // A second frame where one pixel exceeds the peak: it must clamp to the same byte as peak.
        var frameAbovePeak = MakeFp16Frame(2, 1, (x, _) =>
        {
            float v = x == 0 ? (float)peak : (float)peak * 4;
            return (v, v, v, 1.0f);
        }, monitor);
        var imageAbovePeak = ToneMapper.MapToSdr(frameAbovePeak, new ToneMapOptions(PeakOverride: peak));
        var (_, _, rPeakPixel, _) = ReadOutputPixel(imageAbovePeak, 0, 0);
        var (_, _, rAbovePeakPixel, _) = ReadOutputPixel(imageAbovePeak, 1, 0);
        Assert.Equal(255, rPeakPixel);
        Assert.Equal(rPeakPixel, rAbovePeakPixel);
    }

    [Fact]
    public void MapToSdr_OnBgra8SrgbFrame_Throws()
    {
        var monitor = MakeMonitor();
        var frame = new CapturedFrame(FrameFormat.Bgra8Srgb, 1, 1, 4, new byte[] { 10, 20, 30, 255 }, monitor);
        Assert.Throws<InvalidOperationException>(() => ToneMapper.MapToSdr(frame, new ToneMapOptions()));
    }

    [Fact]
    public void Bgra8SrgbPassthrough_LeavesBytesUnchanged_AndForcesOpaqueAlpha()
    {
        var monitor = MakeMonitor();
        // B=10, G=20, R=30, A=128 (not opaque) — passthrough must force alpha to 255 and leave BGR untouched.
        var frame = new CapturedFrame(FrameFormat.Bgra8Srgb, 1, 1, 4, new byte[] { 10, 20, 30, 128 }, monitor);

        var image = SdrImage.FromCapturedFrame(frame, new ToneMapOptions());
        var (b, g, r, a) = ReadOutputPixel(image, 0, 0);

        Assert.Equal(10, b);
        Assert.Equal(20, g);
        Assert.Equal(30, r);
        Assert.Equal(255, a);
    }

    [Fact]
    public void RespectsNonTightStride_PaddedRowsDoNotCorruptPixels()
    {
        var monitor = MakeMonitor(sdrWhiteNits: 240.0);
        // 2x2 frame with extra row padding beyond width*8, to prove Stride (not Width*BytesPerPixel)
        // is what indexing uses.
        int paddedStride = 2 * 8 + 32;
        var frame = MakeFp16Frame(2, 2, (x, y) => (x == 0 && y == 0) ? (3.0f, 3.0f, 3.0f, 1.0f) : (0f, 0f, 0f, 1.0f),
            monitor, strideOverride: paddedStride);

        var image = ToneMapper.MapToSdr(frame, new ToneMapOptions());
        var (_, _, r00, _) = ReadOutputPixel(image, 0, 0);
        var (_, _, r10, _) = ReadOutputPixel(image, 1, 0);
        var (_, _, r01, _) = ReadOutputPixel(image, 0, 1);

        Assert.Equal(255, r00);
        Assert.Equal(0, r10);
        Assert.Equal(0, r01);
    }
}
