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

    /// <summary>The ordered color-format list shown by BOTH readout surfaces (the standalone
    /// eyedropper's rows and the magnifier loupe's value lines, item 22), managed from the
    /// eyedropper's gear popover: toggle, reorder, and add/edit/delete custom entries. Each entry's
    /// Format is a <see cref="Color.ColorFormatTemplate"/> string ("%Re"/"%Hu"/... parameters).
    /// Empty (the default) means "not migrated yet": consumers seed from
    /// <see cref="Color.ColorFormatCatalog.BuiltIns"/> with the legacy ColorFormatShow* bools
    /// applied (see <see cref="Color.ColorFormatCatalog.EffectiveFormats"/>), and only persist the
    /// list once the user first edits it (same contract as <see cref="PaletteColors"/>).</summary>
    public List<Color.ColorFormatEntry> ColorFormats { get; init; } = new();

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

    // ---------- Sharing/upload subsystem (Sharing/*) ----------

    /// <summary>Configured share-upload provider instances: built-in providers the user has touched
    /// (credentials/enabled state), plus every "Custom..." one they've added. Never carries a
    /// not-yet-configured built-in's row - see Sharing/ShareProviderCatalog.EffectiveConfigs, which
    /// layers a fresh disabled placeholder for every untouched built-in on top of this list at read
    /// time so this stays empty on a fresh install rather than pre-populating seven rows nobody
    /// asked for. Stored PLAINTEXT like every other RoeSnipSettings field, INCLUDING each config's
    /// API keys/tokens (Sharing/ShareProviderConfig.Values) - the settings UI says so explicitly next
    /// to any secret field. Mirrors the WPF app's own independent ShareProviders field byte-for-byte
    /// in shape (both apps' settings.json stay separate files by design - see SettingsStore's own
    /// doc comment).</summary>
    public List<Sharing.ShareProviderConfig> ShareProviders { get; init; } = new();

    /// <summary>Which configured provider (by Sharing/ShareProviderConfig.Id) a plain Share click
    /// uploads to. Null, or a stale/disabled id, falls back to the first enabled configured provider
    /// - see Sharing/ShareManager.ResolveDefault.</summary>
    public string? DefaultShareProviderId { get; init; } = null;

    // ---------- Recording (item 20) — ported verbatim from the WPF app's own RoeSnipSettings ----------

    /// <summary>MP4 recording audio sources, toggled from the recording chrome's Setup panel and
    /// persisted immediately. Both default off: a screen recorder must never capture the user's
    /// microphone or system audio without an explicit opt-in. GIF recordings ignore both.</summary>
    public bool RecordMicrophone { get; init; } = false;
    public bool RecordSystemAudio { get; init; } = false;

    /// <summary>GIF recording size/quality tier, chosen from the recording chrome's Setup-only GIF
    /// row and persisted immediately (same SettingsStore.Save best-effort pattern as the audio
    /// toggles above). A plain string, not the <see cref="Recording.Gif.GifSizePreset"/> enum
    /// itself - this record's JSON persistence style keeps every field JSON-primitive so the file
    /// stays human-editable and forward/backward compatible across builds that add or reorder enum
    /// members. Parsed via <see cref="Recording.Gif.GifSizePresets.Parse"/>, which fails safe to
    /// "Quality" (this default) for any unknown or corrupt value rather than throwing.</summary>
    public string GifSizePreset { get; init; } = "Quality";

    /// <summary>MP4 recording size/quality tier - the same recording-chrome size row as
    /// <see cref="GifSizePreset"/> above, but format's own separate memory: switching between GIF
    /// and MP4 takes must not clobber the other format's last-picked tier. Same plain-string,
    /// same <see cref="Recording.Gif.GifSizePresets.Parse"/> fail-safe-to-"Quality" convention,
    /// and (despite the "Gif"-namespaced parser/enum) the SAME <see cref="Recording.Gif.GifSizePreset"/>
    /// enum, not a parallel MP4-only one.</summary>
    public string Mp4SizePreset { get; init; } = "Quality";

    /// <summary>GIF recording framerate: the chrome's FPS slider persists here, independent of
    /// <see cref="GifSizePreset"/> - the two are orthogonal user choices (quality vs. cadence, see
    /// RoeSnip.Core's Recording/RecordingSizeEstimator.cs own doc comment for why). A plain int, not
    /// validated at load time: an old build, a hand-edited settings file, or a future build's
    /// differently-shaped range could all leave a value here that falls outside today's
    /// <see cref="Recording.RecordingSizeEstimator.GifMinFps"/>..<see cref="Recording.RecordingSizeEstimator.GifMaxFps"/>
    /// range - rather than throw or silently keep an invalid capture rate, every USE site clamps
    /// this through <see cref="Recording.RecordingSizeEstimator.ClampFps"/> against those live
    /// bounds (same fail-safe-at-use-time convention <see cref="GifSizePreset"/>'s own Parse already
    /// uses). Default matches <see cref="Recording.RecordingSizeEstimator.GifDefaultFps"/>.</summary>
    public int GifFps { get; init; } = 25;

    /// <summary>MP4 recording framerate - same fail-safe-at-use-time contract as <see cref="GifFps"/>
    /// above, clamped against <see cref="Recording.RecordingSizeEstimator.Mp4MinFps"/>..
    /// <see cref="Recording.RecordingSizeEstimator.Mp4MaxFps"/> instead, and its own independent
    /// settings key (format-specific memory, same split as <see cref="GifSizePreset"/>/
    /// <see cref="Mp4SizePreset"/>). Default matches
    /// <see cref="Recording.RecordingSizeEstimator.Mp4DefaultFps"/>.</summary>
    public int Mp4Fps { get; init; } = 30;

    public static RoeSnipSettings Default { get; } = new();

    private static string DefaultSaveDirectory() =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "RoeSnip");
}
