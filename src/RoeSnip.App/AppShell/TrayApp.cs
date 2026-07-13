using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Win32;
using RoeSnip.Core.Imaging;
using RoeSnip.Core.Settings;

namespace RoeSnip.App.AppShell;

/// <summary>The tray-app shell (PLAN-XPLAT.md §3.2): an Avalonia <see cref="TrayIcon"/> with a
/// native menu, the SharpHook global hotkey, single-instance enforcement (mutex + pipe via
/// <see cref="SingleInstance"/>), the Windows PrintScreen consent flow, and the
/// <see cref="ITrayNotifier"/> notifications the capture flow surfaces back through this class.
/// The tray icon is STRICTLY optional (DESIGN-XPLAT.md): a failure to create it is caught and
/// logged, and startup (hotkey, pipe listener) proceeds — the app stays fully operable via the
/// CLI verbs alone.</summary>
public sealed class TrayApp : ITrayNotifier
{
    private const string PrintScreenRegistryKeyPath = @"Control Panel\Keyboard";
    private const string PrintScreenValueName = "PrintScreenKeyForSnippingEnabled";

    private static TrayApp? s_current;
    private static InstanceSignal s_initialAction = InstanceSignal.None;
    private static bool s_automationEnabled;

    private readonly IClassicDesktopStyleApplicationLifetime _lifetime;

    private RoeSnipSettings _settings = RoeSnipSettings.Default;
    private HotkeyManager? _hotkeyManager;
    private TrayIcon? _trayIcon;
    private CancellationTokenSource? _pipeListenerCts;
    private SettingsWindow? _openSettingsWindow;
    private AutomationServer? _automationServer;
    private bool _exiting;

    private TrayApp(IClassicDesktopStyleApplicationLifetime lifetime)
    {
        _lifetime = lifetime;
    }

    /// <summary>Runs the tray app (the no-args launch path). A plain launch while another instance
    /// is already running REPLACES that instance with this one (item 13a, WPF TrayApp.cs:46-133
    /// semantics) — it asks the old one to exit, waits for it to release the single-instance lock
    /// (force-terminating it as a last resort, see SingleInstance.KillOtherInstances), then takes
    /// over. That way "just run the exe" always means "run the latest build", never "poke whatever
    /// is already running".</summary>
    public static int Run(string[] args)
    {
        if (Array.IndexOf(args, "--self-update-now") >= 0)
        {
            // One-shot "force update now": check GitHub and, if there is a newer release, download
            // + swap it and let the new build take over via replace-on-run, then exit — no tray
            // icon, no single-instance mutex (so it never fights the running instance the new
            // build replaces). See RunSelfUpdateNow.
            return RunSelfUpdateNow();
        }

        return RunResident(InstanceSignal.None);
    }

    /// <summary>The shared resident-instance entry: acquire the single-instance lock, then start
    /// the Avalonia lifetime; once the framework is up, <paramref name="initialAction"/> is
    /// performed (the "become the resident instance and do the requested thing" half of the bare
    /// CLI verbs, PLAN-XPLAT.md §3.2/§6 flag 4). <see cref="InstanceSignal.None"/> (a plain launch)
    /// takes over any running instance in place (item 13a); a real verb (TriggerCapture/
    /// TriggerSettings) just signals the running instance to do that one thing without
    /// replacing it, and this process exits.</summary>
    internal static int RunResident(InstanceSignal initialAction)
    {
        var instanceLock = SingleInstance.TryAcquire();
        if (instanceLock is null)
        {
            if (initialAction != InstanceSignal.None)
            {
                // An explicit CLI verb — just ask the resident instance to do that thing; this
                // process exits immediately, no takeover.
                SingleInstance.SignalExistingInstance(initialAction);
                return 0;
            }

            instanceLock = SingleInstance.TryTakeOver(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(2));
            if (instanceLock is null)
            {
                Console.Error.WriteLine("RoeSnip: could not take over from the running instance; leaving it in place.");
                return 0;
            }
        }

        s_initialAction = initialAction;
        // Dev-gated automation channel (AppShell/AutomationServer.cs): read from the REAL process
        // args (not whatever CliOptions parsed args into) so ROESNIP_AUTOMATION=1 or a literal
        // `--automation` argument gates it the same way regardless of which entry point led here —
        // a bare `RoeSnip.exe --automation` launch (args = ["--automation"]) and `RoeSnip capture`/
        // `RoeSnip settings` with the env var set both need this to agree.
        s_automationEnabled = AutomationServer.IsRequested(Environment.GetCommandLineArgs());
        try
        {
            int exitCode = Program.BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(Array.Empty<string>());
            return exitCode;
        }
        finally
        {
            s_current?._automationServer?.Stop();
            s_current?._pipeListenerCts?.Cancel();
            s_current = null;
            instanceLock.Dispose();
        }
    }

