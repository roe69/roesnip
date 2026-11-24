using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Win32;
using RoeSnip.App.Overlay;
using RoeSnip.Core.Capture;
using RoeSnip.Core.Clipboard;
using RoeSnip.Core.Diagnostics;
using RoeSnip.Core.Imaging;
using RoeSnip.Core.Settings;
using RoeSnip.Core.Updates;

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

    // Item 16 design tokens for the toast window built below — literal Color fields rather than an
    // App.axaml resource lookup, since ShowToast constructs its whole visual tree in code (same
    // convention WPF's own Recording/RecordingChrome.cs uses for its in-code DangerFill/DangerSolid
    // constants). Values mirror App.axaml's RlBgElevatedBrush/RlTextPrimaryBrush/RlPrimaryGoldBrush/
    // RlDangerHoverBrush exactly, so a toast reads as the same near-black+orange product as every
    // XAML surface instead of the old generic dark-gray-with-a-blue-accent look.
    private static readonly Color ToastBackground = Color.FromRgb(0x18, 0x18, 0x1D); // RlBgElevatedBrush
    private static readonly Color ToastText = Color.FromRgb(0xED, 0xED, 0xF0); // RlTextPrimaryBrush
    private static readonly Color ToastAccentBorder = Color.FromRgb(0xFF, 0x6B, 0x35); // RlPrimaryGoldBrush
    private static readonly Color ToastErrorBorder = Color.FromRgb(0xDC, 0x26, 0x26); // RlDangerHoverBrush

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
    // The kept-capture menu items and the menu they belong to, held so RefreshKeptCaptureItems can
    // add, re-label and remove them as captures come and go.
    private NativeMenu? _trayMenu;
    private NativeMenuItem? _copyAgainItem;
    private NativeMenuItem? _saveLastItem;
    private bool _exiting;
    private bool _shareRequestInFlight; // UI thread only
#if WINDOWS
    // Kept in a field so ExitApplication can DETACH it (SystemEvents is process-global): a
    // broadcast landing after teardown must not run a resume re-warm against a shut-down app.
    // Ported from the WPF app's identical field/detach pair; the App has no
    // DisplaySettingsChanged subscription to mirror (see docs/PARITY.md), so only this one
    // applies here.
    private PowerModeChangedEventHandler? _powerModeChangedHandler;
    // Dedup for OnSystemResumed: it can now fire from TWO sources for one wake (the
    // PowerRegisterSuspendResumeNotification callback — see Platform.Windows
    // SuspendResumeNotifications — and the SystemEvents belt). Environment.TickCount64 of the
    // last handled resume; 0 = never. Ported from the WPF app's identical field.
    private long _lastResumeHandledTick;
