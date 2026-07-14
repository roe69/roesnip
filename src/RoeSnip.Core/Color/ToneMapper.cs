using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Threading.Tasks;
using RoeSnip.Core.Capture;
using RoeSnip.Core.Diagnostics;
using RoeSnip.Core.Imaging;

namespace RoeSnip.Core.Color;

public readonly record struct ToneMapOptions(
    double Knee = 0.90,
    double? PeakOverride = null,   // null => derive: clamp(min(M, MaxLuminanceNits/SdrWhiteNits), 2.0, double.MaxValue)
    double Epsilon = 1e-3           // M <= 1.0 + Epsilon => exact SDR pass-through, no shoulder, no dither
);

/// <summary>The tone-map pipeline. See DESIGN.md "Tone-map pipeline" for the full spec; the exact
/// Hermite shoulder formula is pinned in PLAN.md §3.1 so all implementers (and tests) agree on it.
/// Only accepts Fp16ScRgb frames — Bgra8Srgb frames must go through SdrImage.FromCapturedFrame's
/// passthrough branch instead (do not call MapToSdr on a Bgra8Srgb frame; it throws).
///
/// Ported to Core from the WPF app's src/RoeSnip/Color/ToneMapper.cs (item 10, r5-latency) with
/// exactly one behavioral generalization (PLAN-XPLAT.md §2.5): the pass-1/pass-2 scale step
/// generalizes from the WPF app's hardcoded <c>80.0 / Monitor.SdrWhiteNits</c> to
/// <c>1.0 / frame.SdrWhiteInBufferUnits</c> — algebraically identical on Windows (where
/// SdrWhiteInBufferUnits == SdrWhiteNits / 80.0), and correct for macOS EDR buffers (where it is
/// 1.0). The peak derivation is UNCHANGED — that ratio is real nits-to-nits, not buffer units.
///
/// Two implementations (r5-latency), both ported verbatim from the WPF reference apart from the
/// scale-step generalization above:
/// <list type="bullet">
/// <item><see cref="MapToSdr"/> — the optimized production path. The per-FP16-bit-pattern work
/// (half→float widening, SDR-white scaling, negative clamp, sRGB encode, quantize) is precomputed
/// into two 65536-entry LUTs built with the EXACT same scalar expressions the reference uses, so
/// identity is by construction for every possible input bit pattern (including NaN/Inf/denormals).
/// The frame-max scan (pass 1) is AVX2-vectorized with an exact bit-manipulation FP16→FP32
/// widening (Giesen's algorithm; IEEE conversions are exact, and multiply-by-scale/max are the
/// same single-rounding operations as the scalar code); any 4-pixel block containing a NaN falls
/// back to the scalar per-pixel semantics so NaN propagation matches the reference exactly. Only
/// shoulder-mapped pixels (HDR highlights above the knee — a small minority even in HDR frames)
/// run the full scalar Hermite math, verbatim. The AVX2 path only ever engages when both
/// <see cref="Avx2.IsSupported"/> and <see cref="Vector256.IsHardwareAccelerated"/> are true (never
/// the case on the arm64 macOS target); the scalar fallback below is what actually ships there.</item>
/// <item><see cref="MapToSdrScalar"/> — the original scalar implementation, kept verbatim as the
/// reference. The optimized path must stay BYTE-IDENTICAL to it; ToneMapperEquivalenceTests
/// asserts equality on randomized fixed-seed frames covering the whole-frame pass-through branch,
/// the shoulder branch, and adversarial raw bit patterns.</item>
/// </list></summary>
public static class ToneMapper
{
    public static SdrImage MapToSdr(CapturedFrame frame, ToneMapOptions opts) => MapToSdr(frame, opts, reuseOutput: null);

