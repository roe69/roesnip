using RoeSnip.App.AppShell;
using Xunit;

namespace RoeSnip.App.Tests;

/// <summary>Item 14 / WPF Bug 3: DescribeVirtualKey must print a real key name for every key the
/// SettingsWindow hotkey capture can actually produce, falling back to raw hex only for an
/// unmapped virtual-key code.</summary>
public class HotkeyDisplayFormatTests
{
    [Fact]
    public void DescribeVirtualKey_PrintScreen_IsFriendlyName()
    {
        Assert.Equal("PrintScreen", HotkeyDisplayFormat.DescribeVirtualKey(HotkeyManager.VkSnapshot));
    }

    [Theory]
    [InlineData(0x41, "A")] // letters
    [InlineData(0x5A, "Z")]
    [InlineData(0x30, "0")] // digits
    [InlineData(0x39, "9")]
    [InlineData(0x70, "F1")] // function keys
    [InlineData(0x87, "F24")]
    [InlineData(0x60, "NumPad0")]
    [InlineData(0x69, "NumPad9")]
    [InlineData(0x20, "Space")]
    [InlineData(0x2E, "Delete")]
    [InlineData(0x2D, "Insert")]
    [InlineData(0x21, "PageUp")]
    [InlineData(0x22, "PageDown")]
    [InlineData(0x23, "End")]
    [InlineData(0x24, "Home")]
    [InlineData(0x25, "Left")]
    [InlineData(0x26, "Up")]
    [InlineData(0x27, "Right")]
    [InlineData(0x28, "Down")]
    [InlineData(0x13, "Pause")]
    public void DescribeVirtualKey_MappedKeys_ReturnRealNames(uint virtualKey, string expected)
    {
        Assert.Equal(expected, HotkeyDisplayFormat.DescribeVirtualKey(virtualKey));
    }

    [Fact]
    public void DescribeVirtualKey_UnmappedKey_FallsBackToRawHex()
    {
        // 0xFF has no MapKeyToVirtualKey/VirtualKeyToKeyCode entry (never produced by a real capture).
        Assert.Equal("VK 0xFF", HotkeyDisplayFormat.DescribeVirtualKey(0xFF));
    }

    [Theory]
    [InlineData(0u, 0x41u, "A")]
    [InlineData(HotkeyManager.ModControl, 0x41u, "Ctrl+A")]
    [InlineData(HotkeyManager.ModControl | HotkeyManager.ModAlt, 0x41u, "Ctrl+Alt+A")]
    [InlineData(HotkeyManager.ModControl | HotkeyManager.ModAlt | HotkeyManager.ModShift | HotkeyManager.ModWin, 0x41u, "Ctrl+Alt+Shift+Win+A")]
    [InlineData(0u, 0x2Cu, "PrintScreen")] // VkSnapshot
    public void DescribeHotkey_OrdersModifiersThenKey(uint modifiers, uint virtualKey, string expected)
    {
        Assert.Equal(expected, HotkeyDisplayFormat.DescribeHotkey(modifiers, virtualKey));
    }
}