#endif

    // Latches the click-to-update fallback toast to once per release version: a persistently
    // failing download is retried every periodic tick (desirable - the network hiccup or the
    // asset could be transient), but re-showing the toast on every one of those retries would be
    // hourly notification spam for a problem the user has already been told about once. Only ever
    // touched from Windows-only, [SupportedOSPlatform("windows")]-guarded methods.
    private Version? _updateToastShownForVersion;

    // Linux/macOS passive-notice equivalent: toasts once per release so an hourly cadence
    // re-alerts on each NEW version without nagging about the same one every tick.
    private Version? _passiveNoticeShownForVersion;

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
            // build replaces). See RunSelfUpdateNow. Environment.Exit so no stray foreground
            // thread can keep this one-shot alive.
            int code = RunSelfUpdateNow();
            Console.Out.Flush();
            Environment.Exit(code);
            return code;
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
                FileLog.Write("RoeSnip: could not take over from the running instance; leaving it in place.");
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
        // Crash-loop guard (hardening item 7), first statement in this method: a bad release that
        // crashes at/near startup must be caught before ANYTHING else here gets a chance to also
        // crash (settings load, the tray icon itself). See
        // UpdateManager.CheckUpdateHealthAtStartup's own doc comment for the full contract; when it
        // restores and relaunches the previous build, this process's only remaining job is to get
        // out of the way immediately - no tray icon, no hotkey, nothing. Every copy that can
        // self-update (Windows, a Linux AppImage, a macOS .app bundle); a Linux tar.gz / dev folder
        // run never writes a pending-verify marker.
        bool selfUpdates = UpdateManager.SelfUpdateSupported;
        UpdateManager.HealthCheckAction healthAction = UpdateManager.HealthCheckAction.ProceedImmediateCleanup;
        if (selfUpdates)
        {
            healthAction = UpdateManager.CheckUpdateHealthAtStartup();
            if (healthAction == UpdateManager.HealthCheckAction.Restored)
            {
                _lifetime.Shutdown();
                return;
            }
        }

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
            // deterministically from agents/E2E instead of synthetic mouse/UIA. TriggerCapture and
            // the two settings-window hooks are all passed by reference (not re-implemented) so a
            // `trigger`/`settings` command runs the exact same path the tray icon/hotkey/
            // single-instance signal already do.
            _automationServer = new AutomationServer(
                TriggerCapture, OpenSettingsForAutomation, CloseSettingsForAutomation,
                CopyKeptCapture, SaveKeptCapture);
            AutomationServer.ShowToastHook = text => ShowToast(text, isError: false, durationMs: 6000, onClick: null);
            _automationServer.Start();
        }

        // Self-update (item 13b, periodic in this later pass): best-effort cleanup of any leftover
        // .old/.new from a prior install/update swap, then — only when this IS the installed copy
        // — a background loop that checks GitHub for a newer release and, if found, downloads and
        // applies it automatically (see CheckForUpdatesAndAutoApplyAsync for the idle-wait guard on
        // the relaunch that follows), then repeats on a user-configurable cadence for as long as
        // this process lives (a tray resident runs for weeks, so a startup-only check almost never
        // lands a release). Self-updating copies: Windows, a Linux AppImage, a macOS .app bundle.
        // A Linux tar.gz / dev folder run, and a not-yet-installed AppImage / bundle, get the
        // passive "new version available" notice loop instead.
        if (selfUpdates)
        {
            // Crash-loop guard: when this launch is still pending health verification
            // (ProceedDeferredCleanup), the ".old" rollback target must survive until the health
            // milestone below proves this launch isn't crash-looping - only the always-safe ".new"
            // download leftover is cleaned up now. The common case (ProceedImmediateCleanup) is
            // byte-for-byte what this did before the crash-loop guard existed.
            if (healthAction == UpdateManager.HealthCheckAction.ProceedImmediateCleanup)
            {
                UpdateManager.CleanupStaleUpdateFiles();
            }
            else
            {
                UpdateManager.CleanupDownloadLeftover();
            }
            // Background cleanup that may need a bounded retry (a still-locked file must never
            // stall startup): the source a prior Install() "move" left behind AND the ".old" a
            // just-applied update swapped out.
            _ = Task.Run(RunPendingUpdateFileCleanup);
            if (healthAction == UpdateManager.HealthCheckAction.ProceedImmediateCleanup)
            {
                _ = Task.Run(RunImmediateStaleExeCleanup);
            }
            else
            {
                // The health milestone (hardening item 7): this launch is a post-update
                // verification run.
                _ = RunHealthMilestoneAsync();
                if (UpdateManager.FirstLaunchAfterUpdate)
                {
                    // Updates install on their own (UpdateCheckFrequency); say so once, after the fact.
                    string version = UpdateManager.CurrentVersionText;
                    DispatcherTimer.RunOnce(
                        () => ShowToast(
                            $"RoeSnip updated to {version} - click for what's new",
                            isError: false,
                            durationMs: 8000,
                            onClick: () => OpenUrl(UpdateManager.ReleasePageUrl(version))),
                        TimeSpan.FromSeconds(2));
                }
            }
            if (UpdateManager.IsInstalled)
            {
                // Gated once here: what this process runs from cannot change while it lives.
                _ = RunUpdateLoopAsync();
            }
            else if (!OperatingSystem.IsWindows())
            {
                // A Linux AppImage / macOS bundle run before Install keeps the notice it had.
                _ = RunPassiveNoticeLoopAsync();
            }
        }
        else
        {
            _ = RunPassiveNoticeLoopAsync();
        }

        // Cold-start warmup (item 17, ported from the WPF app's TrayApp.StartWarmup/RunWarmup):
        // runs on its own background thread so it can never delay the tray icon/pipe listener
        // above coming up, and never blocks (or is blocked by) the PrintScreen consent flow below.
        StartWarmup();

        Dispatcher.UIThread.Post(() => _ = CompleteStartupAsync());
    }

    private static void RunPendingUpdateFileCleanup()
    {
        UpdateManager.ProcessPendingSourceCleanup();
        if (UpdateManager.IsInstalled)
        {
            // Startup ensure (not just install-time): a user-deleted shortcut / menu entry heals
            // itself. No-op when it is already right.
            if (OperatingSystem.IsWindows())
            {
                StartMenuShortcut.EnsureFor(UpdateManager.InstalledExePath);
            }
            else if (OperatingSystem.IsLinux())
            {
                LinuxDesktopIntegration.EnsureMenuEntry(UpdateManager.InstallTargetPath, UpdateManager.CurrentVersionText);
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Hardening item 10: reclaims the %TEMP%\.net\RoeSnip single-file self-extraction folders
        // every launch of this build leaves behind (never cleaned up by the .NET host itself; see
        // RoeSnip.Core.Updates.SelfExtractCleanup's own doc comment - this app and the WPF app share
        // the same %TEMP%\.net\RoeSnip parent, deliberately). No-op for a portable/dev run.
        // Unconditional (not gated on healthAction like the two blocks above it in the caller): it
        // only ever touches %TEMP%, never the install dir, so none of the "is this launch a verified
        // rollback target" concerns those cleanups exist for apply here.
        SelfExtractCleanup.CleanupSiblingExtractionDirs();
    }

    /// <summary>Crash-loop guard (hardening item 7): the ".old" retry-cleanup, split out of
    /// <see cref="RunPendingUpdateFileCleanup"/> so it can be skipped independently when this
    /// launch is still pending health verification (see the healthAction branch at the Start() call
    /// site).</summary>
    private static void RunImmediateStaleExeCleanup() => UpdateManager.CleanupStaleExeWithRetry();

    /// <summary>Crash-loop guard (hardening item 7): the health milestone, called directly (not via
    /// Task.Run - unlike RunPendingUpdateFileCleanup/RunImmediateStaleExeCleanup above, this method
    /// does no blocking I/O until AFTER its own await, so nothing here needs offloading off the
    /// calling thread) once a <see cref="UpdateManager.HealthCheckAction.ProceedDeferredCleanup"/>
    /// launch has been up for ~15s.</summary>
    private async Task RunHealthMilestoneAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        UpdateManager.CompleteHealthMilestone();
    }

    private async Task CompleteStartupAsync()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                _settings = await ResolvePrintScreenConsentAsync(_settings);
            }
            // Linux/GNOME: free the hotkey from gnome-shell before binding it (no prompt).
            _settings = GnomePrintScreenTakeover.ApplyTakeover(_settings, previous: null);

            _hotkeyManager = new HotkeyManager(() => Dispatcher.UIThread.Post(TriggerCapture));
            _hotkeyManager.Register(_settings);
            _hotkeyManager.WaylandStatusChanged += () => Dispatcher.UIThread.Post(MaybeShowWaylandHotkeyNotice);
            WarnIfPrintScreenConflict();
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
            FileLog.Write($"RoeSnip: startup did not fully complete: {ex.Message}");
        }
    }

    /// <summary>The one-time Wayland hotkey notice: shows HotkeyManager.WaylandHotkeyStatus (how the
    /// capture shortcut works on this session, or that none does) once, also when the status
    /// arrives late (WaylandStatusChanged, a portal bind answering after startup). The flag is persisted immediately after the toast is raised, so
    /// a crash-loop during startup can never show it more than once ever.</summary>
    private void MaybeShowWaylandHotkeyNotice()
    {
        if (_hotkeyManager?.WaylandHotkeyStatus is not string status || _settings.WaylandHotkeyNoticeShown)
        {
            return;
        }

        ShowToast(
            status,
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
            FileLog.Write($"RoeSnip: failed to persist the Wayland hotkey notice flag: {ex.Message}");
        }
        _settings = updated;
    }

    /// <summary>Wayland, a user's capture before instant capture was allowed: asks for the screen
    /// share (the Settings button's dialog) and captures from the stream once allowed. Never the
    /// Screenshot portal in its place (GNOME's shutter sound and screen flash).</summary>
    private async Task AskForScreenShareThenCaptureAsync()
    {
        if (_shareRequestInFlight)
        {
            FileLog.Write("RoeSnip: capture ignored: the screen-share dialog is still waiting for an answer");
            return;
        }
        _shareRequestInFlight = true;
        try
        {
            FileLog.Write($"RoeSnip: capture before instant capture was allowed ({InstantCaptureAccess.UnavailableReason}); asking for the screen share");
            ShowToast(
                "Allow RoeSnip to see your screens once: pick Entire screen / all monitors, then Share. " +
                "Next time PrintScreen is instant.",
                isError: false,
                durationMs: 10000,
                onClick: null);

            ScreenShareRequestResult result;
            try
            {
                result = await InstantCaptureAccess.RequestAsync("a capture");
            }
            catch (Exception ex)
            {
                FileLog.Write($"RoeSnip: screen-share request failed: {ex.Message}");
                result = ScreenShareRequestResult.Declined;
            }
            FileLog.Write($"RoeSnip: screen share for a capture: {result}");

            if (result == ScreenShareRequestResult.Declined)
            {
                ShowToast(
                    "No screenshot taken: screen access was not allowed. Press PrintScreen again, " +
                    "or use Settings > Allow instant capture.",
                    isError: true,
                    durationMs: 8000,
                    onClick: () => OpenSettings(focusInstantCapture: true));
                return;
            }
            // Allowed: the stream is open. Unsupported: the backend's policy decides (never GNOME's portal).
            TriggerCaptureCore(mayAskForShare: false);
        }
        finally
        {
            _shareRequestInFlight = false;
        }
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

    private void TriggerCapture() => TriggerCaptureCore(mayAskForShare: true);

    private void TriggerCaptureCore(bool mayAskForShare)
    {
        // A recording is live: PrtScr stops+saves it instead of starting a new capture flow (item
        // 21d, WPF reference TrayApp.cs:255-260). Deliberately BEFORE MarkTriggerTimestamp below: a
        // stop-triggering PrtScr is not a new capture trigger and must not stamp latency
        // instrumentation or touch the flash.
        if (Recording.RecordingOrchestrator.IsActive)
        {
            Recording.RecordingOrchestrator.RequestPrtScrAction();
            return;
        }

        // Wayland before instant capture is allowed: ask for the share first, then capture from the
        // stream. Before MarkTriggerTimestamp so capture-to-overlay measures the stream path.
        if (mayAskForShare && InstantCaptureAccess.CaptureMustAskFirst)
        {
            _ = AskForScreenShareThenCaptureAsync();
            return;
        }

        // Honest trigger-based latency instrumentation (item 18, ported from the WPF reference):
        // stamp the moment the ACTUAL trigger happened — before the flash, before capture, before
        // anything else — so every downstream latency log measures what the user really
        // experienced, not from whenever the flash happened to start (or didn't, if it's
        // disabled/unavailable/fails). Must be the first statement in this method.
        Overlay.OverlayController.MarkTriggerTimestamp();

        // Instant-response flash (item 18, Windows only — see Overlay/FlashDimmer.cs): dim every
        // monitor within milliseconds of the trigger, BEFORE RunCaptureFlowAsync starts the
        // pool-side capture (the dispatcher pump stays live during it — post-sleep stall fix; the
        // flash covers the capture latency visually). TryShowFlash itself no-ops (returns false) on
        // non-Windows and when ROESNIP_NO_FLASH=1 is set, so this is unconditionally safe to call —
        // those cases simply fall straight through to the direct capture-then-show path, which is
        // also the PERMANENT behavior on Linux/macOS. The ReleaseFlash in ObserveCaptureTask's
        // finally is the backstop that guarantees the flash never outlives the flow on any exit path
        // (capture failed on every monitor, CaptureGate busy, an unexpected exception). The flash no
        // longer touches OS foreground at all (capture-fidelity fix, 2026-08-02 — see
        // FlashDimmer.ShowAllCore's own comment at the removal site). This closes the ACTIVATION-
        // triggered dismissal path only (a tooltip/hover-popup that self-dismisses on
        // WM_ACTIVATE/WM_ACTIVATEAPP now survives); it does not guarantee every tooltip survives —
        // any topmost window placed over an already-hovering tooltip can still make that tooltip's
        // own hover-tracking self-dismiss via WM_MOUSELEAVE with no dependency on foreground at all.
        // See docs/CAPTURE-FIDELITY-SPEC.md item 1.
        bool flashShown = false;
        try
        {
            var flashWatch = Stopwatch.StartNew();
            var monitors = Volatile.Read(ref s_cachedMonitors) ?? new CaptureService().EnumerateMonitors();
            flashShown = Overlay.OverlayController.TryShowFlash(monitors);
            if (flashShown)
            {
                FileLog.Write($"RoeSnip: hotkey-to-dim {flashWatch.ElapsedMilliseconds} ms");
            }
        }
        catch (Exception ex)
        {
            // The flash is a pure perceived-latency optimization — its failure must never block the
            // actual capture.
            FileLog.Write($"RoeSnip: flash dimmer failed (non-fatal): {ex.Message}");
        }

        // Fire-and-forget from a UI event handler: RunCaptureFlowAsync reports its own failures
        // via ITrayNotifier (this) internally, but the task itself must still be observed so an
        // unexpected exception can't become an unobserved-task-exception crash later.
        _ = ObserveCaptureTask(AppComposition.RunCaptureFlowAsync(_settings, this), flashShown);
    }

    private async Task ObserveCaptureTask(Task captureTask, bool flashShown)
    {
        try
        {
            await captureTask;
        }
        catch (Exception ex)
        {
            ShowError($"Capture failed: {ex.Message}");
        }
        finally
        {
            if (flashShown)
            {
                Overlay.OverlayController.ReleaseFlash();
            }
            // Re-enumerate for the NEXT trigger's flash placement off the hot path (monitors may
            // have changed while the overlay was up).
            RefreshMonitorCacheInBackground();
        }
    }

    private void OpenSettings() => OpenSettings(focusInstantCapture: false);

    private void OpenSettings(bool focusInstantCapture)
    {
        if (_openSettingsWindow is not null)
        {
            _openSettingsWindow.Activate();
            if (focusInstantCapture)
            {
                _openSettingsWindow.FocusInstantCapture();
            }
            return;
        }

        var window = new SettingsWindow(
            _settings,
            _hotkeyManager?.WaylandHotkeyStatus,
            _hotkeyManager?.IsUnavailableOnWayland == true,
            updated => _ = ApplyUpdatedSettingsAsync(updated),
            // Bugs 2 & 5 (SettingsWindow's own field-level doc comment): suspend the hotkey only
            // for the (brief) span it is actively capturing a new combination; Unregister/Register
            // (not Dispose) so the SAME HotkeyManager instance/hook keeps running underneath.
            suspendGlobalHotkey: () => _hotkeyManager?.Unregister(),
            resumeGlobalHotkey: () => _hotkeyManager?.Register(_settings),
            // Item 15's "Restart elevated now": mirrors the WPF app's RestartElevatedNow exiting
            // its WinForms message loop so the elevated task can take over the single-instance
            // lock — this app's equivalent full teardown is ExitApplication (same path the tray
            // menu's Exit item and a replace-on-run signal already use).
            exitApplication: ExitApplication);
        window.Icon = AppIcon;
        window.Closed += (_, _) => _openSettingsWindow = null;
        _openSettingsWindow = window;
        window.Show();
        if (focusInstantCapture)
        {
            window.FocusInstantCapture();
        }
    }

    /// <summary>Automation-only entry point (AppShell/AutomationServer.cs's `settings`/`open`
    /// command), ported from the WPF app's own OpenSettingsForAutomation: calls the SAME
    /// <see cref="OpenSettings"/> the tray icon/menu uses rather than constructing a second window,
    /// so an automated `screenshot` right after this sees the exact window a human would.
    /// Idempotent like a second tray click (activates the existing window), so there is currently no
    /// failure path other than an exception from window construction itself - kept
    /// string?-returning to match every other `*ForAutomation` hook's error-or-null shape. Only
    /// reachable through AutomationServer, which the automation gate (ROESNIP_AUTOMATION=1 /
    /// --automation) alone decides whether to even construct.</summary>
    internal string? OpenSettingsForAutomation()
    {
        try
        {
            OpenSettings();
            return null;
        }
        catch (Exception ex)
        {
            return $"could not open the settings window: {ex.Message}";
        }
    }

    /// <summary>Automation-only entry point (AppShell/AutomationServer.cs's `settings`/`close`
    /// command): closes the single tracked settings window (same field <see cref="OpenSettings"/>
    /// maintains), which raises the window's own Closed handler and clears
    /// <see cref="_openSettingsWindow"/> exactly as a real close would. Errors if there is nothing
    /// open to close, matching this file's other `*ForAutomation`-style "requires an active X" error
    /// convention.</summary>
    internal string? CloseSettingsForAutomation()
    {
        if (_openSettingsWindow is null)
        {
            return "settings \"close\" requires an open settings window";
        }
        _openSettingsWindow.Close();
        return null;
    }

    private async Task ApplyUpdatedSettingsAsync(RoeSnipSettings updated)
    {
        var previous = _settings;
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
            _settings = GnomePrintScreenTakeover.ApplyTakeover(_settings, previous);
            _hotkeyManager?.Register(_settings);
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: failed to re-register the hotkey: {ex.Message}");
        }
    }

    private void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;

        _pipeListenerCts?.Cancel();
        _hotkeyManager?.Dispose();
