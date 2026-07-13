using RoeSnip.Core.Recording.Gif;
using Xunit;

namespace RoeSnip.Core.Tests;

/// <summary>Locks the exact documented per-preset <see cref="GifEncoderOptions"/> values (the
/// spec's contract, not just "some options object comes out") and the settings-string parse
/// mapping — a silent edit to either would otherwise only surface as a benchmark drift, not a
/// clear failure pointing at the actual preset table. QUALITY ONLY since the quality/framerate
/// decoupling workstream: no motion-floor/framerate fields exist on GifEncoderOptions anymore, so
/// there is nothing left here to assert about them.</summary>
public class GifSizePresetTests
{
    [Fact]
    public void ForPreset_Max_MatchesDocumentedValues()
    {
        var opts = GifSizePresets.ForPreset(GifSizePreset.Max);

        Assert.Equal(0, opts.ChannelTolerance);
        Assert.Equal(0, opts.PaletteReuseErrorThreshold);
        Assert.Equal(255, opts.MaxPaletteColors);
        // DitherErrorFloor/KeyframeInterval untouched — every tier leaves these at the
        // GifEncoderOptions defaults (see the class doc comment for why).
        Assert.Equal((byte)3, opts.DitherErrorFloor);
        Assert.Null(opts.KeyframeInterval);
    }

    [Fact]
    public void ForPreset_Quality_MatchesTodaysDefaults()
    {
        var opts = GifSizePresets.ForPreset(GifSizePreset.Quality);

        Assert.Equal(4, opts.ChannelTolerance);
        Assert.Equal(4, opts.PaletteReuseErrorThreshold);
        Assert.Equal(255, opts.MaxPaletteColors);

        // Quality must be byte-identical to the pre-preset GifEncoderOptions() default — a
        // recording made before this preset scheme existed must encode exactly the same today.
        Assert.Equal(new GifEncoderOptions(), opts);
    }

    [Fact]
    public void ForPreset_Balanced_MatchesDocumentedValues()
    {
        var opts = GifSizePresets.ForPreset(GifSizePreset.Balanced);

        Assert.Equal(6, opts.ChannelTolerance);
        Assert.Equal(6, opts.PaletteReuseErrorThreshold);
        Assert.Equal(128, opts.MaxPaletteColors);
    }

    [Fact]
    public void ForPreset_Compact_MatchesTunedValues()
    {
        var opts = GifSizePresets.ForPreset(GifSizePreset.Compact);

        Assert.Equal(8, opts.ChannelTolerance);
        Assert.Equal(8, opts.PaletteReuseErrorThreshold);
        Assert.Equal(64, opts.MaxPaletteColors);
        // 12,000, not the original 350,000 — retuned 2026-07-13 after a visual QA pass found the
        // original value streaked smooth gradient/photo-like content; see GifSizePresets.ForPreset's
        // own Compact comment for the full before/after reasoning.
        Assert.Equal(12_000, opts.LossyRunThresholdSq);
        // 0.75, added by the same-day tier-spread pass: the retuned (visually constrained) lossy
        // threshold left Compact's blended byte ratio bunched within ~6% of Balanced's, so the
        // "Low" tier gets its real size step from resolution instead — see GifSizePresets' own
        // TIER SPREAD doc paragraph for the measured 0.865 -> 0.498 effect.
        Assert.Equal(0.75, opts.RenderScale);
    }

    [Fact]
    public void ForPreset_Minimal_MatchesTunedValues()
    {
        var opts = GifSizePresets.ForPreset(GifSizePreset.Minimal);

        Assert.Equal(12, opts.ChannelTolerance);
        Assert.Equal(12, opts.PaletteReuseErrorThreshold);
        // 16, not the section-4 starting point of 32 — widened during calibration (GifSizeBenchmarkTests'
        // blended-ratio gate), see GifSizePresets' own class doc comment for the measured table.
        Assert.Equal(16, opts.MaxPaletteColors);
        Assert.Equal(120_000, opts.LossyRunThresholdSq);
        Assert.Equal(0.5, opts.RenderScale);
    }

    [Fact]
    public void ForPreset_MaxAndQuality_HaveNoLossyRunExtension()
    {
        // Max/Quality must stay bit-exact-vs-pre-workstream — see GifEncoderOptions.
        // LossyRunThresholdSq's own doc comment for why 0 disables the mechanism entirely.
        Assert.Equal(0, GifSizePresets.ForPreset(GifSizePreset.Max).LossyRunThresholdSq);
        Assert.Equal(0, GifSizePresets.ForPreset(GifSizePreset.Quality).LossyRunThresholdSq);
        Assert.Equal(1.0, GifSizePresets.ForPreset(GifSizePreset.Max).RenderScale);
        Assert.Equal(1.0, GifSizePresets.ForPreset(GifSizePreset.Quality).RenderScale);
    }

