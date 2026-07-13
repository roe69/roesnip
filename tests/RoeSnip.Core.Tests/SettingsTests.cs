using System;
using System.IO;
using RoeSnip.Core.Settings;
using Xunit;

namespace RoeSnip.Core.Tests;

/// <summary>Exercises SettingsStore against isolated temp-directory paths only — never the real
/// per-OS config-directory settings.json — via the public path-taking overloads of Load/Save.</summary>
public class SettingsTests : IDisposable
{
    private readonly string _tempDir;

    public SettingsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"roesnip_core_settings_test_{Guid.NewGuid():N}");
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
        // _tempDir is never created in this test — simulates a fresh install with no config dir yet.
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
            ColorPickerEnabled = true,
            RecentPickedColors = new List<string> { "#AABBCC", "#112233" },
            ColorFormatShowHex = false,
            ColorFormatShowRgb = true,
            ColorFormatShowHsl = false,
            ColorFormatShowNits = true,
            ColorFormatShowHsv = true,
            ColorFormatShowCmyk = true,
            MagnifierSampleRadius = 9,
            TextFontFamily = "Consolas",
            TextFontSize = 32.0,
            TextBold = true,
            TextItalic = true,
            CustomColors = new List<string> { "#FF00FF", "#00FFAA" },
            PaletteColors = new List<string> { "#E53935", "#123456", "#654321" },
        };

        SettingsStore.Save(original, settingsPath);
        Assert.True(File.Exists(settingsPath));

        var loaded = SettingsStore.Load(settingsPath);

        // RoeSnipSettings.RecentPickedColors/CustomColors/PaletteColors/ShareProviders are all
        // List<T>, whose default (reference) equality means a freshly-deserialized instance never ==
        // the original list instance even with identical content — assert their contents explicitly
        // (xUnit's Assert.Equal does a structural/sequence compare for IEnumerable<T>), then
        // neutralize them to the same reference before the whole-record equality check below so it
        // isn't tripped up by that same reference-equality quirk on the remaining (scalar) fields.
        // Mirrors the WPF app's own RoeSnip.Tests/SettingsTests.cs treatment of the same fields.
        Assert.Equal(original.RecentPickedColors, loaded.RecentPickedColors);
        Assert.Equal(original.CustomColors, loaded.CustomColors);
        Assert.Equal(original.PaletteColors, loaded.PaletteColors);
        Assert.Equal(original.ShareProviders, loaded.ShareProviders);
        var originalWithLoadedLists = original with
        {
            RecentPickedColors = loaded.RecentPickedColors,
            CustomColors = loaded.CustomColors,
            PaletteColors = loaded.PaletteColors,
            ShareProviders = loaded.ShareProviders,
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
        Assert.Equal(original.ShareProviders, loaded.ShareProviders);
        var originalWithLoadedLists = original with
        {
            RecentPickedColors = loaded.RecentPickedColors,
            CustomColors = loaded.CustomColors,
            PaletteColors = loaded.PaletteColors,
            ShareProviders = loaded.ShareProviders,
        };
        Assert.Equal(originalWithLoadedLists, loaded);
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

    [Fact]
    public void SaveThenLoad_RoundTripsDefaults()
    {
        Directory.CreateDirectory(_tempDir);
        string settingsPath = PathFor("settings.json");

        SettingsStore.Save(RoeSnipSettings.Default, settingsPath);
        var loaded = SettingsStore.Load(settingsPath);

        // Mirrors the WPF app's own SaveThenLoad_RoundTripsUxRound2Defaults: every field this work
        // item added must serialize AND deserialize back to its documented default, not just
        // whatever the in-memory record initializer happens to produce.
        Assert.False(loaded.ColorPickerEnabled);
        Assert.Empty(loaded.RecentPickedColors);
        Assert.True(loaded.ColorFormatShowHex);
        Assert.True(loaded.ColorFormatShowRgb);
        Assert.True(loaded.ColorFormatShowHsl);
        Assert.True(loaded.ColorFormatShowNits);
        Assert.False(loaded.ColorFormatShowHsv);
        Assert.False(loaded.ColorFormatShowCmyk);
        Assert.Equal(5, loaded.MagnifierSampleRadius);
        Assert.Equal("Segoe UI", loaded.TextFontFamily);
        Assert.Equal(20.0, loaded.TextFontSize);
        Assert.False(loaded.TextBold);
        Assert.False(loaded.TextItalic);
        Assert.Empty(loaded.CustomColors);
        Assert.Empty(loaded.PaletteColors); // empty = "not migrated" sentinel, mirrors the WPF app
    }

    [Fact]
    public void Load_JsonMissingNewFields_FallsBackToTheirDefaults()
    {
        // Simulates a settings.json written by an OLDER Core build that predates this work item's
        // fields entirely (not just a hand-added unknown field, which
        // Load_JsonWithUnknownFields_IgnoresThemAndUsesKnownValues above already covers) — every
        // field this item added must be entirely ABSENT from the JSON and still resolve to its
        // record-initializer default rather than throwing or deserializing to a CLR default (e.g.
        // TextFontSize silently becoming 0.0 instead of 20.0).
        Directory.CreateDirectory(_tempDir);
        string settingsPath = PathFor("settings.json");
        File.WriteAllText(settingsPath, """
            {
              "SchemaVersion": 1,
              "CopyOnSelect": true
            }
            """);

        var loaded = SettingsStore.Load(settingsPath);

        Assert.True(loaded.CopyOnSelect);
        Assert.False(loaded.ColorPickerEnabled);
        Assert.Empty(loaded.RecentPickedColors);
        Assert.True(loaded.ColorFormatShowHex);
        Assert.False(loaded.ColorFormatShowHsv);
        Assert.Equal(5, loaded.MagnifierSampleRadius);
        Assert.Equal("Segoe UI", loaded.TextFontFamily);
        Assert.Equal(20.0, loaded.TextFontSize);
        Assert.Empty(loaded.CustomColors);
        Assert.Empty(loaded.PaletteColors);
    }
}