    /// <summary>Called by App.OnFrameworkInitializationCompleted once Avalonia is up on the
    /// resident path.</summary>
    internal static void OnFrameworkReady(IClassicDesktopStyleApplicationLifetime lifetime)
    {
        s_current = new TrayApp(lifetime);
        s_current.Start();
    }

    private void Start()
    {
        _settings = AppComposition.LoadSettingsOrDefault();

        // Tray icon first, then pipe listener, then the (potentially modal) consent flow — the
        // same ordering rationale as the WPF app: an unanswered first-launch dialog must not leave
        // the app invisible and unsignallable.
        CreateTrayIconDefensively();

        _pipeListenerCts = new CancellationTokenSource();
        _ = SingleInstance.ListenForSignalsAsync(OnInstanceSignal, _pipeListenerCts.Token);

        if (s_automationEnabled)
        {
            // Dev-gated automation channel (AppShell/AutomationServer.cs) for driving this app
            // deterministically from agents/E2E instead of synthetic mouse/UIA. TriggerCapture is
            // passed by reference (not re-implemented) so a `trigger` command runs the exact same
            // path the tray icon/hotkey/single-instance signal already do.
            _automationServer = new AutomationServer(TriggerCapture);
            _automationServer.Start();
        }

        // Self-update (item 13b): best-effort cleanup of any leftover .old/.new from a prior
        // install/update swap, then — only when this IS the installed copy — a silent background
        // check for a newer GitHub release that, if found, downloads and applies it automatically
        // (see CheckForUpdatesOnStartupAsync for the idle-wait guard on the relaunch that
        // follows). Windows-only, per the class's own doc comment; Linux/macOS instead get a
        // passive "new version available" notice with no self-swap (item 13d).
        if (OperatingSystem.IsWindows())
        {
            UpdateManager.CleanupStaleUpdateFiles();
            // Background cleanup that may need a bounded retry (a still-locked file must never
            // stall startup): the source exe a prior Install() "move" left behind AND the ".old" a
            // just-applied update swapped out. A named method (not an inline lambda) so the
            // platform-compat analyzer can see this Task.Run reference is itself directly inside
            // the IsWindows() guard above — it cannot see through an anonymous method body.
            _ = Task.Run(RunPendingUpdateFileCleanup);
            if (UpdateManager.IsInstalled)
            {
                _ = CheckForUpdatesOnStartupAsync();
            }
        }
        else
        {
            _ = CheckForNewVersionPassivelyAsync();
        }

        Dispatcher.UIThread.Post(() => _ = CompleteStartupAsync());
    }

    [SupportedOSPlatform("windows")]
    private static void RunPendingUpdateFileCleanup()
    {
        UpdateManager.ProcessPendingSourceCleanup();
        UpdateManager.CleanupStaleExeWithRetry();
    }

