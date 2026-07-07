using System.IO;
using System.Windows.Media.Imaging;

namespace RoeSnip.Imaging;

public static class PngWriter
{
    public static void WriteFile(string path, SdrImage image)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image.ToBitmapSource()));
        encoder.Save(stream);
    }

    public static byte[] Encode(SdrImage image)
    {
        using var stream = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image.ToBitmapSource()));
        encoder.Save(stream);
        return stream.ToArray();
    }
}
