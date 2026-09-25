namespace RoeSnip.Core.Settings;

/// <summary>The overlay's rebindable shortcuts: Copy (default Ctrl+C) and Upload (default Ctrl+U,
/// the toolbar's Share button - an upload to the default provider). A binding keeps the global
/// hotkey's persisted shape on every OS: MOD_* flags plus a Windows virtual-key code. On macOS the
/// overlays read Cmd as Control, so a stored Ctrl binding answers to Cmd there.
///
/// Every other overlay key is fixed, and a rebindable shortcut may not take one of them: the
/// overlays check the rebindable pair first, so a binding that landed on Ctrl+S would silently
/// shadow Save rather than share the key. <see cref="FixedActionFor"/> is the one list both
/// settings windows reject against.</summary>
public static class OverlayShortcuts
{
    public const uint ModAlt = 0x1;
    public const uint ModControl = 0x2;
    public const uint ModShift = 0x4;
    public const uint ModWin = 0x8;

    public const uint DefaultCopyModifiers = ModControl;
    public const uint DefaultCopyVirtualKey = 0x43; // C
    public const uint DefaultUploadModifiers = ModControl;
    public const uint DefaultUploadVirtualKey = 0x55; // U

    private const uint VkBack = 0x08;
    private const uint VkEnter = 0x0D;
    private const uint VkEscape = 0x1B;
    private const uint VkLeft = 0x25;
    private const uint VkDown = 0x28;
    private const uint VkDelete = 0x2E;

    /// <summary>Exact match: the pressed modifiers must equal the bound ones, so Ctrl+Shift+C does
    /// not fire a Ctrl+C binding. A virtual key of 0 (a hand-edited settings file) never matches.</summary>
    public static bool Matches(uint boundModifiers, uint boundVirtualKey, uint pressedModifiers, uint pressedVirtualKey) =>
        boundVirtualKey != 0 && boundVirtualKey == pressedVirtualKey && boundModifiers == pressedModifiers;

    /// <summary>The fixed overlay action already on this key, or null when it is free. Esc, Enter,
    /// Delete, Backspace and the arrows are taken with any modifiers (the overlays act on them
    /// whatever else is held); the Ctrl letters only as the exact combination.</summary>
    public static string? FixedActionFor(uint modifiers, uint virtualKey)
    {
        switch (virtualKey)
        {
            case VkEscape: return "Cancel";
            case VkEnter: return "Confirm";
            case VkDelete:
            case VkBack: return "Delete annotation";
            case >= VkLeft and <= VkDown: return "Move selection";
        }

        if (modifiers == ModControl)
        {
            switch (virtualKey)
            {
                case 0x53: return "Save";       // S
                case 0x5A: return "Undo";       // Z
                case 0x59: return "Redo";       // Y
                case 0x41: return "Select all"; // A
            }
        }
        if (modifiers == (ModControl | ModShift) && virtualKey == 0x5A)
        {
            return "Redo";
        }
        return null;
    }

    /// <summary>Why a captured key can't become one of the two shortcuts (a short phrase the
    /// settings window shows in the key box), or null when it can. The other
    /// rebindable shortcut and the global capture hotkey are passed in as they stand in the window
    /// right now (possibly unsaved), so two unsaved edits can't collide either.</summary>
    public static string? Rejection(
        uint modifiers, uint virtualKey,
        string otherName, uint otherModifiers, uint otherVirtualKey,
        uint hotkeyModifiers, uint hotkeyVirtualKey)
    {
        if ((modifiers & ModWin) != 0)
        {
            return "Win-key combinations belong to the OS";
        }
        if (FixedActionFor(modifiers, virtualKey) is { } action)
        {
            return $"Already {action}";
        }
        if (modifiers == otherModifiers && virtualKey == otherVirtualKey)
        {
            return $"Already {otherName}";
        }
        if (modifiers == hotkeyModifiers && virtualKey == hotkeyVirtualKey)
        {
            return "Already the capture hotkey";
        }
        return null;
    }
}
