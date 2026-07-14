using System;
using System.IO;
using System.Text.Json;
using RoeSnip.Core.Diagnostics;

namespace RoeSnip.Core.Updates;

/// <summary>Crash-loop protection (hardening item 7): a small JSON sidecar,
/// "update-health.json", persisted in each app's CONFIG directory (never the install dir - see
/// each UpdateManager's own HealthMarkerDirectory, e.g. the WPF app's %APPDATA%\RoeSnip settings
/// directory) so it survives the install-dir file swap ApplyUpdateAsync performs. This class is the
/// framework-free bookkeeping only - plain file IO, no locking beyond best-effort (the RMW calls
/// below are same-process-sequential: one tray resident, one startup, never concurrent writers) and
/// no retry/backoff machinery. The per-app orchestration (when to call which method, and what to do
/// on disk when a rollback fires) lives in each UpdateManager - see its CheckUpdateHealthAtStartup/
/// CompleteHealthMilestone/TryRestorePreviousBuild.
///
/// The scenario this closes: a release that crashes at (or shortly after) startup is auto-applied
/// fleet-wide by the periodic update loop, and the crashing build itself unconditionally deletes
/// its own rollback target (the ".old" exe a swap renames the previous good build to) before it has
/// any chance to prove it actually works - so replace-on-run leaves NO process running, forever,
/// with no way back short of a manual reinstall. The fix has three moving parts, all persisted here:
///
/// 1. PENDING VERIFY (<see cref="PendingVersion"/>/<see cref="AttemptCount"/>): the version an
///    update swap JUST launched into, and how many times a launch of that exact version has been
///    observed since. <see cref="RecordPendingUpdate"/> writes this right before the swapped-in exe
///    is started; <see cref="RecordLaunchAttempt"/> increments the counter at the START of every
///    launch (not via a crash handler - a native crash or hard kill skips managed handlers
///    entirely, so the counter must count LAUNCHES, and a launch that never crashes simply
///    increments once and then gets cleared by <see cref="ClearPending"/> once the caller decides
///    the build is healthy).
/// 2. HEALTH MILESTONE (<see cref="ClearPending"/>): once a launch has stayed up long enough to be
///    trusted (each TrayApp's own "tray icon shown plus ~15s of uptime" gate), the caller clears
///    the marker - this build is proven, the NEXT update starts a fresh pending-verify cycle, and
///    this is also the earliest point at which it's safe to delete the ".old" rollback target
///    (still done by the caller, via its own CleanupStaleExeWithRetry, not by this class - keeping
///    the "when is it safe to delete .old" file-system decision the RollbackAttemptThreshold-th
///    consecutive failed launch never sees).
/// 3. QUARANTINE (<see cref="Quarantine"/>/<see cref="IsQuarantined"/>): recorded by the caller's
///    own auto-restore path when <see cref="AttemptCount"/> reaches <see cref="RollbackAttemptThreshold"/>
///    without ever reaching the health milestone. Essential, easy to miss: without it, the restored
///    previous build would immediately see the same bad release as "a newer version is available"
///    (it IS newer - that's the whole reason it looked like an update) and re-download/re-apply it,
///    looping forever. The ordinary downgrade guard (never offer a release <= CurrentVersion) does
///    not help here for exactly that reason. Single slot, cleared automatically the moment a
///    STRICTLY NEWER release appears - a fixed re-release supersedes the bad one it replaces, and
///    re-quarantining is exactly what the attempt counter is for if the new one is ALSO broken.
///
/// Missing/corrupt/unreadable marker file, or any field absent from it, all resolve to the default,
/// healthy <see cref="State"/> (PendingVersion null, AttemptCount 0, QuarantinedVersion null) -
/// matching every other JSON file in this codebase (SettingsStore, CaptureCache): fail closed on
/// parsing, never throw into a caller that's usually mid-startup.</summary>
public static class UpdateHealthMarker
{
    private const string FileName = "update-health.json";

    /// <summary>Consecutive launches of the SAME pending-verify version, none of which ever reached
    /// the health milestone, before the caller auto-restores the previous build. 3 means a bad
    /// release gets the first post-update launch plus two more chances (covers a one-off transient
    /// startup fault) before the fleet gives up on it - short enough that a genuinely broken release
    /// doesn't cost thousands of machines more than a handful of crash-loop cycles.</summary>
    public const int RollbackAttemptThreshold = 3;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>The marker's on-disk shape. Property-based (not positional) record, matching
    /// RoeSnipSettings' own convention, so System.Text.Json's default deserialization already gives
    /// "missing field = default" for free (a field absent from an older/hand-edited file simply
    /// never assigns over the init default) without any custom constructor-parameter handling.</summary>
    public sealed record State
    {
        /// <summary>The version an update swap most recently launched into and is still waiting to
        /// see proven healthy; null means "nothing pending" (either never updated, or the last
        /// update already cleared this via <see cref="ClearPending"/>).</summary>
        public string? PendingVersion { get; init; }

        /// <summary>How many launches of <see cref="PendingVersion"/> have been observed via
        /// <see cref="RecordLaunchAttempt"/> since it was set, none of which reached the health
        /// milestone yet. Meaningless (and always reset to 0) whenever <see cref="PendingVersion"/>
        /// is null.</summary>
        public int AttemptCount { get; init; }

        /// <summary>The version a prior auto-restore rolled back away from, if any; null means
        /// nothing is quarantined. See the class doc comment's point 3.</summary>
        public string? QuarantinedVersion { get; init; }
    }

    private static string FilePath(string directory) => Path.Combine(directory, FileName);

