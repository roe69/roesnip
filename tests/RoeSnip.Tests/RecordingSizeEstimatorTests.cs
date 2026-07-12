using RoeSnip.Recording;
using RoeSnip.Recording.Gif;
using Xunit;

namespace RoeSnip.Tests;

/// <summary>The pure live-estimate math behind the recording chrome's size readout — no live
/// encoder/chrome needed, see RecordingSizeEstimator's own doc comment.</summary>
public class RecordingSizeEstimatorTests
{
    // ---------- Mp4BytesPerSecond ----------

    [Fact]
    public void Mp4BytesPerSecond_QualityNoAudio_IsBitrateOverEight()
    {
        // Mp4Encoder.ComputeBitrate(1280, 720, 30) = 2,764,800 (unclamped) -> /8 = 345,600 B/s.
        double bps = RecordingSizeEstimator.Mp4BytesPerSecond(1280, 720, 30, GifSizePreset.Quality, audioEnabled: false);
        Assert.Equal(345_600, bps);
    }

    [Fact]
    public void Mp4BytesPerSecond_QualityWithAudio_AddsTheAacTrackRate()
    {
        double bps = RecordingSizeEstimator.Mp4BytesPerSecond(1280, 720, 30, GifSizePreset.Quality, audioEnabled: true);
        Assert.Equal(345_600 + Mp4Encoder.AudioAacBytesPerSecond, bps);
        Assert.Equal(361_600, bps);
    }

    [Fact]
    public void Mp4BytesPerSecond_MaxPreset_MatchesItsOwnBitrateFactor()
    {
        // ComputeBitrate(1280, 720, 30, Max) = 11,059,200 (unclamped) -> /8 = 1,382,400 B/s.
        double bps = RecordingSizeEstimator.Mp4BytesPerSecond(1280, 720, 30, GifSizePreset.Max, audioEnabled: false);
        Assert.Equal(1_382_400, bps);
    }

    [Fact]
    public void Mp4BytesPerSecond_ClampedBitrate_StillAddsAudioOverheadOnTop()
    {
        // ComputeBitrate(64, 64, 12, Balanced) clamps to the 1.5 Mbps floor -> /8 = 187,500 B/s.
        // The clamp happens entirely inside ComputeBitrate; the audio term is added afterward, so
        // a clamped video rate must not swallow or otherwise interact with the audio addition.
        double withoutAudio = RecordingSizeEstimator.Mp4BytesPerSecond(64, 64, 12, GifSizePreset.Balanced, audioEnabled: false);
        double withAudio = RecordingSizeEstimator.Mp4BytesPerSecond(64, 64, 12, GifSizePreset.Balanced, audioEnabled: true);
        Assert.Equal(187_500, withoutAudio);
        Assert.Equal(187_500 + Mp4Encoder.AudioAacBytesPerSecond, withAudio);
    }

    [Fact]
    public void Mp4BytesPerSecond_DoublingFps_DoublesUnclampedRate()
    {
        // Mp4Encoder.ComputeBitrate scales linearly by fps (0.1 bpp * fps), so the byte-rate
        // estimate composes with the quality axis for free — this locks that composition rather
        // than re-deriving ComputeBitrate's own formula (that's Mp4EncoderTests' job).
        double at30 = RecordingSizeEstimator.Mp4BytesPerSecond(1280, 720, 30, GifSizePreset.Quality, audioEnabled: false);
        double at60 = RecordingSizeEstimator.Mp4BytesPerSecond(1280, 720, 60, GifSizePreset.Quality, audioEnabled: false);
        Assert.Equal(at30 * 2.0, at60, precision: 3);
    }

    // ---------- GifTypicalBytesPerSecond (fps, preset) ----------

    [Fact]
    public void GifTypicalBytesPerSecond_Quality_AtDefaultFps_MatchesTheBenchmarkDerivedConstant()
    {
        // 0.0406 * 640 * 400 * 25 * 1.0 = 259,840.
        double bps = RecordingSizeEstimator.GifTypicalBytesPerSecond(640, 400, 25, GifSizePreset.Quality);
        Assert.Equal(259_840, bps, precision: 3);
    }

    [Fact]
    public void GifTypicalBytesPerSecond_Max_IsAtLeastQuality()
    {
        // Max's measured blended benchmark ratio is >= 1.0 (its own bit-exact palette-reuse
        // threshold, vs. Quality's coarser one, never produces SMALLER output on the calibration
        // content) — unlike the pre-workstream estimate, Max is no longer asserted equal to Quality.
        double quality = RecordingSizeEstimator.GifTypicalBytesPerSecond(640, 400, 25, GifSizePreset.Quality);
        double max = RecordingSizeEstimator.GifTypicalBytesPerSecond(640, 400, 25, GifSizePreset.Max);
        Assert.True(max >= quality, $"Max ({max}) should be >= Quality ({quality})");
    }

