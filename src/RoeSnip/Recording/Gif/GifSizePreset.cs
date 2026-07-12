using System;

namespace RoeSnip.Recording.Gif;

/// <summary>User-facing recording size/quality tier, picked from the recording chrome's Setup-only
/// size row (shown for BOTH formats as of the size-tiers workstream — see RecordingChrome's size
/// preset row) and persisted as a plain string, per format, on
/// <see cref="RoeSnip.RoeSnipSettings.GifSizePreset"/> / <see cref="RoeSnip.RoeSnipSettings.Mp4SizePreset"/>
/// (matching that record's string-not-enum persistence convention — see its doc comment). Despite
/// the "Gif"-namespaced home (kept here rather than moved to a format-neutral namespace — see this
/// file's own history for that call), the enum is used by both GIF and MP4 takes: MP4's
/// <see cref="RoeSnip.Recording.Mp4Encoder.ComputeBitrate(int,int,int,GifSizePreset)"/> overload
/// consumes it too, rather than inventing a parallel MP4-only enum for what is the same four-tier
/// concept.
///
/// Enum member names and persisted strings are unchanged by the quality/framerate decoupling
/// workstream (settings compat — a preset string written to disk before that workstream still
/// parses the same way after it). Display labels shown to the user read Max/High/Medium/Low/Min —
/// see <see cref="GifSizePresets.DisplayLabel"/> for that mapping.
///
/// A monotone progression from Max (the most faithful reproduction GIF's palette format can offer)
/// through today's shipped defaults (Quality, byte-identical to the pre-preset behavior) down to
/// the most aggressive shrink (Minimal, added by the quality/fps expansion workstream — the first
/// tier with a real lossy lever beyond palette size: run-extension color reuse plus a half-
/// resolution render, see <see cref="GifEncoderOptions.LossyRunThresholdSq"/> and
/// <see cref="GifEncoderOptions.RenderScale"/>) — see <see cref="GifSizePresets.ForPreset"/> for
/// the exact numbers and <see cref="GifSizePresets.Parse"/> for the string mapping.</summary>
public enum GifSizePreset { Max, Quality, Balanced, Compact, Minimal }

