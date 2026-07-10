using System;
using System.IO;
using RoeSnip.Capture;
using Vortice.WIC;
using WicPixelFormat = Vortice.WIC.PixelFormat;

namespace RoeSnip.Imaging;

/// <summary>Writes the untouched HDR original (DESIGN.md "Save HDR") as JPEG XR — no annotations,
/// raw crop, FP16 scRGB values preserved above 1.0 (headroom).
///
/// Encoder choice (per the acceptance gate in DESIGN.md / PLAN.md §3.3, actually run — not
/// guessed): WPF's <c>WmpBitmapEncoder</c>/<c>BitmapDecoder</c> pair was tried first and FAILS the
/// round-trip test — WPF flattens JXR to 32bpp Pbgra32 on *both* encode and decode regardless of
/// the source <see cref="System.Windows.Media.PixelFormats.Rgba128Float"/> bitmap supplied to it
/// (verified empirically: a 3.0 value came back as ~0.78, i.e. clipped to [0,1] and re-encoded).
/// Talking to WIC directly via <c>Vortice.Direct2D1</c>'s WIC bindings (namespace <see
/// cref="Vortice.WIC"/>) with pixel format <see cref="WicPixelFormat.Format128bppRGBAFloat"/> and
/// container format <see cref="ContainerFormat.Wmp"/> DOES preserve the float headroom — verified
/// with the same 3.0-in/3.0-out probe. So this class bypasses WPF's imaging stack entirely for
/// both write and (in the test) read.</summary>
public static class JxrWriter
{
    /// <summary>Writes <paramref name="frame"/> cropped to <paramref name="cropPx"/> as a .jxr file
    /// at <paramref name="path"/>. Fp16ScRgb frames are written as their true linear scRGB values
    /// (headroom above 1.0 preserved). Bgra8Srgb frames (degenerate SDR-only case) are decoded to
    /// linear scRGB via <see cref="CapturedFrame.ReadPixelScRgb"/> (which internally uses
    /// ColorMath.SrgbByteToLinear plus the monitor's SDR white scale) before encoding, so the file
    /// is still valid scRGB rather than raw 0..1 sRGB-encoded bytes.</summary>
    public static void Write(string path, CapturedFrame frame, RectPhysical cropPx)
    {
        byte[] floatPixels = BuildFloatBuffer(frame, cropPx, out int width, out int height, out uint stride);

        using var factory = new IWICImagingFactory();
        using var fileStream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var encoder = factory.CreateEncoder(ContainerFormat.Wmp, fileStream, BitmapEncoderCacheOption.NoCache);
        using var frameEncode = encoder.CreateNewFrame(out var encoderOptions);
        frameEncode.Initialize(encoderOptions);
        frameEncode.SetSize((uint)width, (uint)height);

        var pixelFormat = WicPixelFormat.Format128bppRGBAFloat;
        frameEncode.SetPixelFormat(ref pixelFormat);
        if (pixelFormat != WicPixelFormat.Format128bppRGBAFloat)
        {
            throw new InvalidOperationException(
                $"WIC JXR encoder negotiated an unexpected pixel format ({pixelFormat}) instead of " +
                "128bppRGBAFloat; HDR headroom would be lost.");
        }

        frameEncode.WritePixels((uint)height, stride, floatPixels);
        frameEncode.Commit();
        encoder.Commit();
    }

    /// <summary>Packs the crop region into a tightly-packed R32G32B32A32Float buffer (matching
    /// WIC's 128bppRGBAFloat channel order), reading every source pixel through <see
    /// cref="CapturedFrame.ReadPixelScRgb"/> so both source formats (and <see
    /// cref="CapturedFrame.Stride"/> row padding) are handled uniformly and correctly.</summary>
    private static byte[] BuildFloatBuffer(
        CapturedFrame frame, RectPhysical cropPx, out int width, out int height, out uint stride)
    {
        var r = cropPx.Normalized();
        if (r.Left < 0 || r.Top < 0 || r.Right > frame.Width || r.Bottom > frame.Height)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cropPx), $"Crop rect {r} is out of bounds for a {frame.Width}x{frame.Height} frame.");
        }

        width = r.Width;
        height = r.Height;
        const int bytesPerPixel = 16; // 4 x float32
        stride = (uint)(width * bytesPerPixel);
        var buffer = new byte[stride * (uint)height];

        for (int y = 0; y < height; y++)
        {
            int srcY = r.Top + y;
            int rowOffset = (int)(y * stride);
            for (int x = 0; x < width; x++)
            {
                int srcX = r.Left + x;
                var v = frame.ReadPixelScRgb(srcX, srcY);
                int o = rowOffset + x * bytesPerPixel;
                BitConverter.TryWriteBytes(buffer.AsSpan(o, 4), v.X);
                BitConverter.TryWriteBytes(buffer.AsSpan(o + 4, 4), v.Y);
                BitConverter.TryWriteBytes(buffer.AsSpan(o + 8, 4), v.Z);
                // Captured alpha isn't meaningful (per DESIGN.md); the HDR export is always opaque.
                BitConverter.TryWriteBytes(buffer.AsSpan(o + 12, 4), 1f);
            }
        }

        return buffer;
    }
}

file static class ModuleInit
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Init() => AppComposition.WriteJxr = JxrWriter.Write;
}
