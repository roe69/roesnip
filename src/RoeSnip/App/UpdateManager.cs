using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

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
/// eye instead (see PLAN's update-spec).</summary>
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

    /// <summary>True when this process is already running FROM the installed copy
    /// (%LOCALAPPDATA%\RoeSnip\RoeSnip.exe) - the "Install RoeSnip" tray item only makes sense
    /// when this is false, and update-checks only make sense when it's true (a portable/dev copy
    /// has nothing sensible to swap itself for).</summary>
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
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RoeSnip-Updater");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    /// <summary>Best-effort delete of update leftovers (a ".old" from a prior swap that's unlocked
    /// once that older process exited, and any ".new" abandoned by a download that never
    /// completed). Called once at startup; never throws.</summary>
    public static void CleanupStaleUpdateFiles()
    {
        TryDelete(StaleExePath);
        TryDelete(DownloadingExePath);
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

            if (File.Exists(InstalledExePath))
            {
                try
                {
                    File.Copy(currentExe, InstalledExePath, overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Locked by a running installed instance - rename it out of the way first
                    // (renaming a running exe is allowed on Windows; that process keeps running
                    // against its now-unnamed handle until it exits and CleanupStaleUpdateFiles
                    // deletes the .old on a later startup), then copy the current build in fresh.
                    TryDelete(StaleExePath);
                    File.Move(InstalledExePath, StaleExePath);
                    File.Copy(currentExe, InstalledExePath, overwrite: true);
                }
            }
            else
            {
                File.Copy(currentExe, InstalledExePath, overwrite: false);
            }

            try
            {
                SetInstalledRunAtStartup();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"RoeSnip: install could not set run-at-startup (non-fatal): {ex.Message}");
            }

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
                const int maxAttempts = 10;
                const int retryDelayMs = 200; // ~2s total, bounded so a still-locked file never stalls startup
                for (int attempt = 0; attempt < maxAttempts; attempt++)
                {
                    try
                    {
                        File.Delete(sourcePath);
                        break;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        if (attempt == maxAttempts - 1)
                        {
                            Console.Error.WriteLine($"RoeSnip: could not delete old source exe '{sourcePath}' after install (non-fatal): {ex.Message}");
                            break;
                        }

                        Thread.Sleep(retryDelayMs);
                    }
                }
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

    /// <summary>The latest GitHub release worth updating to: its version and the direct download
    /// URL for the "RoeSnip.exe" asset.</summary>
    public sealed record UpdateInfo(Version Version, string DownloadUrl);

    /// <summary>Checks the GitHub Releases API for a newer published build. Returns null (never
    /// throws) whenever there's nothing to offer: the repo is private (404), there's no network,
    /// the response doesn't parse, the release has no "RoeSnip.exe" asset, or its version isn't
    /// newer than <see cref="CurrentVersion"/>.</summary>
    public static async Task<UpdateInfo?> CheckForUpdateAsync()
    {
        try
        {
            string url = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";
            using HttpResponseMessage response = await HttpClient.GetAsync(url).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"RoeSnip: update check got HTTP {(int)response.StatusCode} from GitHub.");
                return null;
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using JsonDocument document = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            JsonElement root = document.RootElement;

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
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                if (asset.TryGetProperty("name", out JsonElement nameElement) &&
                    string.Equals(nameElement.GetString(), ReleaseAssetName, StringComparison.OrdinalIgnoreCase) &&
                    asset.TryGetProperty("browser_download_url", out JsonElement urlElement))
                {
                    downloadUrl = urlElement.GetString();
                    break;
                }
            }

            if (string.IsNullOrEmpty(downloadUrl) || releaseVersion <= CurrentVersion)
            {
                return null;
            }

            return new UpdateInfo(releaseVersion, downloadUrl);
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

    /// <summary>Downloads the release exe, swaps it in for <see cref="InstalledExePath"/>, and
    /// launches it - the new build then takes over via replace-on-run. A truncated/failed download
    /// never touches the installed exe; a failure partway through the swap rolls back to the
    /// previous exe (from the ".old" it just renamed) so the install is never left without a
    /// runnable copy. Rethrows on failure so the caller (tray menu / balloon click) can surface it
    /// to the user - this method never leaves an exception unswallowed on its own.</summary>
    public static async Task ApplyUpdateAsync(UpdateInfo info)
    {
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
