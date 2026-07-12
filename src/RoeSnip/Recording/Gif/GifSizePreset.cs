using System;

namespace RoeSnip.Recording.Gif;

/// <summary>User-facing GIF size/quality tier, picked from the recording chrome's Setup-only GIF
/// row (see RecordingChrome's GIF-only pill row) and persisted as a plain string on
/// <see cref="RoeSnip.RoeSnipSettings.GifSizePreset"/> (matching that record's string-not-enum
/// persistence convention — see its doc comment). A monotone progression from today's shipped
/// defaults (Quality, byte-identical to the pre-preset behavior) down to the most aggressive
/// shrink (Compact) — see <see cref="GifSizePresets.ForPreset"/> for the exact numbers and
/// <see cref="GifSizePresets.Parse"/> for the string mapping.</summary>
public enum GifSizePreset { Quality, Balanced, Compact }

/// <summary>Maps <see cref="GifSizePreset"/> to the concrete <see cref="GifEncoderOptions"/> tuning
/// it represents, and back from the persisted settings string. Kept as a separate static class
/// (rather than instance members on the enum, which C# doesn't support anyway) alongside the enum
/// itself so both pieces of the preset scheme live in one small file.
///
/// The three tiers were derived from a 54-encode calibration sweep at 640x400 across the
/// benchmark's static/scroll/noise content shapes (see GifSizeBenchmarkTests):
///   - ChannelTolerance 4->12 measured ZERO size effect on any swept content; it is kept as a
///     real-capture noise guard (sensor/compression dither on a live capture, absent from the
///     synthetic benchmarks), not a size lever, so it only inches up slightly per tier.
///   - MaxPaletteColors 255->128->64 measured -12%/-23% on color-rich (noise-like) content with NO
///     measurable quality-metric loss, and zero effect on low-color text/UI content — a strictly
///     good lever.
///   - The LargeMotion/HugeMotion area-fraction thresholds and their delay floors are the DOMINANT
///     lever: tightening the fractions (so more candidate frames count as "large"/"huge" motion)
///     and raising the floors (so those candidates wait longer between emits) measured scroll
///     -43% and noise -33% in the sweep at the values Balanced/Compact below start from. Bytes
///     scale ~linearly with emitted frame count on motion content, so this is the knob actually
///     tuned to hit the Balanced/Compact size gates in GifSizeBenchmarkTests.
///   - PaletteReuseErrorThreshold is raised in step with ChannelTolerance/ MaxPaletteColors (kept
///     equal to ChannelTolerance throughout) so a smaller, coarser palette doesn't force spurious
///     rebuilds it would otherwise still fit under a tighter reuse threshold.
/// DitherErrorFloor and KeyframeInterval are left at their <see cref="GifEncoderOptions"/> defaults
/// in every tier — neither showed up as a size lever in the sweep, and both affect visual behavior
/// (dither gating, re-baseline cadence) orthogonal to what these presets are meant to trade off.</summary>
public static class GifSizePresets
{
    /// <summary>Today's shipped GifEncoder defaults, unchanged — byte-identical output to every
    /// GIF recorded before this preset scheme existed. The other two tiers are a strict shrink
    /// from these numbers, never a quality improvement past them.</summary>
    public static GifEncoderOptions ForPreset(GifSizePreset preset) => preset switch
    {
        GifSizePreset.Quality => new GifEncoderOptions(
            ChannelTolerance: 4,
            PaletteReuseErrorThreshold: 4,
            MaxPaletteColors: 255,
            LargeMotionAreaFraction: 0.30,
            LargeMotionDelayFloorCs: 5,
            HugeMotionAreaFraction: 0.65,
            HugeMotionDelayFloorCs: 8),

        GifSizePreset.Balanced => new GifEncoderOptions(
            ChannelTolerance: 6,
            PaletteReuseErrorThreshold: 6,
            MaxPaletteColors: 128,
            LargeMotionAreaFraction: 0.10,
            LargeMotionDelayFloorCs: 6,
            HugeMotionAreaFraction: 0.50,
            HugeMotionDelayFloorCs: 12),

        // Floor values verified against GifSizeBenchmarkTests (bytes/sec on the scroll and noise
        // scenarios): measured scroll 51,742.9 B/s (29.9% of Quality's 173,279.0 B/s) and noise
        // 1,664,808.6 B/s (38.8% of Quality's 4,293,334.0 B/s), both comfortably inside the <=55%
        // budget, with scroll's emitted frame count still well above the >=50-frames-over-10s (>=5fps
        // effective) floor — so the calibration sweep's starting-point floors (8cs/16cs) were kept
        // as-is rather than pushed further; there was no size/smoothness tradeoff left to spend.
        GifSizePreset.Compact => new GifEncoderOptions(
            ChannelTolerance: 8,
            PaletteReuseErrorThreshold: 8,
            MaxPaletteColors: 64,
            LargeMotionAreaFraction: 0.05,
            LargeMotionDelayFloorCs: 8,
            HugeMotionAreaFraction: 0.40,
            HugeMotionDelayFloorCs: 16),

        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Unknown GifSizePreset."),
    };

    /// <summary>Parses the persisted settings string (<see cref="RoeSnip.RoeSnipSettings.GifSizePreset"/>)
    /// case-insensitively; any unrecognized or missing value fails SAFE to <see cref="GifSizePreset.Quality"/>
    /// rather than throwing, matching the rest of this settings record's fail-closed-to-default
    /// convention (see SettingsStore.Load's own doc comment) — a garbled or future-schema value on
    /// disk must never crash the recording flow, only silently fall back to today's behavior.</summary>
    public static GifSizePreset Parse(string? value) =>
        Enum.TryParse<GifSizePreset>(value, ignoreCase: true, out var preset) ? preset : GifSizePreset.Quality;
}
