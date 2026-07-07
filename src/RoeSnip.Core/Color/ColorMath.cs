namespace RoeSnip.Core.Color;

public static class ColorMath
{
    /// <summary>Proper piecewise sRGB EOTF^-1 (linear -> encoded, both in [0,1]).</summary>
    public static float SrgbEncode(float linear01) =>
        linear01 <= 0.0031308f ? linear01 * 12.92f : 1.055f * MathF.Pow(linear01, 1f / 2.4f) - 0.055f;

    /// <summary>Piecewise sRGB EOTF (encoded -> linear, both in [0,1]).</summary>
    public static float SrgbDecode(float encoded01) =>
        encoded01 <= 0.04045f ? encoded01 / 12.92f : MathF.Pow((encoded01 + 0.055f) / 1.055f, 2.4f);

    /// <summary>Convenience: decode an sRGB byte straight to linear [0,1].</summary>
    public static float SrgbByteToLinear(byte encoded) => SrgbDecode(encoded / 255f);

    /// <summary>Clamp to [0,1] then round-to-nearest into a byte (0-255).</summary>
    public static byte QuantizeRoundNearest(float linear01AfterEncode)
    {
        float clamped = Math.Clamp(linear01AfterEncode, 0f, 1f);
        return (byte)MathF.Round(clamped * 255f, MidpointRounding.AwayFromZero);
    }
}
