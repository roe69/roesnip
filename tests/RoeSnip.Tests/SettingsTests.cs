using System;
using System.Collections.Generic;
using System.IO;
using RoeSnip.App;
using Xunit;

namespace RoeSnip.Tests;

/// <summary>Exercises SettingsStore against isolated temp-directory paths only — never the real
/// %APPDATA%\RoeSnip\settings.json — via the public path-taking overloads of Load/Save.</summary>
public class SettingsTests : IDisposable
{
    private readonly string _tempDir;

    public SettingsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"roesnip_settings_test_{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private string PathFor(string fileName) => Path.Combine(_tempDir, fileName);

    [Fact]
    public void Load_MissingDirectory_ReturnsDefault_AndDoesNotCreateFile()
    {
        // _tempDir is never created in this test — simulates a fresh install with no %APPDATA%\RoeSnip yet.
        string settingsPath = PathFor("settings.json");

        var loaded = SettingsStore.Load(settingsPath);

        Assert.Equal(RoeSnipSettings.Default, loaded);
        Assert.False(File.Exists(settingsPath));
        Assert.False(Directory.Exists(_tempDir));
    }

    [Fact]
    public void Load_MissingFile_ButExistingDirectory_ReturnsDefault_AndDoesNotCreateFile()
    {
        Directory.CreateDirectory(_tempDir);
        string settingsPath = PathFor("settings.json");

        var loaded = SettingsStore.Load(settingsPath);

        Assert.Equal(RoeSnipSettings.Default, loaded);
        Assert.False(File.Exists(settingsPath));
    }

    [Fact]
    public void Load_EmptyFile_ReturnsDefault_AndLeavesFileUntouched()
    {
        Directory.CreateDirectory(_tempDir);
        string settingsPath = PathFor("settings.json");
        File.WriteAllText(settingsPath, string.Empty);

        var loaded = SettingsStore.Load(settingsPath);

        Assert.Equal(RoeSnipSettings.Default, loaded);
        Assert.Equal(string.Empty, File.ReadAllText(settingsPath));
    }

    [Fact]
    public void Load_CorruptJson_ReturnsDefault_AndLeavesCorruptFileUntouched()
    {
        Directory.CreateDirectory(_tempDir);
        string settingsPath = PathFor("settings.json");
        const string corrupt = "{ this is not valid json !!! ";
        File.WriteAllText(settingsPath, corrupt);

        var loaded = SettingsStore.Load(settingsPath);

        Assert.Equal(RoeSnipSettings.Default, loaded);
        // Fail-closed: the corrupt file on disk must be left exactly as found, never overwritten.
        Assert.Equal(corrupt, File.ReadAllText(settingsPath));
    }

