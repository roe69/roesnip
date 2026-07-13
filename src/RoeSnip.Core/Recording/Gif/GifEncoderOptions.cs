using System;

namespace RoeSnip.Core.Recording.Gif;

/// <summary>Every tunable in the delta/palette scheme documented on
/// <see cref="RoeSnip.Core.Recording.GifEncoder"/>, gathered into one immutable record instead of
/// scattered constants so the whole scheme's numbers can be read (and, in tests, overridden) in
/// one place. Defaults are the values the design was tuned against; production code should pass
/// <see langword="null"/> (the parameterless default) unless a test specifically needs different
/// thresholds.
///
/// QUALITY ONLY: this record no longer carries any framerate/pacing knob. Quality and framerate
/// are orthogonal user choices (the quality/framerate decoupling workstream) — how faithfully a
/// frame is reproduced (tolerance, palette size, dithering) is entirely this record's concern, and
/// how OFTEN a frame is captured at all is entirely the capture-side schedule throttle's concern
/// (RegionRecorder). The two used to be tangled: emit-rate delay floors keyed to how much of the
/// canvas a candidate frame's bbox covered used to live here and silently throttle a GIF's
/// effective framerate as a side effect of picking a size/quality tier. That scheme is gone —
/// see GifEncoder's class doc for what replaced it.</summary>
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
/// <param name="KeyframeInterval">How long (media/timestamp clock, not wall clock) between
/// full-canvas re-baseline keyframes; <see langword="null"/> means the default of 15 seconds.
/// Keyframes only piggyback on a frame that would emit anyway, so a take that never changes emits
/// no keyframes and never drifts, by construction.</param>
/// <param name="LossyRunThresholdSq">Gifsicle-style lossy run-extension (quality/fps expansion
/// workstream): redmean-SQUARED distance (same metric/units as
/// <see cref="GifNearestColorLut"/>'s own nearest-color search — see <see cref="GifDelta"/>'s class
/// doc for the exact rule). 0 (the default) disables the mechanism entirely, giving Max/Quality
/// their documented bit-exact-vs-pre-workstream behavior. Above 0, a changed pixel that is close
/// enough to the ADJACENT already-painted palette entry reuses that entry instead of a fresh LUT
/// lookup, turning near-solid runs (UI fills, glyph anti-aliasing, scroll-band backgrounds) into
/// genuinely longer LZW runs — the real per-tier size lever this workstream adds, since sweeping
/// ChannelTolerance/MaxPaletteColors alone (the pre-workstream tiers) was found to barely move the
/// needle on the text/UI-dominant content this app actually records.</param>
/// <param name="RenderScale">Quality/fps expansion workstream: box-filter downsample factor applied
/// to the captured canvas BEFORE it ever reaches the diff/palette pipeline (1.0, the default, is a
/// no-op — every pre-workstream tier keeps its native resolution). Must be 0.25-1.0. Below 1.0, a
/// real pixel-count cut (not a quantization trick) — RecordingController box-filters the tone-mapped
/// frame into a smaller reused buffer once per frame (no per-frame allocation, see that class' own
/// LOH/Gen2 discipline note) and GifEncoder.Create is handed the SCALED canvas dimensions, floored
/// to even. Two tiers use this: Minimal at 0.5 (a 16-color palette and even a generous lossy
/// threshold run out of headroom on genuinely color-rich content, and a smaller canvas is the only
/// remaining lever once those two are already maxed out) and Compact at 0.75 (the tier-spread
/// pass — once the 2026-07-13 retune visually constrained the lossy threshold it only bought ~13%,
/// leaving Compact bunched against Balanced; resolution is the one honest lever left that shrinks
/// bytes without re-introducing the streaking that retune fixed — see GifSizePresets' own TIER
/// SPREAD doc paragraph).</param>
public sealed record GifEncoderOptions(
    byte ChannelTolerance = 4,
    byte PaletteReuseErrorThreshold = 4,
    int MaxPaletteColors = 255,
    byte DitherErrorFloor = 3,
    TimeSpan? KeyframeInterval = null,
    int LossyRunThresholdSq = 0,
    double RenderScale = 1.0)
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

    /// <summary>Redeclared purely to validate at construction — same reasoning as
    /// <see cref="MaxPaletteColors"/>'s own redeclaration above: a scale outside 0.25-1.0 would
    /// either upscale (RenderScale &gt; 1.0, nonsensical — this option only ever shrinks the
    /// canvas) or downscale so far the canvas could floor to zero in
    /// RecordingController's floor-to-even math, both better caught here than deep inside the
    /// capture pipeline.</summary>
    public double RenderScale { get; init; } = RenderScale is >= 0.25 and <= 1.0
        ? RenderScale
        : throw new ArgumentOutOfRangeException(nameof(RenderScale), RenderScale,
            "RenderScale must be 0.25-1.0 — this option only ever shrinks the captured canvas, never enlarges it.");

    /// <summary>Resolves <see cref="KeyframeInterval"/>'s null-means-15s default.</summary>
    public TimeSpan EffectiveKeyframeInterval => KeyframeInterval ?? DefaultKeyframeInterval;
}
