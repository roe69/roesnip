using System;

namespace RoeSnip.Core.Capture;

/// <summary>Sanity checks on captured pixel buffers (DESIGN.md "Failure modes": the DD
/// black-frame quirk observed on NVIDIA RTX + HDR — a structurally-successful capture whose
/// buffer is all zeros, including alpha, which a real desktop frame never has).</summary>
public static class FrameSanity
{
    /// <summary>True if every byte in the buffer is zero. Early-exits on the first nonzero
    /// byte (vectorized via IndexOfAnyExcept). An empty buffer is trivially all-zero.</summary>
    public static bool IsAllZero(ReadOnlySpan<byte> buffer) =>
        buffer.IndexOfAnyExcept((byte)0) < 0;

    /// <summary>True if every non-alpha byte in a BGRA8 buffer is zero — byte offset 3 of each
    /// 4-byte pixel (alpha) is skipped. For capturers that unconditionally force alpha to 255
    /// (e.g. X11's ConvertZPixmapToBgra, which has no real per-pixel alpha to read), <see cref="IsAllZero"/>
    /// can never fire because the forced alpha bytes are never zero — this variant is what those
    /// capturers must use instead. An empty buffer is trivially all-zero-color, matching
    /// <see cref="IsAllZero"/>'s convention.</summary>
    public static bool IsAllZeroColor(ReadOnlySpan<byte> bgra)
    {
        for (int i = 0; i < bgra.Length; i++)
        {
            if (i % 4 == 3) continue; // alpha byte — excluded
            if (bgra[i] != 0) return false;
        }
        return true;
    }
}
