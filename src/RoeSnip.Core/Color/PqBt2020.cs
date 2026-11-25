using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using RoeSnip.Core.Capture;

namespace RoeSnip.Core.Color;

/// <summary>Converts an HDR screencast frame (half-float RGBA, PQ-encoded BT.2020: GNOME 50's
/// RGBA_F16 stream on a BT.2100 monitor) into the linear BT.709 scRGB the rest of Core works in
/// (1.0 = 80 nits, same as Windows), so <see cref="ToneMapper"/>, the loupe's nits readout and the
/// .jxr export treat a Linux HDR frame exactly like a Windows one.
///
/// Per channel a 65536-entry table maps the half bit pattern to nits/80 (PQ EOTF, ST 2084), then a
/// 3x3 matrix moves BT.2020 primaries to BT.709. Out-of-gamut colors keep their negative components
/// (scRGB allows them; the tone-mapper clamps). Alpha is forced to 1.0: the stream is opaque.
/// Mutter's PQ reference white is 203 nits (<see cref="ReferenceWhiteNits"/>), so a frame built with
/// <see cref="CreateFrame"/> reports SdrWhiteNits 203 and SdrWhiteInBufferUnits 203/80.</summary>
public static class PqBt2020
{
    /// <summary>Reference (SDR) white of a PQ screencast: ITU-R BT.2408 / mutter's default.</summary>
    public const double ReferenceWhiteNits = 203.0;

    /// <summary>Nits per scRGB unit.</summary>
    public const double ScRgbNits = 80.0;

    private const double M1 = 2610.0 / 16384.0, M2 = 2523.0 / 4096.0 * 128.0;
    private const double C1 = 3424.0 / 4096.0, C2 = 2413.0 / 4096.0 * 32.0, C3 = 2392.0 / 4096.0 * 32.0;

    // Linear BT.2020 -> linear BT.709 (ITU-R BT.2087), row-major.
    private const float M00 = 1.6604910f, M01 = -0.5876411f, M02 = -0.0728499f;
    private const float M10 = -0.1245505f, M11 = 1.1328999f, M12 = -0.0083494f;
    private const float M20 = -0.0181508f, M21 = -0.1005789f, M22 = 1.1187297f;

    private static readonly Lazy<float[]> s_lut = new(BuildLut);

    /// <summary>The PQ EOTF: encoded signal 0..1 to absolute luminance in nits (0..10000).</summary>
    public static double PqToNits(double encoded)
    {
        if (!(encoded > 0)) return 0;
        if (encoded > 1) encoded = 1;
        double p = Math.Pow(encoded, 1.0 / M2);
        double num = Math.Max(p - C1, 0);
        double den = C2 - C3 * p;
        return 10000.0 * Math.Pow(num / den, 1.0 / M1);
    }

    /// <summary>Linear scRGB per-channel value (1.0 = 80 nits) of a PQ-encoded half bit pattern.
    /// NaN and negative patterns read as 0, values above 1.0 clamp to 1.0 (10000 nits).</summary>
    public static float PqHalfToScRgb(ushort halfBits) => s_lut.Value[halfBits];

    private static float[] BuildLut()
    {
        var lut = new float[65536];
        for (int i = 0; i < lut.Length; i++)
        {
            double e = (double)BitConverter.UInt16BitsToHalf((ushort)i);
            lut[i] = double.IsNaN(e) ? 0f : (float)(PqToNits(e) / ScRgbNits);
        }
        return lut;
    }

    /// <summary>Converts one pixel (PQ BT.2020 half bit patterns) to linear BT.709 scRGB.</summary>
    public static void ConvertPixel(ushort r, ushort g, ushort b, out float outR, out float outG, out float outB)
    {
        var lut = s_lut.Value;
        float lr = lut[r], lg = lut[g], lb = lut[b];
        outR = M00 * lr + M01 * lg + M02 * lb;
        outG = M10 * lr + M11 * lg + M12 * lb;
        outB = M20 * lr + M21 * lg + M22 * lb;
    }

    /// <summary>Converts a tightly-or-loosely packed half RGBA frame in place (R,G,B,A, 8 bytes per
    /// pixel): PQ BT.2020 in, linear BT.709 scRGB out, alpha 1.0.</summary>
    public static void ConvertInPlace(byte[] pixels, int stride, int width, int height)
    {
        var lut = s_lut.Value;
        Parallel.For(0, height, y =>
        {
            var row = MemoryMarshal.Cast<byte, ushort>(pixels.AsSpan(y * stride, width * 8));
            for (int x = 0; x < width; x++)
            {
                int o = x * 4;
                float lr = lut[row[o]], lg = lut[row[o + 1]], lb = lut[row[o + 2]];
                row[o] = BitConverter.HalfToUInt16Bits((Half)(M00 * lr + M01 * lg + M02 * lb));
                row[o + 1] = BitConverter.HalfToUInt16Bits((Half)(M10 * lr + M11 * lg + M12 * lb));
                row[o + 2] = BitConverter.HalfToUInt16Bits((Half)(M20 * lr + M21 * lg + M22 * lb));
                row[o + 3] = 0x3C00; // 1.0
            }
        });
    }

    /// <summary>Wraps a half RGBA PQ BT.2020 buffer (converted in place here) as an
    /// <see cref="FrameFormat.Fp16ScRgb"/> frame with SDR white at <see cref="ReferenceWhiteNits"/>.
    /// <paramref name="maxLuminanceNits"/> is the display's peak (EDID), 0 for unknown (1000).</summary>
    public static CapturedFrame CreateFrame(
        byte[] pixels, int stride, int width, int height, MonitorInfo monitor, double maxLuminanceNits)
    {
        ConvertInPlace(pixels, stride, width, height);
        var hdrMonitor = monitor with
        {
            AdvancedColorActive = true,
            SdrWhiteNits = ReferenceWhiteNits,
            MaxLuminanceNits = maxLuminanceNits > 0 ? maxLuminanceNits : 1000.0,
        };
        return new CapturedFrame(FrameFormat.Fp16ScRgb, width, height, stride, pixels, hdrMonitor,
            sdrWhiteInBufferUnits: ReferenceWhiteNits / ScRgbNits);
    }
}
