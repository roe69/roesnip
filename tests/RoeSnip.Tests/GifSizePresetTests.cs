using RoeSnip.Recording.Gif;
using Xunit;

namespace RoeSnip.Tests;

/// <summary>Locks the exact documented per-preset <see cref="GifEncoderOptions"/> values (the
/// spec's contract, not just "some options object comes out") and the settings-string parse
/// mapping — a silent edit to either would otherwise only surface as a benchmark drift, not a
/// clear failure pointing at the actual preset table.</summary>
public class GifSizePresetTests
{
    [Fact]
    public void ForPreset_Quality_MatchesTodaysDefaults()
    {
        var opts = GifSizePresets.ForPreset(GifSizePreset.Quality);

        Assert.Equal(4, opts.ChannelTolerance);
        Assert.Equal(4, opts.PaletteReuseErrorThreshold);
        Assert.Equal(255, opts.MaxPaletteColors);
        Assert.Equal(0.30, opts.LargeMotionAreaFraction);
        Assert.Equal((ushort)5, opts.LargeMotionDelayFloorCs);
        Assert.Equal(0.65, opts.HugeMotionAreaFraction);
        Assert.Equal((ushort)8, opts.HugeMotionDelayFloorCs);

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
        Assert.Equal(0.10, opts.LargeMotionAreaFraction);
        Assert.Equal((ushort)6, opts.LargeMotionDelayFloorCs);
        Assert.Equal(0.50, opts.HugeMotionAreaFraction);
        Assert.Equal((ushort)12, opts.HugeMotionDelayFloorCs);
    }

    [Fact]
    public void ForPreset_Compact_MatchesTunedValues()
    {
        var opts = GifSizePresets.ForPreset(GifSizePreset.Compact);

        Assert.Equal(8, opts.ChannelTolerance);
        Assert.Equal(8, opts.PaletteReuseErrorThreshold);
        Assert.Equal(64, opts.MaxPaletteColors);
        Assert.Equal(0.05, opts.LargeMotionAreaFraction);
        Assert.Equal(0.40, opts.HugeMotionAreaFraction);
        // Floors verified against GifSizeBenchmarkTests's scroll/noise gates (see
        // GifSizePresets.ForPreset's doc comment for the measured percentages) — the calibration
        // sweep's starting-point values already cleared the <=55%-of-Quality budget on both
        // scenarios with margin to spare, so no further tuning was needed.
        Assert.Equal((ushort)8, opts.LargeMotionDelayFloorCs);
        Assert.Equal((ushort)16, opts.HugeMotionDelayFloorCs);
    }

    [Theory]
    [InlineData("Quality", GifSizePreset.Quality)]
    [InlineData("Balanced", GifSizePreset.Balanced)]
    [InlineData("Compact", GifSizePreset.Compact)]
    [InlineData("quality", GifSizePreset.Quality)]
    [InlineData("BALANCED", GifSizePreset.Balanced)]
    [InlineData("cOmPaCt", GifSizePreset.Compact)]
    public void Parse_KnownValues_CaseInsensitive(string value, GifSizePreset expected)
    {
        Assert.Equal(expected, GifSizePresets.Parse(value));
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
}
