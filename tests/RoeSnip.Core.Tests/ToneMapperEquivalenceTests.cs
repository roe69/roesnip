using System.Diagnostics;
using RoeSnip.Core.Capture;
using RoeSnip.Core.Color;
using Xunit;
using Xunit.Abstractions;

namespace RoeSnip.Core.Tests;

/// <summary>Ported from tests/RoeSnip.Tests/ToneMapperEquivalenceTests.cs (item 10). The hard
/// constraint of the r5-latency tone-map optimization: the optimized ToneMapper.MapToSdr must
/// produce BYTE-IDENTICAL output to ToneMapper.MapToSdrScalar (the original implementation, kept
/// as the reference) for every input — pass-through frames, shoulder frames, and adversarial raw
/// FP16 bit patterns (NaN/Inf/denormals/negatives) alike. All frames are randomized with fixed
/// seeds so failures reproduce. One mechanical change from the WPF original: every synthetic
/// CapturedFrame supplies <c>sdrWhiteInBufferUnits: sdrWhiteNits / 80.0</c> (the Windows scRGB
/// convention Core generalizes from — see ToneMapper's class doc), and MonitorInfo uses Core's
/// BackendKey/Scale shape instead of a raw HMONITOR.</summary>
public class ToneMapperEquivalenceTests
{
    private readonly ITestOutputHelper _output;

    public ToneMapperEquivalenceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static MonitorInfo MakeMonitor(double sdrWhiteNits, double maxLuminanceNits) =>
        new(
            Index: 0,
            DeviceName: @"\\.\TESTDISPLAY",
            BackendKey: "0x0",
            BoundsPx: new RectPhysical(0, 0, 1, 1),
            DpiX: 96,
            DpiY: 96,
            Scale: 1.0,
            AdvancedColorActive: true,
            SdrWhiteNits: sdrWhiteNits,
            MaxLuminanceNits: maxLuminanceNits,
            IsPrimary: true);