/// <summary>Maps <see cref="GifSizePreset"/> to the concrete <see cref="GifEncoderOptions"/> tuning
/// it represents, and back from the persisted settings string. Kept as a separate static class
/// (rather than instance members on the enum, which C# doesn't support anyway) alongside the enum
/// itself so both pieces of the preset scheme live in one small file.
///
/// QUALITY ONLY: since the quality/framerate decoupling workstream, every tier trades off
/// per-pixel tolerance, palette reuse threshold, and palette color count — and nothing about HOW
/// OFTEN a frame is captured. The four original tiers were derived from a 54-encode calibration
/// sweep at 640x400 across the benchmark's static/scroll/noise content shapes (see
/// GifSizeBenchmarkTests):
///   - ChannelTolerance 4->12 measured ZERO size effect on any swept content; it is kept as a
///     real-capture noise guard (sensor/compression dither on a live capture, absent from the
///     synthetic benchmarks), not a size lever, so it only inches up slightly per tier.
///   - MaxPaletteColors 255->128->64 measured -12%/-23% on color-rich (noise-like) content with NO
///     measurable quality-metric loss, and zero effect on low-color text/UI content — a strictly
///     good lever, but on its own not enough to make Balanced/Compact/Minimal (see below) actually
///     look meaningfully different on typical low-color screen content (the sweep's dominant lever,
///     motion-keyed emit-rate delay floors, was deleted along with the framerate coupling — see
///     GifEncoderOptions' and GifEncoder's class docs for what replaced it: the capture-side
///     schedule throttle, driven directly by the user's chosen fps, independent of this tier
///     choice).
///   - PaletteReuseErrorThreshold is raised in step with ChannelTolerance/MaxPaletteColors (kept
///     equal to ChannelTolerance throughout) so a smaller, coarser palette doesn't force spurious
///     rebuilds it would otherwise still fit under a tighter reuse threshold.
/// DitherErrorFloor and KeyframeInterval are left at their <see cref="GifEncoderOptions"/> defaults
/// in every tier — neither showed up as a size lever in the sweep, and both affect visual behavior
/// (dither gating, re-baseline cadence) orthogonal to what these presets are meant to trade off.
///
/// QUALITY/FPS EXPANSION (Balanced/Compact/Minimal): the four-tier sweep above found palette size
/// alone was a weak lever on the text/UI-dominant content this app actually records. Balanced,
/// Compact and Minimal now ALSO carry a nonzero <see cref="GifEncoderOptions.LossyRunThresholdSq"/>
/// — gifsicle-style lossy run-extension that reuses an adjacent already-painted palette index when
/// the new pixel is perceptually close enough (redmean-squared), turning near-solid runs (UI fills,
/// anti-aliased glyph edges, scroll-band backgrounds) into genuinely longer LZW runs instead of
/// alternating between two or three near-identical palette entries. Compact and Minimal
/// additionally shrink the captured canvas via <see cref="GifEncoderOptions.RenderScale"/> (a real
/// pixel-count cut, not a quantization trick): Compact at 0.75 (added by the tier-spread pass, see
/// the TIER SPREAD paragraph below) and Minimal at 0.5. Minimal also drops to a 16-color palette
/// (down from the section-4 starting point of 32 — widened during calibration, see below) since
/// even a generous lossy threshold alone could not close the gap on this benchmark's structured
/// scroll content.
///
/// CALIBRATION (GifSizeBenchmarkTests.QualityAxis_BlendedScrollNoiseRatio_MeetsCalibratedTargets,
/// 640x400@25fps, blended = mean of the scroll and noise scenarios' byte ratio vs. Quality/High):
///   tier      scroll ratio   noise ratio   blended   target band
///   Max       1.068          1.000         1.034     (>= 1.0, no band)
///   Quality   1.000          1.000         1.000     (reference)
///   Balanced  0.961          0.884         0.922     0.85-0.97
///   Compact   0.588          0.409         0.498     0.42-0.58
///   Minimal   0.371          0.007         0.189     &lt;= 0.2
/// Monotonicity Minimal &lt; Compact &lt; Balanced &lt; Quality &lt;= Max holds throughout.
///
/// TIER SPREAD (2026-07-13, applied AFTER the visual retune described below): the retune
/// deliberately traded byte savings for smooth-content visual quality, which left Compact's
/// blended ratio at 0.865 — bunched within ~6% of Balanced's 0.922, because once the lossy run
/// threshold is visually constrained it only buys ~13% over Quality on this benchmark's content
/// mix. A "Low" tier that is a near-alias of "Medium" is not a real user choice, so Compact gained
/// RenderScale 0.75: the same honest resolution-based lever Minimal already used at 0.5, a real
/// 0.5625x pixel-count cut, rather than yet another quantization knob the visual gates would
/// immediately constrain right back. Measured effect: blended 0.865 -> 0.498 (scroll
/// 0.967 -> 0.588, noise 0.762 -> 0.409), almost exactly the 0.5625 x 0.865 ~= 0.49 pixel-factor
/// prediction — scroll lands a little ABOVE it because GIF's fixed per-frame GCE/Image-Descriptor
/// header bytes don't shrink with the canvas, noise a little BELOW because a smaller canvas also
/// hands the 64-entry palette fewer distinct colors to lose. Text stays fully legible at 0.75,
/// just softer than Balanced, and GifSizeBenchmarkTests' permanent scale-aware visual gates hold
/// that line: Compact scroll meanErr measured 1.26 (vs its own box-downsampled source — the same
/// ground-truth convention Minimal's gate already used), gradient meanErr 19.26, essentially
/// unchanged from the full-resolution 19.33 since the lossy threshold, not the scale, dominates
/// gradient error. The RETUNED paragraph below predates this change: its Compact figures (scroll
/// 0.967 / noise 0.762 / blended 0.865) describe the retune's own full-resolution outcome, i.e.
/// the exact starting point this paragraph's 0.75 scale was then applied to.
///
/// RETUNED
/// 2026-07-13 (visual QA pass, see LossyRunThresholdSq's per-tier comments below for the specific
/// numbers): the ORIGINAL calibration above (Balanced 90,000 / Compact 350,000, blended 0.742/0.415)
/// was tuned purely against these byte-ratio targets with no gradient/photo-content visual gate, and
/// at those thresholds Compact's redmean-squared distance ceiling worked out to ~77% of the maximum
/// possible (black-vs-white) redmean distance — generous enough that on smooth content (a diagonal
/// RGB gradient, a plasma/photo-like scene; see TierQaHarness, since deleted, and the meanErr-bounded
/// gates this pass added below) it produced visible horizontal/diagonal STREAKING: the lossy run
/// tracker's anchor color stays fixed while a whole run of pixels drifts away from it, so a threshold
/// generous enough to help the noise scenario a lot was also generous enough to merge dozens of
/// pixels of a slow gradient into one flat streak. The retuned thresholds below trade most of that
/// noise-scenario byte savings away (noise ratio 0.519->0.884 for Balanced, 0.036->0.762 for Compact)
/// specifically because smooth-content visual quality, not the byte target, is the real requirement
/// — see this class's own per-tier comments for the exact "why this number" reasoning. The scroll
/// column barely moved (0.966->0.961 Balanced, 0.795->0.967 Compact — the tiny Compact INCREASE is
/// real, not a bug: a smaller lossy threshold occasionally lets ChannelTolerance/dither differences
/// alone produce a marginally larger delta than the old threshold's aggressive merging did on this
/// specific structured content) because scroll's deliberately well-separated flat colors were always
/// mostly untouched by run-extension at ANY reasonable threshold — see GifSizeBenchmarkTests.
/// ScrollFrame's own doc comment. RecordingSizeEstimator.GifTypicalBytesPerSecond's qFactors are set
/// to these exact blended numbers — see that class's own doc comment.</summary>
public static class GifSizePresets
{
    /// <summary>Today's shipped GifEncoder defaults, unchanged — byte-identical output to every
    /// GIF recorded before this preset scheme existed. Balanced/Compact/Minimal are a strict
    /// shrink from these numbers; Max is the one tier ABOVE Quality — see its own doc comment below
    /// for the honest framing of what "lossless" means for a palette-limited format.</summary>
    public static GifEncoderOptions ForPreset(GifSizePreset preset) => preset switch
    {
        // As lossless as GIF gets: pixel-exact deltas (ChannelTolerance 0 — a change of even one
        // unit on any channel repaints the pixel, no "close enough" fudge). NOT pixel-perfect in an
        // absolute sense, and it would be dishonest to claim so: GIF's Local Color Table still caps
        // out at 255 opaque colors per frame (MaxPaletteColors: 255, the format's own hard ceiling
        // — see GifEncoderOptions' doc comment), so any single frame whose changed region genuinely
        // contains more than 255 distinct colors (e.g. a photo-like gradient or video-like noise)
        // still gets quantized and dithered down to fit, same as every other tier. What Max
        // actually buys over Quality is a strict palette reuse threshold (0, keyed to
        // ChannelTolerance — see PaletteReuseErrorThreshold's own doc comment) so a palette is only
        // reused when the new frame's pixels are already an EXACT match to existing entries, never
        // an approximate one. DitherErrorFloor/KeyframeInterval are left at GifEncoderOptions' own
        // defaults, same as every other tier (see this class' own doc comment for why those two are
        // orthogonal to the size/quality tradeoff these presets tune).
        GifSizePreset.Max => new GifEncoderOptions(
            ChannelTolerance: 0,
            PaletteReuseErrorThreshold: 0,
            MaxPaletteColors: 255),

        GifSizePreset.Quality => new GifEncoderOptions(
            ChannelTolerance: 4,
            PaletteReuseErrorThreshold: 4,
            MaxPaletteColors: 255),

        // LossyRunThresholdSq 1,500 (RETUNED 2026-07-13, was 90,000 — see this class's own doc
        // comment for the full before/after table and why): sqrt(1500) is a redmean-squared distance
        // small enough that on a slow diagonal gradient (~1.66 combined-channel units of drift per
        // adjacent pixel at this benchmark's translation speed) the lossy run tracker's fixed anchor
        // only holds for roughly a dozen pixels before a fresh LUT lookup resets it — visually just a
        // faint fine-grained texture on a smooth ramp, "barely distinguishable from Quality at arm's
        // length" (the calibration mandate's own phrase), rather than the old threshold's 60-plus-
        // pixel runs that read as an obvious streak. Scroll's blended ratio barely moves (0.961, was
        // 0.966) because scroll's flat well-separated colors were never the mechanism's real target;
        // the actual cost of this retune shows up in the noise ratio instead (0.884, was 0.519) —
        // accepted deliberately, since a byte target was never the actual requirement here.
        GifSizePreset.Balanced => new GifEncoderOptions(
            ChannelTolerance: 6,
            PaletteReuseErrorThreshold: 6,
            MaxPaletteColors: 128,
            LossyRunThresholdSq: 1500),

        // LossyRunThresholdSq 12,000 (RETUNED 2026-07-13, was 350,000 — see this class's own doc
        // comment for the full before/after table and why): the OLD number's sqrt was ~77% of the
        // maximum possible redmean distance (black vs. white), which turned a smooth gradient/plasma
        // scene into an obvious multi-pixel-wide streak — nowhere near "clearly degraded but not
        // streaky-broken" (the calibration mandate's own phrase for this tier). 12,000 still lets
        // Compact visibly out-degrade Balanced on a gradient (a clearly visible diagonal banding
        // texture, not Balanced's near-imperceptible fine grain) while keeping the gradient's actual
        // shape and color structure fully intact, and scroll text stays fully legible throughout —
        // the mechanism still barely engages on scroll's flat colors at any reasonable threshold.
        // Noise ratio at full resolution recovered from 0.036 to 0.762 in that retune for the same
        // reason as Balanced's retune above: a byte target was never the actual requirement.
        //
        // RenderScale 0.75 (TIER SPREAD pass, 2026-07-13 — see this class's own doc comment for the
        // full story): the retuned lossy threshold left Compact's blended ratio (0.865) bunched
        // within ~6% of Balanced's, so the "Low" tier gets its real size step from resolution
        // instead — a 0.5625x pixel-count cut that took the measured blended ratio to 0.498 while
        // keeping text fully legible (RecordingController.BoxDownsampleGifFrame's area-average
        // ratio math is scale-generic, so 0.75's mixed 1-and-2-pixel source rects need no special
        // code path — see that method's own doc comment).
        GifSizePreset.Compact => new GifEncoderOptions(
            ChannelTolerance: 8,
            PaletteReuseErrorThreshold: 8,
            MaxPaletteColors: 64,
            LossyRunThresholdSq: 12000,
            RenderScale: 0.75),

        // The most aggressive tier: a 16-color palette (widened down from the section-4 starting
        // point of 32 during calibration — "if truly needed", and it was), a generous lossy run
        // threshold, AND a half-resolution render (RenderScale 0.5 — a real 4x pixel-count cut once
        // combined with the 2x2 box filter's own smoothing, not just a quantization trick) so the
        // blended ratio can clear the <=0.2 target the four-lever tiers above cannot reach on their
        // own. Still decoded and mean-error-checked against its own (downsampled) source in
        // GifSizeBenchmarkTests.Minimal_Scroll_VisuallySane_MeanErrorBounded (a loose bound, not a
        // pixel-exact one) so calibration can never converge on illegible garbage.
        GifSizePreset.Minimal => new GifEncoderOptions(
            ChannelTolerance: 12,
            PaletteReuseErrorThreshold: 12,
            MaxPaletteColors: 16,
            LossyRunThresholdSq: 120000,
            RenderScale: 0.5),

        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Unknown GifSizePreset."),
    };

