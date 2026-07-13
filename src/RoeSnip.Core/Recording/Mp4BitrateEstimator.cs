using System;
using RoeSnip.Core.Recording.Gif;

namespace RoeSnip.Core.Recording;

/// <summary>Portable slice of the WPF app's own <c>Mp4Encoder</c>: the pure bitrate heuristic and the
/// AAC audio byte-rate constant, with zero Media Foundation/Vortice dependency, extracted so
/// <see cref="RecordingSizeEstimator"/> can compute its MP4 estimate without pulling Windows-only
/// encode machinery into this portable assembly. The WPF app's <c>Mp4Encoder.ComputeBitrate</c> and
/// <c>Mp4Encoder.AudioAacBytesPerSecond</c> delegate straight here — this is the one source of truth
/// for both numbers, not a parallel copy.</summary>
public static class Mp4BitrateEstimator
{
    /// <summary>Bitrate heuristic: 0.1 bits/pixel/frame (a standard "medium quality" H.264 rule of
    /// thumb), clamped to a sane [2, 16] Mbps band so a tiny selection doesn't starve the encoder
    /// and a huge one doesn't produce an unreasonably large file. This is the parameterless-preset
    /// overload: its behavior is pinned forever by <see cref="ComputeBitrate(int,int,int,GifSizePreset)"/>'s
    /// Quality case, which must return exactly this value — see that overload's own doc comment.</summary>
    public static long ComputeBitrate(int width, int height, int fps) =>
        Math.Clamp((long)(0.1 * width * height * fps), 2_000_000, 16_000_000);

    /// <summary>Recording-size-tiers overload: applies a per-tier multiplier to the same 0.1bpp
    /// heuristic <see cref="ComputeBitrate(int,int,int)"/> uses, each with its own clamp band so a
    /// tiny selection at any tier still gets a usable minimum bitrate and a huge one at any tier
    /// still has a ceiling. <see cref="GifSizePreset.Quality"/> is handled by delegating straight
    /// to the three-argument overload rather than recomputing "1.0x [2M,16M]" here a second time —
    /// that overload's existing tests (and every existing MP4 call site that doesn't pass a preset)
    /// must see byte-identical output to before this tiers workstream existed. The other tiers'
    /// factors/clamps (see the switch below) were chosen to bracket Quality symmetrically: Max
    /// roughly quadruples the target bitrate for near-visually-lossless H.264 at typical recording
    /// resolutions, Balanced/Compact roughly mirror the GIF-side Balanced/Compact size reduction so
    /// the two formats' tiers read as the same promise ("smaller, more compressed") even though the
    /// underlying codecs are unrelated. Minimal (quality/fps expansion workstream) continues that
    /// same downward slope at 0.15x — MP4/H.264 has no GIF-style palette/lossy-run lever to spend
    /// this tier's extra shrink on, so it is simply a lower target bitrate with its own, lower
    /// clamp band.</summary>
    public static long ComputeBitrate(int width, int height, int fps, GifSizePreset preset)
    {
        if (preset == GifSizePreset.Quality)
        {
            return ComputeBitrate(width, height, fps);
        }

        (double factor, long min, long max) = preset switch
        {
            GifSizePreset.Max => (4.0, 8_000_000L, 64_000_000L),
            GifSizePreset.Balanced => (0.6, 1_500_000L, 10_000_000L),
            GifSizePreset.Compact => (0.35, 1_000_000L, 6_000_000L),
            GifSizePreset.Minimal => (0.15, 500_000L, 3_000_000L),
            _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Unknown GifSizePreset."),
        };
        return Math.Clamp((long)(factor * 0.1 * width * height * fps), min, max);
    }

    /// <summary>128 kbps AAC, the standard screen-recording choice: 128000 / 8 = 16000 bytes/second
    /// — named here so both the WPF app's live encoder (its sink-writer media-type attribute) and
    /// <see cref="RecordingSizeEstimator"/>'s MP4 audio-overhead term cite the exact same figure
    /// instead of re-deriving/duplicating it.</summary>
    public const int AudioAacBytesPerSecond = 16_000;
}