    /// <summary>Builds an FP16 frame directly from per-channel Half values so denormals and raw
    /// bit patterns survive exactly as specified (no float round-trip).</summary>
    private static CapturedFrame MakeFrame(
        int width, int height, MonitorInfo monitor, Func<int, int, int, ushort> halfBitsFn, int extraStridePadding = 0)
    {
        int stride = width * 8 + extraStridePadding;
        var bytes = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                for (int c = 0; c < 4; c++)
                {
                    ushort bits = halfBitsFn(x, y, c);
                    int o = y * stride + x * 8 + c * 2;
                    bytes[o] = (byte)bits;
                    bytes[o + 1] = (byte)(bits >> 8);
                }
            }
        }
        return new CapturedFrame(
            FrameFormat.Fp16ScRgb, width, height, stride, bytes, monitor,
            sdrWhiteInBufferUnits: monitor.SdrWhiteNits / 80.0);
    }

    private static ushort Bits(float value) => BitConverter.HalfToUInt16Bits((Half)value);

    private static void AssertByteIdentical(CapturedFrame frame, ToneMapOptions opts)
    {
        var optimized = ToneMapper.MapToSdr(frame, opts);
        var scalar = ToneMapper.MapToSdrScalar(frame, opts);
        Assert.Equal(scalar.Pixels, optimized.Pixels);
    }

    [Theory]
    [InlineData(80.0, 61, 23)]   // odd width => SIMD tail coverage (61*4 = 244 halves, not a multiple of 16)
    [InlineData(240.0, 64, 16)]
    [InlineData(240.0, 3, 1)]    // narrower than one SIMD block => pure scalar-tail rows
    public void PassThroughFrame_RandomSdrValues_ByteIdentical(double sdrWhiteNits, int width, int height)
    {
        var monitor = MakeMonitor(sdrWhiteNits, 1000.0);
        var rnd = new Random(4242);
        // Values whose post-scale max stays <= 1.0 => whole-frame exact pass-through branch.
        float ceiling = (float)(sdrWhiteNits / 80.0);
        var frame = MakeFrame(width, height, monitor,
            (_, _, _) => Bits((float)rnd.NextDouble() * ceiling * 0.999f));

        AssertByteIdentical(frame, new ToneMapOptions());
    }

    [Theory]
    [InlineData(80.0, 61, 23, null, null)]
    [InlineData(240.0, 61, 23, null, null)]
    [InlineData(80.0, 64, 32, 0.75, 4.0)]  // knee/peak overrides
    [InlineData(80.0, 64, 32, 0.99, null)]
    public void ShoulderFrame_RandomHdrValues_ByteIdentical(
        double sdrWhiteNits, int width, int height, double? knee, double? peakOverride)
    {
        var monitor = MakeMonitor(sdrWhiteNits, 1000.0);
        var rnd = new Random(1337);
        // ~70% SDR-range values, ~30% HDR highlights up to 6.0 post-scale: exercises both the
        // per-pixel pass-through and the shoulder/dither branches within a shoulder-mode frame.
        float scaleToScRgb = (float)(sdrWhiteNits / 80.0);
        var frame = MakeFrame(width, height, monitor, (_, _, c) =>
        {
            if (c == 3) return Bits(1.0f); // alpha
            double v = rnd.NextDouble() < 0.7
                ? rnd.NextDouble() * 1.05
                : 1.0 + rnd.NextDouble() * 5.0;
            return Bits((float)(v * scaleToScRgb));
        });

        var opts = knee is null ? new ToneMapOptions(PeakOverride: peakOverride)
                                : new ToneMapOptions(Knee: knee.Value, PeakOverride: peakOverride);
        AssertByteIdentical(frame, opts);
    }

    [Fact]
    public void AdversarialRawBitPatterns_IncludingNaNInfDenormalsNegatives_ByteIdentical()
    {
        var monitor = MakeMonitor(240.0, 1000.0);
        var rnd = new Random(90210);
        // Fully random 16-bit patterns: uniformly hits negatives, denormals, Inf and NaN in every
        // channel (including alpha), plus a padded stride to prove row indexing stays honest.
        var frame = MakeFrame(61, 23, monitor,
            (_, _, _) => (ushort)rnd.Next(0, 65536), extraStridePadding: 24);

        AssertByteIdentical(frame, new ToneMapOptions());
    }

    [Fact]
    public void NaNConfinedToSingleChannels_ByteIdentical()
    {
        // A NaN in ANY channel makes the scalar reference discard that whole pixel from the M
        // scan — this frame plants isolated NaNs among otherwise-bright HDR pixels so a lane-wise
        // SIMD max that kept the non-NaN channels alive would compute a different M (and thus
        // different bytes) than the reference.
        var monitor = MakeMonitor(80.0, 1000.0);
        var rnd = new Random(555);
        var frame = MakeFrame(64, 8, monitor, (x, y, c) =>
        {
            if (c == 3) return Bits(1.0f);
            if ((x + y * 64) % 17 == 0 && c == 1) return 0x7E01; // NaN in G only
            return Bits((float)(rnd.NextDouble() * 4.0));
        });

        AssertByteIdentical(frame, new ToneMapOptions());
    }

    [Fact]
    public void MJustAboveAndBelowPassThroughEpsilon_ByteIdentical()
    {
        var monitor = MakeMonitor(80.0, 1000.0);
        foreach (float peakValue in new[] { 1.0f, 1.0005f, 1.002f, 1.5f })
        {
            var rnd = new Random(31);
            float captured = peakValue; // avoid modified-closure surprises
            var frame = MakeFrame(33, 9, monitor, (x, y, c) =>
                x == 0 && y == 0 && c == 0 ? Bits(captured) : Bits((float)rnd.NextDouble()));
            AssertByteIdentical(frame, new ToneMapOptions());
        }
    }

    [Fact]
    public void EdrConvention_SdrWhiteInBufferUnitsOne_ByteIdentical()
    {
        // Core-only case (no WPF equivalent): the macOS-EDR-style convention, where 1.0 buffer
        // unit IS SDR white (SdrWhiteInBufferUnits = 1.0) rather than Windows scRGB's
        // 80-nits-per-unit rule. Proves the optimized path's LUT keying (which is built per-scale,
        // i.e. per-1.0/SdrWhiteInBufferUnits) stays byte-identical to the scalar reference under
        // that generalized convention too, not just the Windows-equivalent one covered above.
        var monitor = MakeMonitor(240.0, 1000.0);
        var rnd = new Random(7331);
        var bytes = new byte[2 * 8 * 4];
        for (int x = 0; x < 4; x++)
        {
            for (int c = 0; c < 4; c++)
            {
                double v = c == 3 ? 1.0 : rnd.NextDouble() * 3.0;
                ushort bits = Bits((float)v);
                int o = x * 8 + c * 2;
                bytes[o] = (byte)bits;
                bytes[o + 1] = (byte)(bits >> 8);
            }
        }
        var frame = new CapturedFrame(
            FrameFormat.Fp16ScRgb, 4, 1, 4 * 8, bytes, monitor, sdrWhiteInBufferUnits: 1.0);

        AssertByteIdentical(frame, new ToneMapOptions());
    }

    [Fact]
    public void FullSizeHdrFrame_ByteIdentical_AndReportsSpeed()
    {
        // Full 1440p-sized frame: the honest before/after measurement for the r5-latency work,
        // and a large-surface equivalence check in one. Timings go to test output.
        var monitor = MakeMonitor(240.0, 1000.0);
        var rnd = new Random(2026);
        var frame = MakeFrame(2560, 1440, monitor, (_, _, c) =>
        {
            if (c == 3) return Bits(1.0f);
            double v = rnd.NextDouble() < 0.9 ? rnd.NextDouble() * 3.0 : 3.0 + rnd.NextDouble() * 9.0;
            return Bits((float)v);
        });

        var opts = new ToneMapOptions();

        var scalarWatch = Stopwatch.StartNew();
        var scalar = ToneMapper.MapToSdrScalar(frame, opts);
        scalarWatch.Stop();

        var optimizedWatch = Stopwatch.StartNew();
        var optimized = ToneMapper.MapToSdr(frame, opts);
        optimizedWatch.Stop();

        _output.WriteLine($"2560x1440 HDR frame: scalar {scalarWatch.ElapsedMilliseconds} ms, optimized {optimizedWatch.ElapsedMilliseconds} ms");
        Assert.Equal(scalar.Pixels, optimized.Pixels);
    }

    [Fact]
    public void ReuseOutputOverload_ByteIdentical_ToFreshAllocation()
    {
        // item 20 (recording cadence) depends on the reuseOutput overload allocating nothing per
        // frame; this proves it also stays byte-identical to the null-buffer (fresh-allocation)
        // call, not just to the scalar reference.
        var monitor = MakeMonitor(240.0, 1000.0);
        var rnd = new Random(99);
        var frame = MakeFrame(64, 16, monitor, (_, _, c) =>
        {
            if (c == 3) return Bits(1.0f);
            double v = rnd.NextDouble() < 0.7 ? rnd.NextDouble() : 1.0 + rnd.NextDouble() * 4.0;
            return Bits((float)v);
        });

        var fresh = ToneMapper.MapToSdr(frame, new ToneMapOptions());
        var reused = new byte[64 * 4 * 16];
        var viaReuse = ToneMapper.MapToSdr(frame, new ToneMapOptions(), reused);

        Assert.Same(reused, viaReuse.Pixels);
        Assert.Equal(fresh.Pixels, viaReuse.Pixels);
    }
}
