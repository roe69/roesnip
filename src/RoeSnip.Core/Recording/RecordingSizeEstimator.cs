using System;
using System.Globalization;
using RoeSnip.Core.Recording.Gif;

namespace RoeSnip.Core.Recording;

/// <summary>Pure, allocation-light math behind the recording chrome's live "this will be about X
/// big" readout (recording-size-tiers workstream). No dependency on GifEncoder/Mp4Encoder's live
/// pipeline objects — everything here is derived from the same inputs the chrome already has at
/// hand (selection size, the checked size chip, the audio toggles), so it can run on every chip
/// click / region resize / audio toggle without touching the encoder at all. Kept in its own file,
/// unit-tested directly (RecordingSizeEstimatorTests), rather than folded into RecordingChrome —
/// that class stays thin UI glue that calls into this math, never owns it.</summary>
public static class RecordingSizeEstimator
{
    /// <summary>GIF capture framerate bounds (quality/fps expansion workstream — replaces the old
    /// four-value allowed set with a free-slider range, see RecordingChrome's FPS row). The upper
    /// bound is a HARD format ceiling, not a taste choice: GIF89a's Graphic Control Extension delay
    /// field is whole centiseconds, and every real decoder clamps a 0-1cs delay up to 10cs (see
    /// GifEncoder's MinDelayCs/ProvisionalDelayCs), so 2cs (50fps) is the fastest a frame can
    /// legitimately be told to hold for — 60fps (1.67cs) is simply unrepresentable. Below that
    /// ceiling, ANY integer fps from 5 to 50 is legal even though most don't divide 100 evenly
    /// (e.g. 37fps -> 2.70cs/frame): GifEncoder.PatchLastDelay rounds each frame's real duration to
    /// the nearest whole centisecond and carries the sub-centisecond remainder forward
    /// (<c>_delayCarryCs</c>) into the next frame's rounding, so a long run at a non-divisor rate
    /// still averages out to EXACTLY the requested fps over time instead of silently drifting —
    /// that carry-forward is what makes the old four-divisor-only restriction unnecessary. The 5fps
    /// floor is a sanity minimum (a capture slower than that stops feeling like a "recording" at
    /// all), not a format constraint.</summary>
    public const int GifMinFps = 5;
    public const int GifMaxFps = 50;

    /// <summary>MP4 capture framerate bounds. MP4/H.264 has no GIF-style whole-centisecond
    /// constraint (Media Foundation's FrameRate attribute is an arbitrary rational, see the WPF
    /// app's Mp4Encoder.Create's MFSetAttributeRatio call), so the ceiling here is simply a sane
    /// upper bound for screen-recording content, not a hard format limit the way GIF's 50fps is.</summary>
    public const int Mp4MinFps = 5;
    public const int Mp4MaxFps = 60;

    public const int GifDefaultFps = 25;
    public const int Mp4DefaultFps = 30;

    /// <summary>Clamps an arbitrary int (e.g. a parsed-but-unvalidated settings value, or a slider
    /// drag that briefly overshoots) into <paramref name="minFps"/>..<paramref name="maxFps"/> —
    /// used by settings parsing (so a garbled or future-schema fps value on disk fails safe to the
    /// nearest legal value rather than crashing or silently keeping an invalid capture rate) and by
    /// RecordingChrome's slider/automation's `fps` command (so neither can ever hand a
    /// live session an out-of-range value). Replaces the old allowed-set + SnapFps
    /// nearest-neighbor scheme now that fps is a free integer slider, not four fixed choices.</summary>
    public static int ClampFps(int requested, int minFps, int maxFps) => Math.Clamp(requested, minFps, maxFps);

    /// <summary>Bytes/second an MP4 take at this preset is expected to produce: the video bitrate
    /// (see <see cref="Mp4BitrateEstimator.ComputeBitrate(int,int,int,GifSizePreset)"/>) converted
    /// from bits to bytes, plus the AAC track's own byte rate when audio is enabled — see
    /// <see cref="Mp4BitrateEstimator.AudioAacBytesPerSecond"/> for that figure and where it's cited
    /// on the WPF app's own live encoder side. Unlike GIF's estimate below, this one is a tight
    /// bound, not a "typical activity" guess: MF's sink writer targets the requested AvgBitrate
    /// continuously regardless of motion, so the number here is what an MP4 take of ANY content
    /// actually produces.
    ///
    /// Already fps-parameterized before the quality/framerate decoupling workstream — ComputeBitrate
    /// scales its 0.1-bits/pixel/frame heuristic by fps directly, so the quality axis (preset) and
    /// the framerate axis (fps) already compose correctly here with no formula change needed;
    /// only the GIF estimate below needed a formula rewrite to add the same composition.</summary>
    public static double Mp4BytesPerSecond(int width, int height, int fps, GifSizePreset preset, bool audioEnabled)
    {
        long videoBitrate = Mp4BitrateEstimator.ComputeBitrate(width, height, fps, preset);
        double videoBytesPerSecond = videoBitrate / 8.0;
        return videoBytesPerSecond + (audioEnabled ? Mp4BitrateEstimator.AudioAacBytesPerSecond : 0);
    }

