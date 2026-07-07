namespace RoeSnip.Core.Imaging;

/// <summary>SkiaSharp-based replacement for the WPF app's PngBitmapEncoder-based PngWriter
/// (PLAN.md §2.6/§3.1) — same two-method surface, same BGRA8-straight-alpha-tightly-packed
/// contract as SdrImage guarantees, so no caller needs to change.</summary>
public static class PngWriter
{
    public static void WriteFile(string path, SdrImage image)
    {
        using var stream = new System.IO.FileStream(path, System.IO.FileMode.Create, System.IO.FileAccess.Write);
        using var data = EncodeToData(image);
        data.SaveTo(stream);
    }

    public static byte[] Encode(SdrImage image)
    {
        using var data = EncodeToData(image);
        return data.ToArray();
    }

    private static SkiaSharp.SKData EncodeToData(SdrImage image)
    {
        var info = new SkiaSharp.SKImageInfo(image.Width, image.Height, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Unpremul);
        using var bitmap = new SkiaSharp.SKBitmap(info);
        // SdrImage.Stride == Width * 4 always (tightly packed, per its own contract) and matches
        // SKImageInfo's default RowBytes for Bgra8888 exactly — a straight Marshal.Copy into
        // Skia-owned pixel memory is safe (no manual pinning of the managed array required).
        System.Runtime.InteropServices.Marshal.Copy(image.Pixels, 0, bitmap.GetPixels(), image.Pixels.Length);
        using var skImage = SkiaSharp.SKImage.FromBitmap(bitmap);
        return skImage.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
    }
}
