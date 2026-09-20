using System;
using System.IO;
using RoeSnip.Core.Clipboard;
using Xunit;

namespace RoeSnip.Core.Tests;

/// <summary>The still capture the resident keeps so it can be re-copied or saved from the tray
/// after the overlay has closed (Clipboard/LastCapture.cs).</summary>
public class LastCaptureTests
{
    private static KeptCapture Capture(int w = 4, int h = 2, DateTime? taken = null) =>
        new(w, h, new byte[w * 4 * h], taken ?? new DateTime(2026, 9, 20, 18, 42, 7));

    [Fact]
    public void Describe_NamesSizeAndTime()
    {
        Assert.Equal("1920 x 1080, 18:42", Capture(1920, 1080).Describe());
    }

    [Fact]
    public void SuggestSavePath_UsesTheCaptureTimeNotTheSaveTime()
    {
        string dir = Path.Combine(Path.GetTempPath(), "RoeSnipTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Equal(
                Path.Combine(dir, "roesnip_20260920_184207.png"),
                Capture().SuggestSavePath(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SuggestSavePath_NeverOverwritesAnEarlierSaveOfTheSameCapture()
    {
        string dir = Path.Combine(Path.GetTempPath(), "RoeSnipTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var kept = Capture();
            string first = kept.SuggestSavePath(dir);
            File.WriteAllBytes(first, Array.Empty<byte>());
            string second = kept.SuggestSavePath(dir);
            File.WriteAllBytes(second, Array.Empty<byte>());

            Assert.Equal(Path.Combine(dir, "roesnip_20260920_184207_2.png"), second);
            Assert.Equal(Path.Combine(dir, "roesnip_20260920_184207_3.png"), kept.SuggestSavePath(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Keep_ReplacesTheOneKeptCaptureAndAnnouncesIt()
    {
        int changes = 0;
        void OnChanged() => changes++;
        LastCapture.Changed += OnChanged;
        try
        {
            LastCapture.Keep(8, 4, new byte[8 * 4 * 4], DateTime.Now);
            var first = LastCapture.Current;
            LastCapture.Keep(2, 2, new byte[2 * 4 * 2], DateTime.Now);

            Assert.NotNull(first);
            Assert.Equal(2, LastCapture.Current!.Width); // only ever one, the newest
            LastCapture.Clear();
            Assert.Null(LastCapture.Current);
            Assert.Equal(3, changes); // two keeps and the clear
        }
        finally
        {
            LastCapture.Changed -= OnChanged;
            LastCapture.Clear();
        }
    }
}
