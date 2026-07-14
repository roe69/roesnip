using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
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
    private static string PendingSourceCleanupMarkerPath => Path.Combine(InstallDir, "pending-source-cleanup.txt");

    /// <summary>The running build's version, straight off the assembly (set by the csproj's
    /// &lt;Version&gt;) — compared against each release's tag_name.</summary>
    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);

    /// <summary>"1.0.4"-shaped display text for <see cref="CurrentVersion"/> — the SDK always
    /// fills in a Revision component (always 0 for this project's x.y.z &lt;Version&gt; scheme),
    /// which reads as noise ("1.0.4.0") anywhere this is shown to the user (About window, tray
    /// tooltip, update-check result). Matches the release tag's own x.y.z shape exactly. Portable
    /// (no OS check) — item 13c surfaces this in About/tooltip on every OS.</summary>
    public static string CurrentVersionText
    {
        get
        {
            Version v = CurrentVersion;
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

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
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RoeSnip.App-Updater");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    // ---------------- portable: version check (every OS) ----------------

    /// <summary>The latest GitHub release worth telling the user about: its version, the direct
    /// download URL for the Windows asset (null when this OS/release has none — never used off
    /// Windows), the release page URL (what the Linux/macOS passive notice links to), and that same
    /// Windows asset's "digest"/"size" fields straight off the releases payload (used by
    /// <see cref="ApplyUpdateAsync"/> to verify the download - see AssetDigest's doc comment).
    /// <see cref="Digest"/> is null whenever the payload didn't carry one - callers fail open on
    /// that, never fail closed.</summary>
    public sealed record UpdateInfo(Version Version, string? DownloadUrl, string ReleaseUrl, string? Digest = null, long? Size = null);

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
                    Console.Error.WriteLine("RoeSnip: update check up to date (304, conditional request, does not count against the GitHub rate limit).");
                    return null;
                case ProbeStatus.RateLimited:
                    Console.Error.WriteLine("RoeSnip: update check skipped - GitHub rate limit backoff is active.");
                    return null;
                case ProbeStatus.Failed:
                    Console.Error.WriteLine($"RoeSnip: update check failed (non-fatal): {probe.Detail}");
                    return null;
            }

            using JsonDocument document = probe.Json!;
            UpdateInfo? update = ParseUpdateInfo(document.RootElement, CurrentVersion, requireWindowsAsset: OperatingSystem.IsWindows());
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
            Console.Error.WriteLine($"RoeSnip: update check failed (non-fatal): {ex.Message}");
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
    /// and ReleaseUrl.</summary>
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

        string? downloadUrl = null;
        string? digest = null;
        long? size = null;
        if (root.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                if (asset.TryGetProperty("name", out JsonElement nameElement) &&
                    string.Equals(nameElement.GetString(), ReleaseAssetName, StringComparison.OrdinalIgnoreCase) &&
                    asset.TryGetProperty("browser_download_url", out JsonElement urlElement))
                {
                    downloadUrl = urlElement.GetString();
                    if (asset.TryGetProperty("digest", out JsonElement digestElement))
                    {
                        digest = digestElement.GetString();
                    }

                    if (asset.TryGetProperty("size", out JsonElement sizeElement) && sizeElement.TryGetInt64(out long sizeValue))
                    {
                        size = sizeValue;
                    }

                    break;
                }
            }
        }

        if (releaseVersion <= currentVersion)
        {
            return null;
        }

        if (requireWindowsAsset && string.IsNullOrEmpty(downloadUrl))
        {
            return null;
        }

        return new UpdateInfo(releaseVersion, downloadUrl, releaseUrl, digest, size);
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
    }

    /// <summary>Background bounded-retry delete of the ".old" exe a prior update swapped out —
    /// right after an update hand-off the just-replaced process can still be exiting and holding
    /// its renamed exe locked, so <see cref="CleanupStaleUpdateFiles"/>'s single synchronous
    /// attempt can miss it. Never throws; call on a background thread.</summary>
    [SupportedOSPlatform("windows")]
    public static void CleanupStaleExeWithRetry() => TryDeleteWithRetry(StaleExePath);

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
            Console.Error.WriteLine($"RoeSnip: could not clean up stale update file '{path}' (non-fatal): {ex.Message}");
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
                    Console.Error.WriteLine($"RoeSnip: could not delete '{path}' after retries (non-fatal): {ex.Message}");
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
                Console.Error.WriteLine("RoeSnip: install failed - could not determine the current executable path.");
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
                Console.Error.WriteLine($"RoeSnip: install could not set run-at-startup (non-fatal): {ex.Message}");
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
                Console.Error.WriteLine($"RoeSnip: install could not persist the run-at-startup setting (non-fatal): {ex.Message}");
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
                Console.Error.WriteLine($"RoeSnip: could not record source exe for post-install cleanup (non-fatal): {ex.Message}");
            }

            Process.Start(new ProcessStartInfo(InstalledExePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: install failed: {ex.Message}");
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
            Console.Error.WriteLine($"RoeSnip: post-install source cleanup failed (non-fatal): {ex.Message}");
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

        string downloadPath = DownloadingExePath;
        try
        {
            Directory.CreateDirectory(InstallDir);
            TryDelete(downloadPath);

            using (HttpResponseMessage response = await HttpClient
                       .GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead)
                       .ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using Stream source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                await using (FileStream destination = File.Create(downloadPath))
                {
                    await source.CopyToAsync(destination).ConfigureAwait(false);
                }
            }

            var downloaded = new FileInfo(downloadPath);
            if (!downloaded.Exists || downloaded.Length == 0)
            {
                TryDelete(downloadPath);
                throw new IOException($"Downloaded update for {info.Version} was empty or missing.");
            }

            if (info.Size is long expectedSize && downloaded.Length != expectedSize)
            {
                // Cheap truncation catch ahead of the hash - a short/long download is almost always
                // a broken transfer, and saying so plainly beats a generic "digest mismatch".
                TryDelete(downloadPath);
                throw new IOException(
                    $"Downloaded update for {info.Version} was {downloaded.Length} bytes, expected {expectedSize} (truncated download).");
            }

            bool? digestVerified = await AssetDigest.VerifyAsync(downloadPath, info.Digest).ConfigureAwait(false);
            if (digestVerified == false)
            {
                // Bytes don't match GitHub's own published sha256 for this asset - corruption or a
                // tampered download-CDN path. Never swap this into the install; the caller's normal
                // failure path (log + rethrow) leaves the current exe untouched and retries next cycle.
                TryDelete(downloadPath);
                throw new IOException($"Downloaded update for {info.Version} failed SHA-256 verification.");
            }

            if (digestVerified is null)
            {
                // The release payload had no usable "digest" field (older GitHub API shape, or
                // genuinely absent) - fail OPEN rather than block updates on a field this project
                // doesn't control. See AssetDigest's doc comment for why fail-closed here wouldn't
                // add real security anyway (digest and URL travel the same channel).
                Console.Error.WriteLine($"RoeSnip: update to {info.Version} has no verifiable digest in the release payload - skipping hash check.");
            }

            TryDelete(StaleExePath);

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
                // Roll back: never leave the install without a runnable exe.
                if (renamedCurrent && !File.Exists(InstalledExePath) && File.Exists(StaleExePath))
                {
                    File.Move(StaleExePath, InstalledExePath);
                }

                throw;
            }

            if (beforeLaunch is not null)
            {
                await beforeLaunch().ConfigureAwait(false);
            }

            Process.Start(new ProcessStartInfo(InstalledExePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            TryDelete(downloadPath);
            Console.Error.WriteLine($"RoeSnip: update to {info.Version} failed: {ex.Message}");
            throw;
        }
    }
}
