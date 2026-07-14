using System;

namespace RoeSnip.Core.Updates;

/// <summary>How often a long-lived tray resident re-checks GitHub Releases for a newer build,
/// persisted as a plain string on <c>RoeSnipSettings.UpdateCheckFrequency</c> (both apps' own
/// records) — same string-not-enum persistence convention as
/// <see cref="RoeSnip.Core.Recording.Gif.GifSizePreset"/> (see that enum's own doc comment): a
/// hand-editable, forward/backward compatible JSON value rather than a numeric enum whose meaning
/// shifts if members are ever reordered.
///
/// <see cref="StartupOnly"/> is deliberately the floor, not a full "off": it is exactly the
/// behavior the app shipped with before this feature (a single check when the process starts) and
/// stays available for anyone who wants that. There is no "never check" option — the installed
/// copy has checked at startup unconditionally since v1.6.0, and running the portable build is
/// already a full opt-out; adding an Off row here would just reopen the "updates never fired"
/// support case this feature exists to close.
///
/// <see cref="EveryMinute"/> is excluded from <see cref="UpdateCheckFrequencies.UiChoices"/> on
/// purpose — it exists solely so a developer (or the live verification pass for this feature) can
/// hand-edit settings.json to exercise a real periodic tick without waiting an hour. It still
/// parses and runs correctly if set; only the Settings window won't offer it.</summary>
public enum UpdateCheckFrequency
{
    StartupOnly,
    EveryMinute,
    Every30Minutes,
    Hourly,
    Every6Hours,
    Daily,
}

/// <summary>Parsing, interval mapping, and display labels for <see cref="UpdateCheckFrequency"/>.
/// Kept as a separate static class (mirrors <c>GifSizePresets</c>) alongside the enum itself so the
/// whole persisted-value scheme lives in one small, framework-free file every consumer (both apps'
/// TrayApps and Settings windows) can share unmodified.</summary>
public static class UpdateCheckFrequencies
{
    /// <summary>Parses the persisted settings string case-insensitively; any unrecognized, missing,
    /// or garbled value fails SAFE to <see cref="UpdateCheckFrequency.Hourly"/> rather than
    /// throwing — same fail-closed-to-default convention the rest of the settings record uses (see
    /// SettingsStore.Load's own doc comment). A missing field in an old settings.json (any build
    /// that predates this feature) resolves here too, and upgrading a long-lived tray resident from
    /// "never periodically checks" to "checks hourly" by default is the intended fix for the
    /// "updates never fired" report this feature responds to, not an accidental side effect.
    ///
    /// The all-digits rejection and the <see cref="Enum.IsDefined(System.Type,object)"/> check both
    /// matter: this is a plain, non-Flags enum, so <c>Enum.TryParse</c> happily accepts any integer
    /// string ("7", "-1") and returns an UNDEFINED value of the underlying int rather than failing —
    /// a hand-editor typing a number (plausibly meaning "every 7 hours", which this enum doesn't even
    /// offer) would otherwise sail past this guard and blow up <see cref="Interval"/>'s switch inside
    /// the fire-and-forget update loop, silently killing periodic checking for the rest of the
    /// process lifetime. Rejecting ALL numeric strings up front (not just undefined ones) also stops
    /// the surprising case where a small integer happens to line up with a real member's ordinal
    /// ("2" silently meaning Every30Minutes to someone who typed it thinking "every 2 hours") - this
    /// field is documented as taking enum member names, never numbers.</summary>
    public static UpdateCheckFrequency Parse(string? value) =>
        !string.IsNullOrEmpty(value)
        && !IsAllDigits(value)
        && Enum.TryParse<UpdateCheckFrequency>(value, ignoreCase: true, out var frequency)
        && Enum.IsDefined(frequency)
            ? frequency
            : UpdateCheckFrequency.Hourly;

