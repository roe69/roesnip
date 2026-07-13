using System;
using RoeSnip.Core.Capture;

namespace RoeSnip.Core.Imaging;

/// <summary>BGRA8, straight alpha (255 = opaque), tightly packed rows (Stride == Width * 4),
/// top-down. This is the one and only "what you see is what you get" image type — the overlay
/// preview, the clipboard payload, and the saved PNG are all built from this type.
/// Ported from the WPF app with the WPF-only ToBitmapSource() method removed — Core has zero UI
/// dependency (DESIGN-XPLAT.md); Avalonia's WriteableBitmap conversion is a WP-X3 extension method
/// living in RoeSnip.App (PLAN-XPLAT.md §2.7/§3.3).</summary>
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
        FromCapturedFrame(frame, opts, reuseOutput: null);

    /// <summary><paramref name="reuseOutput"/>: recording-cadence call sites (up to 50fps - item 20)
    /// pass a persistent, exactly-frame-sized buffer here to avoid a fresh (LOH-sized) allocation per
    /// captured frame - mirrors <see cref="Color.ToneMapper.MapToSdr(CapturedFrame,Color.ToneMapOptions,byte[]?)"/>'s
    /// own reuseOutput contract, extended to the Bgra8Srgb passthrough branch too (item 10 only added
    /// it to the tone-mapped path, since that is the only branch its own recording-cadence caller at
    /// the time exercised; item 20 needs both, since a recording can target an SDR-only monitor).
    /// Must already be sized <c>width*4*height</c> when non-null - same "caller's own scratch buffer,
    /// never allocated here" contract every other reuse-buffer parameter in this codebase uses.</summary>
    public static SdrImage FromCapturedFrame(CapturedFrame frame, Color.ToneMapOptions opts, byte[]? reuseOutput) =>
        frame.Format == FrameFormat.Fp16ScRgb
            ? Color.ToneMapper.MapToSdr(frame, opts, reuseOutput)
            : FromBgra8Passthrough(frame, reuseOutput);

    private static SdrImage FromBgra8Passthrough(CapturedFrame frame, byte[]? reuseOutput = null)
    {
        int width = frame.Width;
        int height = frame.Height;
        var output = reuseOutput ?? new byte[width * 4 * height];
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
}
