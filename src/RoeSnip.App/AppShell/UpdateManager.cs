using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using RoeSnip.Core.Diagnostics;
using RoeSnip.Core.Settings;
using RoeSnip.Core.Updates;

namespace RoeSnip.App.AppShell;

/// <summary>Install-to-%LOCALAPPDATA%\RoeSnip.App + self-update from GitHub Releases, ported from
/// src/RoeSnip/App/UpdateManager.cs (WPF reference, esp. :41-48, :59-75, :206-309, :349-358,
/// :435-527). The actual install/swap/registry mechanics are WINDOWS ONLY (replace-on-run's
/// takeover, the atomic .old/.new exe swap, and the HKCU Run key all assume a real Windows install
/// directory and executable) — <see cref="Install"/>/<see cref="ApplyUpdateAsync"/>/
/// <see cref="CleanupStaleUpdateFiles"/>/<see cref="CleanupStaleExeWithRetry"/>/
/// <see cref="ProcessPendingSourceCleanup"/> are all attributed windows-only and TrayApp never
/// calls them off Windows (item 13d: Linux/macOS only get a passive "new version available" toast
/// linking the release page, never a self-swap — see docs/PARITY.md's accepted-limitations list).
/// <see cref="CheckForUpdateAsync"/> and the CurrentVersion*/<see cref="InstallExists"/>/
/// <see cref="IsInstalled"/> reads are portable (an Assembly version read plus a GET to the GitHub
/// API is OS-agnostic) so they compile and run identically everywhere — that's what the passive
/// Linux/macOS notice reuses.
///
/// DISTINCT identity from the WPF app's own UpdateManager (both apps can be installed side by
/// side; see docs/PARITY.md's Notes section): <see cref="InstallDir"/> is
/// %LOCALAPPDATA%\RoeSnip.App (not \RoeSnip), the Run key value is "RoeSnip.App" (matches
/// <see cref="StartupManager"/>'s own value name, not the WPF app's "RoeSnip"), and the release
/// asset this downloads is "RoeSnipApp-win-x64.exe" — NOT "RoeSnip.exe", which is the WPF app's
/// own asset name. Matching that name here would let this self-updater silently download and swap
/// itself for the WPF exe, bricking the install; release.yml publishes both assets under their own
/// distinct names specifically so this can never happen.
///
/// Every public entry point here is a convenience, never load-bearing: a private repo (404 until
/// the release goes public), no network, a truncated download, or a locked file must never throw
/// out into the tray or leave the install dir without a runnable exe. Not covered by an automated
/// test for the file-system/registry mutation paths, for the same reason the WPF reference isn't —
/// they mutate the real registry/filesystem and talk to a real HTTP endpoint, reviewed by eye
/// instead. The pure JSON-parsing/version-compare core (<see cref="ParseUpdateInfo"/>) is fully
/// unit-tested (RoeSnip.App.Tests/UpdateManagerTests.cs) since it takes no network or OS
/// dependency at all.</summary>
public static class UpdateManager
{
    private const string GitHubOwner = "roe69";
    private const string GitHubRepo = "roesnip";

    // Load-bearing exact match against release.yml's Windows RoeSnip.App asset name — see the
    // class doc comment's warning about never letting this collide with the WPF app's own
    // "RoeSnip.exe" asset.
    private const string ReleaseAssetName = "RoeSnipApp-win-x64.exe";

    // release.yml attaches this alongside the plain asset (hardening item 9: gzip -9 cuts ~61% off
    // the transit size fleet-wide, at zero cost to the installed-file tradeoff - the exe on disk
    // stays byte-identical and uncompressed). ParseUpdateInfo prefers this asset when present and
    // falls back to the plain one so a CI slip that only publishes one of the two never blocks
    // updates.
    private const string GzReleaseAssetName = ReleaseAssetName + ".gz";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    // Matches StartupManager.ValueName exactly — both write/read the same HKCU value, so this
    // updater's Install() and the Settings "run at startup" toggle can never fight over two
    // different values for the same app.
    private const string RunValueName = "RoeSnip.App";

    // One shared HttpClient for the process lifetime (never one-per-call — see MSDN's socket
    // exhaustion guidance) with the User-Agent GitHub's API mandates and rejects requests without.
    private static readonly HttpClient HttpClient = CreateHttpClient();

    // Serializes ApplyUpdateAsync across callers: the silent startup auto-update can be parked in
    // its beforeLaunch idle-wait for minutes with the new exe already swapped in, and without this
    // a concurrent manual "Check for updates" click would download into the same
    // DownloadingExePath (IOException) and, worse, launch (and kill this process via
    // replace-on-run) with no idle check of its own — defeating the whole point of the startup
    // path's guard. Queueing here means a manual click during that window simply waits; it
    // completes only after the parked call's own launch has already ended the process.
    private static readonly SemaphoreSlim ApplyUpdateLock = new(1, 1);

    // One shared instance for the process lifetime, matching HttpClient above - its in-memory ETag
    // is exactly what makes a periodic tray-resident check cheap (see the class's own doc comment):
    // a fresh instance every call would throw the ETag away and turn every periodic check back into
    // a full, uncached GET.
    private static readonly GitHubLatestReleaseClient ReleaseClient = new(GitHubOwner, GitHubRepo);