    private static bool IsAllDigits(string value)
    {
        int start = value[0] == '-' ? 1 : 0;
        if (start >= value.Length)
        {
            return false;
        }
        for (int i = start; i < value.Length; i++)
        {
            if (!char.IsAsciiDigit(value[i]))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>How long a tray resident should wait between periodic checks, or null for
    /// <see cref="UpdateCheckFrequency.StartupOnly"/> (no periodic loop tick should ever fire — the
    /// caller's loop just keeps polling the setting for a change). A startup-only check almost
    /// never actually delivers an update in practice: the app runs as a tray resident for weeks
    /// between launches, so the periodic interval this returns is the mechanism that really keeps
    /// an install current, and the one-time startup check is just its first iteration.</summary>
    public static TimeSpan? Interval(UpdateCheckFrequency frequency) => frequency switch
    {
        UpdateCheckFrequency.StartupOnly => null,
        UpdateCheckFrequency.EveryMinute => TimeSpan.FromMinutes(1),
        UpdateCheckFrequency.Every30Minutes => TimeSpan.FromMinutes(30),
        UpdateCheckFrequency.Hourly => TimeSpan.FromHours(1),
        UpdateCheckFrequency.Every6Hours => TimeSpan.FromHours(6),
        UpdateCheckFrequency.Daily => TimeSpan.FromHours(24),
        _ => throw new ArgumentOutOfRangeException(nameof(frequency), frequency, "Unknown UpdateCheckFrequency."),
    };

    /// <summary>User-facing label for the Settings window's frequency combo. Not the enum member's
    /// own name (mirrors <c>GifSizePresets.DisplayLabel</c>'s same rationale) so the persisted enum
    /// member names can stay settings-compatible even if the wording shown to the user changes
    /// later.</summary>
    public static string DisplayLabel(UpdateCheckFrequency frequency) => frequency switch
    {
        UpdateCheckFrequency.StartupOnly => "At startup only",
        UpdateCheckFrequency.EveryMinute => "Every minute",
        UpdateCheckFrequency.Every30Minutes => "Every 30 minutes",
        UpdateCheckFrequency.Hourly => "Every hour",
        UpdateCheckFrequency.Every6Hours => "Every 6 hours",
        UpdateCheckFrequency.Daily => "Once a day",
        _ => throw new ArgumentOutOfRangeException(nameof(frequency), frequency, "Unknown UpdateCheckFrequency."),
    };

    /// <summary>Ordered choices for the Settings window's frequency combo. Deliberately excludes
    /// <see cref="UpdateCheckFrequency.EveryMinute"/> — see that member's own doc comment. Opening
    /// Settings while EveryMinute happens to be the stored value shows Hourly selected instead (via
    /// <see cref="Parse"/>), and saving from there rewrites the setting to Hourly; that is
    /// acceptable since EveryMinute is a dev-only value nobody reaches through the UI.</summary>
    public static readonly UpdateCheckFrequency[] UiChoices =
    {
        UpdateCheckFrequency.StartupOnly,
        UpdateCheckFrequency.Every30Minutes,
        UpdateCheckFrequency.Hourly,
        UpdateCheckFrequency.Every6Hours,
        UpdateCheckFrequency.Daily,
    };

    /// <summary>Random spread applied on top of <paramref name="interval"/> (capped at 5 minutes,
    /// scaled by <paramref name="random01"/> which callers pass as <c>Random.Shared.NextDouble()</c>
    /// so this stays a pure, directly unit-testable function) so that a fleet of machines which all
    /// wake at the same moment (everyone logging in at 9:00) doesn't stampede GitHub's API in the
    /// same second. Scales with the interval itself (interval/4) rather than always maxing out, so
    /// EveryMinute's dev-only 1-minute cadence isn't dominated by a flat 5-minute jitter.</summary>
    public static TimeSpan Jitter(TimeSpan interval, double random01)
    {
        TimeSpan cap = TimeSpan.FromMinutes(5);
        TimeSpan quarter = interval / 4;
        TimeSpan max = quarter < cap ? quarter : cap;
        return max * random01;
    }
}
