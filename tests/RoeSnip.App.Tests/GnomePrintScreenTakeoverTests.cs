using RoeSnip.App.AppShell;
using RoeSnip.Core.Settings;
using Xunit;

namespace RoeSnip.App.Tests;

/// <summary>GNOME PrintScreen takeover against a fake gsettings store (GNOME 50 defaults).</summary>
public class GnomePrintScreenTakeoverTests
{
    private const string Schema = GnomePrintScreenTakeover.ShellSchema;

    private sealed class FakeGsettings
    {
        public readonly Dictionary<string, string> Values = new()
        {
            ["show-screenshot-ui"] = "['Print']",
            ["screenshot"] = "['<Shift>Print']",
            ["screenshot-window"] = "['<Alt>Print']",
            ["show-screen-recording-ui"] = "['<Ctrl><Shift><Alt>R']",
        };

        public readonly List<string> Sets = new();

        public string? Run(string[] args)
        {
            if (args.Length < 3 || args[1] != Schema)
            {
                return null;
            }

            if (args[0] == "get")
            {
                return Values.TryGetValue(args[2], out var v) ? v : null;
            }

            if (args[0] == "set" && args.Length == 4)
            {
                Values[args[2]] = args[3];
                Sets.Add(args[2]);
                return "";
            }

            return null;
        }
    }

    private static readonly RoeSnipSettings BarePrint = new() { HotkeyModifiers = 0, HotkeyVirtualKey = HotkeyManager.VkSnapshot };

    [Fact]
    public void Detect_BarePrint_IsScreenshotUi()
    {
        var fake = new FakeGsettings();
        var held = new GnomePrintScreenTakeover(fake.Run).Detect(0, HotkeyManager.VkSnapshot);
        Assert.Equal(new[] { "show-screenshot-ui" }, held.Keys);
    }

    [Theory]
    [InlineData(HotkeyManager.ModShift, "screenshot")]
    [InlineData(HotkeyManager.ModAlt, "screenshot-window")]
    public void Detect_ModifiedPrint(uint mods, string key)
    {
        var fake = new FakeGsettings();
        Assert.Equal(new[] { key }, new GnomePrintScreenTakeover(fake.Run).Detect(mods, HotkeyManager.VkSnapshot).Keys);
    }

    [Fact]
    public void Detect_NormalisesAliases()
    {
        var fake = new FakeGsettings();
        var held = new GnomePrintScreenTakeover(fake.Run)
            .Detect(HotkeyManager.ModControl | HotkeyManager.ModShift | HotkeyManager.ModAlt, 0x52);
        Assert.Equal(new[] { "show-screen-recording-ui" }, held.Keys);
    }

    [Fact]
    public void Detect_CtrlPrint_IsFree()
    {
        var fake = new FakeGsettings();
        Assert.Empty(new GnomePrintScreenTakeover(fake.Run).Detect(HotkeyManager.ModControl, HotkeyManager.VkSnapshot));
    }

    [Fact]
    public void TakeOver_ClearsOnlyTheConflictingKey_AndBacksItUp()
    {
        var fake = new FakeGsettings();
        var updated = new GnomePrintScreenTakeover(fake.Run).TakeOver(BarePrint, 0, HotkeyManager.VkSnapshot);

        Assert.Equal("@as []", fake.Values["show-screenshot-ui"]);
        Assert.Equal("['<Shift>Print']", fake.Values["screenshot"]);
        Assert.Equal(new[] { "show-screenshot-ui" }, fake.Sets);
        Assert.Equal("['Print']", updated.GnomeShellKeybindingBackup["show-screenshot-ui"]);
        Assert.Empty(BarePrint.GnomeShellKeybindingBackup);
    }

    [Fact]
    public void TakeOver_KeepsOtherAcceleratorsOnTheKey()
    {
        var fake = new FakeGsettings();
        fake.Values["show-screenshot-ui"] = "['Print', '<Super>s']";
        new GnomePrintScreenTakeover(fake.Run).TakeOver(BarePrint, 0, HotkeyManager.VkSnapshot);
        Assert.Equal("['<Super>s']", fake.Values["show-screenshot-ui"]);
    }

    [Fact]
    public void Restore_WritesBackupBack_AndEmptiesIt()
    {
        var fake = new FakeGsettings();
        var takeover = new GnomePrintScreenTakeover(fake.Run);
        var taken = takeover.TakeOver(BarePrint, 0, HotkeyManager.VkSnapshot);

        var restored = takeover.Restore(taken);

        Assert.Equal("['Print']", fake.Values["show-screenshot-ui"]);
        Assert.Empty(restored.GnomeShellKeybindingBackup);
    }

