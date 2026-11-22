using RoeSnip.App.AppShell;
using Xunit;

namespace RoeSnip.App.Tests;

/// <summary>Wayland capture hotkey: stored VK/MOD_* hotkey to the GlobalShortcuts portal trigger
/// and GNOME accelerator, and the gsettings custom-keybindings list merge.</summary>
public class LinuxShortcutFormatTests
{
    private const uint Ctrl = HotkeyManager.ModControl, Alt = HotkeyManager.ModAlt,
        Shift = HotkeyManager.ModShift, Win = HotkeyManager.ModWin;

    [Theory]
    [InlineData(0u, 0x2Cu, "Print")]
    [InlineData(Ctrl | Shift, 0x53u, "CTRL+SHIFT+s")]
    [InlineData(Win | Shift | Alt | Ctrl, 0x41u, "CTRL+ALT+SHIFT+LOGO+a")]
    [InlineData(Alt, 0x70u, "ALT+F1")]
    [InlineData(0u, 0x87u, "F24")]
    [InlineData(Ctrl, 0x35u, "CTRL+5")]
    [InlineData(Shift, 0x63u, "SHIFT+KP_3")]
    [InlineData(Ctrl, 0x20u, "CTRL+space")]
    [InlineData(0u, 0x21u, "Page_Up")]
    [InlineData(0u, 0x13u, "Pause")]
    public void ToPortalTrigger_UsesXdgShortcutSyntax(uint mods, uint vk, string expected)
    {
        Assert.Equal(expected, LinuxShortcutFormat.ToPortalTrigger(mods, vk));
    }

    [Fact]
    public void ToPortalTrigger_UnmappedKey_IsNull()
    {
        Assert.Null(LinuxShortcutFormat.ToPortalTrigger(Ctrl, 0xBA));
    }

    [Fact]
    public void EveryHookKey_HasAPortalTrigger()
    {
        for (uint vk = 0; vk <= 0xFF; vk++)
        {
            bool hook = !HotkeyDisplayFormat.DescribeVirtualKey(vk).StartsWith("VK 0x", StringComparison.Ordinal);
            Assert.Equal(hook, LinuxShortcutFormat.KeysymName(vk) is not null);
        }
    }

    [Theory]
    [InlineData(0u, 0x2Cu, "Print")]
    [InlineData(Ctrl | Shift, 0x53u, "<Control><Shift>s")]
    [InlineData(Win | Alt, 0x44u, "<Alt><Super>d")]
    public void ToGtkAccelerator_UsesGtkSyntax(uint mods, uint vk, string expected)
    {
        Assert.Equal(expected, LinuxShortcutFormat.ToGtkAccelerator(mods, vk));
    }

    [Theory]
    [InlineData("<Control><Shift>s", "<Shift><Primary>S", true)]
    [InlineData("<Ctrl>Print", "<Control>Print", true)]
    [InlineData("<Super>d", "<Mod4>d", true)]
    [InlineData("Print", "<Shift>Print", false)]
    [InlineData("", "Print", false)]
    public void AcceleratorsEqual_IgnoresOrderCaseAndAliases(string a, string b, bool expected)
    {
        Assert.Equal(expected, LinuxShortcutFormat.AcceleratorsEqual(a, b));
    }

    [Fact]
    public void ParseStringArray_EmptyTyped()
    {
        Assert.Empty(LinuxShortcutFormat.ParseStringArray("@as []"));
    }

    [Fact]
    public void ParseStringArray_ReadsEntries()
    {
        var list = LinuxShortcutFormat.ParseStringArray(
            "['/org/gnome/settings-daemon/plugins/media-keys/custom-keybindings/custom0/', '/x/custom1/']");
        Assert.Equal(new[] { "/org/gnome/settings-daemon/plugins/media-keys/custom-keybindings/custom0/", "/x/custom1/" }, list);
    }

    [Fact]
    public void ParseStringArray_SingleStringWithEscapes()
    {
        Assert.Equal(new[] { "it's" }, LinuxShortcutFormat.ParseStringArray(@"'it\'s'"));
        Assert.Equal(new[] { "it's" }, LinuxShortcutFormat.ParseStringArray("\"it's\""));
    }

    [Fact]
    public void Merge_AppendsOurPathAfterExisting()
    {
        var merged = LinuxShortcutFormat.MergeKeybindingList(new[] { "/a/custom0/" }, LinuxShortcutFormat.GnomeRoeSnipPath);
        Assert.Equal(new[] { "/a/custom0/", LinuxShortcutFormat.GnomeRoeSnipPath }, merged);
    }

    [Fact]
    public void Merge_EmptyList()
    {
        var merged = LinuxShortcutFormat.MergeKeybindingList(new string[0], LinuxShortcutFormat.GnomeRoeSnipPath);
        Assert.Equal(new[] { LinuxShortcutFormat.GnomeRoeSnipPath }, merged);
    }

    [Fact]
    public void Merge_AlreadyPresent_IsNull()
    {
        string withoutSlash = LinuxShortcutFormat.GnomeRoeSnipPath.TrimEnd('/');
        Assert.Null(LinuxShortcutFormat.MergeKeybindingList(new[] { "/a/", withoutSlash }, LinuxShortcutFormat.GnomeRoeSnipPath));
    }

    [Fact]
    public void FormatStringArray_RoundTrips()
    {
        Assert.Equal("@as []", LinuxShortcutFormat.FormatStringArray(new string[0]));
        string text = LinuxShortcutFormat.FormatStringArray(new[] { "/a/", "/b/" });
        Assert.Equal("['/a/', '/b/']", text);
        Assert.Equal(new[] { "/a/", "/b/" }, LinuxShortcutFormat.ParseStringArray(text));
    }

    [Fact]
    public void CaptureCommand_QuotesPathsWithSpaces()
    {
        Assert.Equal("/opt/RoeSnip/RoeSnip capture", LinuxShortcutFormat.CaptureCommand("/opt/RoeSnip/RoeSnip"));
        Assert.Equal("'/home/u/My Apps/RoeSnip.AppImage' capture",
            LinuxShortcutFormat.CaptureCommand("/home/u/My Apps/RoeSnip.AppImage"));
        Assert.Equal(@"'/a/it'\''s/RoeSnip' capture", LinuxShortcutFormat.CaptureCommand("/a/it's/RoeSnip"));
    }

    [Fact]
    public void GVariantString_EscapesQuotes()
    {
        Assert.Equal(@"'it\'s \\ ok'", LinuxShortcutFormat.GVariantString(@"it's \ ok"));
        Assert.Equal(new[] { @"it's \ ok" }, LinuxShortcutFormat.ParseStringArray(LinuxShortcutFormat.GVariantString(@"it's \ ok")));
    }

    [Theory]
    [InlineData("/opt/RoeSnip/RoeSnip capture", true)]
    [InlineData("'/home/u/RoeSnip.AppImage' capture", true)]
    [InlineData("flameshot gui", false)]
    [InlineData("/opt/roesnip/RoeSnip settings", false)]
    [InlineData(null, false)]
    public void IsRoeSnipCaptureCommand(string? command, bool expected)
    {
        Assert.Equal(expected, LinuxShortcutFormat.IsRoeSnipCaptureCommand(command));
    }
}