    /// <summary>Parses the persisted settings string (<see cref="RoeSnip.RoeSnipSettings.GifSizePreset"/>)
    /// case-insensitively; any unrecognized or missing value fails SAFE to <see cref="GifSizePreset.Quality"/>
    /// rather than throwing, matching the rest of this settings record's fail-closed-to-default
    /// convention (see SettingsStore.Load's own doc comment) — a garbled or future-schema value on
    /// disk must never crash the recording flow, only silently fall back to today's behavior.</summary>
    public static GifSizePreset Parse(string? value) =>
        Enum.TryParse<GifSizePreset>(value, ignoreCase: true, out var preset) ? preset : GifSizePreset.Quality;

    /// <summary>User-facing chip label for the recording chrome's size row (quality/framerate
    /// decoupling workstream, stage 3; extended to five tiers by the quality/fps expansion
    /// workstream) — deliberately NOT the enum member's own name: the enum members and persisted
    /// settings strings stay Max/Quality/Balanced/Compact/Minimal unchanged (settings compat, see
    /// this file's own class doc comment), but the row reads Max/High/Medium/Low/Min so the scale
    /// doesn't visually collide with the sibling FPS row's own numeric label. A pure function (no
    /// chrome/UI dependency) so the mapping is unit-testable directly rather than only observable
    /// through a live WPF window.</summary>
    public static string DisplayLabel(GifSizePreset preset) => preset switch
    {
        GifSizePreset.Max => "Max",
        GifSizePreset.Quality => "High",
        GifSizePreset.Balanced => "Medium",
        GifSizePreset.Compact => "Low",
        GifSizePreset.Minimal => "Min",
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Unknown GifSizePreset."),
    };
}
