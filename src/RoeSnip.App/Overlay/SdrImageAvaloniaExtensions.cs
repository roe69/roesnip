using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using RoeSnip.Core.Imaging;

namespace RoeSnip.App.Overlay;

/// <summary>The Core/UI seam (PLAN-XPLAT.md §3.3): converts the portable BGRA8 SdrImage into an
/// Avalonia WriteableBitmap for on-screen display. The exact mirror of the WPF app's removed
/// SdrImage.ToBitmapSource() — it lives here, not in Core, because Core has zero UI deps.</summary>
public static class SdrImageAvaloniaExtensions
{
    public static WriteableBitmap ToAvaloniaBitmap(this SdrImage image)
    {
        var wb = new WriteableBitmap(
            new PixelSize(image.Width, image.Height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using var fb = wb.Lock();
        // Row-by-row copy, not one bulk copy: ILockedFramebuffer.RowBytes is an Avalonia/backend
        // implementation detail and is not guaranteed to equal SdrImage.Stride, even though both
        // happen to be Width*4 today (PLAN-XPLAT.md §3.3's own note).
        for (int y = 0; y < image.Height; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(
                image.Pixels, y * image.Stride, fb.Address + y * fb.RowBytes, image.Stride);
        }
        return wb;
    }
}
