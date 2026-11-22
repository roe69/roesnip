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
    public async Task Resolve_Yes_TakesOver_AndMarksAnswered()
    {
        var fake = new FakeGsettings();
        var saved = new List<RoeSnipSettings>();
        var result = await new GnomePrintScreenTakeover(fake.Run)
            .ResolveAsync(BarePrint, (_, _) => Task.FromResult<bool?>(true), saved.Add);

        Assert.True(result.PrintScreenPromptAnswered);
        Assert.Equal(0u, result.HotkeyModifiers);
        Assert.Equal("@as []", fake.Values["show-screenshot-ui"]);
        Assert.Single(saved);
    }

    [Fact]
    public async Task Resolve_No_SwitchesToCtrlPrint_LeavesGnome()
    {
        var fake = new FakeGsettings();
        var result = await new GnomePrintScreenTakeover(fake.Run)
            .ResolveAsync(BarePrint, (_, _) => Task.FromResult<bool?>(false), _ => { });

        Assert.True(result.PrintScreenPromptAnswered);
        Assert.Equal(HotkeyManager.ModControl, result.HotkeyModifiers);
        Assert.Equal("['Print']", fake.Values["show-screenshot-ui"]);
    }

    [Fact]
    public async Task Resolve_Dismissed_AsksAgainLater()
    {
        var fake = new FakeGsettings();
        var saved = new List<RoeSnipSettings>();
        var result = await new GnomePrintScreenTakeover(fake.Run)
            .ResolveAsync(BarePrint, (_, _) => Task.FromResult<bool?>(null), saved.Add);

        Assert.False(result.PrintScreenPromptAnswered);
        Assert.Empty(saved);
        Assert.Equal("['Print']", fake.Values["show-screenshot-ui"]);
    }

    [Fact]
    public async Task Resolve_AlreadyAnswered_DoesNotAsk()
    {
        var fake = new FakeGsettings();
        bool asked = false;
        var settings = BarePrint with { PrintScreenPromptAnswered = true };
        var result = await new GnomePrintScreenTakeover(fake.Run)
            .ResolveAsync(settings, (_, _) => { asked = true; return Task.FromResult<bool?>(true); }, _ => { });

        Assert.False(asked);
        Assert.Same(settings, result);
    }

    [Fact]
    public async Task Resolve_NoConflict_DoesNotAsk()
    {
        var fake = new FakeGsettings();
        fake.Values["show-screenshot-ui"] = "@as []";
        bool asked = false;
        await new GnomePrintScreenTakeover(fake.Run)
            .ResolveAsync(BarePrint, (_, _) => { asked = true; return Task.FromResult<bool?>(true); }, _ => { });
        Assert.False(asked);
    }
}
