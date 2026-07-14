using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using RoeSnip.Core.Updates;

namespace RoeSnip.App;

/// <summary>Install-to-%LOCALAPPDATA% + self-update from GitHub Releases. RoeSnip can be run
/// straight from wherever it was downloaded to (a portable exe); <see cref="Install"/> is the
/// one-time "make this the run-at-startup copy" step, and <see cref="CheckForUpdateAsync"/> /
/// <see cref="ApplyUpdateAsync"/> keep that installed copy current. Both the install hand-off and
/// the update hand-off rely on the SAME replace-on-run behavior TrayApp.Run already implements:
/// launching RoeSnip.exe when another instance holds the single-instance mutex asks that instance
/// to exit and takes over, so all this class has to do is copy/swap the exe on disk and
/// Process.Start the installed path - it does not need to terminate the current process itself.
///
/// Every public entry point here is a convenience, never load-bearing: a private repo (404 until
/// roe69/roesnip goes public), no network, a truncated download, or a locked file must never
/// throw out into the tray or leave %LOCALAPPDATA%\RoeSnip without a runnable RoeSnip.exe. Not
/// covered by an automated test for the same reason StartupManager isn't - it mutates the real
/// registry/filesystem and talks to a real HTTP endpoint; the file/registry logic is reviewed by
/// eye instead (see docs/PARITY.md's Notes section for how that manual review is tracked).</summary>
public static class UpdateManager
{
    private const string GitHubOwner = "roe69";
    private const string GitHubRepo = "roesnip";
    private const string ReleaseAssetName = "RoeSnip.exe";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "RoeSnip";

    // One shared HttpClient for the process lifetime (never one-per-call - see MSDN's socket
    // exhaustion guidance) with the User-Agent GitHub's API mandates and rejects requests without.
    private static readonly HttpClient HttpClient = CreateHttpClient();

    // One shared instance for the process lifetime, matching HttpClient above - its in-memory ETag
    // is exactly what makes a periodic tray-resident check cheap (see the class's own doc comment):
    // a fresh instance every call would throw the ETag away and turn every periodic check back into
    // a full, uncached GET.
    private static readonly GitHubLatestReleaseClient ReleaseClient = new(GitHubOwner, GitHubRepo);

    // Serializes ApplyUpdateAsync across callers: the silent startup auto-update can be parked in
    // its beforeLaunch idle-wait for minutes with the new exe already swapped in, and without this
    // a concurrent manual "Check for updates" click would download into the same DownloadingExePath
    // (IOException) and, worse, launch (and kill this process via replace-on-run) with no idle check
    // of its own - defeating the whole point of the startup path's guard. Queueing here means a
    // manual click during that window simply waits; it completes only after the parked call's own
    // launch has already ended the process.
    private static readonly SemaphoreSlim ApplyUpdateLock = new(1, 1);

    public static string InstallDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RoeSnip");

    public static string InstalledExePath { get; } = Path.Combine(InstallDir, "RoeSnip.exe");

    private static string StaleExePath => InstalledExePath + ".old";
    private static string DownloadingExePath => Path.Combine(InstallDir, "RoeSnip.exe.new");
    private static string PendingSourceCleanupMarkerPath => Path.Combine(InstallDir, "pending-source-cleanup.txt");