    [Fact]
    public void GifTypicalBytesPerSecond_Balanced_AppliesTheDocumentedFactor()
    {
        // 0.922 is GifSizeBenchmarkTests' measured blended (scroll+noise, mean) ratio vs. Quality —
        // see RecordingSizeEstimator.GifBalancedFactor's own doc comment for the full table.
        // Retuned 2026-07-13 (was 0.742) alongside GifSizePresets.ForPreset's LossyRunThresholdSq
        // retune — see that class's own comment for why the factor moved this much closer to 1.0.
        double quality = RecordingSizeEstimator.GifTypicalBytesPerSecond(640, 400, 25, GifSizePreset.Quality);
        double balanced = RecordingSizeEstimator.GifTypicalBytesPerSecond(640, 400, 25, GifSizePreset.Balanced);
        Assert.Equal(quality * 0.922, balanced, precision: 3);
    }

    [Fact]
    public void GifTypicalBytesPerSecond_Compact_AppliesTheDocumentedFactor()
    {
        // 0.498: the 2026-07-13 retune (was 0.415, briefly 0.865 at full resolution) followed by
        // the same-day tier-spread pass's RenderScale 0.75 — the factor folds the 0.5625x
        // pixel-count effect in, same convention as Minimal's (see GifCompactFactor's own doc
        // comment for the derivation).
        double quality = RecordingSizeEstimator.GifTypicalBytesPerSecond(640, 400, 25, GifSizePreset.Quality);
        double compact = RecordingSizeEstimator.GifTypicalBytesPerSecond(640, 400, 25, GifSizePreset.Compact);
        Assert.Equal(quality * 0.498, compact, precision: 3);
    }

    [Fact]
    public void GifTypicalBytesPerSecond_Minimal_AppliesTheDocumentedFactor()
    {
        // Minimal's factor already folds in the RenderScale-0.5 pixel-count effect (see
        // GifMinimalFactor's own doc comment) — it is a plain function of the SAME full-size
        // selected region every other tier's estimate is computed from.
        double quality = RecordingSizeEstimator.GifTypicalBytesPerSecond(640, 400, 25, GifSizePreset.Quality);
        double minimal = RecordingSizeEstimator.GifTypicalBytesPerSecond(640, 400, 25, GifSizePreset.Minimal);
        Assert.Equal(quality * 0.189, minimal, precision: 3);
    }

    [Fact]
    public void GifTypicalBytesPerSecond_QualityAxisIsMonotoneNonIncreasing_AtFixedFps()
    {
        // Minimal <= Compact <= Balanced <= Quality <= Max at any fixed fps — the quality axis
        // alone must never make the estimate go UP when moving to a smaller/coarser tier.
        double max = RecordingSizeEstimator.GifTypicalBytesPerSecond(640, 400, 25, GifSizePreset.Max);
        double quality = RecordingSizeEstimator.GifTypicalBytesPerSecond(640, 400, 25, GifSizePreset.Quality);
        double balanced = RecordingSizeEstimator.GifTypicalBytesPerSecond(640, 400, 25, GifSizePreset.Balanced);
        double compact = RecordingSizeEstimator.GifTypicalBytesPerSecond(640, 400, 25, GifSizePreset.Compact);
        double minimal = RecordingSizeEstimator.GifTypicalBytesPerSecond(640, 400, 25, GifSizePreset.Minimal);
        Assert.True(minimal <= compact);
        Assert.True(compact <= balanced);
        Assert.True(balanced <= quality);
        Assert.True(quality <= max);
    }

    [Fact]
    public void GifTypicalBytesPerSecond_ScalesLinearlyWithPixelCount()
    {
        double small = RecordingSizeEstimator.GifTypicalBytesPerSecond(640, 400, 25, GifSizePreset.Quality);
        double doubled = RecordingSizeEstimator.GifTypicalBytesPerSecond(1280, 400, 25, GifSizePreset.Quality);
        Assert.Equal(small * 2.0, doubled, precision: 3);
    }

    [Fact]
    public void GifTypicalBytesPerSecond_ScalesLinearlyWithFps_QualityAxisOrthogonal()
    {
        // The framerate axis is now free of the quality axis: doubling fps must double the estimate
        // at every tier, independent of which tier is picked — the core assertion of the
        // quality/framerate decoupling workstream.
        foreach (var preset in new[] { GifSizePreset.Max, GifSizePreset.Quality, GifSizePreset.Balanced, GifSizePreset.Compact, GifSizePreset.Minimal })
        {
            double at10 = RecordingSizeEstimator.GifTypicalBytesPerSecond(640, 400, 10, preset);
            double at20 = RecordingSizeEstimator.GifTypicalBytesPerSecond(640, 400, 20, preset);
            double at50 = RecordingSizeEstimator.GifTypicalBytesPerSecond(640, 400, 50, preset);
            Assert.Equal(at10 * 2.0, at20, precision: 3);
            Assert.Equal(at10 * 5.0, at50, precision: 3);
        }
    }

