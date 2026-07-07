namespace RoeSnip.Color;

public static class Dither
{
    // 4x4 Bayer matrix (values 0-15), normalized to [-0.5, +0.5) then scaled to a
    // ±0.5/255 (in [0,1]-normalized-linear-encoded-byte space) offset.
    private static readonly int[,] Bayer4x4 =
    {
        {  0,  8,  2, 10 },
        { 12,  4, 14,  6 },
        {  3, 11,  1,  9 },
        { 15,  7, 13,  5 },
    };

    /// <summary>Returns a value in [-0.5/255, +0.5/255].</summary>
    public static float Offset01(int x, int y)
    {
        int bayerValue = Bayer4x4[y & 3, x & 3]; // 0..15
        float normalized = (bayerValue + 0.5f) / 16f; // (0, 1)
        float centered = normalized - 0.5f; // (-0.5, 0.5)
        return centered / 255f;
    }
}