#if WINDOWS
        // SystemEvents is process-global: detach BEFORE the lifetime shuts down, so a power
        // broadcast landing during teardown can't fire OnSystemResumed against a torn-down app.
        if (_powerModeChangedHandler is not null)
        {
            SystemEvents.PowerModeChanged -= _powerModeChangedHandler;
        }
        RoeSnip.Platform.Windows.SuspendResumeNotifications.Unregister();
        RoeSnip.Platform.Windows.DisplayStateNotifications.Unregister();
#endif
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

    // ---------------- Cold-start warmup (items 17 + 18) ----------------

    /// <summary>The monitor list the instant-response flash uses (item 18) — TriggerCapture must
    /// not pay a fresh DXGI/portal/RandR enumeration on the hot path. Seeded by warmup, refreshed in
    /// the background after every capture flow; a stale list costs at worst one flash on outdated
    /// bounds (the real overlay always re-enumerates via CaptureService, and FlashDimmer's own
    /// set-comparison self-heals on the NEXT trigger once the background refresh lands).</summary>
    private static IReadOnlyList<MonitorInfo>? s_cachedMonitors;

    /// <summary>Pre-pays the heavy first-capture costs off the UI thread, ported from the WPF app's
    /// TrayApp.StartWarmup/RunWarmup (TrayApp.cs:872-1145), now including its flash-dimmer step
    /// (item 18 — see Overlay/FlashDimmer.cs's own doc comment for the Windows-only park-don't-hide
    /// mechanics this pre-creates). What runs here: monitor enumeration (seeds
    /// <see cref="s_cachedMonitors"/>), pre-creation of the per-monitor flash dimmer windows
    /// (marshalled to the UI thread — they are Avalonia windows), JIT of the tone-map/PNG-encode
    /// path, construction of the real OverlayWindow type (XAML-load + layout JIT), and one REAL
    /// throwaway capture per monitor through the exact CaptureService path a hotkey press takes
    /// (pre-provisioning WgcCapturer's cached device on Windows via
    /// <see cref="RoeSnip.Platform.Windows.WgcCapturer.Prewarm"/>). That throwaway capture may
    /// briefly show the OS capture border on WGC-fallback monitors — accepted at tray startup, same
    /// as the WPF reference (no session is left running, so no border persists). Fully try/caught
    /// and never fatal — a warmup failure just means the first real hotkey press is as slow as it
    /// always was, not a crash.
    ///
    /// No parked overlay WINDOW pool yet (only the flash dimmer's own tiny parked windows) — see
    /// docs/PARITY.md item 18's own note: the WPF reference's OverlayWindowPool (pre-built,
    /// pre-rendered, single-use overlay windows) is explicitly optional follow-up work per that
    /// item's own instructions ("if the pool destabilizes anything, land the flash dimmer alone
    /// first"). Every real OverlayWindow in this port is still constructed on-demand inside
    /// RunCaptureFlowAsync/OverlayController.RunAsync.</summary>
    private void StartWarmup()
    {
        // Cold-start CaptureGate race fix (ported from the WPF app's identical S5 comment): flag
        // the gate as "warmup pending" BEFORE the warmup thread even starts, so a trigger that
        // fires in the brief window between StartWarmup returning and RunWarmup's first
        // CaptureGate.TryEnter (WarmupCaptureSessions) still sees WarmupPending=true and waits
        // rather than racing straight past the not-yet-busy gate into paying first-time
        // capture-backend init itself. RunWarmup clears this in a finally on every exit path.
        CaptureGate.WarmupPending = true;

#if WINDOWS
        // Sleep/resume invalidation (post-sleep stall fix, ported from the WPF app): nothing else
        // in the process knows the machine slept — the WGC keepalive only catches
        // DeviceRemovedReason (a resume-stale capture stack usually reports healthy), so the first
        // post-wake hotkey used to pay the whole staleness discovery (frame-wait timeouts,
        // re-provisions, ghost-display enumeration) on the hot path. Handler stays tiny:
        // SystemEvents delivers on its own broadcast thread. Windows only — no other platform has
        // a power-event seam here (see docs/PARITY.md); the net8.0 TFM compiles without the
        // Microsoft.Win32.SystemEvents package entirely.
        _powerModeChangedHandler = (_, e) =>
        {
            if (e.Mode == PowerModes.Resume)
            {
                OnSystemResumed("SystemEvents.PowerModeChanged");
            }
        };
        SystemEvents.PowerModeChanged += _powerModeChangedHandler;

        // SystemEvents alone is NOT a working resume signal on this class of machine: it never
        // maps PBT_APMRESUMEAUTOMATIC (the broadcast modern wakes actually deliver), and a
        // 2026-07-23 log audit of the WPF daily driver found 10 wakes with ZERO
        // PowerModeChanged(Resume) deliveries — the re-warm below had never run once. The
        // callback-based power registration hears every wake; the SystemEvents subscription above
        // stays as a belt, deduped inside OnSystemResumed. Ported from the WPF app.
        RoeSnip.Platform.Windows.SuspendResumeNotifications.Register(OnSystemResumed);
        RoeSnip.Platform.Windows.DisplayStateNotifications.Register(OnConsoleDisplayState);
#endif

        var thread = new Thread(RunWarmup)
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "RoeSnip-Warmup",
        };
        thread.Start();
    }

