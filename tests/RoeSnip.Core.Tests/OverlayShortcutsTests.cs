using System.IO;
using RoeSnip.Core.Settings;
using Xunit;
using static RoeSnip.Core.Settings.OverlayShortcuts;

namespace RoeSnip.Core.Tests;

/// <summary>The overlay's rebindable Copy/Upload shortcuts (Settings/OverlayShortcuts.cs).</summary>
public class OverlayShortcutsTests
{
    private const uint VkC = 0x43;
    private const uint VkU = 0x55;
    private const uint VkSnapshot = 0x2C;

    [Fact]
    public void Defaults_AreCtrlCAndCtrlU()
    {
        var settings = RoeSnipSettings.Default;
        Assert.Equal((ModControl, VkC), (settings.CopyShortcutModifiers, settings.CopyShortcutVirtualKey));
        Assert.Equal((ModControl, VkU), (settings.UploadShortcutModifiers, settings.UploadShortcutVirtualKey));
    }

    [Fact]
    public void SettingsFileWithoutTheFields_LoadsTheDefaults()
    {
        string path = Path.Combine(Path.GetTempPath(), $"roesnip_shortcuts_{System.Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ \"SchemaVersion\": 1 }");
        try
        {
            var loaded = SettingsStore.Load(path);
            Assert.Equal(VkC, loaded.CopyShortcutVirtualKey);
            Assert.Equal(VkU, loaded.UploadShortcutVirtualKey);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Matches_IsExactOnModifiers()
    {
        Assert.True(Matches(ModControl, VkC, ModControl, VkC));
        Assert.False(Matches(ModControl, VkC, ModControl | ModShift, VkC));
        Assert.False(Matches(ModControl, VkC, 0, VkC));
        Assert.False(Matches(ModControl, VkC, ModControl, VkU));
    }

    [Fact]
    public void Matches_NeverFiresForAnUnboundKey()
    {
        Assert.False(Matches(0, 0, 0, 0));
    }

    [Theory]
    [InlineData(0u, 0x1Bu, "Cancel")]
    [InlineData(ModShift, 0x0Du, "Confirm")]
    [InlineData(0u, 0x2Eu, "Delete annotation")]
    [InlineData(ModControl, 0x25u, "Move selection")]
    [InlineData(ModControl, 0x53u, "Save")]
    [InlineData(ModControl, 0x5Au, "Undo")]
    [InlineData(ModControl | ModShift, 0x5Au, "Redo")]
    [InlineData(ModControl, 0x59u, "Redo")]
    [InlineData(ModControl, 0x41u, "Select all")]
    public void FixedActionFor_NamesTheTakenKeys(uint modifiers, uint virtualKey, string expected)
    {
        Assert.Equal(expected, FixedActionFor(modifiers, virtualKey));
    }

    [Theory]
    [InlineData(ModControl | ModShift, 0x53u)] // Ctrl+Shift+S
    [InlineData(ModAlt, 0x53u)]                // Alt+S
    [InlineData(ModControl, VkU)]
    [InlineData(0u, VkU)]
    public void FixedActionFor_LeavesOtherCombinationsFree(uint modifiers, uint virtualKey)
    {
        Assert.Null(FixedActionFor(modifiers, virtualKey));
    }

    [Fact]
    public void Rejection_TurnsAwayTheOtherShortcutTheHotkeyAndWinCombos()
    {
        Assert.Equal("Already Upload", Rejection(ModControl, VkU, "Upload", ModControl, VkU, 0, VkSnapshot));
        Assert.Equal("Already the capture hotkey", Rejection(0, VkSnapshot, "Upload", ModControl, VkU, 0, VkSnapshot));
        Assert.Equal("Already Save", Rejection(ModControl, 0x53, "Upload", ModControl, VkU, 0, VkSnapshot));
        Assert.Equal("Win-key combinations belong to the OS", Rejection(ModWin, VkC, "Upload", ModControl, VkU, 0, VkSnapshot));
    }

    [Fact]
    public void Rejection_AcceptsAFreeCombination()
    {
        Assert.Null(Rejection(ModControl | ModShift, VkC, "Upload", ModControl, VkU, 0, VkSnapshot));
    }
}
