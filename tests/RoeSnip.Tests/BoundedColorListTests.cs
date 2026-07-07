using System.Collections.Generic;
using RoeSnip.Overlay;
using Xunit;

namespace RoeSnip.Tests;

/// <summary>The LRU push helper shared by RecentPickedColors (ColorPickerWindow) and CustomColors
/// (toolbar "+" swatch).</summary>
public class BoundedColorListTests
{
    [Fact]
    public void Push_OnEmptyList_ReturnsSingleEntry()
    {
        var result = BoundedColorList.Push(new List<string>(), "#FF0000", 8);
        Assert.Equal(new[] { "#FF0000" }, result);
    }

    [Fact]
    public void Push_PrependsNewest()
    {
        var current = new List<string> { "#111111", "#222222" };
        var result = BoundedColorList.Push(current, "#333333", 8);
        Assert.Equal(new[] { "#333333", "#111111", "#222222" }, result);
    }

    [Fact]
    public void Push_ExistingDuplicate_MovesToFrontInsteadOfDuplicating()
    {
        var current = new List<string> { "#111111", "#222222", "#333333" };
        var result = BoundedColorList.Push(current, "#222222", 8);
        Assert.Equal(new[] { "#222222", "#111111", "#333333" }, result);
    }

    [Fact]
    public void Push_DuplicateComparisonIsCaseInsensitive()
    {
        var current = new List<string> { "#AABBCC" };
        var result = BoundedColorList.Push(current, "#aabbcc", 8);
        Assert.Equal(new[] { "#aabbcc" }, result);
    }

    [Fact]
    public void Push_CapsAtMaxCount_EvictingOldest()
    {
        var current = new List<string> { "#1", "#2", "#3", "#4", "#5", "#6", "#7", "#8" };
        var result = BoundedColorList.Push(current, "#9", maxCount: 8);
        Assert.Equal(8, result.Count);
        Assert.Equal("#9", result[0]);
        Assert.DoesNotContain("#8", result); // the oldest entry was evicted
    }

    [Fact]
    public void Push_MaxCountZero_ReturnsEmpty()
    {
        var result = BoundedColorList.Push(new List<string> { "#1" }, "#2", maxCount: 0);
        Assert.Empty(result);
    }
}
