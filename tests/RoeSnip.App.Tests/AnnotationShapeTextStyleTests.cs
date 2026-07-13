using Avalonia;
using Avalonia.Media;
using RoeSnip.App.Overlay;
using Xunit;

namespace RoeSnip.App.Tests;

/// <summary>The text-style fields added to AnnotationShape/AnnotationLayer.CommitText for item 08's
/// toolbar text-style row (Bold/Italic/font family) — mirrors WPF's AnnotationShape.TextFontFamily/
/// TextBold/TextItalic (src/RoeSnip/Overlay/AnnotationLayer.cs:66-68). No Avalonia Application/
/// visual-tree bootstrap needed, same rationale as AnnotationLayerSelectEditTests.</summary>
public class AnnotationShapeTextStyleTests
{
    [Fact]
    public void CommitText_DefaultsMatchSegoeUiRegular()
    {
        var layer = new AnnotationLayer();
        var shape = layer.CommitText(new Point(10, 10), "hello", Colors.White, 16.0)!;

        Assert.Equal("Segoe UI", shape.TextFontFamily);
        Assert.False(shape.TextBold);
        Assert.False(shape.TextItalic);
    }

    [Fact]
    public void CommitText_PassesThroughTheRequestedStyle()
    {
        var layer = new AnnotationLayer();
        var shape = layer.CommitText(new Point(10, 10), "hello", Colors.White, 16.0, "Consolas", bold: true, italic: true)!;

        Assert.Equal("Consolas", shape.TextFontFamily);
        Assert.True(shape.TextBold);
        Assert.True(shape.TextItalic);
    }

    [Fact]
    public void Clone_CopiesTextStyleFields_IndependentlyOfTheOriginal()
    {
        var original = new AnnotationShape
        {
            Tool = AnnotationTool.Text,
            Text = "hi",
            TextFontFamily = "Consolas",
            TextBold = true,
            TextItalic = true,
        };

        var clone = original.Clone();

        Assert.Equal("Consolas", clone.TextFontFamily);
        Assert.True(clone.TextBold);
        Assert.True(clone.TextItalic);
    }
}