    public static string InstallDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RoeSnip.App");

    public static string InstalledExePath { get; } = Path.Combine(InstallDir, "RoeSnip.exe");

    private static string StaleExePath => InstalledExePath + ".old";
    private static string DownloadingExePath => Path.Combine(InstallDir, "RoeSnip.exe.new");

    // Distinct temp name for the compressed transit asset, so a download in flight never collides
    // with DownloadingExePath (the decompressed target ApplyUpdateCoreAsync's existing swap logic
    // already expects) and both are independently coverable by CleanupStaleUpdateFiles.
    private static string GzDownloadingExePath => DownloadingExePath + ".gz";

    private static string PendingSourceCleanupMarkerPath => Path.Combine(InstallDir, "pending-source-cleanup.txt");

    /// <summary>Where the crash-loop-guard marker (update-health.json) lives - the portable config
    /// directory (<see cref="ConfigPaths.ConfigDirectory"/>), never <see cref="InstallDir"/>: the
    /// install dir is exactly what an update swap replaces wholesale, so a marker written there
    /// could never survive across the swap it exists to guard.</summary>
    private static string HealthMarkerDirectory => ConfigPaths.ConfigDirectory;

    /// <summary>Hardening item 8: records the outcome of an update check (or check+apply) to the
    /// durable last-update-status.json breadcrumb, so a missed toast can still be answered later
    /// via <see cref="LastCheckSummary"/> in the About box. Best-effort, never throws - see
    /// UpdateStatusMarker.Record's own doc comment.</summary>
    public static void RecordLastCheckOutcome(string? version, string outcome) =>
        UpdateStatusMarker.Record(HealthMarkerDirectory, version, outcome);

    /// <summary>One user-facing line describing the last recorded update check, or null when
    /// nothing has ever been recorded - see UpdateStatusMarker.DescribeLastCheck.</summary>
    public static string? LastCheckSummary() => UpdateStatusMarker.DescribeLastCheck(HealthMarkerDirectory);