    /// <summary>Reads the marker. Missing file, empty file, or anything that fails to parse all
    /// return a default (healthy, nothing pending or quarantined) state without writing or
    /// overwriting anything on disk - a corrupt file is left exactly as found, same fail-closed
    /// contract as SettingsStore.Load. Public so callers that only need to inspect (never mutate)
    /// state - e.g. a diagnostics/about surface - don't need a round trip through a mutating
    /// method.</summary>
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
            FileLog.Write($"RoeSnip: update-health marker unreadable/corrupt, treating as healthy: {ex.Message}");
            return new State();
        }
    }

    /// <summary>Plain overwrite - no atomic temp-file swap (unlike SettingsStore.Save). Deliberate:
    /// every caller in this file is same-process-sequential (see the class doc comment), so there is
    /// no concurrent-writer race to protect against, and a marker file is disposable bookkeeping, not
    /// user data - worst case a torn write on a crash mid-save is read back as corrupt JSON on the
    /// next launch and Load's catch above resets to a healthy default, which is the same outcome as
    /// "no marker at all" and never worse than what a missing marker already does.</summary>
    private static void Save(string directory, State state)
    {
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(FilePath(directory), JsonSerializer.Serialize(state, JsonOptions));
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: could not write the update-health marker (non-fatal): {ex.Message}");
        }
    }

    /// <summary>Called by ApplyUpdateCoreAsync right before it launches the freshly swapped-in exe:
    /// records <paramref name="targetVersion"/> as pending verification with a fresh attempt count.
    /// Deliberately does not touch <see cref="State.QuarantinedVersion"/> - applying an update and
    /// quarantining a version are unrelated axes of this same file, and an update being applied at
    /// all already implies CheckForUpdateAsync's own quarantine gate let it through.</summary>
    public static void RecordPendingUpdate(string directory, string targetVersion)
    {
        State state = Load(directory);
        Save(directory, state with { PendingVersion = targetVersion, AttemptCount = 0 });
    }

    /// <summary>Called once, as early as possible in a normal startup (before the tray icon, hotkey,
    /// or anything else that could itself crash). When the marker's PendingVersion names EXACTLY
    /// <paramref name="currentVersion"/>, this launch is a post-update verification run - the
    /// attempt counter is incremented and persisted immediately (read-modify-write), so a crash
    /// before the caller's own health milestone runs still counts even though the crash itself never
    /// gets a chance to run any cleanup code (a native crash or a hard kill skips managed handlers
    /// entirely - this is why the counter increments at launch time, not from a crash handler).
    /// Any other case (no marker, or a marker naming some other version - e.g. this is a plain
    /// restart of an already-verified build, or a hand-rolled downgrade) leaves the file untouched
    /// and simply returns whatever was loaded.</summary>
    public static State RecordLaunchAttempt(string directory, string currentVersion)
    {
        State state = Load(directory);
        if (state.PendingVersion is null || state.PendingVersion != currentVersion)
        {
            return state;
        }

        State updated = state with { AttemptCount = state.AttemptCount + 1 };
        Save(directory, updated);
        return updated;
    }

    /// <summary>The health milestone: called once a launch has been up long enough to be trusted.
    /// Clears PendingVersion/AttemptCount so this build is never rolled back again - but ONLY when
    /// the marker's PendingVersion still names EXACTLY <paramref name="expectedVersion"/> (this
    /// launch's own version). That guard matters for a fast chained update: this process's health
    /// timer can fire AFTER a newer update has already swapped in and recorded ITS OWN pending
    /// version, and clearing that unconditionally would wipe out the newer launch's crash-loop
    /// protection before it ever got a chance to prove itself - degrading straight back to the
    /// pre-item-7 "no rollback target" failure mode for exactly the machines that updated fastest.
    /// Returns true when it actually cleared something, false when there was nothing of THIS
    /// version's to clear (nothing pending, or a marker for some other version) - callers use that
    /// to decide whether the deferred ".old" cleanup that normally follows a clear is theirs to run
    /// (see each UpdateManager's CompleteHealthMilestone).</summary>
    public static bool ClearPending(string directory, string expectedVersion)
    {
        State state = Load(directory);
        if (state.PendingVersion != expectedVersion)
        {
            return false;
        }

        Save(directory, state with { PendingVersion = null, AttemptCount = 0 });
        return true;
    }

    /// <summary>Records <paramref name="version"/> as quarantined (the version an auto-restore just
    /// rolled back away from) and clears any pending-verify state in the same write - the rollback
    /// IS the resolution of whatever was pending. Single slot: a later call overwrites whatever was
    /// quarantined before, since a rollback can only ever be reacting to the most recently applied
    /// bad release.</summary>
    public static void Quarantine(string directory, string version)
    {
        State state = Load(directory);
        Save(directory, state with { QuarantinedVersion = version, PendingVersion = null, AttemptCount = 0 });
    }

    /// <summary>True when <paramref name="candidateVersion"/> - a release CheckForUpdateAsync is
    /// about to offer - is the exact version this machine already auto-rolled-back away from. Both
    /// apps' CheckForUpdateAsync must skip it: see the class doc comment's point 3 for why the
    /// ordinary "never offer a release &lt;= CurrentVersion" downgrade guard cannot do this job (the
    /// quarantined release is NEWER than the restored build now running, which is exactly why it
    /// looked like an update in the first place). A candidate STRICTLY NEWER than the quarantined
    /// version clears the quarantine as a side effect and returns false - a fixed re-release
    /// supersedes the bad one it replaces.</summary>
    public static bool IsQuarantined(string directory, Version candidateVersion)
    {
        State state = Load(directory);
        if (state.QuarantinedVersion is null || !Version.TryParse(state.QuarantinedVersion, out Version? quarantined))
        {
            return false;
        }

        if (candidateVersion > quarantined)
        {
            Save(directory, state with { QuarantinedVersion = null });
            return false;
        }

        return candidateVersion == quarantined;
    }
}