    /// <summary>The running build's version, straight off the assembly (set by the csproj's
    /// &lt;Version&gt;) - compared against each release's tag_name.</summary>
    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);

    /// <summary>"1.0.4"-shaped display text for <see cref="CurrentVersion"/> - the SDK always fills
    /// in a Revision component (always 0 for this project's x.y.z &lt;Version&gt; scheme), which
    /// reads as noise ("1.0.4.0") anywhere this is shown to the user (About box, tray tooltip,
    /// update-check result). Matches the release tag's own x.y.z shape exactly.</summary>
    public static string CurrentVersionText
    {
        get
        {
            Version v = CurrentVersion;
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    /// <summary>True when an installed copy already exists on disk
    /// (%LOCALAPPDATA%\RoeSnip\RoeSnip.exe), regardless of whether THIS process is that copy. Gates
    /// the one-time "Install RoeSnip" tray item: once an install exists it must not be re-offered,
    /// even when the user happens to be running a rebuilt dev copy, a fresh download, or a copy that
    /// took over the running install via replace-on-run (all of which leave <see cref="IsInstalled"/>
    /// false while the app is, to the user, plainly already installed).</summary>
    public static bool InstallExists
    {
        get
        {
            try
            {
                return File.Exists(InstalledExePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <summary>True when this process is already running FROM the installed copy
    /// (%LOCALAPPDATA%\RoeSnip\RoeSnip.exe). Update-checks only make sense when it's true (a
    /// portable/dev copy has nothing sensible to swap itself for); the "Install RoeSnip" item is
    /// gated on <see cref="InstallExists"/> instead, not this.</summary>
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
        // class) deterministically fails the ~186 MB Windows asset download below ~14.4 Mbps
        // sustained - a slow-but-working connection would then retry-and-fail every hourly check
        // forever. 15 minutes comfortably covers that download on very ordinary consumer upstream
        // while still eventually giving up on a truly hung connection.
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RoeSnip-Updater");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    /// <summary>Best-effort delete of update leftovers (a ".old" from a prior swap that's unlocked
    /// once that older process exited, and any ".new" abandoned by a download that never
    /// completed). Called once, synchronously, at startup - a single fast attempt that clears both
    /// in the common case (a normal launch, where any prior process is long gone). The just-updated
    /// case, where the replaced process is still exiting and holding its renamed ".old" locked, is
    /// covered by <see cref="CleanupStaleExeWithRetry"/> on a background thread. Never throws.</summary>
    public static void CleanupStaleUpdateFiles()
    {
        TryDelete(StaleExePath);
        TryDelete(DownloadingExePath);
    }

    /// <summary>Background bounded-retry delete of the ".old" exe a prior update swapped out. The
    /// synchronous <see cref="CleanupStaleUpdateFiles"/> above tries once, but right after an update
    /// hand-off the just-replaced process can still be exiting and holding its renamed exe locked,
    /// so that first attempt misses and a full ~170 MB copy would otherwise sit in the install dir
    /// until the NEXT launch. This retries briefly so the swap frees its old artefact the same
    /// session and the install dir never keeps more than the one live exe. Never throws; call on a
    /// background thread (the retry wait must never stall startup).</summary>
    public static void CleanupStaleExeWithRetry()
    {
        TryDeleteWithRetry(StaleExePath);
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
            Console.Error.WriteLine($"RoeSnip: could not clean up stale update file '{path}' (non-fatal): {ex.Message}");
        }
    }

    /// <summary>Deletes <paramref name="path"/> with a short bounded retry - a file just released by
    /// a sibling process that is only now exiting (a swapped-out ".old" exe, or the source exe a
    /// "move" install left behind) can stay briefly locked. ~2 s total, bounded so a file that never
    /// unlocks can't stall the caller; never throws. Call on a background thread.</summary>
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

    /// <summary>Copies the currently-running exe to %LOCALAPPDATA%\RoeSnip, points the HKCU Run
    /// key + RunAtStartup setting at that copy, and launches it - the installed copy then takes
    /// over via replace-on-run (TrayApp.Run's single-instance signal), so this call never needs to
    /// terminate the caller itself. Every failure is caught and logged; a failed install just
    /// leaves the caller running exactly as before.</summary>
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
            // never observable half-written: a crash/kill mid-copy would otherwise leave a truncated
            // RoeSnip.exe that still satisfies InstallExists (permanently hiding the "Install" item)
            // yet is not runnable, with no in-app repair path. Same swap discipline ApplyUpdateAsync
            // uses for updates.
            string stagingPath = DownloadingExePath;
            TryDelete(stagingPath);
            File.Copy(currentExe, stagingPath, overwrite: true);

            TryDelete(StaleExePath);
            bool movedExisting = false;
            if (File.Exists(InstalledExePath))
            {
                // Rename any existing install out of the way first - works even when a running
                // installed instance holds it locked (renaming a running exe is allowed on Windows;
                // that process keeps running against its now-renamed handle until it exits, and
                // CleanupStaleExeWithRetry deletes the .old once it does).
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
                RoeSnipSettings settings = AppComposition.LoadSettings?.Invoke() ?? RoeSnipSettings.Default;
                SettingsStore.Save(settings with { RunAtStartup = true });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"RoeSnip: install could not persist the run-at-startup setting (non-fatal): {ex.Message}");
            }

            // Make install behave like a MOVE, not a copy: the source exe (currentExe, confirmed
            // above to differ from InstalledExePath) can't delete itself while it's still running,
            // so record it for the installed copy to clean up on its next startup, once the
            // replace-on-run handoff below has this process exit and release its file lock.
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
            // Rethrow so the caller (TrayApp) keeps running and shows an error instead of exiting the
            // tray on a silent failure. The install is best-effort up to here: if the copy failed the
            // portable exe is untouched and any prior installed copy still runs, so surfacing the
            // error and staying put is the correct outcome.
            Console.Error.WriteLine($"RoeSnip: install failed: {ex.Message}");
            throw;
        }
    }

    /// <summary>Best-effort cleanup of the source exe left behind by a prior <see cref="Install"/>
    /// "move" (see the marker written there): reads <see cref="PendingSourceCleanupMarkerPath"/> if
    /// present and, only when the recorded path exists, is a file literally named "RoeSnip.exe",
    /// and is NOT <see cref="InstalledExePath"/>, deletes it with a short bounded retry (the old
    /// process may still be releasing its file lock right after the replace-on-run handoff). The
    /// marker is removed afterward regardless of whether the delete succeeded. Runs on a background
    /// thread (see the Task.Run at the call site) so a locked file's retry loop can never stall
    /// startup, and never throws into the tray.</summary>
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
    /// Environment.ProcessPath - here the CALLER is still the non-installed process, so the key
    /// has to be pointed at the installed copy explicitly instead.</summary>
    private static void SetInstalledRunAtStartup()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        key?.SetValue(RunValueName, $"\"{InstalledExePath}\"", RegistryValueKind.String);
    }

    /// <summary>The latest GitHub release worth updating to: its version, the direct download URL
    /// for the "RoeSnip.exe" asset, and that same asset's "digest"/"size" fields straight off the
    /// releases payload (used by <see cref="ApplyUpdateAsync"/> to verify the download - see
    /// AssetDigest's doc comment). <see cref="Digest"/> is null whenever the payload didn't carry
    /// one (an older GitHub API response shape, or genuinely absent) - callers fail open on that,
    /// never fail closed.</summary>
    public sealed record UpdateInfo(Version Version, string DownloadUrl, string? Digest = null, long? Size = null);

    /// <summary>Checks the GitHub Releases API for a newer published build. Returns null (never
    /// throws) whenever there's nothing to offer: the repo is private (404), there's no network,
    /// the response doesn't parse, the release has no "RoeSnip.exe" asset, its version isn't newer
    /// than <see cref="CurrentVersion"/>, or GitHub answered 304/rate-limited.
    ///
    /// Routed through <see cref="ReleaseClient"/> (see GitHubLatestReleaseClient's own doc comment
    /// for the full contract): a conditional If-None-Match request that comes back 304 costs nothing
    /// against GitHub's unauthenticated rate limit, which is what makes an hourly-by-default periodic
    /// loop (see TrayApp.RunUpdateLoopAsync) cheap. The ETag is committed ONLY when this method is
    /// about to return null because there's genuinely no update - never when a payload parsed to a
    /// real update, and never on a rate-limited/failed probe - so a stored ETag always safely means
    /// "304 = still no update"; if an update was found but its later download/apply failed, the next
    /// check gets a full uncached GET and a real chance to retry rather than silently 304'ing forever.
    /// <paramref name="bypassBackoff"/> forces a live network attempt even inside an active
    /// rate-limit backoff window - the manual "Check for updates" menu item passes true because a
    /// deliberate user click deserves a real answer.</summary>
    public static async Task<UpdateInfo?> CheckForUpdateAsync(bool bypassBackoff = false)
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
            JsonElement root = document.RootElement;
            UpdateInfo? update = ParsePayload(root, CurrentVersion);
            if (update is null)
            {
                // No update found in this payload - commit the ETag so the NEXT check can 304 for
                // free. See this method's doc comment for why an update-found-but-not-applied path
                // must never reach this line.
                ReleaseClient.CommitETag(probe.ETag);
            }

            return update;
        }
        catch (Exception ex)
        {
            // A private repo (404), a network failure, or a malformed response all mean the same
            // thing to the caller: no update available right now. Never let any of it crash the
            // tray - log to stderr and move on.
            Console.Error.WriteLine($"RoeSnip: update check failed (non-fatal): {ex.Message}");
            return null;
        }
    }

    /// <summary>Pure parse of one "releases/latest" JSON payload - split out of
    /// <see cref="CheckForUpdateAsync"/> unchanged from its previous inline form, just relocated so
    /// that method can decide whether to commit the probe's ETag based on this method's result.
    /// Public (this repo's convention for making a testable slice unit-testable without an
    /// InternalsVisibleTo edit - see e.g. RecordingController.StartAsync, AutomationServer,
    /// ToolCursorCache) and takes <paramref name="currentVersion"/> as a parameter rather than
    /// reading the static <see cref="CurrentVersion"/> directly, so tests can exercise version
    /// gating without depending on the running assembly's own version. The sole call site above
    /// passes <see cref="CurrentVersion"/>.</summary>
    public static UpdateInfo? ParsePayload(JsonElement root, Version currentVersion)
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

        if (!root.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? downloadUrl = null;
        string? digest = null;
        long? size = null;
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

        if (string.IsNullOrEmpty(downloadUrl) || releaseVersion <= currentVersion)
        {
            return null;
        }

        return new UpdateInfo(releaseVersion, downloadUrl, digest, size);
    }

    /// <summary>Downloads the release exe, swaps it in for <see cref="InstalledExePath"/>, and
    /// launches it - the new build then takes over via replace-on-run, which kills this process. A
    /// truncated/failed download never touches the installed exe; a failure partway through the swap
    /// rolls back to the previous exe (from the ".old" it just renamed) so the install is never left
    /// without a runnable copy. <paramref name="beforeLaunch"/> is awaited after the swap-in
    /// succeeds and right before Process.Start - the swap itself is safe to do while the app is busy
    /// (a running exe can be renamed out from under it), but the launch is not: replace-on-run would
    /// kill this instance mid-snip or mid-recording. Callers driven by an explicit user click
    /// (menu Yes, balloon click, --self-update-now) pass null and apply as soon as it is their turn;
    /// the silent startup auto-update passes a delegate that waits for the app to go idle first.
    /// Calls are serialized on <see cref="ApplyUpdateLock"/> so a manual click landing while the
    /// startup path is parked in its idle-wait cannot download into the same in-progress file or
    /// launch (and kill this process) ahead of that guard - it queues behind it instead. Rethrows on
    /// failure so the caller can surface it to the user - this method never leaves an exception
    /// unswallowed on its own.</summary>
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

    private static async Task ApplyUpdateCoreAsync(UpdateInfo info, Func<Task>? beforeLaunch)
    {
        string downloadPath = DownloadingExePath;
        // Set when the swap-in fails AND the rollback-to-previous-exe recovery also fails: the
        // outer catch's usual "always delete downloadPath" cleanup would otherwise throw away the
        // one surviving, already digest-verified asset on a disk that has no runnable exe left at
        // all - see the inner catch below for the full recovery chain this guards.
        bool preserveDownload = false;
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
                        Console.Error.WriteLine($"RoeSnip: rollback to the previous exe failed: {rollbackEx.Message}");
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
                        Console.Error.WriteLine($"RoeSnip: last-resort swap-in also failed, leaving the update download in place for manual recovery: {fallbackEx.Message}");
                    }
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
            if (!preserveDownload)
            {
                TryDelete(downloadPath);
            }

            Console.Error.WriteLine($"RoeSnip: update to {info.Version} failed: {ex.Message}");
            throw;
        }
    }
}
