namespace RoeSnip.Core.Color;

/// <summary>One row of <see cref="Settings.RoeSnipSettings.ColorFormats"/>. For a built-in entry
/// Name matches a <see cref="ColorFormatCatalog.BuiltIns"/> name and Format holds that built-in's
/// template (kept in the settings file so the row survives even if a future build renames/retires
/// the built-in); for a user-defined entry (<see cref="IsCustom"/>) both are free-form. Persisted
/// PascalCase like every other settings type; unknown fields are ignored on load (forward compat).
/// Adapted from the frozen WPF app's own Program.cs ColorFormatEntry record (item 22).</summary>
public sealed record ColorFormatEntry
{
    public string Name { get; init; } = string.Empty;
    public string Format { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;
    public bool IsCustom { get; init; } = false;

    /// <summary>Whether this format ALSO renders under the magnifier loupe's pixel preview (the
    /// loupe shows the subset of enabled formats with this on, so it can stay tighter than the
    /// picker window's rows). Defaults true so entries persisted before this field existed keep
    /// the old both-surfaces behavior.</summary>
    public bool InLoupe { get; init; } = true;
}
