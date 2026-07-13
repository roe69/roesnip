using RoeSnip.App.Recording;
using RoeSnip.Core.Capture;
using Xunit;

namespace RoeSnip.App.Tests;

/// <summary>RecordingController.RoundDownToEven — the H.264 4:2:0 chroma-subsampling floor ported
/// verbatim from the WPF app's own RecordingController, exercised directly (pure geometry, no
/// capture backend/encoder involved).</summary>
public class RecordingControllerTests
{
    [Fact]
    public void EvenSelection_IsUnchanged()
    {
        var selection = RectPhysical.FromSize(10, 20, 400, 300);
        Assert.Equal(selection, RecordingController.RoundDownToEven(selection));
    }

    [Fact]
    public void OddWidthAndHeight_FloorToEven()
    {
        var selection = RectPhysical.FromSize(10, 20, 401, 301);
        var result = RecordingController.RoundDownToEven(selection);
        Assert.Equal(400, result.Width);
        Assert.Equal(300, result.Height);
        Assert.Equal(10, result.Left);
        Assert.Equal(20, result.Top);
    }

    [Fact]
    public void TinyOddSelection_FloorsToTheTwoByTwoMinimum_NeverZero()
    {
        var selection = RectPhysical.FromSize(0, 0, 1, 1);
        var result = RecordingController.RoundDownToEven(selection);
        Assert.Equal(2, result.Width);
        Assert.Equal(2, result.Height);
    }

    [Fact]
    public void InvertedSelection_IsNormalizedFirst()
    {
        // Right < Left / Bottom < Top (a drag that went backward) must be normalized before the
        // even-floor applies, same as RectPhysical.Normalized()'s own contract.
        var selection = new RectPhysical(401, 301, 10, 20);
        var result = RecordingController.RoundDownToEven(selection);
        Assert.Equal(10, result.Left);
        Assert.Equal(20, result.Top);
        Assert.Equal(390, result.Width); // (401-10)=391 -> floored even 390
        Assert.Equal(280, result.Height); // (301-20)=281 -> floored even 280
    }
}
