using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using RoeSnip.Core.Diagnostics;
using RoeSnip.Core.Settings;
using RoeSnip.Core.Updates;

namespace RoeSnip.App.AppShell;

/// <summary>Install + self-update from GitHub Releases for the cross-platform app, ported from
/// src/RoeSnip/App/UpdateManager.cs (WPF reference). One install unit per OS, swapped by rename
/// with a ".old" rollback copy:
/// - Windows: %LOCALAPPDATA%\RoeSnip.App\RoeSnip.exe from "RoeSnipApp-win-&lt;arch&gt;.exe(.gz)".
/// - Linux: $XDG_DATA_HOME/RoeSnip/RoeSnip.AppImage from "RoeSnip-linux-&lt;arch&gt;.AppImage", only
///   when this process runs from an AppImage ($APPIMAGE). A tar.gz / dev folder run gets the
///   passive "new version" notice instead.
/// - macOS: the running .app bundle when it is in /Applications or ~/Applications and the user
///   can write that folder, else ~/Applications/RoeSnip.app (Install copies a .dmg / Downloads run
///   there), from "RoeSnip-osx-&lt;arch&gt;.app.tar.gz", only when this process runs from a bundle.
///
/// Identity is DISTINCT from the WPF app (both can be installed side by side): own install dir,
/// Run value "RoeSnip.App", and never the WPF asset "RoeSnip.exe" (UpdateAssetNames).
///
/// Every public entry point is a convenience, never load-bearing: no network, a private repo, a
/// truncated download or a locked file must never throw into the tray or leave the install without
/// a runnable copy. The JSON parse (<see cref="ParseUpdateInfo(JsonElement, Version, bool)"/>) and
/// the path / swap / file-content helpers in RoeSnip.Core.Updates are unit-tested; the live
/// download and relaunch are verified by hand.</summary>
public static class UpdateManager
{
    private const string GitHubOwner = "roe69";
    private const string GitHubRepo = "roesnip";

    // The asset names this exact process downloads (OS + ProcessArchitecture). Null on an OS/arch
    // no release ships for - such a process never self-updates.
    private static readonly UpdateAssetSelection? Assets = UpdateAssetNames.ForCurrentProcess();

    // The pre-arm64 Windows names, used by the legacy ParseUpdateInfo overload the tests call.
    private static readonly UpdateAssetSelection WindowsX64Assets =
        UpdateAssetNames.For(UpdatePlatform.Windows, Architecture.X64)!;

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    // Matches StartupManager's own value name so Install() and the Settings toggle never fight.
    private const string RunValueName = "RoeSnip.App";

    private static readonly HttpClient HttpClient = CreateHttpClient();

    // Serializes ApplyUpdateAsync: the silent auto-update can be parked in its beforeLaunch idle
    // wait while a manual "Check for updates" click arrives; the click must queue, not race the
    // same staging paths.
    private static readonly SemaphoreSlim ApplyUpdateLock = new(1, 1);