    [Fact]
    public void ForPreset_Balanced_HasTheDocumentedLossyThreshold()
    {
        // 1,500, not the original 90,000 — retuned 2026-07-13 after a visual QA pass found the
        // original value streaked smooth gradient/photo-like content; see GifSizePresets.ForPreset's
        // own Balanced comment for the full before/after reasoning.
        Assert.Equal(1_500, GifSizePresets.ForPreset(GifSizePreset.Balanced).LossyRunThresholdSq);
        Assert.Equal(1.0, GifSizePresets.ForPreset(GifSizePreset.Balanced).RenderScale);
    }

    [Fact]
    public void ForPreset_OnlyCompactAndMinimal_ShrinkTheRenderScale()
    {
        // Max/Quality/Balanced must stay native-resolution (Max/Quality are contractually
        // bit-exact-vs-pre-workstream, and Balanced's whole identity is "quantization levers only");
        // Compact (0.75, tier-spread pass) and Minimal (0.5) are the two tiers that trade
        // resolution for size — a monotone 1.0 > 0.75 > 0.5 progression, matching the tiers' own
        // size ordering.
        foreach (var preset in new[] { GifSizePreset.Max, GifSizePreset.Quality, GifSizePreset.Balanced })
        {
            Assert.Equal(1.0, GifSizePresets.ForPreset(preset).RenderScale);
        }
        Assert.Equal(0.75, GifSizePresets.ForPreset(GifSizePreset.Compact).RenderScale);
        Assert.Equal(0.5, GifSizePresets.ForPreset(GifSizePreset.Minimal).RenderScale);
    }

    [Theory]
    [InlineData("Max", GifSizePreset.Max)]
    [InlineData("Quality", GifSizePreset.Quality)]
    [InlineData("Balanced", GifSizePreset.Balanced)]
    [InlineData("Compact", GifSizePreset.Compact)]
    [InlineData("Minimal", GifSizePreset.Minimal)]
    [InlineData("max", GifSizePreset.Max)]
    [InlineData("MAX", GifSizePreset.Max)]
    [InlineData("quality", GifSizePreset.Quality)]
    [InlineData("BALANCED", GifSizePreset.Balanced)]
    [InlineData("cOmPaCt", GifSizePreset.Compact)]
    [InlineData("mInImAl", GifSizePreset.Minimal)]
    public void Parse_KnownValues_CaseInsensitive(string value, GifSizePreset expected)
    {
        Assert.Equal(expected, GifSizePresets.Parse(value));
    }

    [Fact]
    public void Parse_Max_RoundTripsThroughToString()
    {
        Assert.Equal(GifSizePreset.Max, GifSizePresets.Parse(GifSizePreset.Max.ToString()));
    }

    [Fact]
    public void Parse_Minimal_RoundTripsThroughToString()
    {
        Assert.Equal(GifSizePreset.Minimal, GifSizePresets.Parse(GifSizePreset.Minimal.ToString()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Ultra")]
    [InlineData("Quality2")]
    [InlineData(" Quality")]
    public void Parse_UnknownOrMissingValues_FallBackToQuality(string? value)
    {
        Assert.Equal(GifSizePreset.Quality, GifSizePresets.Parse(value));
    }

    // ---------- DisplayLabel (chrome's Max/High/Medium/Low chip text) ----------

    [Theory]
    [InlineData(GifSizePreset.Max, "Max")]
    [InlineData(GifSizePreset.Quality, "High")]
    [InlineData(GifSizePreset.Balanced, "Medium")]
    [InlineData(GifSizePreset.Compact, "Low")]
    [InlineData(GifSizePreset.Minimal, "Min")]
    public void DisplayLabel_MapsToTheDocumentedFiveWayScale(GifSizePreset preset, string expectedLabel)
    {
        Assert.Equal(expectedLabel, GifSizePresets.DisplayLabel(preset));
    }

    [Fact]
    public void DisplayLabel_NeverEqualsTheEnumsOwnPersistedName_ExceptMax()
    {
        // Regression guard for the exact bug this workstream fixes: the display label must diverge
        // from ToString() for every tier except Max (which happens to keep the same word), or the
        // chrome would still show the pre-decoupling Quality/Balanced/Compact/Minimal wording.
        foreach (var preset in new[] { GifSizePreset.Quality, GifSizePreset.Balanced, GifSizePreset.Compact, GifSizePreset.Minimal })
        {
            Assert.NotEqual(preset.ToString(), GifSizePresets.DisplayLabel(preset));
        }
        Assert.Equal(GifSizePreset.Max.ToString(), GifSizePresets.DisplayLabel(GifSizePreset.Max));
    }
}
