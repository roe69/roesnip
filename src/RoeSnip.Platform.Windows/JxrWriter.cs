using System;
using System.Collections.Generic;
using System.IO;
using RoeSnip.Core.Capture;
using Vortice.WIC;
using WicPixelFormat = Vortice.WIC.PixelFormat;

namespace RoeSnip.Platform.Windows;

/// <summary>Writes the untouched HDR original (DESIGN.md "Save HDR") as JPEG XR — no annotations,
/// raw crop, FP16 scRGB values preserved above 1.0 (headroom).
/// Ported verbatim from src/RoeSnip/Imaging/JxrWriter.cs (PLAN-XPLAT.md §3.2) — only the
/// namespace/project moved; <see cref="CapturedFrame.ReadPixelScRgb"/>'s signature is unchanged in
/// Core, so no functional edit was needed. The AppComposition hook registration moved to
/// RoeSnip.App (Platform.Windows cannot reference the App assembly).
///
/// Encoder choice (per the acceptance gate in DESIGN.md / PLAN.md §3.3, actually run — not
/// guessed): WPF's <c>WmpBitmapEncoder</c>/<c>BitmapDecoder</c> pair was tried first and FAILS the
/// round-trip test — WPF flattens JXR to 32bpp Pbgra32 on *both* encode and decode regardless of
/// the source Rgba128Float bitmap supplied to it (verified empirically: a 3.0 value came back as
/// ~0.78, i.e. clipped to [0,1] and re-encoded). Talking to WIC directly via
/// <c>Vortice.Direct2D1</c>'s WIC bindings (namespace <see cref="Vortice.WIC"/>) with pixel format
/// <see cref="WicPixelFormat.Format128bppRGBAFloat"/> and container format
/// <see cref="ContainerFormat.Wmp"/> DOES preserve the float headroom — verified with the same
/// 3.0-in/3.0-out probe. So this class bypasses WPF's imaging stack entirely for both write and
/// (in the test) read.</summary>
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
        EncodeAndWrite(path, width, height, stride, floatPixels);
    }

    /// <summary>Cross-monitor selection (item 09) HDR save: stitches every contributing monitor's
    /// own RAW crop (<paramref name="crops"/> — one <see cref="SpanningFrameCrop"/> per monitor the
    /// selection actually touches) into one canvas sized to <paramref name="virtualRect"/>, then
    /// writes it through the exact same WIC 128bppRGBAFloat encode path <see cref="Write"/> uses for
    /// a single monitor.
    ///
    /// Well-defined despite different monitors' own SDR-white/peak photometrics — raw scRGB is ONE
    /// absolute linear space (1.0 = 80 nits) for every monitor, independent of that monitor's own
    /// SDR white level or peak brightness. Per-monitor photometrics only ever enter into
    /// TONE-MAPPING (Core.Color.ToneMapper / SdrImage.FromCapturedFrame), a completely different code
    /// path this never touches — every pixel here goes through <see cref="CapturedFrame.ReadPixelScRgb"/>,
    /// the SAME single well-defined per-pixel decode <see cref="BuildFloatBuffer"/> already uses for
    /// the single-monitor case (Fp16 pass-through, or Bgra8 decoded via the sRGB EOTF and rescaled by
    /// that specific monitor's own SdrWhiteNits) — so two crops from monitors with different
    /// photometrics are already directly comparable in the same units (linear scRGB, 1.0 = 80 nits)
    /// by the time either one is written to the canvas; there is nothing left to reconcile between
    /// them.
    ///
    /// Gaps (no contributing monitor covers part of the rect) are left at linear (0,0,0) with alpha
    /// 1 — black, opaque — the same documented choice as the SDR spanning composite
    /// (OverlaySession.RenderSpanningSelection), just in linear scRGB instead of BGRA8. Ported from
    /// the frozen WPF app's src/RoeSnip/Imaging/JxrWriter.cs.</summary>
    public static void WriteSpanning(string path, RectPhysical virtualRect, IReadOnlyList<SpanningFrameCrop> crops)
    {
        var n = virtualRect.Normalized();
        int width = Math.Max(1, n.Width);
        int height = Math.Max(1, n.Height);
        byte[] floatPixels = BuildSpanningFloatBuffer(crops, width, height, out uint stride);
        EncodeAndWrite(path, width, height, stride, floatPixels);
    }

    private static void EncodeAndWrite(string path, int width, int height, uint stride, byte[] floatPixels)
    {
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
                "128bppRGBAFloat — HDR headroom would be lost.");
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

    /// <summary>Cross-monitor selection (item 09) HDR save: same tightly-packed R32G32B32A32Float
    /// layout as <see cref="BuildFloatBuffer"/>, sized to the spanning selection's own virtual canvas
    /// (<paramref name="width"/> x <paramref name="height"/>) instead of one frame's crop. Every
    /// pixel starts as opaque linear black (the documented gap fill — see
    /// <see cref="WriteSpanning"/>'s own doc comment) and is then overwritten by whichever crop(s) in
    /// <paramref name="crops"/> cover it, each read through the same
    /// <see cref="CapturedFrame.ReadPixelScRgb"/> per-pixel decode <see cref="BuildFloatBuffer"/>
    /// uses — so a pixel's monitor of origin never affects its meaning, only which raw bytes back
    /// it.</summary>
    private static byte[] BuildSpanningFloatBuffer(
        IReadOnlyList<SpanningFrameCrop> crops, int width, int height, out uint stride)
    {
        const int bytesPerPixel = 16; // 4 x float32
        stride = (uint)(width * bytesPerPixel);
        var buffer = new byte[stride * (uint)height];

        // Prefill every pixel's alpha to opaque (RGB already zero-initialized = linear black) —
        // gaps that no crop below ever touches stay exactly this: opaque black, matching the SDR
        // spanning composite's own documented gap fill.
        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * (int)stride;
            for (int x = 0; x < width; x++)
            {
                BitConverter.TryWriteBytes(buffer.AsSpan(rowOffset + x * bytesPerPixel + 12, 4), 1f);
            }
        }

        foreach (var crop in crops)
        {
            var r = crop.LocalCropPx.Normalized();
            if (r.Left < 0 || r.Top < 0 || r.Right > crop.Frame.Width || r.Bottom > crop.Frame.Height)
            {
                // A crop rect out of bounds for its own frame is a caller bug — OverlayController
                // derives every crop from the same intersected local selection the SDR spanning
                // composite already validated via SdrImage.Crop's own bounds check. This is a save
                // path (user explicitly asked to write a file), not a per-frame hot loop, so fail
                // loud here rather than silently leave that slice black.
                throw new ArgumentOutOfRangeException(
                    nameof(crops), $"Crop rect {r} is out of bounds for a {crop.Frame.Width}x{crop.Frame.Height} frame.");
            }

            for (int y = 0; y < r.Height; y++)
            {
                int destY = crop.DestY + y;
                if (destY < 0 || destY >= height)
                {
                    continue; // defensive — geometry from OverlayController should never produce this
                }
                int srcY = r.Top + y;
                int rowOffset = destY * (int)stride;
                for (int x = 0; x < r.Width; x++)
                {
                    int destX = crop.DestX + x;
                    if (destX < 0 || destX >= width)
                    {
                        continue;
                    }
                    var v = crop.Frame.ReadPixelScRgb(r.Left + x, srcY);
                    int o = rowOffset + destX * bytesPerPixel;
                    BitConverter.TryWriteBytes(buffer.AsSpan(o, 4), v.X);
                    BitConverter.TryWriteBytes(buffer.AsSpan(o + 4, 4), v.Y);
                    BitConverter.TryWriteBytes(buffer.AsSpan(o + 8, 4), v.Z);
                    BitConverter.TryWriteBytes(buffer.AsSpan(o + 12, 4), 1f);
                }
            }
        }

        return buffer;
    }
}
