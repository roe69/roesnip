using Avalonia;
using RoeSnip.App.Overlay;
using RoeSnip.Core.Capture;
using Xunit;

namespace RoeSnip.App.Tests;

/// <summary>Pure geometry for the loupe's selection-border preview (CAPTURE-FIDELITY-SPEC.md §3):
/// <see cref="Magnifier.MapSelectionToLoupeRect"/> maps a physical-pixel candidate crop rectangle
/// into the loupe's own on-screen DIP square, using the identical per-axis affine the swatch grid
/// itself uses. Factored out as a static, Avalonia-rendering-free function specifically so this
/// mapping is testable without a live Control/DrawingContext — see the method's own doc comment.
/// Mirrors the WPF app's identically-named, identically-shaped test
/// (tests/RoeSnip.Tests/MagnifierSelectionPreviewTests.cs).</summary>
public class MagnifierSelectionPreviewTests
{
    // Matches Magnifier's own historical default: sampleRadius 5 => (2*5+1) = 11 source pixels
    // across a fixed 154 DIP loupe.
    private const double SwatchDip = 154.0 / 11;

    [Fact]
    public void MapSelectionToLoupeRect_SinglePixelAtCenter_MapsToTheCenterSwatchCell()
    {
        // A 1x1 selection exactly at the sampled center pixel: its near edge sits at the "0th"
        // offset from center (i.e. swatch index sampleRadius), its far edge one swatch further —
        // identical in kind to how a single swatch is placed in the Render grid loop.
        var selection = RectPhysical.FromSize(100, 100, 1, 1);

        var rect = Magnifier.MapSelectionToLoupeRect(
            selection, centerX: 100, centerY: 100, sampleRadius: 5,
            loupeLeft: 0, loupeTop: 0, swatchDip: SwatchDip);

        Assert.Equal(5 * SwatchDip, rect.Left, precision: 6);
        Assert.Equal(5 * SwatchDip, rect.Top, precision: 6);
        Assert.Equal(6 * SwatchDip, rect.Right, precision: 6);
        Assert.Equal(6 * SwatchDip, rect.Bottom, precision: 6);
    }

    [Fact]
    public void MapSelectionToLoupeRect_TranslatesByLoupeOrigin()
    {
        var selection = RectPhysical.FromSize(100, 100, 1, 1);

        var atOrigin = Magnifier.MapSelectionToLoupeRect(selection, 100, 100, 5, 0, 0, SwatchDip);
        var offset = Magnifier.MapSelectionToLoupeRect(selection, 100, 100, 5, 40, 60, SwatchDip);

        Assert.Equal(atOrigin.Left + 40, offset.Left, precision: 6);
        Assert.Equal(atOrigin.Top + 60, offset.Top, precision: 6);
        Assert.Equal(atOrigin.Right + 40, offset.Right, precision: 6);
        Assert.Equal(atOrigin.Bottom + 60, offset.Bottom, precision: 6);
    }

    [Fact]
    public void MapSelectionToLoupeRect_UnnormalizedRect_StillProducesANonInvertedRect()
    {
        // Constructed with Right < Left and Bottom < Top (as the live drag path's raw candidate
        // rect briefly can be before Normalized() is applied) — the mapping must normalize first,
        // exactly like SelectionAdorner.Render does before computing its own dipRect.
        var backwards = new RectPhysical(105, 105, 100, 100);

        var rect = Magnifier.MapSelectionToLoupeRect(backwards, 100, 100, 5, 10, 20, SwatchDip);

        Assert.True(rect.Left <= rect.Right);
        Assert.True(rect.Top <= rect.Bottom);
    }

    [Fact]
    public void MapSelectionToLoupeRect_FarEdgeOutsideSampleWindow_MapsOutsideTheLoupeSquare_NoBoundsCheck()
    {
        // A 50px-wide/tall selection whose far corner is well beyond the +/-5-pixel sampled
        // window: the mapped rect legitimately extends past the loupe's own square. The method
        // itself performs no bounds-checking or clamping — callers (Magnifier.Render) are expected
        // to PushClip to the loupe square instead, per the spec's "off-screen edges are simply
        // clipped with no artifact" decision.
        var selection = RectPhysical.FromSize(100, 100, 50, 50);
        const double loupeSize = 154.0;

        var rect = Magnifier.MapSelectionToLoupeRect(selection, 100, 100, 5, 0, 0, SwatchDip);

        Assert.True(rect.Right > loupeSize);
        Assert.True(rect.Bottom > loupeSize);
    }

    [Fact]
    public void MapSelectionToLoupeRect_NearEdgeBeforeSampleWindow_MapsBeforeLoupeOrigin_NoBoundsCheck()
    {
        // Symmetric case: an anchor corner far to the top-left of the sampled window maps to
        // negative coordinates relative to the loupe's own top-left — again, clipping is the
        // caller's job, not this function's.
        var selection = RectPhysical.FromSize(50, 50, 50, 50); // right/bottom edge at (100,100)

        var rect = Magnifier.MapSelectionToLoupeRect(selection, 100, 100, 5, 0, 0, SwatchDip);

        Assert.True(rect.Left < 0);
        Assert.True(rect.Top < 0);
    }
}