    [Fact]
    public void Load_JsonWithUnknownFields_IgnoresThemAndUsesKnownValues()
    {
        Directory.CreateDirectory(_tempDir);
        string settingsPath = PathFor("settings.json");
        File.WriteAllText(settingsPath, """
            {
              "SchemaVersion": 1,
              "SomeFutureField": "ignored please",
              "CopyOnSelect": true
            }
            """);

        var loaded = SettingsStore.Load(settingsPath);

        Assert.True(loaded.CopyOnSelect);
        // Fields absent from the JSON fall back to the record's own defaults.
        Assert.Equal(RoeSnipSettings.Default.HotkeyVirtualKey, loaded.HotkeyVirtualKey);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsEveryField()
    {
        Directory.CreateDirectory(_tempDir);
        string settingsPath = PathFor("settings.json");

        var original = new RoeSnipSettings
        {
            SchemaVersion = 1,
            HotkeyModifiers = 0x0002, // MOD_CONTROL
            HotkeyVirtualKey = 0x2C,  // VK_SNAPSHOT
            SaveDirectory = @"D:\Screenshots\RoeSnip",
            AutoSaveHdrCopy = true,
            ToneMapKneeOverride = 0.85,
            ToneMapPeakOverride = 4.0,
            RunAtStartup = true,
            CopyOnSelect = true,
            PrintScreenPromptAnswered = true,
            RecentPickedColors = new List<string> { "#AABBCC", "#112233" },
            ColorFormatShowHex = false,
            ColorFormatShowRgb = true,
            ColorFormatShowHsl = false,
            ColorFormatShowNits = true,
            TextFontFamily = "Consolas",
            TextFontSize = 32.0,
            TextBold = true,
            TextItalic = true,
            CustomColors = new List<string> { "#FF00FF", "#00FFAA" },
            PaletteColors = new List<string> { "#E53935", "#123456", "#654321" },
            ColorFormats = new List<ColorFormatEntry>
            {
                new() { Name = "HEX", Format = "%Rex%Grx%Blx", Enabled = true },
                new() { Name = "Mine", Format = "R=%Re", Enabled = false, IsCustom = true },
            },
        };

        SettingsStore.Save(original, settingsPath);
        Assert.True(File.Exists(settingsPath));

        var loaded = SettingsStore.Load(settingsPath);

        // RoeSnipSettings.RecentPickedColors/CustomColors are List<string>, whose default
        // (reference) equality means a freshly-deserialized instance never == the original list
        // instance even with identical content — assert their contents explicitly (xUnit's
        // Assert.Equal does a structural/sequence compare for IEnumerable<T>), then neutralize
        // them to the same reference before the whole-record equality check below so it isn't
        // tripped up by that same reference-equality quirk on the remaining (scalar) fields.
        Assert.Equal(original.RecentPickedColors, loaded.RecentPickedColors);
        Assert.Equal(original.CustomColors, loaded.CustomColors);
        Assert.Equal(original.PaletteColors, loaded.PaletteColors);
        // ColorFormatEntry is itself a record, so xUnit's sequence compare checks entry VALUES here
        // (name/format/enabled/custom all round-trip), unlike the string lists above.
        Assert.Equal(original.ColorFormats, loaded.ColorFormats);
        var originalWithLoadedLists = original with
        {
            RecentPickedColors = loaded.RecentPickedColors,
            CustomColors = loaded.CustomColors,
            PaletteColors = loaded.PaletteColors,
            ColorFormats = loaded.ColorFormats,
        };
        Assert.Equal(originalWithLoadedLists, loaded);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsNullOverrides()
    {
        Directory.CreateDirectory(_tempDir);
        string settingsPath = PathFor("settings.json");

        var original = new RoeSnipSettings
        {
            ToneMapKneeOverride = null,
            ToneMapPeakOverride = null,
        };

        SettingsStore.Save(original, settingsPath);
        var loaded = SettingsStore.Load(settingsPath);

        Assert.Null(loaded.ToneMapKneeOverride);
        Assert.Null(loaded.ToneMapPeakOverride);

        // See SaveThenLoad_RoundTripsEveryField for why the list-valued fields need this treatment.
        Assert.Equal(original.RecentPickedColors, loaded.RecentPickedColors);
        Assert.Equal(original.CustomColors, loaded.CustomColors);
        Assert.Equal(original.PaletteColors, loaded.PaletteColors);
        Assert.Equal(original.ColorFormats, loaded.ColorFormats);
        var originalWithLoadedLists = original with
        {
            RecentPickedColors = loaded.RecentPickedColors,
            CustomColors = loaded.CustomColors,
            PaletteColors = loaded.PaletteColors,
            ColorFormats = loaded.ColorFormats,
        };
        Assert.Equal(originalWithLoadedLists, loaded);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsUxRound2Defaults()
    {
        Directory.CreateDirectory(_tempDir);
        string settingsPath = PathFor("settings.json");

        SettingsStore.Save(RoeSnipSettings.Default, settingsPath);
        var loaded = SettingsStore.Load(settingsPath);

        Assert.Empty(loaded.RecentPickedColors);
        Assert.Empty(loaded.CustomColors);
        Assert.Empty(loaded.PaletteColors); // empty = "not migrated" sentinel, see SwatchPalette
        Assert.True(loaded.ColorFormatShowHex);
        Assert.True(loaded.ColorFormatShowRgb);
        Assert.True(loaded.ColorFormatShowHsl);
        Assert.True(loaded.ColorFormatShowNits);
        Assert.Equal("Segoe UI", loaded.TextFontFamily);
        Assert.Equal(20.0, loaded.TextFontSize);
        Assert.False(loaded.TextBold);
        Assert.False(loaded.TextItalic);
    }

    [Fact]
    public void Save_OverwritingExistingFile_ReplacesContentAtomically()
    {
        Directory.CreateDirectory(_tempDir);
        string settingsPath = PathFor("settings.json");

        SettingsStore.Save(new RoeSnipSettings { CopyOnSelect = false }, settingsPath);
        SettingsStore.Save(new RoeSnipSettings { CopyOnSelect = true }, settingsPath);

        var loaded = SettingsStore.Load(settingsPath);
        Assert.True(loaded.CopyOnSelect);

        // No stray .tmp-* files should be left behind after a successful save.
        var leftovers = Directory.GetFiles(_tempDir, "*.tmp-*");
        Assert.Empty(leftovers);
    }

    [Fact]
    public void Save_CreatesMissingDirectory()
    {
        string nestedPath = Path.Combine(_tempDir, "nested", "settings.json");

        SettingsStore.Save(RoeSnipSettings.Default, nestedPath);

        Assert.True(File.Exists(nestedPath));
    }
}