#if WINDOWS
    /// <summary>Resume-from-sleep recovery (see the PowerModeChanged subscription in StartWarmup).
    /// Everything here is proactive re-warming so the FIRST post-wake hotkey is as fast as any
    /// other: note the resume for CaptureCache's DD-memo grace window immediately, then — after a
    /// short settle delay so the display topology and driver have finished waking (resume fires
    /// before DP links retrain; enumerating instantly sees ghost displays like "WinDisc") — drop
    /// every cached WGC device/item/framepool and re-provision fresh ones per current monitor,
    /// and refresh the monitor cache + flash windows against the settled topology. All on a
    /// background thread; the per-monitor re-prewarm takes the same slot gates a real capture
    /// takes, so it can never corrupt a capture the user managed to start first — worst case that
    /// capture pays the old-style staleness discovery and this pass then cleans up after it.</summary>
    private void OnSystemResumed(string source)
    {
        // One wake can now signal twice (power callback + SystemEvents belt) — handle the first,
        // skip the echo. Exchange-then-compare is race-safe: of two concurrent callers exactly one
        // observes the stale tick.
        long now = Environment.TickCount64;
        long last = Interlocked.Exchange(ref _lastResumeHandledTick, now);
        if (last != 0 && now - last < 10_000)
        {
            return;
        }

        FileLog.Write($"RoeSnip: system resumed from sleep ({source}); scheduling capture-stack re-warm.");
        RoeSnip.Core.Capture.CaptureCache.NotePowerResume();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3));
                RoeSnip.Platform.Windows.WgcCapturer.InvalidateAll();
                var monitors = new CaptureService().EnumerateMonitors();
                ReprovisionWgcSlots(monitors);
                FileLog.Write($"RoeSnip: post-resume capture-stack re-warm done ({monitors.Count} monitors).");
            }
            catch (Exception ex)
            {
                FileLog.Write($"RoeSnip: post-resume re-warm failed (non-fatal): {ex.Message}");
            }
            // Settled-topology refresh for the flash windows + cached monitor list (outside the
            // try: even a failed re-warm should not skip this).
            RefreshMonitorCacheInBackground(prewarmFlash: true);

            // Second pass for late-attaching monitors: after a wake the DP links routinely retrain
            // one by one for seconds (the WPF log shows 1 monitor, then all 3 about 4 s later, on
            // every wake), so the pass above can miss monitors that were not attached yet at
            // settle+3s. The WPF app covers this with its DisplaySettingsChanged subscription; this
            // app has none (docs/PARITY.md), so a single delayed re-provision pass is its
            // substitute — a no-op for slots the first pass already provisioned.
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(12));
                var settled = new CaptureService().EnumerateMonitors();
                ReprovisionWgcSlots(settled);
                RefreshMonitorCacheInBackground(prewarmFlash: true);
            }
            catch (Exception ex)
            {
                FileLog.Write($"RoeSnip: post-resume second re-warm pass failed (non-fatal): {ex.Message}");
            }
        });
    }

    private long _lastDisplayOnTick;

    /// <summary>Console display state (0 off, 1 on, 2 dimmed). The first WGC capture after the
    /// monitors were off for a while costs 470-680 ms on all monitors at once (WPF daily-driver
    /// logs, 2026-11; the power plan turns the displays off after 15 min), so when they come back
    /// on one throwaway capture per WGC monitor runs now, while the user is still reaching for the
    /// hotkey. Takes the capture gate like startup warmup does (a hotkey in the window waits, it
    /// would have paid the same cost itself) and skips if a session is already live. Ported from
    /// the WPF app.</summary>
    private void OnConsoleDisplayState(int state)
    {
        FileLog.Write($"RoeSnip: console display {(state == 0 ? "off" : state == 1 ? "on" : "dimmed")}.");
        if (state != 1)
        {
            return;
        }
        long now = Environment.TickCount64;
        long last = Interlocked.Exchange(ref _lastDisplayOnTick, now);
        if (last != 0 && now - last < 5_000)
        {
            return;
        }
        _ = Task.Run(() =>
        {
            var monitors = Volatile.Read(ref s_cachedMonitors);
            if (monitors is null || monitors.Count == 0 || !CaptureGate.TryEnter())
            {
                return;
            }
            CaptureGate.HeldByWarmup = true;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                Parallel.ForEach(monitors, monitor =>
                {
                    if (RoeSnip.Core.Capture.CaptureCache.Default.IsDesktopDuplicationBroken(monitor.DeviceName))
                    {
                        try
                        {
                            RoeSnip.Platform.Windows.WgcCapturer.Prewarm(monitor, throwawayFrame: true);
                        }
                        catch (Exception ex)
                        {
                            FileLog.Write($"RoeSnip: display-on re-warm failed for {monitor.DeviceName} (non-fatal): {ex.Message}");
                        }
                    }
                });
                FileLog.Write($"RoeSnip: display-on capture re-warm done in {watch.ElapsedMilliseconds} ms.");
            }
            finally
            {
                CaptureGate.HeldByWarmup = false;
                CaptureGate.Exit();
            }
        });
    }

    /// <summary>Re-provisions WgcCapturer's cached per-monitor item/device for every monitor, in
    /// parallel (the slots are per-monitor gated, so this mirrors CaptureService's own per-monitor
    /// parallelism). Parallel matters here: post-wake the Windows capture broker can be sick in a
    /// way that makes item creation block on COM server activation for up to its full 120 s
    /// timeout (observed live 2026-07-22 on the WPF daily driver: CO_E_SERVER_EXEC_FAILURE
    /// surfacing exactly 120 s after the call) — sequential prewarm would serialize that worst
    /// case per monitor. Ported from the WPF app's identical helper.</summary>
    private static void ReprovisionWgcSlots(System.Collections.Generic.IReadOnlyList<MonitorInfo> monitors)
    {
        Parallel.ForEach(monitors, monitor =>
        {
            try
            {
                RoeSnip.Platform.Windows.WgcCapturer.Prewarm(monitor, throwawayFrame: false);
            }
            catch (Exception ex)
            {
                FileLog.Write(
                    $"RoeSnip: WGC re-prewarm failed for {monitor.DeviceName} " +
                    $"(non-fatal; the next capture provisions on demand): {ex.Message}");
            }
        });
    }
