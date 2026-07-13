using RoeSnip.Core.Imaging;

namespace RoeSnip.Core.Sharing;

/// <summary>Generates the tiny throwaway PNG ShareProviderEditWindow's Test button uploads - real
/// bytes through the real ShareManager/ProviderSpecShareProvider pipeline (never a canned stub
/// response), so a successful Test is genuine end-to-end evidence the configured provider works.
///
/// Built directly against Core's own SdrImage/PngWriter (SkiaSharp-backed, portable) rather than the
/// WPF app's original System.Drawing/GDI+ implementation - System.Drawing has been Windows-only since
/// .NET 7, which RoeSnip.Core's net8.0 (no-RID) TFM cannot depend on. The gradient is computed
/// per-pixel instead of drawn via a GDI+ LinearGradientBrush; the two are not bit-identical, but
/// nothing depends on that - only "a valid small PNG the provider accepts" matters here.</summary>
public static class ShareTestImage
{
    private const int Size = 32;

    // Same two corner colors the original WPF GDI+ gradient used (0x4A9EFF -> 0xFF6B35), interpolated
    // along the diagonal instead of at GDI+'s 45-degree LinearGradientBrush angle - visually
    // equivalent, not pixel-identical (see class doc comment for why that's fine here).
    private const byte StartR = 0x4A, StartG = 0x9E, StartB = 0xFF;
    private const byte EndR = 0xFF, EndG = 0x6B, EndB = 0x35;

    public static byte[] CreatePngBytes()
    {
        var pixels = new byte[Size * 4 * Size];
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                // Diagonal position in [0, 1], 0 at the top-left corner, 1 at the bottom-right one.
                double t = (x + y) / (2.0 * (Size - 1));

                byte r = Lerp(StartR, EndR, t);
                byte g = Lerp(StartG, EndG, t);
                byte b = Lerp(StartB, EndB, t);

                int o = (y * Size + x) * 4;
                pixels[o + 0] = b;
                pixels[o + 1] = g;
                pixels[o + 2] = r;
                pixels[o + 3] = 255; // opaque
            }
        }

        var image = new SdrImage(Size, Size, pixels);
        return PngWriter.Encode(image);
    }

    private static byte Lerp(byte from, byte to, double t) =>
        (byte)(from + (to - from) * t);
}