    /// <summary>Quality-tier multiplier on the per-pixel-per-frame constant below, one per
    /// <see cref="GifSizePreset"/>. QUALITY/FPS EXPANSION WORKSTREAM: these are no longer a blended
    /// guess — GifSizeBenchmarkTests.QualityAxis_BlendedScrollNoiseRatio_MeetsCalibratedTargets
    /// measured each tier's actual blended (mean of scroll and noise) byte ratio against
    /// High/Quality at 640x400@25fps, and this class's factors are set to EXACTLY those measured
    /// numbers, so the live estimate tracks what the calibrated lossy-run/palette/scale levers
    /// actually produce rather than a hand-picked approximation. Measured ratio table (also cited
    /// on GifSizePresets.ForPreset's own class doc, the levers' home):
    ///   tier      scroll   noise    blended
    ///   Max       1.068    1.000    1.034
    ///   Quality   1.000    1.000    1.000  (reference)
    ///   Balanced  0.961    0.884    0.922
    ///   Compact   0.588    0.409    0.498
    ///   Minimal   0.371    0.007    0.189
    /// Balanced/Compact were RETUNED 2026-07-13 (a visual QA pass found the original 0.742/0.415
    /// figures came from LossyRunThresholdSq values generous enough to visibly streak smooth
    /// gradient/photo-like content — see GifSizePresets.ForPreset's own per-tier comments for the
    /// full before/after story): honest visual quality on the retuned thresholds, not hitting a
    /// byte target, was the actual requirement. GifQualityFactor is pinned to exactly 1.0 by
    /// definition (every other tier's ratio is expressed relative to it, so it can never itself be
    /// anything else). GifMaxFactor is the one tier ABOVE Quality (bit-exact reuse threshold instead
    /// of a coarser one) and was measured >= 1.0, consistent with Max never being smaller than
    /// Quality on any content.</summary>
    private const double GifMaxFactor = 1.034;
    private const double GifQualityFactor = 1.0;
    private const double GifBalancedFactor = 0.922;

    /// <summary>Compact moved AGAIN after the retune above, same day (the tier-spread pass): the
    /// retune had left its blended ratio at 0.865, bunched within ~6% of Balanced's 0.922 — once
    /// the lossy threshold is visually constrained it only buys ~13%, so "Low" was a near-alias of
    /// "Medium". Compact therefore gained RenderScale 0.75 and this factor is the re-measured
    /// blended ratio with that scale folded in, exactly the convention GifMinimalFactor below
    /// established: measured at 0.75-scale on-disk bytes over Quality's full-resolution bytes, so
    /// callers never account for the 0.5625x pixel-count effect separately. 0.498 sits right on
    /// the 0.5625 x 0.865 ~= 0.49 pixel-factor prediction — see GifSizePresets' own TIER SPREAD
    /// doc paragraph for why scroll lands slightly above it (scale-invariant per-frame header
    /// bytes) and noise slightly below (fewer distinct colors at the smaller canvas).</summary>
    private const double GifCompactFactor = 0.498;

    /// <summary>Minimal's measured blended ratio already folds in BOTH of its levers — the lossy
    /// run threshold AND the 0.5 RenderScale half-resolution render — as a single empirical number
    /// against the SAME (full-size) selected region every other tier's estimate is computed from:
    /// a caller never has to separately account for RenderScale's ~0.25 pixel-count effect, because
    /// the benchmark measured Minimal's actual on-disk bytes at half resolution and expressed that
    /// as a ratio of Quality's full-resolution bytes, same as every other tier (Compact's own
    /// factor above now follows this exact convention at 0.75). Notably far BELOW
    /// the naive 0.25 pixel-count expectation (0.189, not ~0.25 or higher) — GIF's per-frame
    /// GCE/Image-Descriptor/LCT header overhead does not shrink with RenderScale, so a smaller
    /// canvas alone would actually raise that overhead's fraction of the total; Minimal's other
    /// three levers (tol 12, 16-color palette, a large lossy run threshold) all still stack on top
    /// and pull the real measured ratio down further than resolution reduction alone would.</summary>
    private const double GifMinimalFactor = 0.189;

