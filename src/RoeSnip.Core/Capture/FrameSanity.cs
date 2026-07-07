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
}
