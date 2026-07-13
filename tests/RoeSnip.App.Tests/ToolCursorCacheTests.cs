using System;
using Avalonia.Media;
using RoeSnip.App.Overlay;
using Xunit;

namespace RoeSnip.App.Tests;

/// <summary>CursorKey.For's normalization rules — the pure part of ToolCursorCache's caching that
/// doesn't require actually rasterizing a cursor bitmap. Ported verbatim from the WPF reference's
/// tests/RoeSnip.Tests/ToolCursorCacheTests.cs against this port's own CursorKey copy.</summary>
public class ToolCursorCacheTests
{
    [Theory]
    [InlineData(AnnotationTool.Rectangle)]
    [InlineData(AnnotationTool.Ellipse)]
    [InlineData(AnnotationTool.Arrow)]
    [InlineData(AnnotationTool.Line)]
    [InlineData(AnnotationTool.Freehand)]
    [InlineData(AnnotationTool.Highlight)]
    [InlineData(AnnotationTool.Pixelate)]
    public void For_StrokeTool_RoundsWidthToNearestPixel(AnnotationTool tool)
    {
        var key = CursorKey.For(tool, Colors.Red, 4.4);
        Assert.Equal(4.0, key.StrokeWidthPx);

        var keyRoundUp = CursorKey.For(tool, Colors.Red, 4.6);
        Assert.Equal(5.0, keyRoundUp.StrokeWidthPx);
    }

    [Theory]
    [InlineData(AnnotationTool.Rectangle)]
    [InlineData(AnnotationTool.Ellipse)]
    [InlineData(AnnotationTool.Arrow)]
    [InlineData(AnnotationTool.Line)]
    [InlineData(AnnotationTool.Freehand)]
    public void For_StrokeTool_SameRoundedWidth_ProducesEqualKeys(AnnotationTool tool)
    {
        var a = CursorKey.For(tool, Colors.Blue, 8.1);
        var b = CursorKey.For(tool, Colors.Blue, 7.9);
        Assert.Equal(a, b);
    }

    [Fact]
    public void For_None_ZeroesStrokeWidth()
    {
        var key = CursorKey.For(AnnotationTool.None, Colors.Green, 12.0);
        Assert.Equal(0.0, key.StrokeWidthPx);
    }

    [Fact]
    public void For_Text_ZeroesStrokeWidth()
    {
        var key = CursorKey.For(AnnotationTool.Text, Colors.Green, 30.0);
        Assert.Equal(0.0, key.StrokeWidthPx);
    }

    [Fact]
    public void For_Text_DifferentStrokeWidthsStillProduceEqualKeys()
    {
        // The text tool never renders a width ring, so two different (last-used draw) stroke widths
        // must not spawn two distinct cache entries for the same (Text, color) combination.
        var a = CursorKey.For(AnnotationTool.Text, Colors.White, 2.0);
        var b = CursorKey.For(AnnotationTool.Text, Colors.White, 30.0);
        Assert.Equal(a, b);
    }

    [Fact]
    public void For_DifferentColors_ProduceDifferentKeys()
    {
        var a = CursorKey.For(AnnotationTool.Rectangle, Colors.Red, 4.0);
        var b = CursorKey.For(AnnotationTool.Rectangle, Colors.Blue, 4.0);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void For_DifferentTools_ProduceDifferentKeys()
    {
        var a = CursorKey.For(AnnotationTool.Rectangle, Colors.Red, 4.0);
        var b = CursorKey.For(AnnotationTool.Ellipse, Colors.Red, 4.0);
        Assert.NotEqual(a, b);
    }
}

/// <summary>CircleSpec.For's sizing rules: the tool cursor is a plain circle whose diameter tracks
/// the stroke width 1:1, capped at a 64x64 bitmap with a numeric label once the width would
/// otherwise need a bigger canvas. Ported verbatim from the WPF reference's CircleSpecTests.</summary>
public class CircleSpecTests
{
    [Theory]
    [InlineData(1.0)]
    [InlineData(10.0)]
    [InlineData(32.0)]
    public void For_WidthWithinCap_DiameterMatchesStrokeWidth1To1(double strokeWidthPx)
    {
        var spec = CircleSpec.For(strokeWidthPx);
        // Below CircleSpec.MinDiameter the circle is clamped to a fixed minimum instead.
        double expected = Math.Max(strokeWidthPx, CircleSpec.MinDiameter);
        Assert.Equal(expected, spec.Diameter);
        Assert.False(spec.ShowLabel);
    }

    [Fact]
    public void For_TinyWidth_ClampsToMinDiameter()
    {
        var spec = CircleSpec.For(1.0);
        Assert.Equal(CircleSpec.MinDiameter, spec.Diameter);
    }

    [Fact]
    public void For_CanvasSize_NeverExceedsMax()
    {
        foreach (double width in new[] { 1.0, 6.0, 20.0, 40.0, 52.0, 64.0, 100.0, 500.0 })
        {
            var spec = CircleSpec.For(width);
            Assert.True(spec.CanvasSize <= CircleSpec.MaxCanvasSize);
        }
    }

    [Fact]
    public void For_WidthExceedingCap_CapsBitmapAndShowsLabel()
    {
        var spec = CircleSpec.For(500.0);
        Assert.Equal(CircleSpec.MaxCanvasSize, spec.CanvasSize);
        Assert.True(spec.ShowLabel);
        Assert.Equal(500.0, spec.LabelWidthPx);
        // The circle itself must still fit inside the capped canvas with room for the halo.
        Assert.True(spec.Diameter < CircleSpec.MaxCanvasSize);
    }

    [Fact]
    public void For_WidthAtExactCapBoundary_DoesNotShowLabel()
    {
        double boundaryWidth = CircleSpec.MaxCanvasSize - CircleSpec.Margin * 2;
        var spec = CircleSpec.For(boundaryWidth);
        Assert.False(spec.ShowLabel);
        Assert.Equal(boundaryWidth, spec.Diameter);
    }
}
