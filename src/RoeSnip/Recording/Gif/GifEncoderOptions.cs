using System;

namespace RoeSnip.Recording.Gif;

/// <summary>Every tunable in the delta/palette/pacing scheme documented on <see cref="RoeSnip.Recording.GifEncoder"/>,
/// gathered into one immutable record instead of scattered constants so the whole scheme's numbers
/// can be read (and, in tests, overridden) in one place. Defaults are the values the design was
/// tuned against; production code should pass <see langword="null"/> (the parameterless default)
/// unless a test specifically needs different thresholds.</summary>
/// <param name="ChannelTolerance">Per-channel (B/G/R independently) tolerance, in 0-255 units,
/// below which a pixel is considered unchanged from the last-painted baseline and mapped to the
/// reserved transparent index instead of being repainted.</param>
/// <param name="PaletteReuseErrorThreshold">Palette reuse fast path: if every newly-painted pixel
/// this frame is within this many units (max-channel, i.e. Chebyshev) of the EXISTING palette's
/// entry it would actually be quantized to (nearest by the same redmean metric
/// <see cref="GifNearestColorLut"/> uses, not simply Chebyshev-nearest), keep that palette rather
/// than rebuilding — see <see cref="GifOctreeQuantizer.MaxErrorAgainst"/>.</param>
/// <param name="MaxPaletteColors">Hard cap on opaque palette colors per frame (one further Local
/// Color Table slot beyond this is always reserved for the transparent index — see the class doc
/// on GifEncoder). Must be 1-255: the GIF89a Image Descriptor's LCT-size field is only 3 bits
/// (physical table size 2-256), and the reserved transparent slot always eats one more, so 255 is
/// the true per-frame ceiling GifEncoder.WriteFrame's size-bits/minCodeSize math can represent —
/// validated here rather than left to fail deep inside WriteFrame with a corrupted Image
/// Descriptor already flushed to disk.</param>
/// <param name="DitherErrorFloor">Bayer ordered dither is gated: only applied to a pixel when its
/// chosen palette entry's per-channel error exceeds this floor. An exact (or near-exact) palette
/// hit is mapped bit-exact, undithered — dithering a pixel that already matches would only add
/// noise.</param>
/// <param name="LargeMotionAreaFraction">Below this fraction of canvas area changed, a candidate
/// frame has no extra delay floor beyond the normal patch-behind timing.</param>
/// <param name="LargeMotionDelayFloorCs">Minimum centiseconds since the last EMITTED frame before a
/// candidate whose changed bbox covers at least <paramref name="LargeMotionAreaFraction"/> of the
/// canvas is allowed to emit; an earlier arrival is skipped (its time folds into the previous
/// frame's patched delay).</param>
/// <param name="HugeMotionAreaFraction">Above this fraction of canvas area changed, the stricter
/// <paramref name="HugeMotionDelayFloorCs"/> applies instead of
/// <paramref name="LargeMotionDelayFloorCs"/>.</param>
/// <param name="HugeMotionDelayFloorCs">Minimum centiseconds since the last emitted frame for a
/// candidate whose changed bbox covers at least <paramref name="HugeMotionAreaFraction"/> of the
/// canvas.</param>
/// <param name="KeyframeInterval">How long (media/timestamp clock, not wall clock) between
/// full-canvas re-baseline keyframes; <see langword="null"/> means the default of 15 seconds.
/// Keyframes only piggyback on a frame that would emit anyway, so a take that never changes emits
/// no keyframes and never drifts, by construction.</param>
public sealed record GifEncoderOptions(
    byte ChannelTolerance = 4,
    byte PaletteReuseErrorThreshold = 4,
    int MaxPaletteColors = 255,
    byte DitherErrorFloor = 3,
    double LargeMotionAreaFraction = 0.30,
    ushort LargeMotionDelayFloorCs = 5,
    double HugeMotionAreaFraction = 0.65,
    ushort HugeMotionDelayFloorCs = 8,
    TimeSpan? KeyframeInterval = null)
{
    private static readonly TimeSpan DefaultKeyframeInterval = TimeSpan.FromSeconds(15);

    /// <summary>Redeclared (rather than left as the plain compiler-synthesized positional property)
    /// purely to validate at construction — see the parameter's doc comment for why 255 is a hard
    /// format ceiling, not a tuning knob. Left unvalidated, a value like 256 would sail through
    /// <see cref="GifOctreeQuantizer.BuildPalette"/> and only blow up deep inside
    /// GifEncoder.WriteFrame, after that frame's (by then corrupted) Image Descriptor and Local
    /// Color Table bytes are already written to the output stream.</summary>
    public int MaxPaletteColors { get; init; } = MaxPaletteColors is >= 1 and <= 255
        ? MaxPaletteColors
        : throw new ArgumentOutOfRangeException(nameof(MaxPaletteColors), MaxPaletteColors,
            "MaxPaletteColors must be 1-255 — the GIF89a Local Color Table format cannot represent more opaque colors than that once the reserved transparent slot is added.");

    /// <summary>Resolves <see cref="KeyframeInterval"/>'s null-means-15s default.</summary>
    public TimeSpan EffectiveKeyframeInterval => KeyframeInterval ?? DefaultKeyframeInterval;
}