    /// <summary><paramref name="reuseOutput"/>: recording-cadence call sites (up to 50fps) pass a
    /// persistent exactly-sized buffer so the tone-map allocates nothing per frame (canvas-sized
    /// arrays are LOH-sized; per-frame LOH churn = Gen2 pauses = frozen UI hit-testing, the
    /// f7aa9a3 lesson). Those calls also skip the per-call timing log — 50 synchronous stderr
    /// writes a second is its own overhead; one-shot captures (null) keep it.</summary>
    public static SdrImage MapToSdr(CapturedFrame frame, ToneMapOptions opts, byte[]? reuseOutput)
    {
        EnsureFp16(frame);
        if (reuseOutput is not null)
        {
            return MapToSdrOptimized(frame, opts, reuseOutput);
        }
        // Timing log (r5-latency instrumentation): per-frame tonemap cost is measurable from the
        // CLI (--capture) without launching the tray app; RunCaptureFlowAsync's aggregate
        // "capture-to-overlay" line remains the interactive-flow number.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var image = MapToSdrOptimized(frame, opts, null);
        FileLog.Write($"RoeSnip: tonemap {frame.Width}x{frame.Height} {watch.ElapsedMilliseconds} ms");
        return image;
    }

    /// <summary>The original scalar tone-map, kept as the byte-exact reference implementation for
    /// the optimized path (and for the equivalence tests). Do not "optimize" this method — its
    /// exact operations and their order define what correct output is.</summary>
    public static SdrImage MapToSdrScalar(CapturedFrame frame, ToneMapOptions opts)
    {
        EnsureFp16(frame);

        int width = frame.Width;
        int height = frame.Height;
        float scale = (float)(1.0 / frame.SdrWhiteInBufferUnits);

        // ---- Pass 1: M = max over all pixels and channels (post-scale, negatives clamped to 0). ----
        float globalMax = 0f;
        var lockObj = new object();
        Parallel.For(0, height,
            () => 0f,
            (y, _, localMax) =>
            {
                var row = frame.Row(y);
                for (int x = 0; x < width; x++)
                {
                    int o = x * 8;
                    float r = MathF.Max(0f, (float)BitConverter.ToHalf(row.Slice(o, 2)) * scale);
                    float g = MathF.Max(0f, (float)BitConverter.ToHalf(row.Slice(o + 2, 2)) * scale);
                    float b = MathF.Max(0f, (float)BitConverter.ToHalf(row.Slice(o + 4, 2)) * scale);
                    float m = MathF.Max(r, MathF.Max(g, b));
                    if (m > localMax) localMax = m;
                }
                return localMax;
            },
            localMax =>
            {
                lock (lockObj)
                {
                    if (localMax > globalMax) globalMax = localMax;
                }
            });

        var (knee, peak, passThroughWholeFrame) = ComputeCurveParams(globalMax, opts, frame.Monitor);

        var output = new byte[width * 4 * height];

        // ---- Pass 2: map to BGRA8 sRGB. ----
        Parallel.For(0, height, y =>
        {
            var row = frame.Row(y);
            int rowOut = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int o = x * 8;
                float r = MathF.Max(0f, (float)BitConverter.ToHalf(row.Slice(o, 2)) * scale);
                float g = MathF.Max(0f, (float)BitConverter.ToHalf(row.Slice(o + 2, 2)) * scale);
                float b = MathF.Max(0f, (float)BitConverter.ToHalf(row.Slice(o + 4, 2)) * scale);

                byte outR, outG, outB;

                float pixelMax = MathF.Max(r, MathF.Max(g, b));
                bool shoulderPixel = !passThroughWholeFrame && pixelMax > knee;

                if (!shoulderPixel)
                {
                    outR = ColorMath.QuantizeRoundNearest(ColorMath.SrgbEncode(Math.Clamp(r, 0f, 1f)));
                    outG = ColorMath.QuantizeRoundNearest(ColorMath.SrgbEncode(Math.Clamp(g, 0f, 1f)));
                    outB = ColorMath.QuantizeRoundNearest(ColorMath.SrgbEncode(Math.Clamp(b, 0f, 1f)));
                }
                else
                {
                    double mClamped = Math.Min(pixelMax, peak);
                    double t = (mClamped - knee) / (peak - knee);
                    double h00 = 2 * t * t * t - 3 * t * t + 1;
                    double h10 = t * t * t - 2 * t * t + t;
                    double h01 = -2 * t * t * t + 3 * t * t;
                    double f = h00 * knee + h10 * (peak - knee) + h01 * 1.0;
                    float factor = (float)(f / pixelMax);

                    float rr = Math.Clamp(r * factor, 0f, 1f);
                    float gg = Math.Clamp(g * factor, 0f, 1f);
                    float bb = Math.Clamp(b * factor, 0f, 1f);

                    float dOff = Dither.Offset01(x, y);

                    outR = ColorMath.QuantizeRoundNearest(ColorMath.SrgbEncode(rr) + dOff);
                    outG = ColorMath.QuantizeRoundNearest(ColorMath.SrgbEncode(gg) + dOff);
                    outB = ColorMath.QuantizeRoundNearest(ColorMath.SrgbEncode(bb) + dOff);
                }

                int po = rowOut + x * 4;
                output[po + 0] = outB;
                output[po + 1] = outG;
                output[po + 2] = outR;
                output[po + 3] = 255;
            }
        });