    // One instance for the process lifetime: its in-memory ETag is what makes periodic checks cheap.
    private static readonly GitHubLatestReleaseClient ReleaseClient = new(GitHubOwner, GitHubRepo);

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name);

    // macOS: the bundle this process runs from when it is a user-writable /Applications or
    // ~/Applications copy, else ~/Applications/RoeSnip.app. Initialised before InstallDir.
    private static readonly string MacInstallBundle = OperatingSystem.IsMacOS()
        ? UpdateInstallLayout.MacInstallTarget(UpdateInstallLayout.BundleOf(Environment.ProcessPath), Home, IsWritableDirectory)
        : string.Empty;

    /// <summary>Directory holding the install unit: %LOCALAPPDATA%\RoeSnip.App,
    /// ~/.local/share/RoeSnip, or the folder of the macOS bundle (/Applications or ~/Applications).</summary>
    public static string InstallDir { get; } =
        OperatingSystem.IsMacOS() ? Path.GetDirectoryName(MacInstallBundle)!
        : OperatingSystem.IsLinux() ? UpdateInstallLayout.LinuxInstallDir(Env, Home)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RoeSnip.App");

    /// <summary>The unit an update swaps: the exe (Windows), the AppImage (Linux) or the .app bundle
    /// directory (macOS).</summary>
    public static string InstallTargetPath { get; } =
        OperatingSystem.IsMacOS() ? MacInstallBundle
        : OperatingSystem.IsLinux() ? UpdateInstallLayout.LinuxAppImagePath(Env, Home)
        : Path.Combine(InstallDir, "RoeSnip.exe");

    /// <summary>The installed copy's executable: what replace-on-run matches a running process's
    /// MainModule path against (on Linux the AppImage file, matched via the process's APPIMAGE).</summary>
    public static string InstalledExePath { get; } =
        OperatingSystem.IsMacOS() ? UpdateInstallLayout.MacBundleExecutable(InstallTargetPath) : InstallTargetPath;

    /// <summary>The unit THIS process runs from: Environment.ProcessPath (Windows), $APPIMAGE
    /// (Linux, null outside an AppImage), or the enclosing .app bundle (macOS, null outside one).</summary>
    public static string? RunningInstallUnitPath =>
        OperatingSystem.IsMacOS() ? UpdateInstallLayout.BundleOf(Environment.ProcessPath)
        : OperatingSystem.IsLinux() ? UpdateInstallLayout.RunningAppImage(Env)
        : Environment.ProcessPath;

    /// <summary>What a launcher (autostart, LaunchAgent, Run key) should start to get THIS copy:
    /// the AppImage on Linux (the process path is a per-run mount), else the process path.</summary>
    public static string? CurrentLaunchPath =>
        OperatingSystem.IsLinux() ? UpdateInstallLayout.RunningAppImage(Env) ?? Environment.ProcessPath
        : Environment.ProcessPath;

    /// <summary>True when this copy can install itself and self-update: always on Windows (x64 /
    /// arm64), on Linux only from an AppImage, on macOS only from a .app bundle.</summary>
    public static bool SelfUpdateSupported =>
        Assets is not null && (OperatingSystem.IsWindows() || RunningInstallUnitPath is not null);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string StaleExePath => InstallTargetPath + ".old";

    // Shared stem of every staging name; each process stages under stem + "-<pid>" (UpdateStaging)
    // so two updaters never write or swap the same file.
    private static string StagingStem =>
        OperatingSystem.IsMacOS() ? Path.Combine(InstallDir, ".RoeSnip-update")
        : OperatingSystem.IsLinux() ? InstallTargetPath + ".new"
        : Path.Combine(InstallDir, "RoeSnip.exe.new");

    private static string ProcessStagingPath => UpdateStaging.ForProcess(StagingStem, Environment.ProcessId);

    // The staged unit swapped in: Windows RoeSnip.exe.new-<pid>, Linux RoeSnip.AppImage.new-<pid>,
    // macOS the extracted bundle inside MacStagingDir (same volume as the target, so the swap is a
    // rename).
    private static string DownloadingExePath =>
        OperatingSystem.IsMacOS() ? Path.Combine(MacStagingDir, UpdateInstallLayout.MacBundleName)
        : ProcessStagingPath;

    // The raw download when it is not the staged unit itself: the Windows .gz transit asset, or the
    // macOS .app.tar.gz.
    private static string GzDownloadingExePath =>
        OperatingSystem.IsMacOS() ? ProcessStagingPath + ".app.tar.gz"
        : DownloadingExePath + ".gz";

    private static string MacStagingDir => ProcessStagingPath;

    /// <summary>Held around every download + swap (update, install, rollback), across processes.</summary>
    private static string UpdateLockPath => UpdateLock.PathFor(InstallTargetPath);

    private const string BusyMessage = "update already in progress by another RoeSnip process; skipping";

    // Bounds the whole download (HttpClient.Timeout stops at the headers with ResponseHeadersRead).
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(15);

    // Free slot for the crash-loop restore's three-way rename (a sibling, so it stays a rename).
    private static string RestoreStagingPath =>
        OperatingSystem.IsWindows() ? DownloadingExePath : InstallTargetPath + ".restore";

    private static string PendingSourceCleanupMarkerPath =>
        OperatingSystem.IsMacOS()
            ? Path.Combine(ConfigPaths.ConfigDirectory, "pending-source-cleanup.txt")
            : Path.Combine(InstallDir, "pending-source-cleanup.txt");

    /// <summary>The crash-loop marker lives in the config directory, never the install dir (which an
    /// update replaces).</summary>
    private static string HealthMarkerDirectory => ConfigPaths.ConfigDirectory;

    public static void RecordLastCheckOutcome(string? version, string outcome) =>
        UpdateStatusMarker.Record(HealthMarkerDirectory, version, outcome);

    public static string? LastCheckSummary() => UpdateStatusMarker.DescribeLastCheck(HealthMarkerDirectory);

    /// <summary>The running build's version (csproj &lt;Version&gt;), compared against tag_name.</summary>
    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);

    /// <summary>"x.y.z" display text (the SDK's always-0 Revision dropped).</summary>
    public static string CurrentVersionText => FormatVersionText(CurrentVersion);

    private static string FormatVersionText(Version v) => $"{v.Major}.{v.Minor}.{v.Build}";

    /// <summary>True when an installed copy exists on disk, whether or not this process is it.
    /// Gates the one-time "Install RoeSnip" tray item.</summary>
    public static bool InstallExists
    {
        get
        {
            try { return File.Exists(InstalledExePath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
    }

    /// <summary>True when this process runs FROM the installed copy. Only then does it self-update.</summary>
    public static bool IsInstalled
    {
        get
        {
            string? current = RunningInstallUnitPath;
            if (string.IsNullOrEmpty(current))
            {
                return false;
            }

            try
            {
                if (!OperatingSystem.IsWindows())
                {
                    // $APPIMAGE and macOS process paths are symlink-resolved; the target may not be.
                    return RealPath.SameFile(current, InstallTargetPath, PathComparison);
                }

                return string.Equals(
                    Path.GetFullPath(current).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(InstallTargetPath).TrimEnd(Path.DirectorySeparatorChar),
                    PathComparison);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
            {
                return false;
            }
        }
    }

    /// <summary>Executable paths a running instance of THIS app may have (replace-on-run's
    /// last-resort kill only touches these): the installed exe and, off Windows, its ".old" slot
    /// (an instance still running from the unit an update just renamed aside).</summary>
    public static string[] InstalledIdentityPaths() =>
        OperatingSystem.IsWindows()
            ? new[] { InstalledExePath }
            : OperatingSystem.IsMacOS()
                ? new[] { InstalledExePath, UpdateInstallLayout.MacBundleExecutable(StaleExePath) }
                : new[] { InstalledExePath, StaleExePath };

    /// <summary>The path replace-on-run compares for a candidate process: on Linux an AppImage
    /// process is identified by its APPIMAGE (its exe is a per-run /tmp/.mount_* path).</summary>
    public static string? ProcessIdentityPath(Process p, string? mainModulePath)
    {
        if (!OperatingSystem.IsLinux())
        {
            return mainModulePath;
        }

        try
        {
            string environ = File.ReadAllText($"/proc/{p.Id}/environ");
            return UpdateInstallLayout.AppImageFromEnviron(environ) ?? mainModulePath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return mainModulePath;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        // AutomaticDecompression only applies when the server sends Content-Encoding (the JSON does;
        // the binary asset download does not).
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };
        // The default 100 s timeout fails the largest asset on slow broadband every time.
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RoeSnip.App-Updater");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    // ---------------- version check (every OS) ----------------

    /// <summary>The latest release worth telling the user about. <see cref="DownloadUrl"/> is the
    /// chosen asset for this OS/arch (the Windows ".gz" sibling when present, then
    /// <see cref="IsGzip"/>); null when the release has none. <see cref="Digest"/>/<see cref="Size"/>
    /// belong to that chosen asset; <see cref="PlainDigest"/> to the uncompressed Windows exe.
    /// Digests are null when the payload carried none - callers fail open on that.</summary>
    public sealed record UpdateInfo(Version Version, string? DownloadUrl, string ReleaseUrl, string? Digest = null, long? Size = null, bool IsGzip = false, string? PlainDigest = null);

    /// <summary><see cref="Update"/> is non-null only for a genuinely newer release.
    /// <see cref="Failed"/> separates a CONFIRMED no-update from an inconclusive probe - never record
    /// the latter as "up to date".</summary>
    public sealed record UpdateCheckResult(UpdateInfo? Update, bool Failed, string? FailureDetail);

    /// <summary>Checks GitHub for a newer release. Never throws. The ETag is committed only on a
    /// confirmed no-update (or when <paramref name="commitEvenWhenUpdateFound"/>: the passive-notice
    /// caller, which never downloads, so has no retry to protect). <paramref name="bypassBackoff"/>
    /// forces a live attempt inside a rate-limit backoff (manual "Check for updates").</summary>
    public static async Task<UpdateCheckResult> CheckForUpdateAsync(bool bypassBackoff = false, bool commitEvenWhenUpdateFound = false)
    {
        try
        {
            ProbeResult probe = await ReleaseClient.ProbeAsync(HttpClient, bypassBackoff).ConfigureAwait(false);
            switch (probe.Status)
            {
                case ProbeStatus.NotModified:
                    FileLog.Write("RoeSnip: update check up to date (304, conditional request, does not count against the GitHub rate limit).");
                    return new UpdateCheckResult(null, Failed: false, FailureDetail: null);
                case ProbeStatus.RateLimited:
                    FileLog.Write("RoeSnip: update check skipped - GitHub rate limit backoff is active.");
                    return new UpdateCheckResult(null, Failed: true, FailureDetail: "rate limited by GitHub, will retry automatically");
                case ProbeStatus.Failed:
                    FileLog.Write($"RoeSnip: update check failed (non-fatal): {probe.Detail}");
                    return new UpdateCheckResult(null, Failed: true, FailureDetail: probe.Detail ?? "update check failed");
            }

            using JsonDocument document = probe.Json!;
            UpdateInfo? update = ParseUpdateInfo(document.RootElement, CurrentVersion, Assets, requireAsset: SelfUpdateSupported);
            if (update is not null && UpdateHealthMarker.IsQuarantined(HealthMarkerDirectory, update.Version))
            {
                // This release crash-looped here and was rolled back - never re-offer it. A newer
                // release clears the quarantine.
                FileLog.Write($"RoeSnip: skipping update to {update.Version} - quarantined after repeated startup failures.");
                update = null;
            }

            if (update is null || commitEvenWhenUpdateFound)
            {
                ReleaseClient.CommitETag(probe.ETag);
            }

            return new UpdateCheckResult(update, Failed: false, FailureDetail: null);
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: update check failed (non-fatal): {ex.Message}");
            return new UpdateCheckResult(null, Failed: true, FailureDetail: ex.Message);
        }
    }

    /// <summary>Legacy overload: parses against the Windows x64 asset names
    /// ("RoeSnipApp-win-x64.exe" + ".gz"). <paramref name="requireWindowsAsset"/> as in
    /// <see cref="ParseUpdateInfo(JsonElement, Version, UpdateAssetSelection?, bool)"/>.</summary>
    public static UpdateInfo? ParseUpdateInfo(JsonElement root, Version currentVersion, bool requireWindowsAsset) =>
        ParseUpdateInfo(root, currentVersion, WindowsX64Assets, requireWindowsAsset);

    /// <summary>Pure parse of one "releases/latest" response. Null when there is nothing
    /// actionable: no tag, an unparseable version, a release not strictly newer than
    /// <paramref name="currentVersion"/>, or (when <paramref name="requireAsset"/>) no matching
    /// asset - a notice this copy could never apply is worse than silence. The ".gz" sibling is
    /// preferred when <paramref name="assets"/> names one and the release has it; each asset keeps
    /// its own digest/size. Names match case-insensitively.</summary>
    public static UpdateInfo? ParseUpdateInfo(JsonElement root, Version currentVersion, UpdateAssetSelection? assets, bool requireAsset)
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
        if (assets is not null &&
            root.TryGetProperty("assets", out JsonElement assetArray) && assetArray.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement asset in assetArray.EnumerateArray())
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

                if (assets.Gz is not null && string.Equals(name, assets.Gz, StringComparison.OrdinalIgnoreCase))
                {
                    gzUrl = urlElement.GetString();
                    gzDigest = digest;
                    gzSize = size;
                }
                else if (string.Equals(name, assets.Primary, StringComparison.OrdinalIgnoreCase))
                {
                    plainUrl = urlElement.GetString();
                    plainDigest = digest;
                    plainSize = size;
                }
            }
        }

        bool isGzip = !string.IsNullOrEmpty(gzUrl);
        string? downloadUrl = isGzip ? gzUrl : plainUrl;
        string? chosenDigest = isGzip ? gzDigest : plainDigest;
        long? chosenSize = isGzip ? gzSize : plainSize;

        if (releaseVersion <= currentVersion)
        {
            return null;
        }

        if (requireAsset && string.IsNullOrEmpty(downloadUrl))
        {
            return null;
        }

        return new UpdateInfo(releaseVersion, downloadUrl, releaseUrl, chosenDigest, chosenSize, isGzip, plainDigest);
    }

    // ---------------- cleanup ----------------

    /// <summary>Startup cleanup of swap leftovers: ".old" (unless a pending-verify marker still
    /// needs it as the rollback target) and any abandoned staging download, holding the update
    /// lock (skipped while another updater runs). Never throws.</summary>
    public static void CleanupStaleUpdateFiles() =>
        WithUpdateLock(InstallTargetPath, "stale update file cleanup", () =>
        {
            if (StaleExeIsSafeToDelete())
            {
                TryDelete(StaleExePath);
            }

            DeleteStaleDownloads();
        });

    /// <summary>Runs <paramref name="action"/> holding <paramref name="target"/>'s update lock.
    /// Skips, with a log line, when another updater holds it or the lock cannot be taken, and when
    /// the target's folder does not exist (nothing to clean, and no folder is created). An updater
    /// can hold the lock for over an hour (the macOS idle gate waits out a long recording with
    /// its bundle staged), so age alone never makes its staging safe to delete.</summary>
    private static void WithUpdateLock(string target, string what, Action action)
    {
        string? dir = Path.GetDirectoryName(target);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            return;
        }

        UpdateLock? held;
        try
        {
            held = UpdateLock.TryAcquire(UpdateLock.PathFor(target));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FileLog.Write($"RoeSnip: {what} skipped - could not take the update lock: {ex.Message}");
            return;
        }

        if (held is null)
        {
            FileLog.Write($"RoeSnip: {what} skipped - {BusyMessage}.");
            return;
        }

        using (held)
        {
            action();
        }
    }

    /// <summary>Bounded-retry ".old" delete (the replaced process may still hold it). Call off the
    /// UI thread. Never throws.</summary>
    public static void CleanupStaleExeWithRetry()
    {
        if (StaleExeIsSafeToDelete())
        {
            TryDeleteWithRetry(StaleExePath);
        }
    }

    // Not gated on IsInstalled: a portable copy shares the config dir (and so the marker) with the
    // installed copy and must not delete the installed copy's rollback target.
    private static bool StaleExeIsSafeToDelete() =>
        UpdateHealthMarker.Load(HealthMarkerDirectory).PendingVersion is null;

    /// <summary>Deletes only abandoned download/staging leftovers (".new", ".new-*" older than
    /// <see cref="UpdateStaging.StaleAge"/>, so another process's in-flight download survives),
    /// never ".old" or ".restore" (after an interrupted rollback it can hold the only good build;
    /// startup repair moves it back). Holds the update lock; skipped while another updater runs.</summary>
    public static void CleanupDownloadLeftover() =>
        WithUpdateLock(InstallTargetPath, "stale download cleanup", DeleteStaleDownloads);

    private static void DeleteStaleDownloads()
    {
        try
        {
            foreach (string stale in UpdateStaging.FindStale(InstallDir, StagingStem, DateTime.UtcNow, UpdateStaging.StaleAge, PathComparison))
            {
                TryDelete(stale);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FileLog.Write($"RoeSnip: could not list stale update downloads (non-fatal): {ex.Message}");
        }
    }

    // ---------------- Crash-loop guard ----------------

    public enum HealthCheckAction
    {
        /// <summary>Nothing pending: start normally and clean up ".old"/".new" now.</summary>
        ProceedImmediateCleanup,

        /// <summary>Post-update verification run below the rollback threshold: start normally, keep
        /// ".old" until <see cref="CompleteHealthMilestone"/>.</summary>
        ProceedDeferredCleanup,

        /// <summary>The previous build was restored and relaunched: exit immediately.</summary>
        Restored,
    }

    /// <summary>Call first thing in TrayApp.Start. Counts this launch against a pending-verify
    /// marker (written by <see cref="ApplyUpdateAsync"/> right before it relaunched) and restores
    /// ".old" once the threshold of launches that never reached the health milestone is hit.</summary>
    public static HealthCheckAction CheckUpdateHealthAtStartup()
    {
        RepairInterruptedSwaps();
        if (!IsInstalled)
        {
            return HealthCheckAction.ProceedImmediateCleanup;
        }

        if (OperatingSystem.IsMacOS())
        {
            try { StartupManager.RepointMacLaunchAgent(InstalledLaunchPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                FileLog.Write($"RoeSnip: could not repoint the LaunchAgent (non-fatal): {ex.Message}");
            }
        }

        UpdateHealthMarker.State state = UpdateHealthMarker.RecordLaunchAttempt(HealthMarkerDirectory, CurrentVersionText);
        if (state.PendingVersion != CurrentVersionText)
        {
            return HealthCheckAction.ProceedImmediateCleanup;
        }

        if (state.AttemptCount >= UpdateHealthMarker.RollbackAttemptThreshold &&
            InstallSwap.Exists(StaleExePath) &&
            TryRestorePreviousBuild(state.AttemptCount))
        {
            return HealthCheckAction.Restored;
        }

        return HealthCheckAction.ProceedDeferredCleanup;
    }

    /// <summary>Linux / macOS: puts a unit back when a crash mid-swap left its path empty (the
    /// bundle swap is two renames). macOS also checks both standard bundle paths, since a .dmg run
    /// targets ~/Applications while the broken install may be the /Applications one. Each target is
    /// repaired only while holding its update lock: a live swap looks just like a crashed one.
    /// Never throws.</summary>
    private static void RepairInterruptedSwaps()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // RestoreStagingPath is the .new download there, never a rollback unit.
        }

        var targets = new System.Collections.Generic.List<string> { InstallTargetPath };
        if (OperatingSystem.IsMacOS())
        {
            targets.Add(UpdateInstallLayout.MacSystemBundlePath);
            targets.Add(UpdateInstallLayout.MacBundlePath(Home));
        }

        foreach (string target in new System.Collections.Generic.HashSet<string>(targets, StringComparer.Ordinal))
        {
            if (!InstallSwap.Exists(target + ".old") && !InstallSwap.Exists(target + ".restore"))
            {
                continue; // nothing to repair; skip the lock (and its file) entirely
            }

            WithUpdateLock(target, $"interrupted-swap repair at '{target}'", () =>
            {
                try
                {
                    if (InstallSwap.RepairInterruptedSwap(target, target + ".old", target + ".restore"))
                    {
                        FileLog.Write($"RoeSnip: repaired an interrupted update or rollback at '{target}'.");
                    }
                }
                catch (Exception ex)
                {
                    FileLog.Write($"RoeSnip: could not repair an interrupted update at '{target}' (non-fatal): {ex.Message}");
                }
            });
        }
    }

    /// <summary>True when this user can create (and so rename) entries in <paramref name="dir"/>:
    /// probes with a hidden file, since /Applications is writable only for admin users.</summary>
    private static bool IsWritableDirectory(string dir)
    {
        string probe = Path.Combine(dir, $".roesnip-write-probe-{Environment.ProcessId}");
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Health milestone (tray up + ~15 s): clears this version's pending marker, then
    /// deletes ".old" - skipped when a newer chained update already owns the marker.</summary>
    public static void CompleteHealthMilestone()
    {
        if (UpdateHealthMarker.ClearPending(HealthMarkerDirectory, CurrentVersionText))
        {
            CleanupStaleExeWithRetry();
        }
    }

    /// <summary>Restores ".old" over the target (the running bad unit is renamed into the ".old"
    /// slot - renaming a running exe / AppImage / bundle is allowed), quarantines this version and
    /// relaunches. False on any failure so this launch just continues.</summary>
    private static bool TryRestorePreviousBuild(int attemptCount)
    {
        string badVersion = CurrentVersionText;
        UpdateLock? updateLock;
        try
        {
            updateLock = UpdateLock.TryAcquire(UpdateLockPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FileLog.Write($"RoeSnip: rollback of {badVersion} skipped - could not take the update lock: {ex.Message}");
            return false;
        }

        if (updateLock is null)
        {
            FileLog.Write($"RoeSnip: rollback of {badVersion} skipped - {BusyMessage}.");
            return false;
        }

        using UpdateLock heldLock = updateLock;
        try
        {
            FileLog.Write(
                $"RoeSnip: {badVersion} failed to reach the startup health milestone {attemptCount} times in a row - " +
                "restoring the previous build and quarantining this version.");

            if (OperatingSystem.IsWindows())
            {
                TryDelete(RestoreStagingPath); // the .new download there, never a rollback unit
            }
            InstallSwap.RestorePrevious(InstallTargetPath, StaleExePath, RestoreStagingPath);
            UpdateHealthMarker.Quarantine(HealthMarkerDirectory, badVersion);
            LaunchInstalledCopy();
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
            InstallSwap.Delete(path);
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: could not clean up stale update file '{path}' (non-fatal): {ex.Message}");
        }
    }

    private static void TryDeleteWithRetry(string path)
    {
        const int maxAttempts = 10;
        const int retryDelayMs = 200; // ~2 s total, so a locked file never stalls startup
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                if (!InstallSwap.Exists(path))
                {
                    return;
                }

                InstallSwap.Delete(path);
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

    // ---------------- launch helpers ----------------

    /// <summary>Starts the installed copy; it then takes over via replace-on-run (which ends this
    /// process). Linux execs the AppImage directly (shell-execute would go through xdg-open);
    /// macOS goes through /usr/bin/open -n so LaunchServices starts the bundle.</summary>
    private static void LaunchInstalledCopy()
    {
        if (OperatingSystem.IsMacOS())
        {
            var psi = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
            psi.ArgumentList.Add("-n");
            psi.ArgumentList.Add(InstallTargetPath);
            using Process? _ = Process.Start(psi);
        }
        else if (OperatingSystem.IsLinux())
        {
            // Else the child inherits this AppImage's FUSE keepalive fd and keeps its mount daemon
            // running (see InheritedDescriptors).
            InheritedDescriptors.MarkCloseOnExec();
            using Process? _ = Process.Start(new ProcessStartInfo(InstallTargetPath) { UseShellExecute = false });
        }
        else
        {
            using Process? _ = Process.Start(new ProcessStartInfo(InstalledExePath) { UseShellExecute = true });
        }
    }

    /// <summary>Runs a system tool and throws when it exits non-zero or hangs past 5 minutes.</summary>
    private static void RunTool(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using Process process = Process.Start(psi) ?? throw new IOException($"Could not start {file}.");
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        _ = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromMinutes(5)))
        {
            try { process.Kill(); } catch { /* already gone */ }
            throw new IOException($"{file} timed out.");
        }

        if (process.ExitCode != 0)
        {
            throw new IOException($"{file} exited {process.ExitCode}: {stderr.Result.Trim()}");
        }
    }

    private static void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    /// <summary>Strips com.apple.quarantine from a bundle so Gatekeeper does not block the swapped-in
    /// copy. Best-effort: xattr exits non-zero when the attribute is absent.</summary>
    private static void StripQuarantine(string bundlePath)
    {
        try
        {
            RunTool("/usr/bin/xattr", "-dr", "com.apple.quarantine", bundlePath);
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: xattr quarantine strip skipped (non-fatal): {ex.Message}");
        }
    }

    // ---------------- install ----------------

    /// <summary>Copies this copy to the install location, turns on start-at-login (+ the Start Menu
    /// shortcut / Linux menu entry), and launches the installed copy, which takes over via
    /// replace-on-run. Logs and rethrows on failure so the tray shows an error and keeps running.</summary>
    public static void Install()
    {
        try
        {
            string? sourceUnit = RunningInstallUnitPath;
            if (string.IsNullOrEmpty(sourceUnit))
            {
                FileLog.Write("RoeSnip: install failed - could not determine what this copy runs from.");
                return;
            }

            Directory.CreateDirectory(InstallDir);

            if (IsInstalled)
            {
                return;
            }

            using UpdateLock? updateLock = UpdateLock.TryAcquire(UpdateLockPath);
            if (updateLock is null)
            {
                throw new IOException("Another RoeSnip process is updating the installed copy; try again in a minute.");
            }

            // Stage next to the target, then rename into place, so the target is never observable
            // half-written.
            string stagingPath = DownloadingExePath;
            TryDelete(stagingPath);
            if (OperatingSystem.IsMacOS())
            {
                TryDelete(MacStagingDir);
                Directory.CreateDirectory(MacStagingDir);
                RunTool("/usr/bin/ditto", sourceUnit, stagingPath);
            }
            else
            {
                File.Copy(sourceUnit, stagingPath, overwrite: true);
                MakeExecutable(stagingPath);
            }

            // Works even while an installed instance runs: it keeps its renamed file/bundle.
            InstallSwap.SwapIn(stagingPath, InstallTargetPath, StaleExePath);
            UpdateHealthMarker.RecordInstalledVersion(HealthMarkerDirectory, CurrentVersionText);
            if (OperatingSystem.IsMacOS())
            {
                TryDelete(MacStagingDir);
                StripQuarantine(InstallTargetPath);
            }

            try
            {
                if (OperatingSystem.IsWindows())
                {
                    SetInstalledRunAtStartup();
                }
                else
                {
                    StartupManager.SetRunAtStartupFor(true, InstalledLaunchPath);
                }
            }
            catch (Exception ex)
            {
                FileLog.Write($"RoeSnip: install could not set run-at-startup (non-fatal): {ex.Message}");
            }

            if (OperatingSystem.IsWindows())
            {
                // Nothing under %LOCALAPPDATA% is search-indexed; a Start Menu shortcut is.
                StartMenuShortcut.EnsureFor(InstalledExePath);
            }
            else if (OperatingSystem.IsLinux())
            {
                LinuxDesktopIntegration.EnsureMenuEntry(InstallTargetPath, CurrentVersionText);
            }

            try
            {
                RoeSnipSettings settings = AppComposition.LoadSettingsOrDefault();
                SettingsStore.Save(settings with { RunAtStartup = true });
            }
            catch (Exception ex)
            {
                FileLog.Write($"RoeSnip: install could not persist the run-at-startup setting (non-fatal): {ex.Message}");
            }

            // Install is a MOVE on Windows and Linux: the installed copy deletes the source on its
            // next start (this process still holds it). macOS leaves the source bundle alone (it is
            // usually on the read-only .dmg, or in /Applications by the user's choice).
            if (!OperatingSystem.IsMacOS())
            {
                try
                {
                    File.WriteAllText(PendingSourceCleanupMarkerPath, sourceUnit);
                }
                catch (Exception ex)
                {
                    FileLog.Write($"RoeSnip: could not record source exe for post-install cleanup (non-fatal): {ex.Message}");
                }
            }

            LaunchInstalledCopy();
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: install failed: {ex.Message}");
            throw;
        }
    }

    /// <summary>What autostart / LaunchAgent entries point at for the installed copy.</summary>
    public static string InstalledLaunchPath => InstalledExePath;

    /// <summary>Deletes the source a prior <see cref="Install"/> "move" left behind: only a file
    /// named "RoeSnip.exe" (Windows) or "RoeSnip*.AppImage" (Linux) that is not the installed copy.
    /// The marker is removed either way. Never throws.</summary>
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
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
            {
                return;
            }

            bool disposable = OperatingSystem.IsWindows()
                ? string.Equals(Path.GetFileName(sourcePath), "RoeSnip.exe", StringComparison.OrdinalIgnoreCase) &&
                  !string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(InstalledExePath), StringComparison.OrdinalIgnoreCase)
                : OperatingSystem.IsLinux() && UpdateInstallLayout.IsDisposableLinuxSource(sourcePath, InstallTargetPath);
            if (disposable)
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

    /// <summary>The HKCU Run value for <see cref="InstalledExePath"/> (StartupManager always
    /// points at the running process, which here is still the non-installed copy).</summary>
    [SupportedOSPlatform("windows")]
    private static void SetInstalledRunAtStartup()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        key?.SetValue(RunValueName, $"\"{InstalledExePath}\"", RegistryValueKind.String);
    }

    // ---------------- apply ----------------

    /// <summary>Downloads this OS/arch's asset, verifies size + SHA-256 against the release
    /// payload, swaps it in with a ".old" rollback, writes the pending-verify marker and launches
    /// the new build (which ends this process via replace-on-run). <paramref name="beforeLaunch"/>
    /// is the idle gate: awaited right before the launch on Windows/Linux, and before the bundle
    /// swap on macOS (a folder-published app lazily loads assemblies from its bundle path, so the
    /// old process must not keep running against a swapped bundle). Serialized on
    /// <see cref="ApplyUpdateLock"/> in-process and on the per-install <see cref="UpdateLock"/>
    /// across processes: when another process holds it, waits up to <paramref name="lockWait"/>
    /// (zero = skip at once, <see cref="ApplyOutcome.Busy"/>). With the lock held, an install that
    /// already is the release is <see cref="ApplyOutcome.AppliedByOtherProcess"/> (re-applying it
    /// would move the just-launched build into ".old" and delete the real previous one).
    /// <paramref name="swapGate"/>, when given, is claimed right before the first swap step; an
    /// abandoned gate makes it throw before anything on disk changes. Rethrows on failure; the
    /// installed copy is never left without a runnable unit.</summary>
    public static async Task<ApplyOutcome> ApplyUpdateAsync(
        UpdateInfo info, Func<Task>? beforeLaunch = null, TimeSpan lockWait = default, SwapGate? swapGate = null)
    {
        await ApplyUpdateLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (string.IsNullOrEmpty(info.DownloadUrl))
            {
                throw new InvalidOperationException($"No downloadable asset for {info.Version} on this platform.");
            }

            (UpdateLock? updateLock, bool contended) = await UpdateLock
                .AcquireAsync(UpdateLockPath, lockWait, TimeSpan.FromSeconds(1))
                .ConfigureAwait(false);
            if (updateLock is null)
            {
                FileLog.Write($"RoeSnip: {BusyMessage} ({info.Version}).");
                return ApplyOutcome.Busy;
            }

            using (updateLock)
            {
                // Always, not only when contended: the other updater may have finished before
                // this one first tried the lock.
                if (await InstalledMatchesAsync(info).ConfigureAwait(false))
                {
                    FileLog.Write(
                        $"RoeSnip: the installed copy already is {info.Version} (installed by another RoeSnip process{(contended ? " while this one waited" : "")}); skipping.");
                    return ApplyOutcome.AppliedByOtherProcess;
                }

                await ApplyUpdateLockedAsync(info, beforeLaunch, swapGate).ConfigureAwait(false);
                return ApplyOutcome.Applied;
            }
        }
        finally
        {
            ApplyUpdateLock.Release();
        }
    }

    public enum ApplyOutcome
    {
        /// <summary>This process swapped the release in and launched it.</summary>
        Applied,

        /// <summary>Another process held the update lock and the install now matches the release.</summary>
        AppliedByOtherProcess,

        /// <summary>Another process still held the update lock after the wait; nothing was done.</summary>
        Busy,
    }

    /// <summary>True when the installed unit already is <paramref name="info"/>'s release.
    /// macOS: CFBundleShortVersionString of the installed bundle's Info.plist. Windows: the
    /// installed exe's file version, else its SHA-256 against the asset's (the plain exe's for a
    /// .gz). Linux: the AppImage's SHA-256 when the release has a digest, else the version the
    /// health marker recorded at the last swap plus a size check. False when unsure.</summary>
    private static async Task<bool> InstalledMatchesAsync(UpdateInfo info)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                string plist = Path.Combine(InstallTargetPath, "Contents", "Info.plist");
                return File.Exists(plist) &&
                       InstalledVersion.Matches(InstalledVersion.FromMacInfoPlist(await File.ReadAllTextAsync(plist).ConfigureAwait(false)), info.Version);
            }

            if (!File.Exists(InstalledExePath))
            {
                return false;
            }

            if (OperatingSystem.IsWindows())
            {
                FileVersionInfo file = FileVersionInfo.GetVersionInfo(InstalledExePath);
                if (InstalledVersion.Matches(new Version(file.FileMajorPart, file.FileMinorPart, file.FileBuildPart), info.Version))
                {
                    return true;
                }
            }

            string? digest = info.IsGzip ? info.PlainDigest : info.Digest;
            bool? digestMatches = await AssetDigest.VerifyAsync(InstalledExePath, digest).ConfigureAwait(false);
            if (digestMatches is not null || !OperatingSystem.IsLinux())
            {
                return digestMatches == true;
            }

            UpdateHealthMarker.State marker = UpdateHealthMarker.Load(HealthMarkerDirectory);
            string version = FormatVersionText(info.Version);
            return (marker.InstalledVersion == version || marker.PendingVersion == version) &&
                   (info.Size is not long size || new FileInfo(InstalledExePath).Length == size);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task ApplyUpdateLockedAsync(UpdateInfo info, Func<Task>? beforeLaunch, SwapGate? swapGate)
    {
        if (OperatingSystem.IsWindows())
        {
            await ApplyUpdateWindowsAsync(info, beforeLaunch, swapGate).ConfigureAwait(false);
        }
        else if (OperatingSystem.IsLinux())
        {
            await ApplyUpdateLinuxAsync(info, beforeLaunch, swapGate).ConfigureAwait(false);
        }
        else if (OperatingSystem.IsMacOS())
        {
            await ApplyUpdateMacAsync(info, beforeLaunch, swapGate).ConfigureAwait(false);
        }
        else
        {
            throw new PlatformNotSupportedException("Self-update is not available on this OS.");
        }
    }

    /// <summary>Downloads <see cref="UpdateInfo.DownloadUrl"/> to <paramref name="fetchPath"/> and
    /// checks size and digest. Deletes the file and throws on any mismatch.</summary>
    private static async Task DownloadAndVerifyAsync(UpdateInfo info, string fetchPath)
    {
        using (var timeout = new CancellationTokenSource(DownloadTimeout))
        using (HttpResponseMessage response = await HttpClient
                   .GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                   .ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            await using Stream source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using (FileStream destination = File.Create(fetchPath))
            {
                try
                {
                    await source.CopyToAsync(destination, timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    throw new IOException($"Download of {info.Version} did not finish within {DownloadTimeout.TotalMinutes:0} minutes.");
                }
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
            TryDelete(fetchPath);
            throw new IOException(
                $"Downloaded update for {info.Version} was {downloaded.Length} bytes, expected {expectedSize} (truncated download).");
        }

        bool? digestVerified = await AssetDigest.VerifyAsync(fetchPath, info.Digest).ConfigureAwait(false);
        if (digestVerified == false)
        {
            TryDelete(fetchPath);
            throw new IOException($"Downloaded update for {info.Version} failed SHA-256 verification.");
        }

        if (digestVerified is null)
        {
            // Fail open: no digest in the payload (see AssetDigest for why fail-closed adds nothing).
            FileLog.Write($"RoeSnip: update to {info.Version} has no verifiable digest in the release payload - skipping hash check.");
        }
    }

    /// <summary>Claims <paramref name="gate"/> right before the first swap step; throws when the run
    /// was abandoned, so nothing on disk changes after the caller gave up.</summary>
    private static void BeginSwap(SwapGate? gate, Version version)
    {
        if (gate is not null && !gate.TryBeginSwap())
        {
            throw new OperationCanceledException($"Update to {version} was abandoned before the swap started.");
        }
    }

    private static async Task ApplyUpdateWindowsAsync(UpdateInfo info, Func<Task>? beforeLaunch, SwapGate? swapGate)
    {
        string downloadPath = DownloadingExePath; // the decompressed exe the swap uses
        string fetchPath = info.IsGzip ? GzDownloadingExePath : downloadPath;
        // Set when the swap-in AND its rollback both failed: keep the one verified asset on disk.
        bool preserveDownload = false;
        try
        {
            Directory.CreateDirectory(InstallDir);
            TryDelete(downloadPath);
            if (fetchPath != downloadPath)
            {
                TryDelete(fetchPath);
            }

            await DownloadAndVerifyAsync(info, fetchPath).ConfigureAwait(false);

            if (info.IsGzip)
            {
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

                // The .gz digest proves the transit bytes; this proves the exe about to be swapped in.
                bool? plainVerified = await AssetDigest.VerifyAsync(downloadPath, info.PlainDigest).ConfigureAwait(false);
                if (plainVerified == false)
                {
                    TryDelete(downloadPath);
                    throw new IOException($"Decompressed update for {info.Version} failed SHA-256 verification.");
                }

                if (plainVerified is null)
                {
                    FileLog.Write($"RoeSnip: decompressed update to {info.Version} has no verifiable plain-asset digest - skipping the post-decompression hash check.");
                }
            }

            BeginSwap(swapGate, info.Version);

            // Retried: a prior .old can still be locked by a process that is only now exiting.
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
                // Roll back; the rollback itself can hit the same transient AV lock, so it is
                // guarded separately and a last-resort retry of the forward move follows.
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

            // Written by THIS process (it knows a swap just happened) before the new one starts.
            UpdateHealthMarker.RecordPendingUpdate(HealthMarkerDirectory, FormatVersionText(info.Version));
            LaunchInstalledCopy();
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

    private static async Task ApplyUpdateLinuxAsync(UpdateInfo info, Func<Task>? beforeLaunch, SwapGate? swapGate)
    {
        string stagedPath = DownloadingExePath; // RoeSnip.AppImage.new, next to the target
        try
        {
            Directory.CreateDirectory(InstallDir);
            TryDelete(stagedPath);

            await DownloadAndVerifyAsync(info, stagedPath).ConfigureAwait(false);
            MakeExecutable(stagedPath);

            BeginSwap(swapGate, info.Version);
            // The running AppImage keeps its mounted inode after the rename to ".old".
            InstallSwap.SwapIn(stagedPath, InstallTargetPath, StaleExePath);

            if (beforeLaunch is not null)
            {
                await beforeLaunch().ConfigureAwait(false);
            }

            UpdateHealthMarker.RecordPendingUpdate(HealthMarkerDirectory, FormatVersionText(info.Version));
            LaunchInstalledCopy();
        }
        catch (Exception ex)
        {
            TryDelete(stagedPath);
            FileLog.Write($"RoeSnip: update to {info.Version} failed: {ex.Message}");
            throw;
        }
    }

    private static async Task ApplyUpdateMacAsync(UpdateInfo info, Func<Task>? beforeLaunch, SwapGate? swapGate)
    {
        string tarPath = GzDownloadingExePath;
        string stagedBundle = DownloadingExePath; // ~/Applications/.RoeSnip-update/RoeSnip.app
        try
        {
            Directory.CreateDirectory(InstallDir);
            TryDelete(tarPath);
            TryDelete(MacStagingDir);

            await DownloadAndVerifyAsync(info, tarPath).ConfigureAwait(false);

            Directory.CreateDirectory(MacStagingDir);
            RunTool("/usr/bin/tar", "-xzf", tarPath, "-C", MacStagingDir);
            TryDelete(tarPath);
            if (!File.Exists(UpdateInstallLayout.MacBundleExecutable(stagedBundle)))
            {
                throw new IOException($"Update archive for {info.Version} did not contain {UpdateInstallLayout.MacBundleName}.");
            }

            // Idle gate BEFORE the swap (see this method group's doc comment).
            if (beforeLaunch is not null)
            {
                await beforeLaunch().ConfigureAwait(false);
            }

            BeginSwap(swapGate, info.Version);
            InstallSwap.SwapIn(stagedBundle, InstallTargetPath, StaleExePath);
            TryDelete(MacStagingDir);
            StripQuarantine(InstallTargetPath);

            UpdateHealthMarker.RecordPendingUpdate(HealthMarkerDirectory, FormatVersionText(info.Version));
            LaunchInstalledCopy();
        }
        catch (Exception ex)
        {
            TryDelete(tarPath);
            TryDelete(MacStagingDir);
            FileLog.Write($"RoeSnip: update to {info.Version} failed: {ex.Message}");
            throw;
        }
    }
}
