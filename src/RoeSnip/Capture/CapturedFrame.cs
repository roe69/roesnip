using System;
using System.Numerics;
using RoeSnip.Color;

namespace RoeSnip.Capture;

public enum FrameFormat
{
    Fp16ScRgb,   // R16G16B16A16_FLOAT, linear scRGB, 1.0 = 80 nits. 8 bytes/pixel (4 x System.Half).
    Bgra8Srgb,   // B8G8R8A8_UNORM, already sRGB-encoded passthrough. 4 bytes/pixel.
}

/// <summary>Owns one monitor's raw captured pixels for the lifetime of a capture session.
/// The buffer is exactly as delivered by the capturer: <see cref="Stride"/> is the real row
/// pitch (may exceed Width * BytesPerPixel due to driver padding) — always index as
/// <c>row * Stride + col * BytesPerPixel</c>, never assume tightly packed rows.
/// Lifetime: created by CaptureService.CaptureAll(); the caller (AppComposition) owns and
/// must Dispose() every frame once the capture session (overlay + any exports) is complete.</summary>
public sealed class CapturedFrame : IDisposable
{
    public FrameFormat Format { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public MonitorInfo Monitor { get; }
    public int BytesPerPixel => Format == FrameFormat.Fp16ScRgb ? 8 : 4;

    private byte[]? _pixels; // null after Dispose()

    public CapturedFrame(FrameFormat format, int width, int height, int stride, byte[] pixels, MonitorInfo monitor)
    {
        Format = format;
        Width = width;
        Height = height;
        Stride = stride;
        Monitor = monitor;
        _pixels = pixels;
    }

    public ReadOnlySpan<byte> Row(int y)
    {
        var pixels = _pixels ?? throw new ObjectDisposedException(nameof(CapturedFrame));
        return pixels.AsSpan(y * Stride, Width * BytesPerPixel);
    }

    /// <summary>Reads pixel (x,y) as linear scRGB (1.0 = 80 nits), regardless of source format.
    /// For Bgra8Srgb frames this decodes the sRGB EOTF and rescales so that byte 255 (linear 1.0)
    /// corresponds to Monitor.SdrWhiteNits, i.e. scRGB = srgbLinear01 * (Monitor.SdrWhiteNits / 80.0).
    /// Used by the magnifier's nits readout — the "killer HDR feature" (DESIGN.md).</summary>
    public Vector4 ReadPixelScRgb(int x, int y)
    {
        var row = Row(y);
        if (Format == FrameFormat.Fp16ScRgb)
        {
            int o = x * 8;
            float r = (float)BitConverter.ToHalf(row.Slice(o, 2));
            float g = (float)BitConverter.ToHalf(row.Slice(o + 2, 2));
            float b = (float)BitConverter.ToHalf(row.Slice(o + 4, 2));
            float a = (float)BitConverter.ToHalf(row.Slice(o + 6, 2));
            return new Vector4(r, g, b, a);
        }
        else
        {
            int o = x * 4;
            byte b8 = row[o], g8 = row[o + 1], r8 = row[o + 2], a8 = row[o + 3];
            float scale = (float)(Monitor.SdrWhiteNits / 80.0);
            float r = ColorMath.SrgbByteToLinear(r8) * scale;
            float g = ColorMath.SrgbByteToLinear(g8) * scale;
            float b = ColorMath.SrgbByteToLinear(b8) * scale;
            return new Vector4(r, g, b, a8 / 255f);
        }
    }

    /// <summary>Photometric luminance in nits for the magnifier readout: max(r,g,b) * 80.</summary>
    public double ReadPixelNits(int x, int y)
    {
        var v = ReadPixelScRgb(x, y);
        return Math.Max(v.X, Math.Max(v.Y, v.Z)) * 80.0;
    }

    public void Dispose() => _pixels = null;
}