        return new SdrImage(width, height, output);
    }

    private static void EnsureFp16(CapturedFrame frame)
    {
        if (frame.Format != FrameFormat.Fp16ScRgb)
        {
            throw new InvalidOperationException(
                "ToneMapper.MapToSdr only accepts Fp16ScRgb frames. Bgra8Srgb frames must go " +
                "through SdrImage.FromCapturedFrame's passthrough branch instead.");
        }
    }

    /// <summary>The knee/peak sanitization + whole-frame pass-through decision, shared verbatim by
    /// the scalar reference and the optimized path so both always derive identical curve
    /// parameters from an identical M. Defensive sanitization (never trust options — audit finding
    /// F): a knee/peak pair where PeakOverride == Knee divides by zero downstream (t = (m - knee) /
    /// (peak - knee)), producing NaN that Math.Clamp does NOT clamp, which renders HDR highlights
    /// as solid black. A Knee greater than peak silently hard-clips the entire frame — exactly the
    /// failure mode this app exists to prevent. Both are sanitized here regardless of what
    /// SettingsWindow already validates, since ToneMapOptions can also be constructed directly
    /// (tests, future callers) without going through that UI.</summary>
    private static (double Knee, double Peak, bool PassThroughWholeFrame) ComputeCurveParams(
        float globalMax, ToneMapOptions opts, MonitorInfo monitor)
    {
        double m0 = globalMax;

        double knee = double.IsFinite(opts.Knee) ? Math.Clamp(opts.Knee, 0.5, 0.99) : 0.90;

        double derivedPeak = Math.Clamp(
            Math.Min(m0, monitor.MaxLuminanceNits / monitor.SdrWhiteNits),
            2.0, double.MaxValue);
        // A PeakOverride is only honored if it's finite AND actually above the (already-sanitized)
        // knee — anything else (non-finite, negative, or <= knee) is "absurd" and the override is
        // ignored entirely in favor of the derived peak, rather than letting a bogus value survive
        // into the Math.Max floor below.
        double peak = opts.PeakOverride is { } peakOverride && double.IsFinite(peakOverride) && peakOverride > knee
            ? peakOverride
            : derivedPeak;
        // Final safety net regardless of which path produced peak above: force at least a minimal
        // shoulder width so peak can never equal (or fall below) knee.
        peak = Math.Max(peak, knee + 0.05);

        bool passThroughWholeFrame = m0 <= 1.0 + opts.Epsilon;
        return (knee, peak, passThroughWholeFrame);
    }

    // ================= Optimized path (r5-latency) =================

    private static SdrImage MapToSdrOptimized(CapturedFrame frame, ToneMapOptions opts, byte[]? reuseOutput)
    {
        int width = frame.Width;
        int height = frame.Height;
        float scale = (float)(1.0 / frame.SdrWhiteInBufferUnits);
        ToneLut lut = GetLut(scale);

        float globalMax = ComputeGlobalMax(frame, scale, lut.Linear);
        var (knee, peak, passThroughWholeFrame) = ComputeCurveParams(globalMax, opts, frame.Monitor);

        var output = reuseOutput ?? new byte[width * 4 * height];

        Parallel.For(0, height, y =>
        {
            var halves = MemoryMarshal.Cast<byte, ushort>(frame.Row(y));
            int rowOut = y * width * 4;

            if (passThroughWholeFrame)
            {
                // The common (pure-SDR) case: every pixel is a straight per-channel table lookup.
                for (int x = 0; x < width; x++)
                {
                    int o = x * 4;
                    int po = rowOut + o;
                    output[po + 0] = lut.Encoded[halves[o + 2]]; // B
                    output[po + 1] = lut.Encoded[halves[o + 1]]; // G
                    output[po + 2] = lut.Encoded[halves[o]];     // R
                    output[po + 3] = 255;
                }
                return;
            }

            for (int x = 0; x < width; x++)
            {
                int o = x * 4;
                float r = lut.Linear[halves[o]];
                float g = lut.Linear[halves[o + 1]];
                float b = lut.Linear[halves[o + 2]];

                float pixelMax = MathF.Max(r, MathF.Max(g, b));
                bool shoulderPixel = pixelMax > knee;

                byte outR, outG, outB;
                if (!shoulderPixel)
                {
                    outR = lut.Encoded[halves[o]];
                    outG = lut.Encoded[halves[o + 1]];
                    outB = lut.Encoded[halves[o + 2]];
                }
                else
                {
                    // Shoulder-mapped pixels run the reference math verbatim (the factor depends
                    // on the per-pixel max, so it cannot be tabulated) — inputs r/g/b are exact by
                    // LUT construction, so outputs are byte-identical to the scalar path.
                    double mClamped = Math.Min(pixelMax, peak);
                    double t = (mClamped - knee) / (peak - knee);
                    double h00 = 2 * t * t * t - 3 * t * t + 1;
                    double h10 = t * t * t - 2 * t * t + t;
                    double h01 = -2 * t * t * t + 3 * t * t;
                    double f = h00 * knee + h10 * (peak - knee) + h01 * 1.0;
                    float factor = (float)(f / pixelMax);

                    float rr = Math.Clamp(r * factor, 0f, 1f);
                    float gg = Math.Clamp(g * factor, 0f, 1f);
                    float bb = Math.Clamp(b * factor, 0f, 1f);

                    float dOff = Dither.Offset01(x, y);

                    outR = ColorMath.QuantizeRoundNearest(ColorMath.SrgbEncode(rr) + dOff);
                    outG = ColorMath.QuantizeRoundNearest(ColorMath.SrgbEncode(gg) + dOff);
                    outB = ColorMath.QuantizeRoundNearest(ColorMath.SrgbEncode(bb) + dOff);
                }

                int po = rowOut + x * 4;
                output[po + 0] = outB;
                output[po + 1] = outG;
                output[po + 2] = outR;
                output[po + 3] = 255;
            }
        });

        return new SdrImage(width, height, output);
    }

    /// <summary>Pass 1 (M scan) with an AVX2 fast path. Exactness argument, term by term:
    /// <see cref="HalfToFloatExactAvx2"/> is an exact FP16→FP32 widening (the conversion is always
    /// exact in IEEE754); multiply-by-scale is the identical single-rounding float multiply the
    /// scalar code performs; max against a ≥0 accumulator subsumes the scalar's MathF.Max(0, ·)
    /// negative clamp; and float max is comparison-based (no rounding), so any association order
    /// yields the same M. The two semantic divergences vector max would introduce are excluded
    /// structurally: NaN-containing blocks (vector lane-max keeps other channels of a NaN pixel
    /// alive; the scalar reference discards the whole pixel) fall back to the exact scalar
    /// per-pixel loop, and -0.0 (vector max can keep -0 where scalar Max(0,-0) returns +0) can
    /// never exceed a non-negative localMax, so it never changes M. The vector path only engages
    /// when BOTH Avx2.IsSupported and Vector256.IsHardwareAccelerated are true (per item 10's
    /// runtime-check requirement) — false on the arm64 macOS target, where MaxScalarPixels alone
    /// is the shipped path.</summary>
    private static float ComputeGlobalMax(CapturedFrame frame, float scale, float[] lutLinear)
    {
        int height = frame.Height;
        float globalMax = 0f;
        var lockObj = new object();
        bool useAvx2 = Avx2.IsSupported && Vector256.IsHardwareAccelerated;

        Parallel.For(0, height,
            () => 0f,
            (y, _, localMax) =>
            {
                var halves = MemoryMarshal.Cast<byte, ushort>(frame.Row(y));
                int i = 0;

                if (useAvx2 && halves.Length >= 16)
                {
                    var scaleVec = Vector256.Create(scale);
                    // Zero the alpha lanes ([r g b a r g b a] layout) so alpha never contributes to M.
                    var rgbMask = Vector256.Create(-1, -1, -1, 0, -1, -1, -1, 0).AsSingle();
                    var acc = Vector256<float>.Zero;

                    int simdEnd = halves.Length - 16;
                    for (; i <= simdEnd; i += 16)
                    {
                        var h16 = Vector256.Create<ushort>(halves.Slice(i, 16)); // 4 pixels
                        var lo = Avx.Multiply(HalfToFloatExactAvx2(h16.GetLower()), scaleVec);
                        var hi = Avx.Multiply(HalfToFloatExactAvx2(h16.GetUpper()), scaleVec);

                        var unordered = Avx.Or(
                            Avx.Compare(lo, lo, FloatComparisonMode.UnorderedNonSignaling),
                            Avx.Compare(hi, hi, FloatComparisonMode.UnorderedNonSignaling));
                        if (Avx.MoveMask(unordered) != 0)
                        {
                            // NaN present in this 4-pixel block — use the reference's exact
                            // per-pixel NaN semantics for it.
                            localMax = MaxScalarPixels(halves, i, i + 16, lutLinear, localMax);
                            continue;
                        }

                        acc = Avx.Max(acc, Avx.And(lo, rgbMask));
                        acc = Avx.Max(acc, Avx.And(hi, rgbMask));
                    }

                    for (int lane = 0; lane < 8; lane++)
                    {
                        float v = acc.GetElement(lane);
                        if (v > localMax) localMax = v;
                    }
                }

                return MaxScalarPixels(halves, i, halves.Length, lutLinear, localMax);
            },
            localMax =>
            {
                lock (lockObj)
                {
                    if (localMax > globalMax) globalMax = localMax;
                }
            });

        return globalMax;
    }

    /// <summary>Scalar tail/fallback for pass 1 — identical semantics to the reference's inner
    /// loop (lutLinear[h] ≡ MathF.Max(0f, (float)half * scale) by construction, same MathF.Max
    /// nesting, same NaN discard-the-pixel behavior). <paramref name="start"/>/<paramref name="end"/>
    /// are half indices and always pixel-aligned (multiples of 4).</summary>
    private static float MaxScalarPixels(ReadOnlySpan<ushort> halves, int start, int end, float[] lutLinear, float localMax)
    {
        for (int i = start; i < end; i += 4)
        {
            float r = lutLinear[halves[i]];
            float g = lutLinear[halves[i + 1]];
            float b = lutLinear[halves[i + 2]];
            float m = MathF.Max(r, MathF.Max(g, b));
            if (m > localMax) localMax = m;
        }
        return localMax;
    }

    /// <summary>Exact FP16→FP32 widening for 8 halves at once (Giesen's shift/rebias algorithm —
    /// .NET 8 exposes no F16C intrinsic, so this is the AVX2 equivalent). Exact for all finite
    /// values including denormals (the renormalizing subtract is exact by Sterbenz's lemma);
    /// Inf maps to Inf; NaN stays NaN (payload bits shift, which is irrelevant — callers never
    /// consume NaN lanes from this routine, see ComputeGlobalMax's fallback).</summary>
    private static Vector256<float> HalfToFloatExactAvx2(Vector128<ushort> halves)
    {
        Vector256<int> u = Avx2.ConvertToVector256Int32(halves);                    // zero-extend to dwords
        Vector256<int> sign = Avx2.ShiftLeftLogical(Avx2.And(u, Vector256.Create(0x8000)), 16);
        Vector256<int> em = Avx2.And(u, Vector256.Create(0x7FFF));                  // exponent+mantissa
        Vector256<int> o = Avx2.ShiftLeftLogical(em, 13);
        Vector256<int> shiftedExp = Vector256.Create(0x7C00 << 13);
        Vector256<int> exp = Avx2.And(o, shiftedExp);
        o = Avx2.Add(o, Vector256.Create((127 - 15) << 23));                        // exponent rebias
        // Inf/NaN: force the FP32 exponent field to all-ones.
        Vector256<int> isInfNan = Avx2.CompareEqual(exp, shiftedExp);
        o = Avx2.Add(o, Avx2.And(isInfNan, Vector256.Create((128 - 16) << 23)));
        // Zero/denormal: renormalize via magic subtract (exact).
        Vector256<int> isZeroDenorm = Avx2.CompareEqual(exp, Vector256<int>.Zero);
        Vector256<float> renormed = Avx.Subtract(
            Avx2.Add(o, Vector256.Create(1 << 23)).AsSingle(),
            Vector256.Create(BitConverter.Int32BitsToSingle(113 << 23)));           // 2^-14
        o = Avx2.BlendVariable(o.AsByte(), renormed.AsInt32().AsByte(), isZeroDenorm.AsByte()).AsInt32();
        return Avx2.Or(o, sign).AsSingle();
    }

    // ---- Per-scale LUTs (65536 entries — one per possible FP16 bit pattern) ----

    private sealed class ToneLut
    {
        /// <summary>Linear[h] == MathF.Max(0f, (float)halfBits(h) * scale) — the reference's
        /// post-scale, negative-clamped channel value.</summary>
        public readonly float[] Linear = new float[65536];

        /// <summary>Encoded[h] == QuantizeRoundNearest(SrgbEncode(Clamp(Linear[h], 0, 1))) — the
        /// reference's full pass-through-branch output byte for that channel.</summary>
        public readonly byte[] Encoded = new byte[65536];
    }

    private static readonly object s_lutGate = new();
    private static readonly Dictionary<int, ToneLut> s_lutCache = new();

    /// <summary>Builds (or returns the cached) LUT pair for a scale. Both tables are computed with
    /// the exact scalar expressions the reference path uses, so table lookups are byte-identical
    /// to recomputation by construction. Cached per scale float-bits: scale depends only on the
    /// frame's SdrWhiteInBufferUnits (which itself depends only on the monitor's SDR white level
    /// per-backend), so a handful of entries covers every monitor; the cache is cleared (not
    /// evicted) past a small bound to keep pathological callers (tests with many synthetic SDR
    /// white levels) from growing it without limit.</summary>
    private static ToneLut GetLut(float scale)
    {
        int key = BitConverter.SingleToInt32Bits(scale);
        lock (s_lutGate)
        {
            if (s_lutCache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        var lut = new ToneLut();
        for (int h = 0; h <= ushort.MaxValue; h++)
        {
            float linear = MathF.Max(0f, (float)BitConverter.UInt16BitsToHalf((ushort)h) * scale);
            lut.Linear[h] = linear;
            lut.Encoded[h] = ColorMath.QuantizeRoundNearest(ColorMath.SrgbEncode(Math.Clamp(linear, 0f, 1f)));
        }

        lock (s_lutGate)
        {
            if (s_lutCache.Count >= 8)
            {
                s_lutCache.Clear();
            }
            s_lutCache[key] = lut;
            return lut;
        }
    }
}
