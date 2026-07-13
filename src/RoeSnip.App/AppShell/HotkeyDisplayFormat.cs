using System;
using System.Collections.Generic;
using SharpHook.Data;

namespace RoeSnip.App.AppShell;

/// <summary>Pure formatting for the SettingsWindow hotkey display text - a public class rather
/// than an internal member of SettingsWindow, per the "make the testable slice public instead of
/// an InternalsVisibleTo edit" convention this codebase already uses (see e.g. Overlay/SizeInput.cs).
///
/// Item 14 / WPF Bug 3 (SettingsWindow.xaml.cs:722-767): the old fallback printed raw hex
/// ("VK 0x53") for every key except PrintScreen. The WPF fix P/Invoked GetKeyNameText via
/// MapVirtualKey; this port has no Win32 window handle to hang that off, so instead it inverts
/// <see cref="HotkeyManager.VirtualKeyToKeyCode"/>'s own symbolic table - SharpHook's KeyCode
/// names (VcA, VcF5, VcNumPad3, VcPrintScreen, ...) map 1:1 onto real key names by stripping the
/// "Vc" prefix. That table already covers exactly the virtual-key set SettingsWindow's capture
/// (via SettingsWindow.MapKeyToVirtualKey) can ever produce, so no second table is needed. Raw
/// hex remains the last-resort fallback for a virtual key with no mapping at all (persisted
/// settings from a future version, or hand-edited settings.json - should not happen from a real
/// capture).</summary>
public static class HotkeyDisplayFormat
{
    public static string DescribeHotkey(uint modifiers, uint virtualKey)
    {
        var parts = new List<string>();
        if ((modifiers & HotkeyManager.ModControl) != 0) parts.Add("Ctrl");
        if ((modifiers & HotkeyManager.ModAlt) != 0) parts.Add("Alt");
        if ((modifiers & HotkeyManager.ModShift) != 0) parts.Add("Shift");
        if ((modifiers & HotkeyManager.ModWin) != 0) parts.Add("Win");
        parts.Add(DescribeVirtualKey(virtualKey));
        return string.Join("+", parts);
    }

    public static string DescribeVirtualKey(uint virtualKey)
    {
        if (HotkeyManager.VirtualKeyToKeyCode(virtualKey) is KeyCode code)
        {
            string name = code.ToString();
            return name.StartsWith("Vc", StringComparison.Ordinal) ? name[2..] : name;
        }

        return $"VK 0x{virtualKey:X2}";
    }
}
