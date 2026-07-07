using System;
using System.IO;
using RoeSnip.Core.Capture;
using Xunit;

namespace RoeSnip.Core.Tests;

/// <summary>Exercises CaptureCache against isolated temp-directory paths only — never the real
/// per-OS config-directory capture-cache.json — via the public path-taking constructor.</summary>
public class CaptureCacheTests : IDisposable
{
    private readonly string _tempDir;

    public CaptureCacheTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"roesnip_core_capturecache_test_{Guid.NewGuid():N}");
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
        // _tempDir is never created — simulates a fresh install with no config dir yet.
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
        // Keys use the real "{DeviceName}::{capturerIndex}" shape (per-slot memo entries are what
        // every current caller actually writes) — a BARE device name is only ever seen as legacy
        // input to the C1 migration path, covered separately; it is not itself a round-trip case.
        var writer = new CaptureCache(CachePath);
        writer.MarkDesktopDuplicationBroken(@"\\.\DISPLAY1::0");
        writer.MarkDesktopDuplicationBroken(@"\\.\DISPLAY3::0");

        // Fresh instance on the same path = simulated app relaunch.
        var reader = new CaptureCache(CachePath);

        Assert.True(reader.IsDesktopDuplicationBroken(@"\\.\DISPLAY1::0"));
        Assert.True(reader.IsDesktopDuplicationBroken(@"\\.\DISPLAY3::0"));
        Assert.False(reader.IsDesktopDuplicationBroken(@"\\.\DISPLAY2::0"));
    }

    [Fact]
    public void DeviceNameLookup_IsCaseInsensitive()
    {
        var cache = new CaptureCache(CachePath);
        cache.MarkDesktopDuplicationBroken(@"\\.\DISPLAY1::0");

        Assert.True(cache.IsDesktopDuplicationBroken(@"\\.\display1::0"));

        var reloaded = new CaptureCache(CachePath);
        Assert.True(reloaded.IsDesktopDuplicationBroken(@"\\.\Display1::0"));
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
        cache.MarkDesktopDuplicationBroken(@"\\.\DISPLAY1::0");

        var reloaded = new CaptureCache(CachePath);
        Assert.True(reloaded.IsDesktopDuplicationBroken(@"\\.\DISPLAY1::0"));
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
    public void Unmark_RemovesEntryAndPersists()
    {
        var cache = new CaptureCache(CachePath);
        cache.MarkDesktopDuplicationBroken(@"\\.\DISPLAY1");

        cache.Unmark(@"\\.\DISPLAY1");

        Assert.False(cache.IsDesktopDuplicationBroken(@"\\.\DISPLAY1"));

        // Fresh instance on the same path = simulated app relaunch — the removal must be persisted,
        // not just cleared from the in-memory memo.
        var reloaded = new CaptureCache(CachePath);
        Assert.False(reloaded.IsDesktopDuplicationBroken(@"\\.\DISPLAY1"));
    }

    [Fact]
    public void Unmark_MissingEntry_DoesNotCreateFile()
    {
        // _tempDir is never created — simulates unmarking something that was never marked broken.
        var cache = new CaptureCache(CachePath);

        cache.Unmark(@"\\.\DISPLAY1");

        Assert.False(File.Exists(CachePath));
        Assert.False(Directory.Exists(_tempDir));
    }

    [Fact]
    public void JsonWithUnknownFields_IgnoresThemAndUsesKnownValues()
    {
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(CachePath, """
            {
              "SchemaVersion": 1,
              "SomeFutureField": "ignored please",
              "DdBrokenDeviceNames": [ "\\\\.\\DISPLAY2::0" ]
            }
            """);

        var cache = new CaptureCache(CachePath);

        Assert.True(cache.IsDesktopDuplicationBroken(@"\\.\DISPLAY2::0"));
        Assert.False(cache.IsDesktopDuplicationBroken(@"\\.\DISPLAY1::0"));
    }

    [Fact]
    public void EnsureLoaded_MigratesBareLegacyEntry_ToSlotZero_AndPersists()
    {
        // C1 audit fix: the pre-generalization cache (and the frozen WPF app) stored bare
        // DeviceName entries with no "::{capturerIndex}" suffix. FallbackCaptureBackend now keys
        // per-capturer-slot as "{DeviceName}::{i}", so a bare entry never matches anything unless
        // it is migrated to slot 0 (the primary capturer) on load.
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(CachePath, """
            {
              "SchemaVersion": 1,
              "DdBrokenDeviceNames": [ "\\\\.\\DISPLAY1" ]
            }
            """);

        var cache = new CaptureCache(CachePath);

        Assert.True(cache.IsDesktopDuplicationBroken(@"\\.\DISPLAY1::0"));
        Assert.False(cache.IsDesktopDuplicationBroken(@"\\.\DISPLAY1"));

        // The migration is persisted, not just applied in-memory — a fresh instance on the same
        // path must see the migrated (suffixed) form, not the original bare entry.
        var reloaded = new CaptureCache(CachePath);
        Assert.True(reloaded.IsDesktopDuplicationBroken(@"\\.\DISPLAY1::0"));
        Assert.False(reloaded.IsDesktopDuplicationBroken(@"\\.\DISPLAY1"));
    }
}