    /// <summary>Constant of proportionality (bytes per pixel per FRAME) behind the GIF estimate
    /// below, derived from GifSizeBenchmarkTests' scroll benchmark measured at Quality: 173,279
    /// bytes/sec at 640x400 (256,000px) and ~16.67fps effective emit rate under the OLD
    /// motion-floor-throttled scheme that benchmark was measured under (the effective rate the old
    /// HugeMotionDelayFloorCs/LargeMotionDelayFloorCs blend produced on that scenario, per
    /// GifSizePresets' now-removed doc comment) works out to 173,279 / 256,000 / 16.67 ≈ 0.0406 bytes
    /// per pixel per FRAME — i.e. the per-frame cost of a typical scroll/text delta, independent of
    /// how many frames per second the capture cadence actually produces. Multiplying this constant
    /// by fps directly (rather than keeping the old per-SECOND constant, which baked in that one
    /// specific throttled rate) is what makes the estimate track the user's chosen framerate now
    /// that framerate is a free variable instead of an emergent side effect of the quality tier.
    /// This is a TYPICAL-activity estimate, not a bound: GifEncoder's delta+palette scheme means
    /// bytes/frame scales with how much of the frame is actually changing, so a genuinely static
    /// take encodes far below this and a full-canvas-noise take (GifSizeBenchmarkTests' "noise"
    /// scenario) encodes far above it — the chrome's estimate text says "(varies with motion)" for
    /// exactly this reason.</summary>
    private const double GifBytesPerPixelPerFrame = 0.0406;

    /// <summary>Typical bytes/second a GIF take at this preset and framerate is expected to
    /// produce: <see cref="GifBytesPerPixelPerFrame"/> times pixel count times fps times the
    /// preset's quality factor (see that constant's doc comment for the derivation, and
    /// <see cref="Mp4BytesPerSecond"/>'s doc comment for why this is the GIF-side counterpart of an
    /// already-fps-parameterized MP4 formula). Quality and framerate now compose as two independent
    /// multiplicative factors, exactly matching the workstream's mandate that they be orthogonal
    /// user choices — doubling fps doubles the estimate at any tier, and moving tiers at any fps
    /// only ever moves the estimate by the tier's own quality factor.</summary>
    public static double GifTypicalBytesPerSecond(int width, int height, int fps, GifSizePreset preset)
    {
        double qFactor = preset switch
        {
            GifSizePreset.Max => GifMaxFactor,
            GifSizePreset.Quality => GifQualityFactor,
            GifSizePreset.Balanced => GifBalancedFactor,
            GifSizePreset.Compact => GifCompactFactor,
            GifSizePreset.Minimal => GifMinimalFactor,
            _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Unknown GifSizePreset."),
        };
        return GifBytesPerPixelPerFrame * width * height * fps * qFactor;
    }

    private static readonly string[] ByteUnits = { "B", "KB", "MB", "GB" };

    /// <summary>Renders a byte count as e.g. "700 KB" or "42 MB" — the largest unit under which the
    /// value is still >= 1 (capped at GB), at most one decimal place and never a trailing ".0"
    /// (<c>"0.#"</c>'s own formatting behavior). Shared by both terms of
    /// <see cref="FormatEstimate"/> so the per-second and per-minute figures are always rendered
    /// the same way.</summary>
    private static string FormatBytes(double bytes)
    {
        double value = Math.Max(0, bytes);
        int unitIndex = 0;
        while (value >= 1024.0 && unitIndex < ByteUnits.Length - 1)
        {
            value /= 1024.0;
            unitIndex++;
        }
        // The loop above promotes on the raw (unrounded) value, but the "0.#" format below rounds
        // to one decimal place afterward. A value just under the 1024 threshold (e.g. 1023.96) is
        // left unpromoted by the loop yet rounds up to "1024" at one-decimal precision, which would
        // otherwise print as e.g. "1024 KB" instead of promoting to "1 MB". Re-check after rounding
        // and promote once more if the rounded display value hit the next unit's threshold.
        if (unitIndex < ByteUnits.Length - 1 &&
            Math.Round(value, 1, MidpointRounding.AwayFromZero) >= 1024.0)
        {
            value /= 1024.0;
            unitIndex++;
        }
        return $"{value.ToString("0.#", CultureInfo.InvariantCulture)} {ByteUnits[unitIndex]}";
    }

    /// <summary>Formats a bytes/second figure as the chrome's live estimate string, e.g.
    /// "~700 KB/s * 42 MB/min" — both a per-second and a per-minute figure, since a viewer sizing
    /// up a short clip cares about the former and one sizing up a longer take cares about the
    /// latter. Deliberately uses "*" rather than an em dash or middle dot as the separator (repo
    /// rule: no em dashes in user-facing strings) and a leading "~" on the whole estimate, since
    /// even the MP4 branch — a tight target-bitrate bound frame-to-frame — only approximates the
    /// container/mux overhead the sink writer actually adds.</summary>
    public static string FormatEstimate(double bytesPerSecond)
    {
        double bytesPerMinute = bytesPerSecond * 60.0;
        return $"~{FormatBytes(bytesPerSecond)}/s * {FormatBytes(bytesPerMinute)}/min";
    }
}
