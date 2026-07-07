using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RoeSnip.Capture;

namespace RoeSnip.Imaging;

/// <summary>BGRA8, straight alpha (255 = opaque), tightly packed rows (Stride == Width * 4),
/// top-down. This is the one and only "what you see is what you get" image type — the overlay
/// preview, the clipboard payload, and the saved PNG are all built from this type.</summary>
public sealed class SdrImage
{
    public int Width { get; }
    public int Height { get; }
    public int Stride => Width * 4;
    public byte[] Pixels { get; } // BGRA8, length == Stride * Height

    public SdrImage(int width, int height, byte[] pixels)
    {
        if (pixels.Length != width * 4 * height)
        {
            throw new ArgumentException(
                $"Pixel buffer length {pixels.Length} does not match width*4*height ({width * 4 * height}).",
                nameof(pixels));
        }

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    /// <summary>The single entry point every call site (CLI, overlay preview, exports) must use
    /// to turn a CapturedFrame into an SdrImage — this is what encodes the format branch from
    /// DESIGN.md ("BGRA8-source frames bypass the tone-mapper entirely"), so no call site needs
    /// to know or duplicate that branch.</summary>
    public static SdrImage FromCapturedFrame(CapturedFrame frame, Color.ToneMapOptions opts) =>
        frame.Format == FrameFormat.Fp16ScRgb
            ? Color.ToneMapper.MapToSdr(frame, opts)
            : FromBgra8Passthrough(frame);

    private static SdrImage FromBgra8Passthrough(CapturedFrame frame)
    {
        int width = frame.Width;
        int height = frame.Height;
        var output = new byte[width * 4 * height];
        for (int y = 0; y < height; y++)
        {
            var row = frame.Row(y);
            int rowOut = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int i = x * 4;
                int o = rowOut + i;
                output[o + 0] = row[i + 0]; // B
                output[o + 1] = row[i + 1]; // G
                output[o + 2] = row[i + 2]; // R
                output[o + 3] = 255;        // force opaque
            }
        }
        return new SdrImage(width, height, output);
    }

    /// <summary>Crops in physical pixels relative to this image's own (0,0); throws if out of bounds.</summary>
    public SdrImage Crop(RectPhysical rectPx)
    {
        var r = rectPx.Normalized();
        if (r.Left < 0 || r.Top < 0 || r.Right > Width || r.Bottom > Height)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rectPx), $"Crop rect {r} is out of bounds for a {Width}x{Height} image.");
        }

        int cropWidth = r.Width;
        int cropHeight = r.Height;
        var output = new byte[cropWidth * 4 * cropHeight];
        for (int y = 0; y < cropHeight; y++)
        {
            int srcOffset = (r.Top + y) * Stride + r.Left * 4;
            int dstOffset = y * cropWidth * 4;
            Array.Copy(Pixels, srcOffset, output, dstOffset, cropWidth * 4);
        }
        return new SdrImage(cropWidth, cropHeight, output);
    }

    /// <summary>WPF BitmapSource, Bgra32, 96 DPI both axes (so 1 device pixel == 1 DIP; required
    /// for the mixed-DPI overlay pattern in DESIGN.md).</summary>
    public BitmapSource ToBitmapSource()
    {
        var bitmap = BitmapSource.Create(
            Width, Height, 96, 96, PixelFormats.Bgra32, null, Pixels, Stride);
        bitmap.Freeze();
        return bitmap;
    }
}
