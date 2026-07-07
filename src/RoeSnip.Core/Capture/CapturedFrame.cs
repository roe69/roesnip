using System;
using System.Numerics;
using RoeSnip.Core.Color;

namespace RoeSnip.Core.Capture;

/// <summary>Owns one monitor's raw captured pixels for the lifetime of a capture session. Identical
/// shape to the WPF app's type (PLAN.md §2.2) with one addition: <see cref="SdrWhiteInBufferUnits"/>.
///
/// ## Key semantics — read this before touching ToneMapper or ReadPixelNits
///
/// The WPF app hardcoded "1.0 buffer unit = 80 nits" everywhere because Windows scRGB defines it
/// that way. macOS SCK's EDR buffers use a DIFFERENT convention: 1.0 buffer unit IS SDR/reference
/// white by construction (EDR headroom is expressed as a multiplier above 1.0, not in nits-per-unit
/// terms). <see cref="SdrWhiteInBufferUnits"/> is what raw buffer value (per channel) corresponds to
/// SDR white for THIS frame, so both conventions can share one ToneMapper:
///   - Windows Fp16ScRgb: <c>Monitor.SdrWhiteNits / 80.0</c> (scRGB's fixed 80-nits-per-unit rule).
///   - macOS EDR Fp16 buffers: <c>1.0</c> (SCK's own convention).
///   - Bgra8Srgb frames (any backend): irrelevant/unused — see the ReadPixelNits note below. Set it
///     to <c>1.0</c> by convention; nothing reads it for this format.
///
/// ToneMapper's pass-1 scale step generalizes from the WPF app's hardcoded
/// <c>scale = 80.0 / Monitor.SdrWhiteNits</c> to <c>scale = 1.0 / SdrWhiteInBufferUnits</c> — algebraically
/// identical on Windows when <c>SdrWhiteInBufferUnits == Monitor.SdrWhiteNits / 80.0</c> (substitute:
/// <c>1.0 / (SdrWhiteNits/80.0) == 80.0/SdrWhiteNits</c>). ToneMapper.MapToSdr still only accepts
/// Fp16ScRgb frames (throws otherwise, unchanged) — this generalization only ever executes on that
/// format, so it never needs to special-case Bgra8Srgb.
///
/// <see cref="ReadPixelNits"/> is trickier because — unlike ToneMapper — it is called for EVERY
/// frame format (the magnifier/color-inspector reads nits from whatever the user is hovering,
/// regardless of whether that monitor is HDR). The WPF app's Bgra8Srgb branch of
/// <see cref="ReadPixelScRgb"/> ALREADY bakes <c>Monitor.SdrWhiteNits / 80.0</c> into its own decode
/// (so a pure-white byte reads back as exactly <c>Monitor.SdrWhiteNits</c> nits via the constant
/// <c>* 80.0</c> in the old ReadPixelNits) — that baked-in scale is NOT the same value as
/// <see cref="SdrWhiteInBufferUnits"/> for this format (which is the unused "1.0" sentinel above).
/// Naively generalizing ReadPixelNits to always do <c>bufferMax / SdrWhiteInBufferUnits * Monitor.SdrWhiteNits</c>
/// for BOTH formats is WRONG for Bgra8Srgb — it would double-apply the SdrWhiteNits scaling and
/// produce <c>SdrWhiteNits² / 80</c> instead of <c>SdrWhiteNits</c> for a white pixel. The fix (baked
/// into the method below): keep the Bgra8Srgb branch doing the exact old constant-<c>*80.0</c> math,
/// unconditionally, and ONLY use the generalized <c>SdrWhiteInBufferUnits</c> formula for Fp16ScRgb.
/// This is exactly why DESIGN-XPLAT.md calls Bgra8Srgb's value "n/a" — it is unused BECAUSE the
/// nits math for that format never needed generalizing in the first place, not because nits
/// don't matter for SDR frames.</summary>
public sealed class CapturedFrame : IDisposable
{
    public FrameFormat Format { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public MonitorInfo Monitor { get; }
    public int BytesPerPixel => Format == FrameFormat.Fp16ScRgb ? 8 : 4;
    public double SdrWhiteInBufferUnits { get; }

    private byte[]? _pixels;

    public CapturedFrame(
        FrameFormat format, int width, int height, int stride, byte[] pixels,
        MonitorInfo monitor, double sdrWhiteInBufferUnits)
    {
        Format = format;
        Width = width;
        Height = height;
        Stride = stride;
        Monitor = monitor;
        SdrWhiteInBufferUnits = sdrWhiteInBufferUnits;
        _pixels = pixels;
    }

    public ReadOnlySpan<byte> Row(int y)
    {
        var pixels = _pixels ?? throw new ObjectDisposedException(nameof(CapturedFrame));
        return pixels.AsSpan(y * Stride, Width * BytesPerPixel);
    }

    /// <summary>Reads pixel (x,y) in this frame's own buffer units (NOT nits) — for Fp16ScRgb this is
    /// the raw linear value (Windows scRGB or macOS EDR, per SdrWhiteInBufferUnits); for Bgra8Srgb
    /// this decodes the sRGB EOTF and rescales by Monitor.SdrWhiteNits/80.0, EXACTLY as the WPF app
    /// did (PLAN.md §2.2) — unchanged, not touched by the SdrWhiteInBufferUnits generalization.</summary>
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

    /// <summary>Photometric nits for the magnifier/color-inspector readout — see the class doc
    /// comment above for why the two formats are NOT both routed through the same formula.</summary>
    public double ReadPixelNits(int x, int y)
    {
        var v = ReadPixelScRgb(x, y);
        double bufferMax = Math.Max(v.X, Math.Max(v.Y, v.Z));
        if (Format == FrameFormat.Bgra8Srgb)
        {
            return bufferMax * 80.0; // unchanged from the WPF app — do not route through SdrWhiteInBufferUnits.
        }
        return bufferMax / SdrWhiteInBufferUnits * Monitor.SdrWhiteNits;
    }

    public void Dispose() => _pixels = null;
}
