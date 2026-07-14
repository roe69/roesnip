using System;
using System.IO;
using System.Text.Json;
using RoeSnip.Core.Diagnostics;

namespace RoeSnip.Core.Updates;

/// <summary>Hardening item 8: a durable "what happened last time this app checked for an update"
/// breadcrumb, "last-update-status.json", persisted in each app's CONFIG directory (same directory
/// as UpdateHealthMarker's own update-health.json - see that class's HealthMarkerDirectory in each
/// UpdateManager). The unattended auto-update path's only failure signal before this existed was a
/// tray balloon the codebase itself documents as unreliable (balloons can be silently eaten by
/// Focus Assist, notification settings, or the Shell) - a missed balloon left no way to later find
/// out whether the last check succeeded, found nothing, or failed. This class fixes that with a
/// single flat record, overwritten (never appended) on every check, so the disk/privacy cost never
/// grows: no history, just "what happened last time".
///
/// Same fail-closed contract as every other JSON sidecar in this codebase (SettingsStore,
/// UpdateHealthMarker): missing/empty/corrupt file resolves to a default State (CheckedUtc/Version/
/// Outcome all null, meaning "never checked, or the marker could not be read") without writing to
/// or otherwise touching a bad file on disk, and <see cref="Record"/> never throws into its caller -
/// a failure to write the breadcrumb must never be treated as the update check itself failing.
///
/// Deliberately framework-free (plain file IO only) so it is shared, unmodified, by both apps'
/// UpdateManagers - see each one's RecordLastCheckOutcome/LastCheckSummary wrapper for the
/// per-app HealthMarkerDirectory plumbing, and TrayApp's ShowAbout for where the resulting one-line
/// summary is surfaced to the user.</summary>
public static class UpdateStatusMarker
{
    private const string FileName = "last-update-status.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>The marker's on-disk shape. Property-based record (not positional), matching
    /// UpdateHealthMarker.State's own convention, so System.Text.Json's default deserialization
    /// already gives "missing field = default" for free.</summary>
    public sealed record State
    {
        /// <summary>When the check this record describes ran, in UTC; null means "never checked"
        /// (or the marker file was missing/corrupt).</summary>
        public DateTime? CheckedUtc { get; init; }

        /// <summary>The release version involved, when there was one to name - null for a plain
        /// "up to date" outcome (nothing was found to check a version against).</summary>
        public string? Version { get; init; }

        /// <summary>Free-text outcome: "UpToDate", "Applied 1.9.0", or "Failed: &lt;message&gt;" -
        /// see <see cref="DescribeLastCheck"/> for how each shape renders for a user-facing
        /// About-box line.</summary>
        public string? Outcome { get; init; }
    }

    private static string FilePath(string directory) => Path.Combine(directory, FileName);

    /// <summary>Reads the marker. Missing file, empty file, or anything that fails to parse all
    /// return a default (nothing recorded) state without writing or overwriting anything on disk -
    /// a corrupt file is left exactly as found, same fail-closed contract as
    /// UpdateHealthMarker.Load.</summary>
    public static State Load(string directory)
    {
        try
        {
            string path = FilePath(directory);
            if (!File.Exists(path))
            {
                return new State();
            }

            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new State();
            }

            return JsonSerializer.Deserialize<State>(json, JsonOptions) ?? new State();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            FileLog.Write($"RoeSnip: last-update-status marker unreadable/corrupt, ignoring: {ex.Message}");
            return new State();
        }
    }

    /// <summary>Records the outcome of a just-completed update check (or check+apply). Best-effort
    /// plain overwrite - no atomic temp-file swap (unlike SettingsStore.Save): this is disposable
    /// diagnostic bookkeeping, not user data, so a torn write on a crash mid-save is read back as
    /// corrupt JSON on the next read and Load's catch above resets to "nothing recorded", which is
    /// never worse than the marker not existing at all. Never throws - a failure to persist the
    /// breadcrumb must not be mistaken by any caller for the update check itself having
    /// failed.</summary>
    public static void Record(string directory, string? version, string outcome)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var state = new State { CheckedUtc = DateTime.UtcNow, Version = version, Outcome = outcome };
            File.WriteAllText(FilePath(directory), JsonSerializer.Serialize(state, JsonOptions));
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: could not write the last-update-status marker (non-fatal): {ex.Message}");
        }
    }

    /// <summary>Renders the marker as a single user-facing line for an About box, or null when
    /// nothing has ever been recorded (a fresh install, or a marker that failed to parse) - callers
    /// should omit the line entirely rather than show a misleading "never checked" that could just
    /// as easily mean "the marker was unreadable". "Failed: " and "Applied " prefixes get friendlier
    /// lower-case phrasing; any other outcome (including the plain "UpToDate" sentinel) passes
    /// through a small explicit mapping - unrecognized text (e.g. a marker written by some future
    /// build with a new outcome shape) still renders verbatim rather than being dropped.</summary>
    public static string? DescribeLastCheck(string directory)
    {
        State state = Load(directory);
        if (state.CheckedUtc is null || string.IsNullOrEmpty(state.Outcome))
        {
            return null;
        }

        string when = state.CheckedUtc.Value.ToLocalTime().ToString("g");
        string detail;
        if (state.Outcome == "UpToDate")
        {
            detail = "up to date";
        }
        else if (state.Outcome.StartsWith("Failed: ", StringComparison.Ordinal))
        {
            detail = "failed: " + state.Outcome["Failed: ".Length..];
        }
        else if (state.Outcome.StartsWith("Applied ", StringComparison.Ordinal))
        {
            detail = "updated to " + state.Outcome["Applied ".Length..];
        }
        else
        {
            detail = state.Outcome;
        }

        return $"Last update check: {when} - {detail}";
    }
}
