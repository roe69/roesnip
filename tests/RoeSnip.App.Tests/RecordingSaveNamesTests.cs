using System;
using System.IO;
using RoeSnip.App.Recording;
using RoeSnip.Core.Recording;
using Xunit;

namespace RoeSnip.App.Tests;

/// <summary>The save dialog's name, extension and type filter - the WPF PickSavePath values.</summary>
public class RecordingSaveNamesTests
{
    [Theory]
    [InlineData(RecordingFormat.Mp4, "roesnip_20261123_090502.mp4")]
    [InlineData(RecordingFormat.Gif, "roesnip_20261123_090502.gif")]
    public void SuggestedFileName_MatchesTheWpfPattern(RecordingFormat format, string expected)
    {
        Assert.Equal(expected, RecordingSaveNames.SuggestedFileName(format, new DateTime(2026, 11, 23, 9, 5, 2)));
    }

    [Fact]
    public void EnsureExtension_AddsTheTakesExtensionOnlyWhenMissing()
    {
        string dir = Path.GetTempPath();
        Assert.Equal(Path.Combine(dir, "clip.mp4"), RecordingSaveNames.EnsureExtension(Path.Combine(dir, "clip"), RecordingFormat.Mp4));
        Assert.Equal(Path.Combine(dir, "clip.gif"), RecordingSaveNames.EnsureExtension(Path.Combine(dir, "clip.gif"), RecordingFormat.Gif));
        Assert.Equal(Path.Combine(dir, "clip.mov"), RecordingSaveNames.EnsureExtension(Path.Combine(dir, "clip.mov"), RecordingFormat.Mp4));
    }

    [Fact]
    public void FileType_IsTheOneFormatOfTheTake()
    {
        var mp4 = RecordingSaveNames.FileType(RecordingFormat.Mp4);
        Assert.Equal("MP4 video (*.mp4)", mp4.Name);
        Assert.Equal(new[] { "*.mp4" }, mp4.Patterns);

        var gif = RecordingSaveNames.FileType(RecordingFormat.Gif);
        Assert.Equal("GIF image (*.gif)", gif.Name);
        Assert.Equal(new[] { "*.gif" }, gif.Patterns);
    }

    [Fact]
    public void AutosavePath_IsNullWithoutTheEnvVar_AndInsideItWhenSet()
    {
        string? old = Environment.GetEnvironmentVariable(RecordingSaveNames.AutosaveEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(RecordingSaveNames.AutosaveEnvVar, null);
            Assert.Null(RecordingSaveNames.AutosavePath(RecordingFormat.Mp4, DateTime.Now));

            string dir = Path.Combine(Path.GetTempPath(), "roesnip-autosave-test");
            Environment.SetEnvironmentVariable(RecordingSaveNames.AutosaveEnvVar, dir);
            var now = new DateTime(2026, 11, 23, 9, 5, 2);
            Assert.Equal(Path.Combine(dir, "roesnip_20261123_090502.gif"), RecordingSaveNames.AutosavePath(RecordingFormat.Gif, now));
        }
        finally
        {
            Environment.SetEnvironmentVariable(RecordingSaveNames.AutosaveEnvVar, old);
        }
    }
}
