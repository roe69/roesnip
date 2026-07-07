using System.Windows.Media;
using RoeSnip.Overlay;
using Xunit;

namespace RoeSnip.Tests;

/// <summary>CursorKey.For's normalization rules (item 1, UX round 3) — the pure part of
/// ToolCursorCache's caching that doesn't require building an actual Win32 cursor.</summary>
public class ToolCursorCacheTests
{
    [Theory]
    [InlineData(AnnotationTool.Rectangle)]
    [InlineData(AnnotationTool.Ellipse)]
    [InlineData(AnnotationTool.Arrow)]
    [InlineData(AnnotationTool.Line)]
    [InlineData(AnnotationTool.Freehand)]
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
