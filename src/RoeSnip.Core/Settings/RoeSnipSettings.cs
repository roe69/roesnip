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
    public bool ColorPickerEnabled { get; init; } = false;        // false => no click-to-pick, no ColorPickerWindow, no loupe colour codes

    /// <summary>Last 8 picked colors from the standalone eyedropper ("Pick"), newest first, stored
    /// as "#RRGGBB". Deduplicated (a re-pick of an existing entry moves it to the front rather than
    /// adding a second copy) by whichever bounded-list helper WP-B's overlay port lands.</summary>
    public List<string> RecentPickedColors { get; init; } = new();

    /// <summary>LEGACY (superseded by the WPF app's ordered/editable ColorFormats list, not yet
    /// ported to Core): the original four per-format visibility toggles plus the short-lived
    /// HSV/CMYK pair. Kept — read once as a migration seed once ColorFormats itself lands, never
    /// written or displayed separately — so a downgrade to an older build still sees its choices,
    /// mirroring the CustomColors -> PaletteColors migration pattern below.</summary>
    public bool ColorFormatShowHex { get; init; } = true;
    public bool ColorFormatShowRgb { get; init; } = true;
    public bool ColorFormatShowHsl { get; init; } = true;
    public bool ColorFormatShowNits { get; init; } = true;
    public bool ColorFormatShowHsv { get; init; } = false;
    public bool ColorFormatShowCmyk { get; init; } = false;

    /// <summary>Magnifier loupe zoom: how many source pixels the FIXED-size loupe shows — a
    /// (2r+1)x(2r+1) block around the cursor, so a SMALLER radius means fewer, bigger pixels (more
    /// zoomed in) inside the same widget footprint. Adjusted live by scrolling the wheel while the
    /// loupe is up, persisted immediately so the last-used zoom is every later session's default.
    /// Clamped on use to the magnifier's own Min/MaxSampleRadius once that lands in Core/overlay.</summary>
    public int MagnifierSampleRadius { get; init; } = 5;

    /// <summary>Last-used text-annotation style (toolbar's text-style group), applied to new text
    /// annotations and carried across overlay sessions.</summary>
    public string TextFontFamily { get; init; } = "Segoe UI";
    public double TextFontSize { get; init; } = 20.0;
    public bool TextBold { get; init; } = false;
    public bool TextItalic { get; init; } = false;

    /// <summary>Custom colors chosen via the toolbar's "+" swatch, stored as "#RRGGBB", newest
    /// first, capped at 8 (LRU), rendered as extra swatches in the toolbar after the built-in
    /// presets. LEGACY as of UX round 5: superseded by <see cref="PaletteColors"/> (the whole
    /// palette is editable now); kept — read once as migration seed, never written or displayed
    /// separately — so a downgrade to an older build still sees its custom colors.</summary>
    public List<string> CustomColors { get; init; } = new();

    /// <summary>The toolbar's entire swatch palette (UX round 5, item 3): "#RRGGBB" strings in
    /// display order, fully user-editable (right-click Replace/Remove, "+" appends), capped at the
    /// swatch palette's own MaxColors once that lands in Core/overlay. Empty (the default) means
    /// "not migrated yet": consumers seed from the default palette plus any legacy
    /// <see cref="CustomColors"/> and only persist the list once the user first edits it.</summary>
    public List<string> PaletteColors { get; init; } = new();

    public static RoeSnipSettings Default { get; } = new();

    private static string DefaultSaveDirectory() =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "RoeSnip");
}
