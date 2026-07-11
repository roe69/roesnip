namespace RoeSnip.Core.Settings;

/// <summary>Same shape as the WPF app's RoeSnipSettings (PLAN.md §2.4 / Phase-A additions) — moved
/// into Core per DESIGN-XPLAT.md's own solution layout ("Settings/ RoeSnipSettings + SettingsStore").
/// HotkeyModifiers/HotkeyVirtualKey remain Windows VK/MOD_* values in shape — see PLAN-XPLAT.md §5
/// hotkey facts for how WP-X2's hotkey code interprets (or ignores) them per OS.</summary>
public sealed record RoeSnipSettings
{
    public int SchemaVersion { get; init; } = 1;
    public uint HotkeyModifiers { get; init; } = 0;
    public uint HotkeyVirtualKey { get; init; } = 0x2C; // VK_SNAPSHOT; meaningful on Windows only
    public string SaveDirectory { get; init; } = DefaultSaveDirectory();
    public bool AutoSaveHdrCopy { get; init; } = false;
    public double? ToneMapKneeOverride { get; init; } = null;
    public double? ToneMapPeakOverride { get; init; } = null;
    public bool RunAtStartup { get; init; } = false;
    public bool CopyOnSelect { get; init; } = false;
    public bool PrintScreenPromptAnswered { get; init; } = false; // Windows-only meaning; harmless elsewhere
    public bool WaylandHotkeyNoticeShown { get; init; } = false; // Wayland-only meaning; harmless elsewhere

    public static RoeSnipSettings Default { get; } = new();

    private static string DefaultSaveDirectory() =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "RoeSnip");
}
