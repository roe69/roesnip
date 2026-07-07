using System;
using System.IO;
using RoeSnip.Capture;
using Xunit;

namespace RoeSnip.Tests;

/// <summary>Exercises CaptureCache against isolated temp-directory paths only — never the real
/// %APPDATA%\RoeSnip\capture-cache.json — via the public path-taking constructor.</summary>
public class CaptureCacheTests : IDisposable
{
    private readonly string _tempDir;

    public CaptureCacheTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"roesnip_capturecache_test_{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private string CachePath => Path.Combine(_tempDir, "capture-cache.json");

    [Fact]
    public void Query_MissingFile_ReturnsFalse_AndDoesNotCreateFile()
    {
        // _tempDir is never created — simulates a fresh install with no %APPDATA%\RoeSnip yet.
        var cache = new CaptureCache(CachePath);

        Assert.False(cache.IsDesktopDuplicationBroken(@"\\.\DISPLAY1"));
        Assert.False(File.Exists(CachePath));
        Assert.False(Directory.Exists(_tempDir));
    }

    [Fact]
    public void Mark_ThenQuery_SameInstance_ReturnsTrue()
    {
        var cache = new CaptureCache(CachePath);

        cache.MarkDesktopDuplicationBroken(@"\\.\DISPLAY2");

        Assert.True(cache.IsDesktopDuplicationBroken(@"\\.\DISPLAY2"));
        Assert.False(cache.IsDesktopDuplicationBroken(@"\\.\DISPLAY1"));
    }

    [Fact]
    public void Mark_CreatesMissingDirectoryAndFile()
    {
        var cache = new CaptureCache(CachePath);

        cache.MarkDesktopDuplicationBroken(@"\\.\DISPLAY1");

        Assert.True(File.Exists(CachePath));
    }

    [Fact]
    public void Mark_RoundTripsAcrossInstances()
    {
        var writer = new CaptureCache(CachePath);
        writer.MarkDesktopDuplicationBroken(@"\\.\DISPLAY1");
        writer.MarkDesktopDuplicationBroken(@"\\.\DISPLAY3");

        // Fresh instance on the same path = simulated app relaunch.
        var reader = new CaptureCache(CachePath);

        Assert.True(reader.IsDesktopDuplicationBroken(@"\\.\DISPLAY1"));
        Assert.True(reader.IsDesktopDuplicationBroken(@"\\.\DISPLAY3"));
        Assert.False(reader.IsDesktopDuplicationBroken(@"\\.\DISPLAY2"));
    }

    [Fact]
    public void DeviceNameLookup_IsCaseInsensitive()
    {
        var cache = new CaptureCache(CachePath);
        cache.MarkDesktopDuplicationBroken(@"\\.\DISPLAY1");

        Assert.True(cache.IsDesktopDuplicationBroken(@"\\.\display1"));

        var reloaded = new CaptureCache(CachePath);
        Assert.True(reloaded.IsDesktopDuplicationBroken(@"\\.\Display1"));
    }

    [Fact]
    public void Mark_SameDeviceTwice_KeepsSingleEntry()
    {
        var cache = new CaptureCache(CachePath);
        cache.MarkDesktopDuplicationBroken(@"\\.\DISPLAY1");
        cache.MarkDesktopDuplicationBroken(@"\\.\DISPLAY1");

        string json = File.ReadAllText(CachePath);
        int occurrences = json.Split("DISPLAY1").Length - 1;
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public void CorruptFile_LoadsAsEmptyMemo_WithoutThrowing()
    {
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(CachePath, "{ this is not valid json !!! ");

        var cache = new CaptureCache(CachePath);

        Assert.False(cache.IsDesktopDuplicationBroken(@"\\.\DISPLAY1"));
    }

    [Fact]
    public void CorruptFile_MarkStillPersists_NewInstanceSeesEntry()
    {
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(CachePath, "not json at all");

        var cache = new CaptureCache(CachePath);
        cache.MarkDesktopDuplicationBroken(@"\\.\DISPLAY1");

        var reloaded = new CaptureCache(CachePath);
        Assert.True(reloaded.IsDesktopDuplicationBroken(@"\\.\DISPLAY1"));
    }

    [Fact]
    public void EmptyFile_LoadsAsEmptyMemo()
    {
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(CachePath, string.Empty);

        var cache = new CaptureCache(CachePath);

        Assert.False(cache.IsDesktopDuplicationBroken(@"\\.\DISPLAY1"));
    }

    [Fact]
    public void JsonWithUnknownFields_IgnoresThemAndUsesKnownValues()
    {
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(CachePath, """
            {
              "SchemaVersion": 1,
              "SomeFutureField": "ignored please",
              "DdBrokenDeviceNames": [ "\\\\.\\DISPLAY2" ]
            }
            """);

        var cache = new CaptureCache(CachePath);

        Assert.True(cache.IsDesktopDuplicationBroken(@"\\.\DISPLAY2"));
        Assert.False(cache.IsDesktopDuplicationBroken(@"\\.\DISPLAY1"));
    }
}