    private async Task CompleteStartupAsync()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                _settings = await ResolvePrintScreenConsentAsync(_settings);
            }

            _hotkeyManager = new HotkeyManager(() => Dispatcher.UIThread.Post(TriggerCapture));
            _hotkeyManager.Register(_settings);
            MaybeShowWaylandHotkeyNotice();

            switch (s_initialAction)
            {
                case InstanceSignal.TriggerCapture:
                    TriggerCapture();
                    break;
                case InstanceSignal.TriggerSettings:
                    OpenSettings();
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: startup did not fully complete: {ex.Message}");
        }
    }

    /// <summary>The one-time Wayland no-global-hotkey notice: fires only when HotkeyManager
    /// determined the hook can never start on this session (libuiohook is X11-only) and the flag
    /// hasn't already been shown. The flag is persisted immediately after the toast is raised, so
    /// a crash-loop during startup can never show it more than once ever.</summary>
    private void MaybeShowWaylandHotkeyNotice()
    {
        if (_hotkeyManager is null || !_hotkeyManager.IsUnavailableOnWayland || _settings.WaylandHotkeyNoticeShown)
        {
            return;
        }

        ShowToast(
            "Global hotkeys aren't available on Wayland — bind a keyboard shortcut to `RoeSnip capture` " +
            "in your desktop settings",
            isError: false,
            durationMs: 8000,
            onClick: null);

        var updated = _settings with { WaylandHotkeyNoticeShown = true };
        try
        {
            SettingsStore.Save(updated);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: failed to persist the Wayland hotkey notice flag: {ex.Message}");
        }
        _settings = updated;
    }

    private void OnInstanceSignal(InstanceSignal signal)
    {
        Dispatcher.UIThread.Post(() =>
        {
            switch (signal)
            {
                case InstanceSignal.TriggerCapture:
                    TriggerCapture();
                    break;
                case InstanceSignal.TriggerSettings:
                    OpenSettings();
                    break;
                case InstanceSignal.Exit:
                    // Replace-on-run (item 13a): a plain launch elsewhere is taking over from us —
                    // exit cleanly so it can acquire the single-instance mutex.
                    ExitApplication();
                    break;
            }
        });
    }

    private void TriggerCapture()
    {
        // Fire-and-forget from a UI event handler: RunCaptureFlowAsync reports its own failures
        // via ITrayNotifier (this) internally, but the task itself must still be observed so an
        // unexpected exception can't become an unobserved-task-exception crash later.
        _ = ObserveCaptureTask(AppComposition.RunCaptureFlowAsync(_settings, this));
    }

    private async Task ObserveCaptureTask(Task captureTask)
    {
        try
        {
            await captureTask;
        }
        catch (Exception ex)
        {
            ShowError($"Capture failed: {ex.Message}");
        }
    }

    private void OpenSettings()
    {
        if (_openSettingsWindow is not null)
        {
            _openSettingsWindow.Activate();
            return;
        }

        bool hotkeyUnavailableOnWayland = _hotkeyManager?.IsUnavailableOnWayland == true;
        var window = new SettingsWindow(_settings, hotkeyUnavailableOnWayland, updated => _ = ApplyUpdatedSettingsAsync(updated));
        window.Closed += (_, _) => _openSettingsWindow = null;
        _openSettingsWindow = window;
        window.Show();
    }

    private async Task ApplyUpdatedSettingsAsync(RoeSnipSettings updated)
    {
        _settings = updated;
        try
        {
            // Same resolved path as startup: if the saved hotkey is bare PrintScreen, run it
            // through the consent resolution before re-arming. PrintScreenPromptAnswered will
            // already be true after the first-ever resolution, so this cannot re-trigger the
            // prompt then — it only fires the one-time dialog in the edge case where the user
            // just switched to bare PrtScr for the first time.
            if (OperatingSystem.IsWindows())
            {
                _settings = await ResolvePrintScreenConsentAsync(_settings);
            }
            _hotkeyManager?.Register(_settings);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: failed to re-register the hotkey: {ex.Message}");
        }
    }

    private void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;

        _pipeListenerCts?.Cancel();
        _hotkeyManager?.Dispose();
        if (_trayIcon is not null)
        {
            try
            {
                _trayIcon.IsVisible = false;
                _trayIcon.Dispose();
            }
            catch { /* best-effort teardown */ }
        }

        _lifetime.Shutdown();
    }

    // ---------------- Self-update (item 13b/13d) ----------------

    /// <summary>Backs the hidden --self-update-now flag: synchronously checks GitHub for a newer
    /// release and, if there is one, downloads + swaps it (UpdateManager.ApplyUpdateAsync, which
    /// then launches the new build so replace-on-run hands off to it) before returning. No tray
    /// icon and no single-instance mutex are ever created here — this process's only job is to
    /// perform the swap and exit, leaving the freshly-launched new build as the running instance.
    /// Windows-only (self-update has no portable swap strategy — item 13d); other OSes print a
    /// pointer to the release page instead. Returns 0 on "updated"/"already current"/"not
    /// applicable here", 1 only if the check/download/swap itself errored; never throws.</summary>
    private static int RunSelfUpdateNow()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Out.WriteLine(
                "RoeSnip: --self-update-now is only available on Windows; grab the latest release " +
                "manually from the release page on other platforms.");
            return 0;
        }

        try
        {
            if (!UpdateManager.IsInstalled)
            {
                // Only the installed copy should force-update itself — see UpdateManager.IsInstalled's
                // own doc comment for why a portable/dev copy has nothing sensible to swap itself for.
                Console.Out.WriteLine($"RoeSnip: --self-update-now applies only to the installed copy ({UpdateManager.InstalledExePath}).");
                return 0;
            }

            UpdateManager.UpdateInfo? update = UpdateManager.CheckForUpdateAsync().GetAwaiter().GetResult();
            if (update is null)
            {
                Console.Out.WriteLine($"RoeSnip: already up to date (current {UpdateManager.CurrentVersion}).");
                return 0;
            }

            Console.Out.WriteLine($"RoeSnip: updating {UpdateManager.CurrentVersion} -> {update.Version}...");
            UpdateManager.ApplyUpdateAsync(update).GetAwaiter().GetResult();
            Console.Out.WriteLine($"RoeSnip: updated to {update.Version}; new build launched.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: self-update failed: {ex.Message}");
            return 1;
        }
    }

    [SupportedOSPlatform("windows")]
    private void OnInstallRoeSnipClicked(object? sender, EventArgs e) => InstallRoeSnip();

    [SupportedOSPlatform("windows")]
    private void OnCheckForUpdatesClicked(object? sender, EventArgs e) => _ = CheckForUpdatesFromMenuAsync();

    /// <summary>The "Install RoeSnip" context-menu item (Windows only, only shown when
    /// <see cref="UpdateManager.InstallExists"/> is false): copies this portable/dev run into
    /// %LOCALAPPDATA%\RoeSnip.App and hands off to it, then exits this process so the installed
    /// copy takes over. Runs off the UI thread (file copy + registry write) and never lets a
    /// failure take the tray down with it: UpdateManager.Install rethrows on failure, so the catch
    /// here shows an error toast and the exit is skipped, leaving the tray running as it was.</summary>
    [SupportedOSPlatform("windows")]
    private void InstallRoeSnip()
    {
        _ = Task.Run(() =>
        {
            try
            {
                UpdateManager.Install();
                Dispatcher.UIThread.Post(ExitApplication);
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => ShowError($"Install failed: {ex.Message}"));
            }
        });
    }

    /// <summary>Silent startup update check (Windows only; only called when
    /// <see cref="UpdateManager.IsInstalled"/> is true). Says nothing when there is nothing to
    /// offer. When a newer release does exist, applies it automatically — no click required — but
    /// the beforeLaunch delegate passed to ApplyUpdateAsync holds the actual relaunch (and the
    /// replace-on-run kill that follows it) until <see cref="WaitForIdleAsync"/> reports idle, so
    /// an update landing mid-snip can never yank the app out from under the user. Falls back to
    /// the click-to-update toast if the auto-apply throws. Never blocks startup: fire-and-forget
    /// from Start().</summary>
    [SupportedOSPlatform("windows")]
    private async Task CheckForUpdatesOnStartupAsync()
    {
        UpdateManager.UpdateInfo? update = await UpdateManager.CheckForUpdateAsync().ConfigureAwait(false);
        if (update is null)
        {
            return;
        }

        Console.Error.WriteLine($"RoeSnip: auto-updating {UpdateManager.CurrentVersion} -> {update.Version}...");
        try
        {
            await UpdateManager.ApplyUpdateAsync(update, WaitForIdleAsync).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: auto-update to {update.Version} failed: {ex.Message}");
            Dispatcher.UIThread.Post(() => ShowUpdateAvailableToast(update));
        }
    }

    /// <summary>Polled rather than event-driven: capture sessions are short-lived (seconds) and
    /// AppComposition exposes no completion signal, so a coarse poll is the simplest thing that
    /// cannot miss a state change. Once a Recording subsystem lands (item 20) this must also poll
    /// its own "active" flag, mirroring the WPF reference's RecordingController.IsActive check.</summary>
    private static async Task WaitForIdleAsync()
    {
        while (AppComposition.IsCaptureBusy)
        {
            await Task.Delay(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        }
    }

    /// <summary>The "Check for updates" context-menu item (Windows only): unlike the silent
    /// startup check, this always reports back via a toast/dialog rather than staying silent — a
    /// deliberate, occasional, user-initiated click should always get a visible answer (up to
    /// date / update offered / check failed).</summary>
    [SupportedOSPlatform("windows")]
    private async Task CheckForUpdatesFromMenuAsync()
    {
        UpdateManager.UpdateInfo? update;
        try
        {
            update = await UpdateManager.CheckForUpdateAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: update check failed (non-fatal): {ex.Message}");
            Dispatcher.UIThread.Post(() => ShowToast(
                "Could not check for updates - GitHub could not be reached.", isError: true, durationMs: 6000, onClick: null));
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (update is not null)
            {
                _ = OfferUpdateAsync(update);
            }
            else
            {
                ShowToast($"RoeSnip {UpdateManager.CurrentVersionText} is up to date.", isError: false, durationMs: 4000, onClick: null);
            }
        });
    }

    /// <summary>Must already be on the UI thread. Asks Yes/No (Avalonia has no blocking
    /// MessageBox — see ShowYesNoDialogAsync) and applies immediately on Yes.</summary>
    [SupportedOSPlatform("windows")]
    private async Task OfferUpdateAsync(UpdateManager.UpdateInfo update)
    {
        bool? answer = await ShowYesNoDialogAsync(
            "RoeSnip",
            $"RoeSnip {update.Version} is available (you have {UpdateManager.CurrentVersionText}). Update now?");
        if (answer == true)
        {
            _ = ApplyUpdateFromToastAsync(update);
        }
    }

    /// <summary>Shows the click-to-update toast (must already be on the UI thread). Startup checks
    /// apply updates automatically without asking (CheckForUpdatesOnStartupAsync); this toast is
    /// only reached as its failure fallback, so the user still has a way to update by hand if the
    /// automatic download/swap failed.</summary>
    [SupportedOSPlatform("windows")]
    private void ShowUpdateAvailableToast(UpdateManager.UpdateInfo info)
    {
        ShowToast(
            $"RoeSnip {info.Version} is available. Click to update.",
            isError: false,
            durationMs: 8000,
            onClick: () => _ = ApplyUpdateFromToastAsync(info));
    }

    /// <summary>Runs off the UI thread (download + file swap); on success the new build takes over
    /// via replace-on-run, so this instance simply gets told to exit shortly afterwards — nothing
    /// further to do here. On failure, surfaces it as an error toast instead of letting it vanish
    /// into an unobserved task.</summary>
    [SupportedOSPlatform("windows")]
    private async Task ApplyUpdateFromToastAsync(UpdateManager.UpdateInfo info)
    {
        try
        {
            await UpdateManager.ApplyUpdateAsync(info).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => ShowError($"Update to {info.Version} failed: {ex.Message}"));
        }
    }

    /// <summary>Linux/macOS (item 13d): no install directory, no exe swap strategy that can be
    /// tested here (see docs/PARITY.md's accepted-limitations list) — just a passive heads-up that
    /// a newer release exists, linking straight to the GitHub release page so the user can grab it
    /// themselves (an AppImage/.dmg download, same as their first install). Never auto-applies
    /// anything.</summary>
    private async Task CheckForNewVersionPassivelyAsync()
    {
        UpdateManager.UpdateInfo? update = await UpdateManager.CheckForUpdateAsync().ConfigureAwait(false);
        if (update is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => ShowToast(
            $"RoeSnip {update.Version} is available - click to view the release",
            isError: false,
            durationMs: 8000,
            onClick: () => OpenUrl(update.ReleaseUrl)));
    }

    // ---------------- PrintScreen / Snipping Tool consent (Windows only) ----------------

    /// <summary>The one-time PrintScreen/Snipping-Tool consent flow, ported from the WPF app's
    /// TrayApp.ResolvePrintScreenConsent (PLAN-XPLAT.md §3.2: "ports unchanged, and
    /// Windows-only"). Applicable only when the configured hotkey is bare PrintScreen. On
    /// Windows 11 an ABSENT HKCU\Control Panel\Keyboard\PrintScreenKeyForSnippingEnabled value
    /// means the Snipping Tool intercept is ON by default, so only an explicitly-present 0 counts
    /// as "no conflict". The registry write happens ONLY as the direct result of an interactive
    /// Yes click. Either answer is persisted with PrintScreenPromptAnswered=true so the dialog
    /// truly happens once ever; dismissing the dialog without answering leaves settings untouched
    /// (asked again next launch). Any registry or dialog failure logs and returns the settings
    /// unchanged (fail open on the UX) without marking the prompt as answered.
    /// The WPF MessageBox became a small Avalonia Yes/No dialog — Avalonia has no built-in
    /// MessageBox; behavior is otherwise identical.</summary>
    [SupportedOSPlatform("windows")]
    private async Task<RoeSnipSettings> ResolvePrintScreenConsentAsync(RoeSnipSettings settings)
    {
        if (settings.HotkeyModifiers != 0 || settings.HotkeyVirtualKey != HotkeyManager.VkSnapshot)
        {
            return settings; // consent flow only applies to the bare-PrintScreen hotkey
        }

        try
        {
            bool interceptExplicitlyDisabled;
            using (var key = Registry.CurrentUser.OpenSubKey(PrintScreenRegistryKeyPath, writable: false))
            {
                interceptExplicitlyDisabled = key?.GetValue(PrintScreenValueName) switch
                {
                    int i => i == 0,
                    string s when int.TryParse(s, out int parsed) => parsed == 0,
                    // Missing key/value (or unparseable) — Win11 default is "intercept ON", so
                    // this must NOT be treated as "no conflict".
                    _ => false,
                };
            }

            if (interceptExplicitlyDisabled)
            {
                return settings; // Windows isn't intercepting a bare PrtScr; nothing to do.
            }

            if (settings.PrintScreenPromptAnswered)
            {
                // Asked once before; honor whatever the persisted answer produced.
                return settings;
            }

            bool? answer = await ShowYesNoDialogAsync(
                "RoeSnip - PrintScreen hotkey",
                "Windows is currently set to open Snipping Tool when you press PrintScreen, which " +
                "would prevent RoeSnip's screenshot hotkey from receiving it.\n\n" +
                "Disable that Windows setting so PrintScreen triggers RoeSnip directly?\n\n" +
                "Choosing \"No\" leaves the Windows setting alone and makes RoeSnip use " +
                "Ctrl+PrintScreen instead.");

            if (answer is null)
            {
                // Dialog dismissed without an answer — don't persist anything; ask again next time.
                return settings;
            }

            RoeSnipSettings updated;
            if (answer == true)
            {
                try
                {
                    using var writableKey = Registry.CurrentUser.OpenSubKey(PrintScreenRegistryKeyPath, writable: true);
                    writableKey?.SetValue(PrintScreenValueName, 0, RegistryValueKind.DWord);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        $"RoeSnip: failed to disable the Snipping Tool PrintScreen intercept: {ex.Message}");
                }
                updated = settings with { PrintScreenPromptAnswered = true };
            }
            else
            {
                updated = settings with
                {
                    HotkeyModifiers = HotkeyManager.ModControl,
                    PrintScreenPromptAnswered = true,
                };
            }

            try
            {
                SettingsStore.Save(updated);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"RoeSnip: failed to persist the PrintScreen consent answer: {ex.Message}");
            }

            return updated;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"RoeSnip: PrintScreen consent check failed, keeping PrintScreen-alone: {ex.Message}");
            return settings;
        }
    }

    /// <summary>Minimal ownerless Yes/No dialog (Avalonia has no MessageBox). Returns true for
    /// Yes, false for No, null if closed without answering.</summary>
    private static Task<bool?> ShowYesNoDialogAsync(string title, string message)
    {
        var tcs = new TaskCompletionSource<bool?>();
        bool? answer = null;

        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(16),
        };

        var yesButton = new Button { Content = "Yes", MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        var noButton = new Button { Content = "No", MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(16, 0, 16, 16),
        };
        buttons.Children.Add(yesButton);
        buttons.Children.Add(noButton);

        var root = new StackPanel();
        root.Children.Add(text);
        root.Children.Add(buttons);

        var window = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            Topmost = true,
            ShowInTaskbar = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = root,
        };

        yesButton.Click += (_, _) => { answer = true; window.Close(); };
        noButton.Click += (_, _) => { answer = false; window.Close(); };
        window.Closed += (_, _) => tcs.TrySetResult(answer);
        window.Show();
        window.Activate();

        return tcs.Task;
    }

    private void ShowAbout()
    {
        try
        {
            var text = new TextBlock
            {
                Text =
                    $"RoeSnip {UpdateManager.CurrentVersionText}\n\n" +
                    "An HDR-correct screenshot tool. On HDR/Advanced-Color displays, RoeSnip captures the " +
                    "true linear scRGB frame and tone-maps it properly (matching the SDR white level and " +
                    "rolling off highlights) instead of producing the washed-out gray screenshots typical " +
                    "of legacy capture tools.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(16),
            };
            var okButton = new Button
            {
                Content = "OK",
                MinWidth = 80,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(16, 0, 16, 16),
            };
            var root = new StackPanel();
            root.Children.Add(text);
            root.Children.Add(okButton);

            var window = new Window
            {
                Title = "About RoeSnip",
                Width = 440,
                SizeToContent = SizeToContent.Height,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Content = root,
            };
            okButton.Click += (_, _) => window.Close();
            window.Show();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: failed to show the About window: {ex.Message}");
        }
    }

    // ---------------- ITrayNotifier (toast windows — Avalonia TrayIcon has no balloons) ----------------

    /// <inheritdoc/>
    public void ShowSavedBalloon(string filePath)
    {
        Dispatcher.UIThread.Post(() => ShowToast(
            $"Saved {Path.GetFileName(filePath)} — click to open folder",
            isError: false,
            durationMs: 4000,
            onClick: () => OpenContainingFolder(filePath)));
    }

    /// <inheritdoc/>
    public void ShowError(string message)
    {
        Console.Error.WriteLine($"RoeSnip: {message}");
        Dispatcher.UIThread.Post(() => ShowToast(message, isError: true, durationMs: 6000, onClick: null));
    }

    /// <inheritdoc/>
    public void ShowShareUploadedBalloon(string url, bool clipboardCopied)
    {
        // Senior-review-style honesty (mirrors the WPF app's own ShowShareUploadedBalloon): only
        // claims "copied the link" when the clipboard write actually succeeded.
        string message = clipboardCopied
            ? "Uploaded and copied the link - click to open it."
            : $"Uploaded: {url} (clipboard was busy) - click to open it.";
        Dispatcher.UIThread.Post(() => ShowToast(
            message,
            isError: false,
            durationMs: 4000,
            onClick: () => OpenUrl(url)));
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: failed to open {url}: {ex.Message}");
        }
    }

    /// <summary>A small topmost, non-activating toast window near the bottom-right of the primary
    /// screen's work area — the Avalonia stand-in for the WPF NotifyIcon balloons (Avalonia's
    /// TrayIcon has no notification API). Positioned BEFORE Show() (physical pixels, primary
    /// screen only — never repositioned after showing, per the mixed-DPI discipline). Any failure
    /// here is swallowed after logging; toasts are best-effort UX, never load-bearing.</summary>
    private void ShowToast(string message, bool isError, int durationMs, Action? onClick)
    {
        try
        {
            var text = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.White,
                Margin = new Thickness(14, 10),
                MaxLines = 3,
            };

            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)),
                BorderBrush = new SolidColorBrush(isError
                    ? Color.FromRgb(0xC0, 0x50, 0x50)
                    : Color.FromRgb(0x4A, 0x9E, 0xFF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Child = text,
            };

            const double toastWidth = 360;
            const double toastHeight = 84;

            var window = new Window
            {
                WindowDecorations = WindowDecorations.None,
                Topmost = true,
                ShowInTaskbar = false,
                ShowActivated = false,
                CanResize = false,
                Width = toastWidth,
                Height = toastHeight,
                Content = border,
                Background = Brushes.Transparent,
                TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent },
            };

            // Physical-pixel positioning from the primary screen's work area, set BEFORE Show().
            var primary = window.Screens.Primary;
            if (primary is not null)
            {
                double scale = primary.Scaling;
                var wa = primary.WorkingArea;
                int px = wa.X + wa.Width - (int)Math.Round((toastWidth + 16) * scale);
                int py = wa.Y + wa.Height - (int)Math.Round((toastHeight + 16) * scale);
                window.Position = new PixelPoint(px, py);
            }

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(durationMs) };
            timer.Tick += (_, _) => { timer.Stop(); window.Close(); };

            window.PointerPressed += (_, _) =>
            {
                timer.Stop();
                window.Close();
                onClick?.Invoke();
            };
            window.Closed += (_, _) => timer.Stop();

            window.Show();
            timer.Start();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: toast notification failed ({ex.Message}); message was: {message}");
        }
    }

    private static void OpenContainingFolder(string filePath)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = true });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo("open", $"-R \"{filePath}\""));
            }
            else
            {
                string? dir = Path.GetDirectoryName(filePath);
                if (dir is not null)
                {
                    Process.Start(new ProcessStartInfo("xdg-open", $"\"{dir}\""));
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: failed to open folder for {filePath}: {ex.Message}");
        }
    }

    // ---------------- Tray icon (programmatic — no asset files needed) ----------------

    private void CreateTrayIconDefensively()
    {
        try
        {
            var menu = new NativeMenu();

            var captureItem = new NativeMenuItem("Capture");
            captureItem.Click += (_, _) => TriggerCapture();
            var settingsItem = new NativeMenuItem("Settings...");
            settingsItem.Click += (_, _) => OpenSettings();
            var aboutItem = new NativeMenuItem("About");
            aboutItem.Click += (_, _) => ShowAbout();
            var exitItem = new NativeMenuItem("Exit");
            exitItem.Click += (_, _) => ExitApplication();

            menu.Items.Add(captureItem);
            menu.Items.Add(settingsItem);
            menu.Items.Add(aboutItem);

            // Self-update (item 13b) is a Windows-only feature (install-to-LOCALAPPDATA + the
            // atomic exe swap have no portable equivalent) — Linux/macOS get a passive background
            // notice instead (CheckForNewVersionPassivelyAsync), no menu items.
            if (OperatingSystem.IsWindows())
            {
                menu.Items.Add(new NativeMenuItemSeparator());
                if (!UpdateManager.InstallExists)
                {
                    // Only offered until an install actually exists on disk (evaluated once, here,
                    // at menu-build time — same as the WPF reference). Gating on InstallExists
                    // (not IsInstalled) means a rebuilt dev copy / fresh download / replace-on-run
                    // takeover of an already-installed app does NOT re-offer "Install". A named
                    // handler (not an inline lambda) — see RunPendingUpdateFileCleanup's comment
                    // for why the platform-compat analyzer needs this shape here.
                    var installItem = new NativeMenuItem("Install RoeSnip");
                    installItem.Click += OnInstallRoeSnipClicked;
                    menu.Items.Add(installItem);
                }
                var checkUpdatesItem = new NativeMenuItem("Check for updates");
                checkUpdatesItem.Click += OnCheckForUpdatesClicked;
                menu.Items.Add(checkUpdatesItem);
            }

            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(exitItem);

            _trayIcon = new TrayIcon
            {
                Icon = CreateTrayIconImage(),
                ToolTipText = $"RoeSnip {UpdateManager.CurrentVersionText}",
                Menu = menu,
                IsVisible = true,
            };
            _trayIcon.Clicked += (_, _) => TriggerCapture();

            TrayIcon.SetIcons(Application.Current!, new TrayIcons { _trayIcon });
        }
        catch (Exception ex)
        {
            _trayIcon = null;
            // Tray is STRICTLY optional (DESIGN-XPLAT.md) — e.g. Linux StatusNotifier may simply
            // not render one. Never let this stop the hotkey/pipe-listener startup.
            Console.Error.WriteLine(
                $"RoeSnip: tray icon unavailable ({ex.Message}). The app remains fully operable via " +
                "the global hotkey and the `RoeSnip capture` / `RoeSnip settings` CLI verbs.");
        }
    }

    /// <summary>Draws the same stylized camera glyph as the WPF app's tray icon (accent-blue body,
    /// flash bump, dark lens) into a 32x32 BGRA raster, PNG-encodes it via Core's PngWriter, and
    /// wraps it as a WindowIcon — no image assets, no System.Drawing dependency.</summary>
    private static WindowIcon CreateTrayIconImage()
    {
        const int size = 32;
        var pixels = new byte[size * 4 * size];

        void FillRect(int x0, int y0, int w, int h, byte r, byte g, byte b)
        {
            for (int y = y0; y < y0 + h; y++)
            {
                if (y < 0 || y >= size) continue;
                for (int x = x0; x < x0 + w; x++)
                {
                    if (x < 0 || x >= size) continue;
                    int o = (y * size + x) * 4;
                    pixels[o + 0] = b;
                    pixels[o + 1] = g;
                    pixels[o + 2] = r;
                    pixels[o + 3] = 255;
                }
            }
        }

        void FillEllipse(double cx, double cy, double rx, double ry, byte r, byte g, byte b)
        {
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    double dx = (x + 0.5 - cx) / rx;
                    double dy = (y + 0.5 - cy) / ry;
                    if (dx * dx + dy * dy <= 1.0)
                    {
                        int o = (y * size + x) * 4;
                        pixels[o + 0] = b;
                        pixels[o + 1] = g;
                        pixels[o + 2] = r;
                        pixels[o + 3] = 255;
                    }
                }
            }
        }

        // 2x scale of the WPF 16x16 glyph: body (1,4,14,10), bump (5,1,6,3), lens ellipse.
        FillRect(2, 8, 28, 20, 0x4A, 0x9E, 0xFF);   // camera body, accent blue
        FillRect(10, 2, 12, 6, 0x4A, 0x9E, 0xFF);   // flash bump
        FillEllipse(16, 18, 8, 6, 0x1E, 0x1E, 0x1E); // lens (dark)
        FillEllipse(16, 18, 4, 3, 0x4A, 0x9E, 0xFF); // lens inner highlight

        var image = new SdrImage(size, size, pixels);
        return new WindowIcon(new MemoryStream(PngWriter.Encode(image)));
    }
}

file static class ModuleInit
{
#pragma warning disable CA2255 // App assembly, not a general-purpose library — the Phase 1 pattern.
    [System.Runtime.CompilerServices.ModuleInitializer]
#pragma warning restore CA2255
    internal static void Init()
    {
        AppComposition.RunTrayApp = TrayApp.Run;
        AppComposition.RunResidentWithInitialAction = TrayApp.RunResident;
    }
}
