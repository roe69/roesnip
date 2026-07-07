using System.Windows.Input;
using RoeSnip.Overlay;
using Xunit;

namespace RoeSnip.Tests;

/// <summary>The pure parts of TextEditKeyForwarder (item 3b's guaranteed fallback tier for typing
/// into a text annotation when the overlay window doesn't hold real OS focus) — key classification
/// and the synthetic ToUnicodeEx key-state array, both independent of any live keyboard/OS state.</summary>
public class TextEditKeyForwarderTests
{
    // Well-known Win32 virtual-key codes (VK_SHIFT = 0x10, VK_CAPITAL = 0x14) — asserted against
    // directly rather than via OverlayInputInterop's (internal) constants, since this test project
    // has no InternalsVisibleTo edit and the values are stable ABI constants either way.
    private const int VkShift = 0x10;
    private const int VkCapital = 0x14;

    [Theory]
    [InlineData(Key.A)]
    [InlineData(Key.Z)]
    [InlineData(Key.D0)]
    [InlineData(Key.D9)]
    [InlineData(Key.Space)]
    [InlineData(Key.OemComma)]
    [InlineData(Key.OemPeriod)]
    [InlineData(Key.NumPad5)]
    public void IsPrintableCandidate_LettersDigitsAndPunctuation_ReturnsTrue(Key key)
    {
        Assert.True(TextEditKeyForwarder.IsPrintableCandidate(key));
    }

    [Theory]
    [InlineData(Key.Enter)]
    [InlineData(Key.Escape)]
    [InlineData(Key.Tab)]
    [InlineData(Key.Back)]
    [InlineData(Key.Delete)]
    [InlineData(Key.Left)]
    [InlineData(Key.Right)]
    [InlineData(Key.Up)]
    [InlineData(Key.Down)]
    [InlineData(Key.Home)]
    [InlineData(Key.End)]
    [InlineData(Key.LeftShift)]
    [InlineData(Key.RightShift)]
    [InlineData(Key.LeftCtrl)]
    [InlineData(Key.LeftAlt)]
    [InlineData(Key.LWin)]
    [InlineData(Key.CapsLock)]
    [InlineData(Key.F1)]
    [InlineData(Key.F12)]
    [InlineData(Key.None)]
    public void IsPrintableCandidate_NavigationEditingModifierAndFunctionKeys_ReturnsFalse(Key key)
    {
        Assert.False(TextEditKeyForwarder.IsPrintableCandidate(key));
    }

    [Fact]
    public void BuildSyntheticKeyState_NoModifiers_AllZero()
    {
        var state = TextEditKeyForwarder.BuildSyntheticKeyState(shiftDown: false, capsLockOn: false);
        Assert.Equal(256, state.Length);
        Assert.Equal(0, state[VkShift]);
        Assert.Equal(0, state[VkCapital]);
    }

    [Fact]
    public void BuildSyntheticKeyState_ShiftDown_SetsHighBitOnShiftEntry()
    {
        var state = TextEditKeyForwarder.BuildSyntheticKeyState(shiftDown: true, capsLockOn: false);
        Assert.Equal(0x80, state[VkShift]);
        Assert.Equal(0, state[VkCapital]);
    }

    [Fact]
    public void BuildSyntheticKeyState_CapsLockOn_SetsLowBitOnCapitalEntry()
    {
        var state = TextEditKeyForwarder.BuildSyntheticKeyState(shiftDown: false, capsLockOn: true);
        Assert.Equal(0, state[VkShift]);
        Assert.Equal(0x01, state[VkCapital]);
    }

    [Fact]
    public void BuildSyntheticKeyState_BothModifiers_SetsBothEntriesIndependently()
    {
        var state = TextEditKeyForwarder.BuildSyntheticKeyState(shiftDown: true, capsLockOn: true);
        Assert.Equal(0x80, state[VkShift]);
        Assert.Equal(0x01, state[VkCapital]);

        // Nothing else in the 256-entry table should be touched by either flag.
        for (int i = 0; i < state.Length; i++)
        {
            if (i == VkShift || i == VkCapital)
            {
                continue;
            }
            Assert.Equal(0, state[i]);
        }
    }
}
