using System;
using System.Threading.Tasks;
using RoeSnip.Capture;
using RoeSnip.Imaging;

namespace RoeSnip.Color;

public readonly record struct ToneMapOptions(
    double Knee = 0.90,
    double? PeakOverride = null,   // null => derive: clamp(min(M, MaxLuminanceNits/SdrWhiteNits), 2.0, double.MaxValue)
    double Epsilon = 1e-3           // M <= 1.0 + Epsilon => exact SDR pass-through, no shoulder, no dither
);

/// <summary>The tone-map pipeline. See DESIGN.md "Tone-map pipeline" for the full spec; the exact
/// Hermite shoulder formula is pinned in PLAN.md §3.1 so all implementers (and tests) agree on it.
/// Only accepts Fp16ScRgb frames — Bgra8Srgb frames must go through SdrImage.FromCapturedFrame's
/// passthrough branch instead (do not call MapToSdr on a Bgra8Srgb frame; it throws).</summary>
public static class ToneMapper
{
    public static SdrImage MapToSdr(CapturedFrame frame, ToneMapOptions opts)
    {
        if (frame.Format != FrameFormat.Fp16ScRgb)
        {
            throw new InvalidOperationException(
                "ToneMapper.MapToSdr only accepts Fp16ScRgb frames. Bgra8Srgb frames must go " +
                "through SdrImage.FromCapturedFrame's passthrough branch instead.");
        }

        int width = frame.Width;
        int height = frame.Height;
        float scale = (float)(80.0 / frame.Monitor.SdrWhiteNits);

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

        double m0 = globalMax;
        double knee = opts.Knee;
        double peak = opts.PeakOverride ?? Math.Clamp(
            Math.Min(m0, frame.Monitor.MaxLuminanceNits / frame.Monitor.SdrWhiteNits),
            2.0, double.MaxValue);
        bool passThroughWholeFrame = m0 <= 1.0 + opts.Epsilon;

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
}