    [Fact]
    public void RestoreIfMovedOff_RestoresOnlyWhenTheHotkeyLeftTheKey()
    {
        var fake = new FakeGsettings();
        var takeover = new GnomePrintScreenTakeover(fake.Run);
        var taken = takeover.TakeOver(BarePrint, 0, HotkeyManager.VkSnapshot);

        Assert.Same(taken, takeover.RestoreIfMovedOff(taken, 0, HotkeyManager.VkSnapshot));
        Assert.Equal("@as []", fake.Values["show-screenshot-ui"]);

        var moved = takeover.RestoreIfMovedOff(taken, HotkeyManager.ModControl, HotkeyManager.VkSnapshot);
        Assert.Equal("['Print']", fake.Values["show-screenshot-ui"]);
        Assert.Empty(moved.GnomeShellKeybindingBackup);
    }

    [Fact]
    public void TakeOver_Twice_KeepsTheOriginalBackup()
    {
        var fake = new FakeGsettings();
        var takeover = new GnomePrintScreenTakeover(fake.Run);
        var taken = takeover.TakeOver(BarePrint, 0, HotkeyManager.VkSnapshot);
        fake.Values["show-screenshot-ui"] = "['Print']"; // GNOME reset it
        var again = takeover.TakeOver(taken, 0, HotkeyManager.VkSnapshot);
        Assert.Equal("['Print']", again.GnomeShellKeybindingBackup["show-screenshot-ui"]);
        Assert.Equal("@as []", fake.Values["show-screenshot-ui"]);
    }

    [Fact]
    public void Apply_DefaultOn_TakesOverWithoutAsking_AndSaves()
    {
        var fake = new FakeGsettings();
        var saved = new List<RoeSnipSettings>();
        var result = new GnomePrintScreenTakeover(fake.Run).Apply(BarePrint, previous: null, saved.Add);

        Assert.True(BarePrint.TakeOverSystemPrintScreen);
        Assert.Equal(0u, result.HotkeyModifiers);
        Assert.Equal("@as []", fake.Values["show-screenshot-ui"]);
        Assert.Equal("['Print']", result.GnomeShellKeybindingBackup["show-screenshot-ui"]);
        Assert.Single(saved);
    }

    [Fact]
    public void Apply_AlreadyTakenOver_ChangesNothing()
    {
        var fake = new FakeGsettings();
        var takeover = new GnomePrintScreenTakeover(fake.Run);
        var taken = takeover.Apply(BarePrint, null, _ => { });
        var saved = new List<RoeSnipSettings>();

        Assert.Same(taken, takeover.Apply(taken, taken, saved.Add));
        Assert.Empty(saved);
    }

    [Fact]
    public void Apply_OptOut_RestoresGnome_AndUsesCtrlPrint()
    {
        var fake = new FakeGsettings();
        var takeover = new GnomePrintScreenTakeover(fake.Run);
        var taken = takeover.Apply(BarePrint, null, _ => { });

        var optedOut = takeover.Apply(taken with { TakeOverSystemPrintScreen = false }, taken, _ => { });

        Assert.Equal("['Print']", fake.Values["show-screenshot-ui"]);
        Assert.Empty(optedOut.GnomeShellKeybindingBackup);
        Assert.Equal(HotkeyManager.ModControl, optedOut.HotkeyModifiers);
        Assert.Equal(HotkeyManager.VkSnapshot, optedOut.HotkeyVirtualKey);
    }

    [Fact]
    public void Apply_OptBackIn_ReturnsToPrintScreen()
    {
        var fake = new FakeGsettings();
        var takeover = new GnomePrintScreenTakeover(fake.Run);
        var optedOut = takeover.Apply(BarePrint with { TakeOverSystemPrintScreen = false }, null, _ => { });
        Assert.Equal(HotkeyManager.ModControl, optedOut.HotkeyModifiers);

        var back = takeover.Apply(optedOut with { TakeOverSystemPrintScreen = true }, optedOut, _ => { });

        Assert.Equal(0u, back.HotkeyModifiers);
        Assert.Equal("@as []", fake.Values["show-screenshot-ui"]);
    }

    [Fact]
    public void Apply_HotkeyMovedOff_RestoresGnome()
    {
        var fake = new FakeGsettings();
        var takeover = new GnomePrintScreenTakeover(fake.Run);
        var taken = takeover.Apply(BarePrint, null, _ => { });

        var moved = takeover.Apply(taken with { HotkeyModifiers = HotkeyManager.ModControl | HotkeyManager.ModShift, HotkeyVirtualKey = 0x53 }, taken, _ => { });

        Assert.Equal("['Print']", fake.Values["show-screenshot-ui"]);
        Assert.Empty(moved.GnomeShellKeybindingBackup);
    }

    [Fact]
    public void Apply_OptOut_FreeHotkey_KeepsIt()
    {
        var fake = new FakeGsettings();
        var settings = new RoeSnipSettings
        {
            HotkeyModifiers = HotkeyManager.ModControl | HotkeyManager.ModShift,
            HotkeyVirtualKey = 0x53,
            TakeOverSystemPrintScreen = false,
        };
        Assert.Same(settings, new GnomePrintScreenTakeover(fake.Run).Apply(settings, null, _ => { }));
    }
}