    /// <summary>The running build's version, straight off the assembly (set by the csproj's
    /// &lt;Version&gt;) — compared against each release's tag_name.</summary>
    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);

    /// <summary>"1.0.4"-shaped display text for <see cref="CurrentVersion"/> — the SDK always
    /// fills in a Revision component (always 0 for this project's x.y.z &lt;Version&gt; scheme),
    /// which reads as noise ("1.0.4.0") anywhere this is shown to the user (About window, tray
    /// tooltip, update-check result). Matches the release tag's own x.y.z shape exactly. Portable
    /// (no OS check) — item 13c surfaces this in About/tooltip on every OS.</summary>
    public static string CurrentVersionText => FormatVersionText(CurrentVersion);

    /// <summary>Shared by <see cref="CurrentVersionText"/> and the crash-loop guard (which needs
    /// the identical "x.y.z" text for a RELEASE's <see cref="Version"/>, parsed from a tag rather
    /// than an assembly, to compare equal to it in UpdateHealthMarker's string-keyed marker).</summary>
    private static string FormatVersionText(Version v) => $"{v.Major}.{v.Minor}.{v.Build}";

    /// <summary>True when an installed copy already exists on disk
    /// (%LOCALAPPDATA%\RoeSnip.App\RoeSnip.exe), regardless of whether THIS process is that copy.
    /// Gates the one-time "Install RoeSnip" tray item.</summary>
    public static bool InstallExists
    {
        get
        {
            try { return File.Exists(InstalledExePath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
    }

    /// <summary>True when this process is already running FROM the installed copy. Update-checks
    /// only make sense when it's true (a portable/dev copy has nothing sensible to swap itself
    /// for); the "Install RoeSnip" item is gated on <see cref="InstallExists"/> instead, not
    /// this.</summary>
    public static bool IsInstalled
    {
        get
        {
            string? current = Environment.ProcessPath;
            if (string.IsNullOrEmpty(current))
            {
                return false;
            }

            try
            {
                return string.Equals(
                    Path.GetFullPath(current),
                    Path.GetFullPath(InstalledExePath),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
            {
                return false;
            }
        }
    }

    private static HttpClient CreateHttpClient()
    {
        // AutomaticDecompression only kicks in when the server actually sends a Content-Encoding
        // header (the releases/latest JSON does - 8475 -> 1256 bytes measured live; the binary
        // asset download doesn't, so this has zero effect there), and it's a no-op on a 304's empty
        // body, so it's free to turn on for every request this client makes rather than special-cased.
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };
        // Default 100s timeout (see ShareManager.CreateHttpClient's own comment for this exact bug
        // class) deterministically fails the largest Windows asset download below sustained
        // broadband speeds - a slow-but-working connection would then retry-and-fail every hourly
        // check forever. 15 minutes comfortably covers that download on very ordinary consumer
        // upstream while still eventually giving up on a truly hung connection.
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RoeSnip.App-Updater");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    // ---------------- portable: version check (every OS) ----------------

    /// <summary>The latest GitHub release worth telling the user about: its version, the direct
    /// download URL for the chosen Windows asset (null when this OS/release has none — never used
    /// off Windows), the release page URL (what the Linux/macOS passive notice links to), and that
    /// same asset's "digest"/"size" fields straight off the releases payload (used by
    /// <see cref="ApplyUpdateAsync"/> to verify the download - see AssetDigest's doc comment).
    /// <see cref="Digest"/> is null whenever the payload didn't carry one - callers fail open on
    /// that, never fail closed. <see cref="IsGzip"/> is true when <see cref="DownloadUrl"/> points
    /// at the ".gz" transit asset (preferred whenever present - see <see cref="ParseUpdateInfo"/>)
    /// rather than the plain exe - <see cref="ApplyUpdateAsync"/> decompresses it after digest
    /// verification, before the swap.</summary>
    public sealed record UpdateInfo(Version Version, string? DownloadUrl, string ReleaseUrl, string? Digest = null, long? Size = null, bool IsGzip = false);

    /// <summary>Checks the GitHub Releases API for a newer published build. Returns null (never
    /// throws) whenever there's nothing to offer: the repo is private (404), there's no network,
    /// the response doesn't parse, the release isn't newer than <see cref="CurrentVersion"/> (see
    /// <see cref="ParseUpdateInfo"/> for the full gating), or GitHub answered 304/rate-limited.
    ///
    /// Routed through <see cref="ReleaseClient"/> (see GitHubLatestReleaseClient's own doc comment
    /// for the full contract): a conditional If-None-Match request that comes back 304 costs nothing
    /// against GitHub's unauthenticated rate limit, which is what makes an hourly-by-default periodic
    /// loop (see TrayApp's periodic update loop) cheap on both Windows (full auto-apply) and
    /// Linux/macOS (passive notice). The ETag is committed when this method is about to return null
    /// because there's genuinely no update - never on a rate-limited/failed probe, so a stored ETag
    /// always safely means "304 = still no update"; if an update was found but its later
    /// download/apply failed, the next check gets a full uncached GET and a real chance to retry
    /// rather than silently 304'ing forever.
    /// <paramref name="bypassBackoff"/> forces a live network attempt even inside an active
    /// rate-limit backoff window - the manual "Check for updates" menu item passes true because a
    /// deliberate user click deserves a real answer.
    /// <paramref name="commitEvenWhenUpdateFound"/> is for the Linux/macOS passive notice ONLY (see
    /// <c>TrayApp.CheckForNewVersionPassivelyAsync</c>): that path never downloads or applies
    /// anything, it just shows a once-per-version toast, so there is no retry to protect by
    /// withholding the ETag - and withholding it anyway would mean every hourly tick between a
    /// release shipping and the user manually upgrading (potentially weeks, since downloads stay
    /// manual there) re-fetches the full payload instead of getting a free 304. A genuinely NEWER
    /// release still changes the resource and answers 200 regardless of a stale ETag, so committing
    /// here cannot mask a real new release.</summary>
    public static async Task<UpdateInfo?> CheckForUpdateAsync(bool bypassBackoff = false, bool commitEvenWhenUpdateFound = false)
    {
        try
        {
            ProbeResult probe = await ReleaseClient.ProbeAsync(HttpClient, bypassBackoff).ConfigureAwait(false);
            switch (probe.Status)
            {
                case ProbeStatus.NotModified:
                    FileLog.Write("RoeSnip: update check up to date (304, conditional request, does not count against the GitHub rate limit).");
                    return null;
                case ProbeStatus.RateLimited:
                    FileLog.Write("RoeSnip: update check skipped - GitHub rate limit backoff is active.");
                    return null;
                case ProbeStatus.Failed:
                    FileLog.Write($"RoeSnip: update check failed (non-fatal): {probe.Detail}");
                    return null;
            }

            using JsonDocument document = probe.Json!;
            UpdateInfo? update = ParseUpdateInfo(document.RootElement, CurrentVersion, requireWindowsAsset: OperatingSystem.IsWindows());
            if (update is not null && UpdateHealthMarker.IsQuarantined(HealthMarkerDirectory, update.Version))
            {
                // This exact release already crash-looped on this machine and was auto-rolled-back
                // (Windows only - see UpdateHealthMarker's own doc comment) - never re-offer it, even
                // as a Linux/macOS passive notice. The ordinary downgrade guard inside
                // ParseUpdateInfo (releaseVersion <= currentVersion) cannot catch this: the
                // quarantined release IS newer than CurrentVersion, that's the whole reason it looked
                // like an update. A fixed re-release under a newer version number clears the
                // quarantine automatically (IsQuarantined's own side effect).
                FileLog.Write($"RoeSnip: skipping update to {update.Version} - quarantined after repeated startup failures.");
                update = null;
            }

            if (update is null || commitEvenWhenUpdateFound)
            {
                // No update found in this payload - or the passive-notice caller has already fully
                // processed whatever was found and there is nothing left to retry. See this method's
                // doc comment for why an update-found-but-not-yet-applied Windows path must never
                // reach this line.
                ReleaseClient.CommitETag(probe.ETag);
            }

            return update;
        }
        catch (Exception ex)
        {
            // A private repo (404), a network failure, or a malformed response all mean the same
            // thing to the caller: no update available right now. Never let any of it crash the
            // tray — log to stderr and move on.
            FileLog.Write($"RoeSnip: update check failed (non-fatal): {ex.Message}");
            return null;
        }
    }

    /// <summary>Pure parse of one GitHub "releases/latest" JSON response — no network, unit-tested
    /// directly against literal JSON (<c>requireWindowsAsset</c> is an explicit parameter, not an
    /// internal <c>OperatingSystem.IsWindows()</c> read, purely so both branches are testable from
    /// this Windows-hosted test project without needing a second OS to run on). Returns null
    /// whenever there's nothing actionable: no tag_name, an unparseable version, or a release
    /// that isn't strictly newer than <paramref name="currentVersion"/>. When
    /// <paramref name="requireWindowsAsset"/> is true (the real Windows caller), ALSO returns null
    /// when the release has no "RoeSnipApp-win-x64.exe" asset — a "version available" notice this
    /// OS could never actually apply would be worse than silence, matching the WPF reference's
    /// original all-or-nothing gate. When false (the Linux/macOS passive-notice caller),
    /// DownloadUrl is populated when present but never required — that notice only needs Version
    /// and ReleaseUrl.
    ///
    /// Scans for BOTH the ".gz" and plain Windows asset names and prefers the ".gz" one when
    /// present (hardening item 9) - each asset carries its OWN digest/size, never mixed across the
    /// two, so verification in <see cref="ApplyUpdateAsync"/> always checks the exact bytes that
    /// were actually downloaded. Falling back to the plain asset when no ".gz" entry exists
    /// protects against a release.yml slip that only publishes one of the two.</summary>
    public static UpdateInfo? ParseUpdateInfo(JsonElement root, Version currentVersion, bool requireWindowsAsset)
    {
        if (!root.TryGetProperty("tag_name", out JsonElement tagElement))
        {
            return null;
        }

        string? tag = tagElement.GetString();
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        string versionText = tag.Length > 0 && (tag[0] == 'v' || tag[0] == 'V') ? tag[1..] : tag;
        if (!Version.TryParse(versionText, out Version? releaseVersion) || releaseVersion is null)
        {
            return null;
        }

        string releaseUrl = root.TryGetProperty("html_url", out JsonElement htmlUrlElement) &&
            htmlUrlElement.GetString() is { Length: > 0 } html
            ? html
            : $"https://github.com/{GitHubOwner}/{GitHubRepo}/releases/tag/{tag}";

        string? plainUrl = null, plainDigest = null;
        long? plainSize = null;
        string? gzUrl = null, gzDigest = null;
        long? gzSize = null;
        if (root.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                if (!asset.TryGetProperty("name", out JsonElement nameElement) ||
                    !asset.TryGetProperty("browser_download_url", out JsonElement urlElement))
                {
                    continue;
                }

                string? name = nameElement.GetString();
                string? digest = asset.TryGetProperty("digest", out JsonElement digestElement) ? digestElement.GetString() : null;
                long? size = asset.TryGetProperty("size", out JsonElement sizeElement) && sizeElement.TryGetInt64(out long sizeValue)
                    ? sizeValue
                    : null;

                if (string.Equals(name, GzReleaseAssetName, StringComparison.OrdinalIgnoreCase))
                {
                    gzUrl = urlElement.GetString();
                    gzDigest = digest;
                    gzSize = size;
                }
                else if (string.Equals(name, ReleaseAssetName, StringComparison.OrdinalIgnoreCase))
                {
                    plainUrl = urlElement.GetString();
                    plainDigest = digest;
                    plainSize = size;
                }
            }
        }

        string? downloadUrl = !string.IsNullOrEmpty(gzUrl) ? gzUrl : plainUrl;
        string? chosenDigest = !string.IsNullOrEmpty(gzUrl) ? gzDigest : plainDigest;
        long? chosenSize = !string.IsNullOrEmpty(gzUrl) ? gzSize : plainSize;
        bool isGzip = !string.IsNullOrEmpty(gzUrl);

        if (releaseVersion <= currentVersion)
        {
            return null;
        }

        if (requireWindowsAsset && string.IsNullOrEmpty(downloadUrl))
        {
            return null;
        }

        return new UpdateInfo(releaseVersion, downloadUrl, releaseUrl, chosenDigest, chosenSize, isGzip);
    }

    // ---------------- Windows-only: cleanup / install / apply ----------------

    /// <summary>Best-effort delete of update leftovers (a ".old" from a prior swap that's unlocked
    /// once that older process exited, and any ".new" abandoned by a download that never
    /// completed). Called once, synchronously, at startup. Never throws.</summary>
    [SupportedOSPlatform("windows")]
    public static void CleanupStaleUpdateFiles()
    {
        TryDelete(StaleExePath);
        TryDelete(DownloadingExePath);
        TryDelete(GzDownloadingExePath);
    }

    /// <summary>Background bounded-retry delete of the ".old" exe a prior update swapped out —
    /// right after an update hand-off the just-replaced process can still be exiting and holding
    /// its renamed exe locked, so <see cref="CleanupStaleUpdateFiles"/>'s single synchronous
    /// attempt can miss it. Never throws; call on a background thread.</summary>
    [SupportedOSPlatform("windows")]
    public static void CleanupStaleExeWithRetry() => TryDeleteWithRetry(StaleExePath);

    /// <summary>Deletes only the abandoned ".new" download leftover, never <see cref="StaleExePath"/>
    /// - used at startup instead of <see cref="CleanupStaleUpdateFiles"/> when the crash-loop guard
    /// (<see cref="CheckUpdateHealthAtStartup"/>) determines this launch is still pending health
    /// verification, so the ".old" rollback target must survive until the health milestone
    /// (<see cref="CompleteHealthMilestone"/>) proves this launch didn't crash-loop.</summary>
    [SupportedOSPlatform("windows")]
    public static void CleanupDownloadLeftover()
    {
        TryDelete(DownloadingExePath);
        TryDelete(GzDownloadingExePath);
    }

    // ---------------- Crash-loop guard (hardening item 7) ----------------

    /// <summary>What <see cref="CheckUpdateHealthAtStartup"/> found and what the caller (TrayApp)
    /// must do next.</summary>
    public enum HealthCheckAction
    {
        /// <summary>Nothing pending (not installed, no marker, or a marker for some other version) -
        /// start up exactly as before this feature existed, cleaning up ".old"/".new" immediately.</summary>
        ProceedImmediateCleanup,

        /// <summary>This launch is a post-update verification run that has not yet failed
        /// <see cref="UpdateHealthMarker.RollbackAttemptThreshold"/> times - start up normally, but
        /// the caller must defer ".old" cleanup to <see cref="CompleteHealthMilestone"/> once this
        /// launch proves itself (only ".new" leftovers, via <see cref="CleanupDownloadLeftover"/>,
        /// are safe to clean up immediately).</summary>
        ProceedDeferredCleanup,

        /// <summary>The previous build was just restored and relaunched - the caller must exit
        /// immediately without creating a tray icon, registering the hotkey, or doing anything
        /// else; this process's only remaining job was to hand off.</summary>
        Restored,
    }

    /// <summary>Crash-loop guard entry point (Windows only - the self-update swap this guards has
    /// no equivalent on Linux/macOS, item 13d): call once, as early as possible in TrayApp.Start
    /// (before the tray icon/hotkey/anything else that could itself crash). See
    /// UpdateHealthMarker's own doc comment for the full three-part design (pending-verify, health
    /// milestone, quarantine); this method is the file-system half of it - deciding whether an
    /// auto-restore is warranted and, if so, performing it.
    ///
    /// A portable/dev run (<see cref="IsInstalled"/> false) can never have a pending marker that
    /// means anything - it never goes through <see cref="ApplyUpdateAsync"/> - so it always gets
    /// <see cref="HealthCheckAction.ProceedImmediateCleanup"/>, identical to every launch before
    /// this feature existed.</summary>
    [SupportedOSPlatform("windows")]
    public static HealthCheckAction CheckUpdateHealthAtStartup()
    {
        if (!IsInstalled)
        {
            return HealthCheckAction.ProceedImmediateCleanup;
        }

        UpdateHealthMarker.State state = UpdateHealthMarker.RecordLaunchAttempt(HealthMarkerDirectory, CurrentVersionText);
        if (state.PendingVersion != CurrentVersionText)
        {
            // No marker, or a marker naming some other version (e.g. a hand-rolled downgrade) -
            // this launch isn't a post-update verification run.
            return HealthCheckAction.ProceedImmediateCleanup;
        }

        if (state.AttemptCount >= UpdateHealthMarker.RollbackAttemptThreshold &&
            File.Exists(StaleExePath) &&
            TryRestorePreviousBuild(state.AttemptCount))
        {
            return HealthCheckAction.Restored;
        }

        return HealthCheckAction.ProceedDeferredCleanup;
    }

    /// <summary>The health milestone: call once, after a <see cref="HealthCheckAction.ProceedDeferredCleanup"/>
    /// launch has stayed up long enough to be trusted (TrayApp's own "tray icon shown plus ~15s of
    /// uptime" gate). Clears the pending-verify marker - this build is proven, the next update
    /// starts a fresh cycle - and only then runs the ".old" cleanup
    /// <see cref="CheckUpdateHealthAtStartup"/> deferred, since it is only safe to delete the
    /// rollback target once this launch is known not to be crash-looping.</summary>
    [SupportedOSPlatform("windows")]
    public static void CompleteHealthMilestone()
    {
        UpdateHealthMarker.ClearPending(HealthMarkerDirectory);
        CleanupStaleExeWithRetry();
    }

    /// <summary>Restores the ".old" build over <see cref="InstalledExePath"/>, quarantines the
    /// currently-running (bad) version so <see cref="CheckForUpdateAsync"/> never re-offers it, and
    /// relaunches the restored build. The currently-running exe can be renamed out from under this
    /// very process - the same trick every update swap already relies on (Windows allows renaming a
    /// running executable's file; the process keeps running against its now-renamed handle) - so
    /// the bad exe is rotated into <see cref="StaleExePath"/>'s slot rather than deleted outright,
    /// letting the RESTORED build's own next-launch cleanup (<see cref="CheckUpdateHealthAtStartup"/>'s
    /// ProceedImmediateCleanup path, since the marker was just cleared by
    /// <see cref="UpdateHealthMarker.Quarantine"/>) delete it normally instead of inventing a second
    /// cleanup path for a one-off leftover file. Returns false (never throws) on any failure, so the
    /// caller falls back to letting this launch continue as an ordinary (if still potentially bad)
    /// startup rather than getting stuck.</summary>
    [SupportedOSPlatform("windows")]
    private static bool TryRestorePreviousBuild(int attemptCount)
    {
        string badVersion = CurrentVersionText;
        try
        {
            FileLog.Write(
                $"RoeSnip: {badVersion} failed to reach the startup health milestone {attemptCount} times in a row - " +
                "restoring the previous build and quarantining this version.");

            string stagingPath = DownloadingExePath; // idle at this point: this runs before any download could start
            TryDelete(stagingPath);
            File.Move(StaleExePath, stagingPath);      // good build -> staging
            File.Move(InstalledExePath, StaleExePath); // bad running exe -> the ".old" slot (cleaned up normally next launch)
            File.Move(stagingPath, InstalledExePath);  // good build -> installed

            UpdateHealthMarker.Quarantine(HealthMarkerDirectory, badVersion);

            Process.Start(new ProcessStartInfo(InstalledExePath) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: auto-restore after repeated startup failures failed (non-fatal, continuing this launch): {ex.Message}");
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: could not clean up stale update file '{path}' (non-fatal): {ex.Message}");
        }
    }

    private static void TryDeleteWithRetry(string path)
    {
        const int maxAttempts = 10;
        const int retryDelayMs = 200; // ~2s total, bounded so a still-locked file never stalls startup
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return;
                }

                File.Delete(path);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == maxAttempts - 1)
                {
                    FileLog.Write($"RoeSnip: could not delete '{path}' after retries (non-fatal): {ex.Message}");
                    return;
                }

                Thread.Sleep(retryDelayMs);
            }
        }
    }

    /// <summary>Copies the currently-running exe to %LOCALAPPDATA%\RoeSnip.App, points the HKCU
    /// Run key + RunAtStartup setting at that copy, and launches it — the installed copy then
    /// takes over via replace-on-run (item 13a), so this call never needs to terminate the caller
    /// itself. Every failure is caught, logged, and rethrown so the caller (TrayApp) keeps running
    /// and shows an error instead of exiting on a silent failure.</summary>
    [SupportedOSPlatform("windows")]
    public static void Install()
    {
        try
        {
            string? currentExe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(currentExe))
            {
                FileLog.Write("RoeSnip: install failed - could not determine the current executable path.");
                return;
            }

            Directory.CreateDirectory(InstallDir);

            if (string.Equals(Path.GetFullPath(currentExe), Path.GetFullPath(InstalledExePath), StringComparison.OrdinalIgnoreCase))
            {
                // Already running from the installed location - nothing to install.
                return;
            }

            // Stage the copy under a temp name, then swap it into place, so InstalledExePath is
            // never observable half-written. Same swap discipline ApplyUpdateAsync uses.
            string stagingPath = DownloadingExePath;
            TryDelete(stagingPath);
            File.Copy(currentExe, stagingPath, overwrite: true);

            TryDelete(StaleExePath);
            bool movedExisting = false;
            if (File.Exists(InstalledExePath))
            {
                // Rename any existing install out of the way first — works even when a running
                // installed instance holds it locked (renaming a running exe is allowed on
                // Windows; that process keeps running against its now-renamed handle until it
                // exits, and CleanupStaleExeWithRetry deletes the .old once it does).
                File.Move(InstalledExePath, StaleExePath);
                movedExisting = true;
            }

            try
            {
                File.Move(stagingPath, InstalledExePath);
            }
            catch
            {
                // Never leave the install location without a runnable exe: put the prior copy back.
                if (movedExisting && !File.Exists(InstalledExePath) && File.Exists(StaleExePath))
                {
                    File.Move(StaleExePath, InstalledExePath);
                }

                throw;
            }

            try
            {
                SetInstalledRunAtStartup();
            }
            catch (Exception ex)
            {
                FileLog.Write($"RoeSnip: install could not set run-at-startup (non-fatal): {ex.Message}");
            }

            // Make the freshly-installed app findable in Windows search: nothing under
            // %LOCALAPPDATA% is indexed, only a Start Menu shortcut is. Never throws.
            StartMenuShortcut.EnsureFor(InstalledExePath);

            try
            {
                RoeSnipSettings settings = AppComposition.LoadSettingsOrDefault();
                SettingsStore.Save(settings with { RunAtStartup = true });
            }
            catch (Exception ex)
            {
                FileLog.Write($"RoeSnip: install could not persist the run-at-startup setting (non-fatal): {ex.Message}");
            }

            // Make install behave like a MOVE, not a copy: the source exe can't delete itself
            // while it's still running, so record it for the installed copy to clean up on its
            // next startup, once the replace-on-run handoff below has this process exit and
            // release its file lock.
            try
            {
                File.WriteAllText(PendingSourceCleanupMarkerPath, currentExe);
            }
            catch (Exception ex)
            {
                FileLog.Write($"RoeSnip: could not record source exe for post-install cleanup (non-fatal): {ex.Message}");
            }

            Process.Start(new ProcessStartInfo(InstalledExePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: install failed: {ex.Message}");
            throw;
        }
    }

    /// <summary>Best-effort cleanup of the source exe a prior <see cref="Install"/> "move" left
    /// behind (see the marker written there): reads <see cref="PendingSourceCleanupMarkerPath"/>
    /// if present and, only when the recorded path exists, is a file literally named
    /// "RoeSnip.exe", and is NOT <see cref="InstalledExePath"/>, deletes it with a short bounded
    /// retry. The marker is removed afterward regardless of whether the delete succeeded. Runs on
    /// a background thread; never throws into the tray.</summary>
    [SupportedOSPlatform("windows")]
    public static void ProcessPendingSourceCleanup()
    {
        string markerPath = PendingSourceCleanupMarkerPath;
        try
        {
            if (!File.Exists(markerPath))
            {
                return;
            }

            string sourcePath = File.ReadAllText(markerPath).Trim();

            if (!string.IsNullOrEmpty(sourcePath) &&
                File.Exists(sourcePath) &&
                string.Equals(Path.GetFileName(sourcePath), "RoeSnip.exe", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(InstalledExePath), StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteWithRetry(sourcePath);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: post-install source cleanup failed (non-fatal): {ex.Message}");
        }
        finally
        {
            TryDelete(markerPath);
        }
    }

    /// <summary>Writes the HKCU Run key directly for <see cref="InstalledExePath"/>. Mirrors
    /// StartupManager.SetRunAtStartup(true), but that helper always points at
    /// Environment.ProcessPath — here the CALLER is still the non-installed process, so the key
    /// has to be pointed at the installed copy explicitly instead.</summary>
    [SupportedOSPlatform("windows")]
    private static void SetInstalledRunAtStartup()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        key?.SetValue(RunValueName, $"\"{InstalledExePath}\"", RegistryValueKind.String);
    }

    /// <summary>Downloads the release's Windows exe, swaps it in for <see cref="InstalledExePath"/>,
    /// and launches it — the new build then takes over via replace-on-run, which kills this
    /// process. A truncated/failed download never touches the installed exe; a failure partway
    /// through the swap rolls back to the previous exe so the install is never left without a
    /// runnable copy. <paramref name="beforeLaunch"/> is awaited after the swap-in succeeds and
    /// right before Process.Start — the swap itself is safe to do while the app is busy, but the
    /// launch is not: replace-on-run would kill this instance mid-snip. Callers driven by an
    /// explicit user click pass null and apply as soon as it is their turn; the silent startup
    /// auto-update passes a delegate that waits for the app to go idle first. Calls are serialized
    /// on <see cref="ApplyUpdateLock"/>. Rethrows on failure so the caller can surface it to the
    /// user.</summary>
    [SupportedOSPlatform("windows")]
    public static async Task ApplyUpdateAsync(UpdateInfo info, Func<Task>? beforeLaunch = null)
    {
        await ApplyUpdateLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await ApplyUpdateCoreAsync(info, beforeLaunch).ConfigureAwait(false);
        }
        finally
        {
            ApplyUpdateLock.Release();
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task ApplyUpdateCoreAsync(UpdateInfo info, Func<Task>? beforeLaunch)
    {
        if (string.IsNullOrEmpty(info.DownloadUrl))
        {
            // ParseUpdateInfo already refuses to return an UpdateInfo without this on Windows, but
            // ApplyUpdateAsync is public — guard the contract explicitly rather than trusting every
            // caller got there through CheckForUpdateAsync.
            throw new InvalidOperationException($"No downloadable Windows asset for {info.Version}.");
        }

        string downloadPath = DownloadingExePath; // final decompressed exe - the swap logic below always uses this slot
        // Distinct temp name for a ".gz" transit asset (item 9) - kept separate from downloadPath
        // so a failure between "downloaded" and "decompressed" never confuses the two, and so
        // CleanupStaleUpdateFiles can find either kind of leftover independently. Equal to
        // downloadPath itself when the chosen asset is the plain exe (no gz step happens at all).
        string fetchPath = info.IsGzip ? GzDownloadingExePath : downloadPath;
        // Set when the swap-in fails AND the rollback-to-previous-exe recovery also fails: the
        // outer catch's usual "always delete downloadPath" cleanup would otherwise throw away the
        // one surviving, already digest-verified asset on a disk that has no runnable exe left at
        // all — see the inner catch below for the full recovery chain this guards.
        bool preserveDownload = false;
        try
        {
            Directory.CreateDirectory(InstallDir);
            TryDelete(downloadPath);
            if (fetchPath != downloadPath)
            {
                TryDelete(fetchPath);
            }

            using (HttpResponseMessage response = await HttpClient
                       .GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead)
                       .ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using Stream source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                await using (FileStream destination = File.Create(fetchPath))
                {
                    await source.CopyToAsync(destination).ConfigureAwait(false);
                }
            }

            var downloaded = new FileInfo(fetchPath);
            if (!downloaded.Exists || downloaded.Length == 0)
            {
                TryDelete(fetchPath);
                throw new IOException($"Downloaded update for {info.Version} was empty or missing.");
            }

            if (info.Size is long expectedSize && downloaded.Length != expectedSize)
            {
                // Cheap truncation catch ahead of the hash - a short/long download is almost always
                // a broken transfer, and saying so plainly beats a generic "digest mismatch". This
                // compares against the CHOSEN asset's own size (the .gz asset's when info.IsGzip),
                // never the decompressed size.
                TryDelete(fetchPath);
                throw new IOException(
                    $"Downloaded update for {info.Version} was {downloaded.Length} bytes, expected {expectedSize} (truncated download).");
            }

            bool? digestVerified = await AssetDigest.VerifyAsync(fetchPath, info.Digest).ConfigureAwait(false);
            if (digestVerified == false)
            {
                // Bytes don't match GitHub's own published sha256 for this asset - corruption or a
                // tampered download-CDN path. Never swap this into the install; the caller's normal
                // failure path (log + rethrow) leaves the current exe untouched and retries next cycle.
                TryDelete(fetchPath);
                throw new IOException($"Downloaded update for {info.Version} failed SHA-256 verification.");
            }

            if (digestVerified is null)
            {
                // The release payload had no usable "digest" field (older GitHub API shape, or
                // genuinely absent) - fail OPEN rather than block updates on a field this project
                // doesn't control. See AssetDigest's doc comment for why fail-closed here wouldn't
                // add real security anyway (digest and URL travel the same channel).
                FileLog.Write($"RoeSnip: update to {info.Version} has no verifiable digest in the release payload - skipping hash check.");
            }

            if (info.IsGzip)
            {
                // fetchPath's bytes are already digest-verified above - decompress into the plain
                // exe the rest of this method (and every downstream caller) expects, then drop the
                // now-redundant .gz. Sub-second local CPU; never inline with the swap itself.
                try
                {
                    await GzipAssetDecompressor.DecompressAsync(fetchPath, downloadPath).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    TryDelete(fetchPath);
                    TryDelete(downloadPath);
                    throw new IOException($"Downloaded update for {info.Version} could not be decompressed: {ex.Message}", ex);
                }

                TryDelete(fetchPath);
            }

            // Retried, not a bare delete: a prior .old can still be held locked by a sibling
            // process that is only now exiting (see TryDeleteWithRetry's own doc comment). A
            // one-shot TryDelete losing that race would make the File.Move below throw
            // "destination exists" AFTER the ~180 MB download above already completed, wasting it
            // for a purely transient lock instead of the ~2s bounded wait paying for itself.
            TryDeleteWithRetry(StaleExePath);

            bool renamedCurrent = false;
            if (File.Exists(InstalledExePath))
            {
                File.Move(InstalledExePath, StaleExePath);
                renamedCurrent = true;
            }

            try
            {
                File.Move(downloadPath, InstalledExePath);
            }
            catch
            {
                // Roll back: never leave the install without a runnable exe. This recovery move can
                // itself fail (the realistic trigger is the same kind of transient AV sharing
                // violation on a fresh exe that failed the move above) - guard it separately so
                // that failure doesn't fall straight through to the outer catch, which would delete
                // downloadPath unconditionally and brick the install with neither a runnable exe nor
                // the one verified asset left to recover from by hand.
                bool recovered = false;
                if (renamedCurrent && !File.Exists(InstalledExePath) && File.Exists(StaleExePath))
                {
                    try
                    {
                        File.Move(StaleExePath, InstalledExePath);
                        recovered = true;
                    }
                    catch (Exception rollbackEx)
                    {
                        FileLog.Write($"RoeSnip: rollback to the previous exe failed: {rollbackEx.Message}");
                    }
                }

                if (!recovered && !File.Exists(InstalledExePath))
                {
                    // Last resort: retry the same move. A running, digest-verified new build beats
                    // an install directory left with no runnable exe at all, and the original
                    // failure is plausibly transient (a momentary AV lock), so a second attempt can
                    // succeed even though the rollback above just failed the identical way.
                    try
                    {
                        File.Move(downloadPath, InstalledExePath);
                    }
                    catch (Exception fallbackEx)
                    {
                        preserveDownload = true;
                        FileLog.Write($"RoeSnip: last-resort swap-in also failed, leaving the update download in place for manual recovery: {fallbackEx.Message}");
                    }
                }

                throw;
            }

            if (beforeLaunch is not null)
            {
                await beforeLaunch().ConfigureAwait(false);
            }

            // Crash-loop guard (hardening item 7): record the version we're about to launch into as
            // pending health verification BEFORE starting it, so the new process's own startup
            // (UpdateManager.CheckUpdateHealthAtStartup) sees the marker on its very first launch.
            // Must be written here, not by the new process itself on its own first line of Main -
            // this process is the one that knows for certain a swap just happened; the new process
            // only knows "I am this version", which is exactly as true on every ordinary relaunch.
            UpdateHealthMarker.RecordPendingUpdate(HealthMarkerDirectory, FormatVersionText(info.Version));

            Process.Start(new ProcessStartInfo(InstalledExePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            if (!preserveDownload)
            {
                TryDelete(downloadPath);
                if (fetchPath != downloadPath)
                {
                    TryDelete(fetchPath);
                }
            }

            FileLog.Write($"RoeSnip: update to {info.Version} failed: {ex.Message}");
            throw;
        }
    }
}