#endif

    private void RunWarmup()
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            // Ordering mirrors the WPF reference's own RunWarmup: the flash windows ARE the instant
            // (few-ms) response — every other step is slower and only matters once the flash is
            // already up, so it comes right after monitor enumeration seeds s_cachedMonitors (which
            // the flash needs before it can position itself).
            var monitors = WarmupCaptureBackendInit();
            Volatile.Write(ref s_cachedMonitors, monitors);
            WarmupFlashWindows(monitors);
            WarmupToneMapAndEncode();
            WarmupOverlayWindowType();
            WarmupCaptureSessions(monitors);
            stopwatch.Stop();
            FileLog.Write($"RoeSnip: warmup completed in {stopwatch.ElapsedMilliseconds} ms");

            // Warmup itself is a full-scale burst (throwaway full-res captures + tonemaps) — sweep
            // its garbage so the app starts its resident life at the small footprint, not at the
            // warmup high-water mark. Schedule() is safe to call from this background thread (see
            // its own doc comment) and defers the actual collect to ApplicationIdle, so it can
            // never suspend the UI thread mid-warmup.
            IdleMemoryTrimmer.Schedule();
        }
        catch (Exception ex)
        {
            // Never let a warmup failure be visible as anything other than "the first real capture
            // is exactly as cold as before" — this is a pure optimization, not a requirement.
            FileLog.Write($"RoeSnip: warmup failed (non-fatal): {ex.Message}");
        }
        finally
        {
            // Unblock any RunCaptureFlowAsync call currently polling WarmupPending, on every
            // possible exit from this method — success, an exception caught above, or a benign
            // zero-monitors early return inside one of the Warmup* steps. A stuck WarmupPending=true
            // would otherwise make every hotkey press wait out the full 5-second poll deadline for
            // nothing.
            CaptureGate.WarmupPending = false;
        }
    }

    /// <summary>Pre-creates the monitor enumeration a real capture pays on its first call (DXGI
    /// factory/adapter/output walk on Windows; portal/RandR queries elsewhere) — otherwise-shared
    /// setup cost that a hotkey press would pay for the first time.</summary>
    private static IReadOnlyList<MonitorInfo> WarmupCaptureBackendInit()
        => new CaptureService().EnumerateMonitors();

    /// <summary>Item 18: marshals the flash dimmer window pre-creation onto the UI thread (Avalonia
    /// windows must live there; the rest of warmup deliberately stays on this background thread).
    /// PrewarmFlash itself no-ops on non-Windows and when there are no monitors, so this is safe to
    /// call unconditionally on every OS.</summary>
    private static void WarmupFlashWindows(IReadOnlyList<MonitorInfo> monitors)
    {
        if (monitors.Count == 0)
        {
            return;
        }
        try
        {
            Dispatcher.UIThread.Post(() => Overlay.OverlayController.PrewarmFlash(monitors));
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: flash dimmer warmup scheduling failed (non-fatal): {ex.Message}");
        }
    }

    /// <summary>Item 18: background monitor re-enumeration for <see cref="s_cachedMonitors"/>,
    /// called after every capture flow (ObserveCaptureTask's finally) and by the post-resume
    /// re-warm (OnSystemResumed, with <paramref name="prewarmFlash"/> so the parked flash windows
    /// are rebuilt against the settled topology too — PrewarmFlash itself refuses to touch windows
    /// while a flash/session is live). No display-change hook yet (unlike the WPF reference's
    /// SystemEvents.DisplaySettingsChanged subscription) — a monitor set change is instead picked
    /// up lazily by the next trigger's own TryShowFlash/FlashDimmer.ShowAll call, whose cold-build
    /// path (FlashDimmer.EnsureCreated's own set-comparison) already handles a stale cached list
    /// correctly, just without the pre-warm before that one trigger. Acceptable: docking/resolution
    /// changes are rare compared to the steady-state hot path this item optimizes for.</summary>
    private static void RefreshMonitorCacheInBackground(bool prewarmFlash = false)
    {
        _ = Task.Run(() =>
        {
            try
            {
                var monitors = new CaptureService().EnumerateMonitors();
                Volatile.Write(ref s_cachedMonitors, monitors);
                if (prewarmFlash && monitors.Count > 0)
                {
                    // Marshalled to the UI thread like WarmupFlashWindows — flash windows are
                    // Avalonia windows.
                    Dispatcher.UIThread.Post(() => Overlay.OverlayController.PrewarmFlash(monitors));
                }
            }
            catch (Exception ex)
            {
                FileLog.Write($"RoeSnip: monitor cache refresh failed (non-fatal): {ex.Message}");
            }
        });
    }

    /// <summary>Exercises the exact code path a real capture's tone-map/encode stretch runs
    /// (SdrImage.FromCapturedFrame -> ToneMapper.MapToSdr -> PngWriter.Encode) against a small
    /// synthetic frame with a fake MonitorInfo — no real capture session, so nothing ever
    /// flashes.</summary>
    private static void WarmupToneMapAndEncode()
    {
        const int size = 256;
        using var frame = CreateSyntheticWarmupFrame(size);

        var sdr = SdrImage.FromCapturedFrame(frame, new RoeSnip.Core.Color.ToneMapOptions());
        _ = PngWriter.Encode(sdr);
    }

    /// <summary>Builds the small synthetic Fp16 scRGB frame shared by
    /// <see cref="WarmupToneMapAndEncode"/> and <see cref="WarmupOverlayWindowType"/> — a fake
    /// MonitorInfo plus <paramref name="size"/>x<paramref name="size"/> pixel data alternating a
    /// plain-SDR value and an HDR-highlight value per column, so ToneMapper's pass-through AND
    /// shoulder/dither branches both actually execute (a uniform frame would only ever hit
    /// whichever single branch its one value falls into). Caller owns and must Dispose the
    /// returned frame. Ported from the WPF app's identical CreateSyntheticWarmupFrame.</summary>
    private static CapturedFrame CreateSyntheticWarmupFrame(int size)
    {
        var monitor = new MonitorInfo(
            Index: -1, DeviceName: "RoeSnip-Warmup", BackendKey: "warmup",
            BoundsPx: RectPhysical.FromSize(0, 0, size, size),
            DpiX: 96, DpiY: 96, Scale: 1.0, AdvancedColorActive: true,
            SdrWhiteNits: 240.0, MaxLuminanceNits: 1000.0, IsPrimary: true);

        Span<byte> dim = stackalloc byte[2];
        Span<byte> bright = stackalloc byte[2];
        BitConverter.TryWriteBytes(dim, (Half)0.5f);    // plain SDR gray — exercises the pass-through branch
        BitConverter.TryWriteBytes(bright, (Half)2.5f); // 200 nits — exercises the shoulder/dither branch

        var pixels = new byte[size * size * 8]; // Fp16ScRgb: 8 bytes/pixel (4 x Half)
        for (int y = 0; y < size; y++)
        {
            int rowOffset = y * size * 8;
            for (int x = 0; x < size; x++)
            {
                var src = (x % 2 == 0) ? dim : bright;
                int pixelOffset = rowOffset + x * 8;
                for (int channel = 0; channel < 4; channel++)
                {
                    pixels[pixelOffset + channel * 2] = src[0];
                    pixels[pixelOffset + channel * 2 + 1] = src[1];
                }
            }
        }

        return new CapturedFrame(
            FrameFormat.Fp16ScRgb, size, size, size * 8, pixels, monitor,
            sdrWhiteInBufferUnits: monitor.SdrWhiteNits / 80.0);
    }

    /// <summary>Warms the real OverlayWindow type: the first-ever construction of OverlayWindow
    /// (AvaloniaXamlLoader.Load for it AND its nested ToolbarControl.axaml) plus custom-control JIT
    /// is a measurable share of the first real overlay's latency. Must run on the UI thread
    /// (Avalonia windows are UI-thread-affine, like WPF's), and deliberately NEVER Show()s the
    /// window: with no HWND ever mapped visible, Measure+Arrange alone is enough to force the
    /// visual tree to build and lay out for real, which is where the load/JIT cost actually lands.
    /// Fire-and-forget from the warmup thread (Dispatcher.UIThread.Post), same as every other
    /// Warmup* step's own non-fatal try/catch — a failure here costs nothing but the optimization.</summary>
    private void WarmupOverlayWindowType()
    {
        Dispatcher.UIThread.Post(() =>
        {
            const int size = 256;
            CapturedFrame? frame = null;
            try
            {
                frame = CreateSyntheticWarmupFrame(size);
                var sdr = SdrImage.FromCapturedFrame(frame, new RoeSnip.Core.Color.ToneMapOptions());

                var window = new OverlayWindow(
                    frame, sdr, RoeSnipSettings.Default,
                    static _ => { }, static _ => { }, static (_, _, _) => { }, static _ => { },
                    static _ => { }, static _ => { }, static _ => { });
                window.Measure(new Size(size, size));
                window.Arrange(new Rect(0, 0, size, size));
                window.Close(); // never Show()n — see method doc.
            }
            catch (Exception ex)
            {
                FileLog.Write($"RoeSnip: overlay window type warmup failed (non-fatal): {ex.Message}");
            }
            finally
            {
                frame?.Dispose();
            }
        });
    }

    /// <summary>One real throwaway capture per monitor at startup: warms the capture backend end
    /// to end and, on Windows, pre-provisions WgcCapturer's per-monitor item/device cache so the
    /// first-ever DD-to-WGC fallback (or first WGC monitor) doesn't pay that cost on a hotkey
    /// press. Holds the process-wide CaptureGate: a concurrent real capture could otherwise race
    /// this one and spuriously poison shared capture-backend state. (A trigger inside this
    /// sub-second window waits it out — see CaptureGate.HeldByWarmup/RunCaptureFlowAsync.)</summary>
    private static void WarmupCaptureSessions(IReadOnlyList<MonitorInfo> monitors)
    {
        if (monitors.Count == 0)
        {
            return;
        }
        if (!CaptureGate.TryEnter())
        {
            FileLog.Write("RoeSnip: skipping warmup capture; a real capture is already in progress.");
            return;
        }
        // Flag the hold as warmup's so a real hotkey press in this window waits for the gate
        // (RunCaptureFlowAsync) instead of being dropped.
        CaptureGate.HeldByWarmup = true;

        var watch = Stopwatch.StartNew();
        try
        {
            var captureService = new CaptureService();
            var frames = captureService.CaptureAll(monitors);
            try
            {
                // Mirrors AppComposition.RunCaptureFlowAsync's real tonemap step (fixed-slot
                // Parallel.For) rather than a sequential foreach, so this warms the same
                // threadpool-ramp + full-size LUT/SIMD path a real hotkey press actually exercises.
                var previews = new SdrImage[frames.Count];
                Parallel.For(0, frames.Count, i =>
                {
                    previews[i] = SdrImage.FromCapturedFrame(frames[i], new RoeSnip.Core.Color.ToneMapOptions());
                });
            }
            finally
            {
                foreach (var frame in frames)
                {
                    frame.Dispose();
                }
            }

#if WINDOWS
            // Monitors whose capture above went through (borderless) Desktop Duplication never
            // touched WGC — pre-provision WgcCapturer's cached item/device for them too (cheap,
            // and sessionless, so no border), covering the cost of a first-ever DD->WGC fallback.
            foreach (var monitor in monitors)
            {
                if (!RoeSnip.Core.Capture.CaptureCache.Default.IsDesktopDuplicationBroken(monitor.DeviceName))
                {
                    try
                    {
                        RoeSnip.Platform.Windows.WgcCapturer.Prewarm(monitor, throwawayFrame: false);
                    }
                    catch (Exception ex)
                    {
                        FileLog.Write(
                            $"RoeSnip: WGC pre-provisioning failed for monitor {monitor.DeviceName} (non-fatal): {ex.Message}");
                    }
                }
            }
#endif
            watch.Stop();
            FileLog.Write(
                $"RoeSnip: warmup capture ({frames.Count}/{monitors.Count} monitors) completed in " +
                $"{watch.ElapsedMilliseconds} ms (the OS capture border may have flashed once on WGC monitors)");
        }
        finally
        {
            CaptureGate.HeldByWarmup = false;
            CaptureGate.Exit();
        }
    }

    // ---------------- Self-update (item 13b/13d) ----------------

    /// <summary>Backs the hidden --self-update-now flag: synchronously checks GitHub for a newer
    /// release and, if there is one, downloads + swaps it (UpdateManager.ApplyUpdateAsync, which
    /// then launches the new build so replace-on-run hands off to it) before returning. No tray
    /// icon and no single-instance mutex are ever created here — this process's only job is to
    /// perform the swap and exit, leaving the freshly-launched new build as the running instance.
    /// Only for copies that self-update (Windows, a Linux AppImage, a macOS .app bundle). Returns 0
    /// on "updated"/"already current"/"not applicable here", 1 if the check/download/swap errored,
    /// another updater still held the install after <see cref="SelfUpdateLockWait"/>, or the whole
    /// run passed <see cref="SelfUpdateDeadline"/> before its swap started (a started swap is always
    /// waited out); one stdout line says which. Never throws.</summary>
    private static int RunSelfUpdateNow()
    {
        var swapGate = new SwapGate();
        Task<int> work = Task.Run(() => RunSelfUpdateNowCore(swapGate));
        if (work.Wait(SelfUpdateDeadline))
        {
            return work.Result;
        }

        // The deadline only abandons a run that has not touched the install yet; once the swap
        // started (two moves on Windows) exiting could leave no exe at the target, so wait it out.
        if (!swapGate.TryAbandon())
        {
            FileLog.Write("RoeSnip: --self-update-now: deadline passed mid-swap; waiting for the swap to finish.");
            return work.GetAwaiter().GetResult();
        }

        SelfUpdateReport($"update failed: no result within {SelfUpdateDeadline.TotalMinutes:0} minutes.");
        return 1;
    }

    // Download (15 min) + waiting out another updater (5 min) + the release check, with headroom.
    private static readonly TimeSpan SelfUpdateDeadline = TimeSpan.FromMinutes(25);

    private static readonly TimeSpan SelfUpdateLockWait = TimeSpan.FromMinutes(5);

    private static void SelfUpdateReport(string text)
    {
        Console.Out.WriteLine($"RoeSnip: {text}");
        FileLog.Write($"RoeSnip: --self-update-now: {text}");
    }

    private static int RunSelfUpdateNowCore(SwapGate swapGate)
    {
        if (!UpdateManager.SelfUpdateSupported)
        {
            Console.Out.WriteLine(
                "RoeSnip: --self-update-now needs the AppImage (Linux) or the .app bundle (macOS); " +
                "grab the latest release manually from the release page for this copy.");
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

            UpdateManager.UpdateCheckResult result = UpdateManager.CheckForUpdateAsync().GetAwaiter().GetResult();
            if (result.Update is null)
            {
                if (result.Failed)
                {
                    SelfUpdateReport($"update check failed: {result.FailureDetail}");
                    return 1;
                }

                SelfUpdateReport($"already up to date (current {UpdateManager.CurrentVersionText}).");
                return 0;
            }

            UpdateManager.UpdateInfo update = result.Update;
            FileLog.Write($"RoeSnip: --self-update-now: updating {UpdateManager.CurrentVersionText} -> {update.Version}...");
            UpdateManager.ApplyOutcome outcome = UpdateManager
                .ApplyUpdateAsync(update, beforeLaunch: null, lockWait: SelfUpdateLockWait, swapGate: swapGate)
                .GetAwaiter().GetResult();
            switch (outcome)
            {
                case UpdateManager.ApplyOutcome.Applied:
                    SelfUpdateReport($"updated to {update.Version}; new build launched.");
                    return 0;
                case UpdateManager.ApplyOutcome.AppliedByOtherProcess:
                    SelfUpdateReport($"updated to {update.Version} by another RoeSnip process.");
                    return 0;
                default:
                    SelfUpdateReport(
                        $"update failed: another RoeSnip process was still updating after {SelfUpdateLockWait.TotalMinutes:0} minutes.");
                    return 1;
            }
        }
        catch (Exception ex)
        {
            SelfUpdateReport($"update failed: {ex.Message}");
            return 1;
        }
    }

    private void OnInstallRoeSnipClicked(object? sender, EventArgs e) => InstallRoeSnip();

    private void OnCheckForUpdatesClicked(object? sender, EventArgs e) => _ = CheckForUpdatesFromMenuAsync();

    /// <summary>The "Install RoeSnip" context-menu item (self-updating copies only, shown while
    /// <see cref="UpdateManager.InstallExists"/> is false): copies this run to the install location
    /// (%LOCALAPPDATA%\RoeSnip.App, ~/.local/share/RoeSnip, ~/Applications) and hands off to it, then exits this process so the installed
    /// copy takes over. Runs off the UI thread (file copy + registry write) and never lets a
    /// failure take the tray down with it: UpdateManager.Install rethrows on failure, so the catch
    /// here shows an error toast and the exit is skipped, leaving the tray running as it was.</summary>
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

    /// <summary>The periodic update loop (self-updating copies; only started when
    /// <see cref="UpdateManager.IsInstalled"/> is true — see the Start() call site's comment).
    /// Scheduling itself (iteration-zero startup check, 1-minute settings-poll wake, wall-clock
    /// elapsed, jitter, StartupOnly handling) is <see cref="UpdatePolling.RunPeriodicAsync"/> — see
    /// its doc comment for the full semantics, which are unchanged here. This method supplies only
    /// the two things that differ per app/platform: where the frequency setting lives
    /// (<c>_settings.UpdateCheckFrequency</c>, re-read fresh on every call so SettingsWindow's
    /// onSaved callback (ApplyUpdatedSettingsAsync) reassigning <c>_settings</c> takes effect on the
    /// very next wake) and what a tick means (CheckForUpdatesAndAutoApplyAsync).</summary>
    private Task RunUpdateLoopAsync() =>
        UpdatePolling.RunPeriodicAsync(() => _settings.UpdateCheckFrequency, CheckForUpdatesAndAutoApplyAsync);

    /// <summary>One check-and-apply cycle. Says nothing when there is nothing to
    /// offer (no update, no network, private-repo 404, a 304/rate-limit backoff —
    /// CheckForUpdateAsync already swallows all of that and returns null). When a newer release
    /// does exist, applies it automatically — no click required — but the beforeLaunch delegate
    /// passed to ApplyUpdateAsync holds the actual relaunch (and the replace-on-run kill that
    /// follows it) until <see cref="WaitForIdleAsync"/> reports idle, so an update landing mid-snip
    /// can never yank the app out from under the user. Falls back to the click-to-update toast if
    /// the auto-apply throws, latched to once per release version
    /// (<see cref="_updateToastShownForVersion"/>) so a persistently failing download retried every
    /// periodic tick does not turn into hourly toast spam; the retries themselves keep happening
    /// regardless, only the notification is latched. Called both as the loop's iteration zero
    /// (RunUpdateLoopAsync) and, on every subsequent tick, as the actual periodic recheck — never
    /// blocks its caller beyond its own await chain.</summary>
    private async Task CheckForUpdatesAndAutoApplyAsync()
    {
        UpdateManager.UpdateCheckResult result = await UpdateManager.CheckForUpdateAsync().ConfigureAwait(false);
        UpdateManager.UpdateInfo? update = result.Update;
        if (update is null)
        {
            // Only a CONFIRMED no-update is "UpToDate" - a rate-limited/network-down/parse-failed
            // probe taught us nothing about whether an update exists and must not be recorded as
            // "checked, all good", or a genuinely broken machine (proxy/firewall/DNS down for weeks)
            // would show a false "up to date" in About forever instead of the failure that is
            // actually happening every single tick.
            UpdateManager.RecordLastCheckOutcome(
                version: null,
                outcome: result.Failed ? $"Failed: {result.FailureDetail}" : "UpToDate");
            return;
        }

        FileLog.Write($"RoeSnip: auto-updating {UpdateManager.CurrentVersion} -> {update.Version}...");
        try
        {
            UpdateManager.ApplyOutcome outcome =
                await UpdateManager.ApplyUpdateAsync(update, WaitForIdleAsync).ConfigureAwait(false);
            if (outcome == UpdateManager.ApplyOutcome.Applied)
            {
                UpdateManager.RecordLastCheckOutcome(update.Version.ToString(), $"Applied {update.Version}");
            }
            // Busy: the other updater launches the new build, which replaces this process.
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: auto-update to {update.Version} failed: {ex.Message}");
            UpdateManager.RecordLastCheckOutcome(update.Version.ToString(), $"Failed: {ex.Message}");
            if (_updateToastShownForVersion != update.Version)
            {
                _updateToastShownForVersion = update.Version;
                Dispatcher.UIThread.Post(() => ShowUpdateAvailableToast(update));
            }
        }
    }

    /// <summary>Polled rather than event-driven: capture sessions are short-lived (seconds) and
    /// AppComposition exposes no completion signal, so a coarse poll is the simplest thing that
    /// cannot miss a state change. A running recording counts as busy too (WPF reference:
    /// RecordingController.IsActive).</summary>
    private static async Task WaitForIdleAsync()
    {
        while (AppComposition.IsCaptureBusy ||
               Recording.RecordingOrchestrator.IsActive ||
               Recording.RecordingController.IsActive)
        {
            await Task.Delay(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        }
    }

    /// <summary>The "Check for updates" context-menu item: unlike the silent
    /// startup check, this always reports back via a toast/dialog rather than staying silent — a
    /// deliberate, occasional, user-initiated click should always get a visible answer (up to
    /// date / update offered / check failed).</summary>
    private async Task CheckForUpdatesFromMenuAsync()
    {
        UpdateManager.UpdateCheckResult result;
        try
        {
            // A deliberate user click deserves a real network answer even if a periodic check
            // recently tripped the rate-limit backoff.
            result = await UpdateManager.CheckForUpdateAsync(bypassBackoff: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: update check failed (non-fatal): {ex.Message}");
            UpdateManager.RecordLastCheckOutcome(version: null, outcome: $"Failed: {ex.Message}");
            Dispatcher.UIThread.Post(() => ShowToast(
                "Could not check for updates - GitHub could not be reached.", isError: true, durationMs: 6000, onClick: null));
            return;
        }

        UpdateManager.UpdateInfo? update = result.Update;
        if (update is null)
        {
            // Only a CONFIRMED no-update (304, or a parsed payload with nothing newer) is "UpToDate" -
            // an inconclusive probe (rate limit, network down, GitHub 5xx, parse failure) must be
            // recorded as a failure, never mistaken for "checked and up to date".
            UpdateManager.RecordLastCheckOutcome(
                version: null,
                outcome: result.Failed ? $"Failed: {result.FailureDetail}" : "UpToDate");
        }
        // else: the outcome is still undecided (user hasn't answered Yes/No yet) -
        // ApplyUpdateFromToastAsync records Applied/Failed once they do.

        Dispatcher.UIThread.Post(() =>
        {
            if (update is not null)
            {
                _ = OfferUpdateAsync(update);
            }
            else if (result.Failed)
            {
                ShowToast($"Could not check for updates: {result.FailureDetail}", isError: true, durationMs: 6000, onClick: null);
            }
            else
            {
                ShowToast($"RoeSnip {UpdateManager.CurrentVersionText} is up to date.", isError: false, durationMs: 4000, onClick: null);
            }
        });
    }

    /// <summary>Must already be on the UI thread. Asks Yes/No (Avalonia has no blocking
    /// MessageBox — see ShowYesNoDialogAsync) and applies immediately on Yes.</summary>
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

    /// <summary>Shows the click-to-update toast (must already be on the UI thread). Every periodic
    /// tick of the update loop (see <see cref="CheckForUpdatesAndAutoApplyAsync"/>,
    /// <see cref="RunUpdateLoopAsync"/>) applies updates automatically without asking; this toast is
    /// only reached as that apply's failure fallback, so the user still has a way to update by hand
    /// if the automatic download/swap failed. Latched once per release version (see
    /// <see cref="_updateToastShownForVersion"/>) so an hourly retry against a persistently failing
    /// download doesn't spam a fresh toast every tick.</summary>
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
    private async Task ApplyUpdateFromToastAsync(UpdateManager.UpdateInfo info)
    {
        try
        {
            UpdateManager.ApplyOutcome outcome = await UpdateManager.ApplyUpdateAsync(info).ConfigureAwait(false);
            if (outcome == UpdateManager.ApplyOutcome.Busy)
            {
                Dispatcher.UIThread.Post(() => ShowToast(
                    "Another RoeSnip is already installing this update.", isError: false, durationMs: 6000, onClick: null));
                return;
            }

            UpdateManager.RecordLastCheckOutcome(info.Version.ToString(), $"Applied {info.Version}");
        }
        catch (Exception ex)
        {
            UpdateManager.RecordLastCheckOutcome(info.Version.ToString(), $"Failed: {ex.Message}");
            Dispatcher.UIThread.Post(() => ShowError($"Update to {info.Version} failed: {ex.Message}"));
        }
    }

    /// <summary>Linux/macOS copies that do not self-update (a tar.gz / dev folder run, or an
    /// AppImage / .app bundle not yet installed): same <see cref="UpdatePolling.RunPeriodicAsync"/>
    /// schedule as <see cref="RunUpdateLoopAsync"/>, but a tick is
    /// <see cref="CheckForNewVersionPassivelyAsync"/>, a heads-up notice only.</summary>
    private Task RunPassiveNoticeLoopAsync() =>
        UpdatePolling.RunPeriodicAsync(() => _settings.UpdateCheckFrequency, CheckForNewVersionPassivelyAsync);

    /// <summary>Passive notice (item 13d): a toast linking the GitHub release page, never an
    /// automatic download. Latched to once per release version (<see cref="_passiveNoticeShownForVersion"/>)
    /// so the now-periodic cadence (see <see cref="RunPassiveNoticeLoopAsync"/>) re-alerts whenever
    /// a NEWER release ships but never repeats the same notice tick after tick.
    ///
    /// Passes <c>commitEvenWhenUpdateFound: true</c>: unlike the Windows auto-apply path, this
    /// method never downloads or retries anything, so there is nothing to protect by withholding the
    /// ETag once a found update has been handled - doing so would otherwise force a full uncached
    /// GET on every periodic tick for as long as the release sits un-upgraded (weeks, since downloads
    /// stay manual here), instead of the free 304 the conditional-GET client exists to provide.</summary>
    private async Task CheckForNewVersionPassivelyAsync()
    {
        UpdateManager.UpdateInfo? update = (await UpdateManager.CheckForUpdateAsync(commitEvenWhenUpdateFound: true).ConfigureAwait(false)).Update;
        if (update is null)
        {
            return;
        }

        if (_passiveNoticeShownForVersion == update.Version)
        {
            return;
        }
        _passiveNoticeShownForVersion = update.Version;

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

                    // The shell only reads PrintScreenKeyForSnippingEnabled at logon, so writing
                    // the value alone leaves Snipping Tool grabbing PrtScr for the rest of this
                    // session. Broadcast WM_SETTINGCHANGE (what the Windows Settings toggle does)
                    // so it applies immediately and the hotkey below actually receives PrtScr,
                    // matching the WPF app's TrayApp.cs:440-459.
                    BroadcastKeyboardSettingChange();
                }
                catch (Exception ex)
                {
                    FileLog.Write(
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
                FileLog.Write(
                    $"RoeSnip: failed to persist the PrintScreen consent answer: {ex.Message}");
            }

            return updated;
        }
        catch (Exception ex)
        {
            FileLog.Write(
                $"RoeSnip: PrintScreen consent check failed, keeping PrintScreen-alone: {ex.Message}");
            return settings;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out IntPtr result);

    private const uint WM_SETTINGCHANGE = 0x001A;
    private static readonly IntPtr HWND_BROADCAST = new(0xffff);
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    /// <summary>Broadcasts the same policy-change notification the WPF app sends after writing
    /// PrintScreenKeyForSnippingEnabled (TrayApp.cs:440-459), so the shell picks the new value up
    /// immediately instead of only at the next logon.</summary>
    [SupportedOSPlatform("windows")]
    private static void BroadcastKeyboardSettingChange()
        => SendMessageTimeout(
            HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero, PrintScreenRegistryKeyPath, SMTO_ABORTIFHUNG, 1000, out _);

    // Well-known screenshot tools that grab the PrintScreen key (process name -> friendly name),
    // ported verbatim from the WPF app's TrayApp.KnownPrintScreenApps.
    private static readonly (string Process, string Name)[] KnownPrintScreenApps =
    {
        ("ShareX", "ShareX"),
        ("Greenshot", "Greenshot"),
        ("Lightshot", "Lightshot"),
        ("Snagit32", "Snagit"),
        ("SnagitEditor", "Snagit"),
        ("PicPick", "PicPick"),
        ("flameshot", "Flameshot"),
        ("Gyazo", "Gyazo"),
        ("ScreenToGif", "ScreenToGif"),
    };

    /// <summary>Startup heads-up if RoeSnip's PrintScreen hotkey is likely being stolen by another
    /// screenshot tool (ShareX etc.) - ported from the WPF app's TrayApp.WarnIfPrintScreenConflict
    /// (TrayApp.cs:791-856). Only fires for a BARE PrintScreen hotkey - Ctrl+PrintScreen and other
    /// combos don't collide with the usual PrtScr grabs. Two signals: the hotkey failed to arm
    /// (another app already owns the key), or a known screenshot app is running (those often grab
    /// PrtScr with a low-level hook this port's own SharpHook-based HotkeyManager cannot detect a
    /// collision with, so they can steal the key even though registration itself "succeeded" -
    /// see HotkeyManager's own class doc comment on how weak that signal already is here). A
    /// non-blocking toast, never a modal, so it can never gate startup.</summary>
    private void WarnIfPrintScreenConflict()
    {
        bool barePrintScreen = _settings.HotkeyModifiers == 0 && _settings.HotkeyVirtualKey == HotkeyManager.VkSnapshot;
        if (!barePrintScreen)
        {
            return;
        }

        string? app = DetectRunningScreenshotApp();
        bool registerFailed = _hotkeyManager?.IsRegistered == false;
        if (app is null && !registerFailed)
        {
            return;
        }

        string who = app ?? "Another program";
        ShowToast(
            $"{who} may be using the PrintScreen key, which can stop RoeSnip's screenshot hotkey from " +
            "working. Close it, or pick a different hotkey in RoeSnip's Settings.",
            isError: true,
            durationMs: 8000,
            onClick: null);
    }

    private static string? DetectRunningScreenshotApp()
    {
        foreach (var (processName, displayName) in KnownPrintScreenApps)
        {
            try
            {
                var procs = Process.GetProcessesByName(processName);
                foreach (var p in procs)
                {
                    p.Dispose();
                }
                if (procs.Length > 0)
                {
                    return displayName;
                }
            }
            catch
            {
                // Best-effort detection - never let it break startup.
            }
        }
        return null;
    }

    /// <summary>Minimal ownerless Yes/No dialog (Avalonia has no MessageBox). Returns true for
    /// Yes, false for No, null if closed without answering. Internal (not private) so SettingsWindow
    /// can reuse it for item 15's "elevated startup enabled — restart now?" prompt, matching the WPF
    /// app's own SettingsWindow using the same System.Windows.MessageBox.Show it already used
    /// everywhere else, rather than inventing a second dialog implementation.</summary>
    internal static Task<bool?> ShowYesNoDialogAsync(string title, string message)
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
            string aboutMessage =
                $"RoeSnip {UpdateManager.CurrentVersionText}\n\n" +
                "An HDR-correct screenshot tool. On HDR/Advanced-Color displays, RoeSnip captures the " +
                "true linear scRGB frame and tone-maps it properly (matching the SDR white level and " +
                "rolling off highlights) instead of producing the washed-out gray screenshots typical " +
                "of legacy capture tools.";

            // Hardening item 8: a durable breadcrumb of the last update check's outcome, since the
            // unattended auto-update path's only other failure signal is a toast that can go
            // unnoticed. Omitted entirely (no line at all) when nothing has ever been recorded yet -
            // see UpdateManager.LastCheckSummary/UpdateStatusMarker.DescribeLastCheck.
            string? lastCheck = UpdateManager.LastCheckSummary();
            if (lastCheck is not null)
            {
                aboutMessage += $"\n\n{lastCheck}";
            }

            var text = new TextBlock
            {
                Text = aboutMessage,
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
                Icon = AppIcon,
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
            FileLog.Write($"RoeSnip: failed to show the About window: {ex.Message}");
        }
    }

    // ---------------- The kept capture (Copy again / Save last capture) ----------------

    /// <summary>The two things that can still be done with the last still capture once the overlay
    /// has closed. They live in the tray menu because the overlay cannot stay up to hold them - it
    /// dims the whole desktop - and the resident is the only part of the app that outlives a
    /// capture. Ported from the WPF app's own tray menu, with two differences forced by this port:
    /// a native menu has no "about to open" hook, so the items are pushed (LastCapture.Changed)
    /// rather than refreshed lazily, and Copy again is only offered where a windowless copy can
    /// actually work (ClipboardService.CanCopyImageWithoutWindow - not X11).</summary>
    private void BuildKeptCaptureItems(NativeMenu menu)
    {
        _trayMenu = menu;

        if (ClipboardService.CanCopyImageWithoutWindow)
        {
            _copyAgainItem = new NativeMenuItem("Copy again");
            _copyAgainItem.Click += (_, _) => CopyKeptCapture();
        }
        _saveLastItem = new NativeMenuItem("Save last capture");
        _saveLastItem.Click += (_, _) => SaveKeptCapture(null);

        // Static event on a process-lifetime singleton: never unsubscribed, by design.
        LastCapture.Changed += () => Dispatcher.UIThread.Post(RefreshKeptCaptureItems);
        RefreshKeptCaptureItems();
    }

    /// <summary>Adds the items once there is something to act on and takes them away again when
    /// there is not, rather than leaving two entries that cannot work. Labels Copy again with what
    /// would actually come back (size and time), so the offer is checkable before it is taken.
    /// They sit directly under "Capture", which is index 1 of a menu whose other entries never
    /// move.</summary>
    private void RefreshKeptCaptureItems()
    {
        if (_trayMenu is not { } menu || _saveLastItem is null)
        {
            return;
        }

        var kept = LastCapture.Current;
        var items = new List<NativeMenuItem>();
        if (_copyAgainItem is not null)
        {
            items.Add(_copyAgainItem);
        }
        items.Add(_saveLastItem);

        if (kept is null)
        {
            foreach (var item in items)
            {
                menu.Items.Remove(item);
            }
            return;
        }

        if (_copyAgainItem is not null)
        {
            _copyAgainItem.Header = $"Copy again ({kept.Describe()})";
        }
        for (int i = 0; i < items.Count; i++)
        {
            if (!menu.Items.Contains(items[i]))
            {
                menu.Items.Insert(1 + i, items[i]);
            }
        }
    }

    /// <summary>Puts the kept capture back on the clipboard, in every format the original copy
    /// used, so the paste it was meant for still works after something else has been copied in
    /// between. Returns null on success, else why not - the automation hook reports the same string
    /// the toast says.</summary>
    internal string? CopyKeptCapture()
    {
        if (LastCapture.Current is not { } kept)
        {
            return "no capture is being kept (take one first)";
        }
        if (!ClipboardService.CanCopyImageWithoutWindow)
        {
            return "this platform's clipboard needs a window to own the copy";
        }

        try
        {
            ClipboardService.CopyImageWithoutWindow(new SdrImage(kept.Width, kept.Height, kept.Pixels));
            FileLog.Write($"RoeSnip: the kept capture ({kept.Describe()}) was copied to the clipboard again");
            ShowToast($"Copied the last capture ({kept.Describe()}) to the clipboard.", isError: false, durationMs: 3000, onClick: null);
            return null;
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: re-copying the kept capture failed: {ex}");
            ShowError($"Could not copy the last capture: {ex.Message}");
            return $"copying the kept capture failed: {ex.Message}";
        }
    }

    /// <summary>Writes the kept capture as a PNG. No file picker: this is a recovery action taken
    /// from a tray menu, not a "where should this go?" moment, so it lands in the configured save
    /// directory under the same roesnip_yyyyMMdd_HHmmss.png name the overlay's own Save would have
    /// offered (stamped with when the capture was TAKEN, not when it was rescued), and the saved
    /// toast hands the user the folder. Never overwrites an earlier save of the same capture - see
    /// KeptCapture.SuggestSavePath. <paramref name="explicitPath"/> is the automation hook's way of
    /// writing somewhere deterministic; it bypasses nothing else.</summary>
    internal string? SaveKeptCapture(string? explicitPath)
    {
        if (LastCapture.Current is not { } kept)
        {
            return "no capture is being kept (take one first)";
        }

        try
        {
            string path;
            if (explicitPath is not null)
            {
                path = explicitPath;
                string? parent = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }
            }
            else
            {
                Directory.CreateDirectory(_settings.SaveDirectory);
                path = kept.SuggestSavePath(_settings.SaveDirectory);
            }

            PngWriter.WriteFile(path, new SdrImage(kept.Width, kept.Height, kept.Pixels));
            FileLog.Write($"RoeSnip: the kept capture ({kept.Describe()}) was saved to {path}");
            ShowSavedBalloon(path);
            return null;
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: saving the kept capture failed: {ex}");
            ShowError($"Could not save the last capture: {ex.Message}");
            return $"saving the kept capture failed: {ex.Message}";
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
        FileLog.Write($"RoeSnip: {message}");
        Dispatcher.UIThread.Post(() => ShowToast(message, isError: true, durationMs: 6000, onClick: null));
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: failed to open {url}: {ex.Message}");
        }
    }

    /// <summary>A small topmost, non-activating toast window next to the mouse cursor (ToastAnchor)
    /// - the Avalonia stand-in for the WPF NotifyIcon balloons (Avalonia's TrayIcon has no
    /// notification API). Positioned BEFORE Show() in physical pixels (re-applied after showing
    /// only on Linux, where the window manager may move it). Any failure
    /// here is swallowed after logging; toasts are best-effort UX, never load-bearing.</summary>
    private void ShowToast(string message, bool isError, int durationMs, Action? onClick)
    {
        try
        {
            var text = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(ToastText),
                Margin = new Thickness(14, 10),
                MaxLines = 3,
            };

            var border = new Border
            {
                Background = new SolidColorBrush(ToastBackground),
                BorderBrush = new SolidColorBrush(isError ? ToastErrorBorder : ToastAccentBorder),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Child = text,
            };

            const double toastWidth = 360;
            const double toastHeight = 84;

            var window = new Window
            {
                WindowDecorations = WindowDecorations.None,
                Icon = AppIcon,
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

            // Physical-pixel position next to the cursor, set BEFORE Show().
            ToastAnchor.Place(window, toastWidth, toastHeight);

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
            FileLog.Write($"RoeSnip: toast notification failed ({ex.Message}); message was: {message}");
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
            FileLog.Write($"RoeSnip: failed to open folder for {filePath}: {ex.Message}");
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
            BuildKeptCaptureItems(menu);
            menu.Items.Add(settingsItem);
            menu.Items.Add(aboutItem);

            // Install + self-update (item 13b): Windows, a Linux AppImage, a macOS .app bundle. A
            // Linux tar.gz / dev folder run only gets the passive notice, no menu items.
            if (UpdateManager.SelfUpdateSupported)
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
                Icon = AppIcon,
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
            FileLog.Write(
                $"RoeSnip: tray icon unavailable ({ex.Message}). The app remains fully operable via " +
                "the global hotkey and the `RoeSnip capture` / `RoeSnip settings` CLI verbs.");
        }
    }

    /// <summary>Item 14 branding: the on-brand app icon (Assets/roesnip.ico, bundled as an
    /// Avalonia resource - see RoeSnip.App.csproj), shared by the tray icon and every window this
    /// class opens (Settings, About, toasts) so they all match the taskbar/titlebar icon a real
    /// install would show. Resolved once and cached; falls back to the original procedurally-drawn
    /// glyph (<see cref="CreateTrayIconImage"/>) if the resource is ever missing or fails to
    /// decode, so startup can never regress/fail because of the icon - same belt-and-braces
    /// fallback shape as the WPF app's own CreateTrayIcon (TrayApp.cs:1328-1346).</summary>
    private static readonly Lazy<WindowIcon> s_appIcon = new(() => LoadBundledIcon() ?? CreateTrayIconImage());

    private static WindowIcon AppIcon => s_appIcon.Value;

    private static WindowIcon? LoadBundledIcon()
    {
        try
        {
            var uri = new Uri("avares://RoeSnip/Assets/roesnip.ico");
            using var stream = AssetLoader.Open(uri);
            return new WindowIcon(stream);
        }
        catch (Exception ex)
        {
            FileLog.Write(
                $"RoeSnip: failed to load the bundled app icon ({ex.Message}); using the procedural glyph instead.");
            return null;
        }
    }

    /// <summary>Draws the same stylized camera glyph as the WPF app's tray icon (accent-blue body,
    /// flash bump, dark lens) into a 32x32 BGRA raster, PNG-encodes it via Core's PngWriter, and
    /// wraps it as a WindowIcon — no image assets, no System.Drawing dependency. The fallback path
    /// for <see cref="AppIcon"/> above.</summary>
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