    // ---------- Fps range bounds / ClampFps ----------

    [Fact]
    public void GifFpsRange_MatchesDocumentedBounds()
    {
        Assert.Equal(5, RecordingSizeEstimator.GifMinFps);
        Assert.Equal(50, RecordingSizeEstimator.GifMaxFps);
    }

    [Fact]
    public void Mp4FpsRange_MatchesDocumentedBounds()
    {
        Assert.Equal(5, RecordingSizeEstimator.Mp4MinFps);
        Assert.Equal(60, RecordingSizeEstimator.Mp4MaxFps);
    }

    [Fact]
    public void GifDefaultFps_Is25()
    {
        Assert.Equal(25, RecordingSizeEstimator.GifDefaultFps);
    }

    [Fact]
    public void Mp4DefaultFps_Is30()
    {
        Assert.Equal(30, RecordingSizeEstimator.Mp4DefaultFps);
    }

    [Theory]
    [InlineData(50, 50)]
    [InlineData(25, 25)]
    [InlineData(5, 5)]
    [InlineData(37, 37)]   // any integer in range is legal now — no snap-to-divisor
    [InlineData(1, 5)]     // clamps up to the floor
    [InlineData(0, 5)]
    [InlineData(51, 50)]   // clamps down to the ceiling
    [InlineData(1000, 50)]
    public void ClampFps_Gif_ClampsIntoRange(int requested, int expected)
    {
        Assert.Equal(expected, RecordingSizeEstimator.ClampFps(requested, RecordingSizeEstimator.GifMinFps, RecordingSizeEstimator.GifMaxFps));
    }

    [Theory]
    [InlineData(60, 60)]
    [InlineData(30, 30)]
    [InlineData(5, 5)]
    [InlineData(45, 45)]
    [InlineData(0, 5)]
    [InlineData(4, 5)]
    [InlineData(61, 60)]
    [InlineData(999, 60)]
    public void ClampFps_Mp4_ClampsIntoRange(int requested, int expected)
    {
        Assert.Equal(expected, RecordingSizeEstimator.ClampFps(requested, RecordingSizeEstimator.Mp4MinFps, RecordingSizeEstimator.Mp4MaxFps));
    }

    // ---------- FormatEstimate ----------

    [Fact]
    public void FormatEstimate_CombinesPerSecondAndPerMinuteFigures()
    {
        // 700 KB/s exactly (716,800 B/s) -> 43,008,000 B/min = 41.015625 MB/min -> rounds to "41 MB".
        string text = RecordingSizeEstimator.FormatEstimate(716_800);
        Assert.Equal("~700 KB/s * 41 MB/min", text);
    }

    [Fact]
    public void FormatEstimate_JustBelowKbBoundary_StaysInBytes()
    {
        string text = RecordingSizeEstimator.FormatEstimate(1023);
        Assert.StartsWith("~1023 B/s", text);
    }

    [Fact]
    public void FormatEstimate_AtKbBoundary_PromotesToKilobytes()
    {
        string text = RecordingSizeEstimator.FormatEstimate(1024);
        Assert.StartsWith("~1 KB/s", text);
    }

    [Fact]
    public void FormatEstimate_AtMbBoundary_PromotesToMegabytes()
    {
        double oneMbPerSecond = 1024.0 * 1024.0;
        string text = RecordingSizeEstimator.FormatEstimate(oneMbPerSecond);
        Assert.StartsWith("~1 MB/s", text);
    }

    [Fact]
    public void FormatEstimate_AtGbBoundary_PromotesToGigabytes()
    {
        double oneGbPerSecond = 1024.0 * 1024.0 * 1024.0;
        string text = RecordingSizeEstimator.FormatEstimate(oneGbPerSecond);
        Assert.StartsWith("~1 GB/s", text);
    }

    [Fact]
    public void FormatEstimate_UsesAtMostOneDecimalPlace_AndTrimsTrailingZero()
    {
        // 1536 B/s = 1.5 KB exactly.
        string text = RecordingSizeEstimator.FormatEstimate(1536);
        Assert.StartsWith("~1.5 KB/s", text);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(500)]
    [InlineData(716_800)]
    [InlineData(50_000_000)]
    public void FormatEstimate_NeverContainsAnEmDash(double bytesPerSecond)
    {
        string text = RecordingSizeEstimator.FormatEstimate(bytesPerSecond);
        Assert.DoesNotContain('—', text); // em dash
        Assert.DoesNotContain('–', text); // en dash, same repo rule
    }
}
