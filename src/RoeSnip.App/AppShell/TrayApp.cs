using System;
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
    private bool _exiting;

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
        // out of the way immediately - no tray icon, no hotkey, nothing. Windows only (the
        // self-update swap this guards is Windows-only, item 13d) - Linux/macOS never write a
        // pending-verify marker in the first place, so there is nothing to check there.
        UpdateManager.HealthCheckAction healthAction = UpdateManager.HealthCheckAction.ProceedImmediateCleanup;
        if (OperatingSystem.IsWindows())
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
            // deterministically from agents/E2E instead of synthetic mouse/UIA. TriggerCapture is
            // passed by reference (not re-implemented) so a `trigger` command runs the exact same
            // path the tray icon/hotkey/single-instance signal already do.
            _automationServer = new AutomationServer(TriggerCapture);
            _automationServer.Start();
        }

        // Self-update (item 13b, periodic in this later pass): best-effort cleanup of any leftover
        // .old/.new from a prior install/update swap, then — only when this IS the installed copy
        // — a background loop that checks GitHub for a newer release and, if found, downloads and
        // applies it automatically (see CheckForUpdatesAndAutoApplyAsync for the idle-wait guard on
        // the relaunch that follows), then repeats on a user-configurable cadence for as long as
        // this process lives. A startup-only check almost never actually delivers an update: this
        // app spends its life as a tray resident running for weeks between launches, not being
        // relaunched often enough to land on a fresh release. RunUpdateLoopAsync's first iteration
        // IS the old startup check; the periodic re-checks after it are what actually keep a
        // long-lived resident current. Windows-only, per the class's own doc comment; Linux/macOS
        // instead get a passive "new version available" notice loop with no self-swap (item 13d).
        if (OperatingSystem.IsWindows())
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
            // stall startup): the source exe a prior Install() "move" left behind AND the ".old" a
            // just-applied update swapped out. A named method (not an inline lambda) so the
            // platform-compat analyzer can see this Task.Run reference is itself directly inside
            // the IsWindows() guard above — it cannot see through an anonymous method body.
            _ = Task.Run(RunPendingUpdateFileCleanup);
            if (healthAction == UpdateManager.HealthCheckAction.ProceedImmediateCleanup)
            {
                _ = Task.Run(RunImmediateStaleExeCleanup);
            }
            else
            {
                // The health milestone (hardening item 7): this launch is a post-update
                // verification run. RunHealthMilestoneAsync is itself [SupportedOSPlatform("windows")]
                // (like RunUpdateLoopAsync below), so it can call CompleteHealthMilestone directly
                // without needing a Task.Run wrapper purely for the analyzer's sake - unlike
                // RunPendingUpdateFileCleanup/RunImmediateStaleExeCleanup above, which are
                // synchronous blocking I/O and genuinely need offloading off this thread.
                _ = RunHealthMilestoneAsync();
            }
            if (UpdateManager.IsInstalled)
            {
                // Gating once here (rather than inside the loop, on every wake) is sound because
                // IsInstalled derives from Environment.ProcessPath, which cannot change for the
                // lifetime of this process. Named method, not an inline lambda, for the same
                // platform-compat-analyzer reason as RunPendingUpdateFileCleanup above.
                _ = RunUpdateLoopAsync();
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

    [SupportedOSPlatform("windows")]
    private static void RunPendingUpdateFileCleanup()
    {
        UpdateManager.ProcessPendingSourceCleanup();
        if (UpdateManager.IsInstalled)
        {
            // Startup ensure (not just install-time): installs made before the shortcut feature
            // existed gain search findability on their first launch after updating, and a
            // user-deleted/corrupted shortcut heals itself. No-op when it already points right.
            StartMenuShortcut.EnsureFor(UpdateManager.InstalledExePath);
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
    /// site) - a named zero-arg method, not a lambda, for the same platform-compat-analyzer reason
    /// as RunPendingUpdateFileCleanup.</summary>
    [SupportedOSPlatform("windows")]
    private static void RunImmediateStaleExeCleanup() => UpdateManager.CleanupStaleExeWithRetry();

    /// <summary>Crash-loop guard (hardening item 7): the health milestone, called directly (not via
    /// Task.Run - unlike RunPendingUpdateFileCleanup/RunImmediateStaleExeCleanup above, this method
    /// does no blocking I/O until AFTER its own await, so nothing here needs offloading off the
    /// calling thread) once a <see cref="UpdateManager.HealthCheckAction.ProceedDeferredCleanup"/>
    /// launch has been up for ~15s. Marked [SupportedOSPlatform("windows")] itself, like
    /// RunUpdateLoopAsync, so the platform-compat analyzer accepts the CompleteHealthMilestone call
    /// inside it without needing a surrounding guard at every call site.</summary>
    [SupportedOSPlatform("windows")]
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

            _hotkeyManager = new HotkeyManager(() => Dispatcher.UIThread.Post(TriggerCapture));
            _hotkeyManager.Register(_settings);
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
            FileLog.Write($"RoeSnip: failed to persist the Wayland hotkey notice flag: {ex.Message}");
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
        // A recording is live: PrtScr stops+saves it instead of starting a new capture flow (item
        // 21d, WPF reference TrayApp.cs:255-260). Deliberately BEFORE MarkTriggerTimestamp below: a
        // stop-triggering PrtScr is not a new capture trigger and must not stamp latency
        // instrumentation or touch the flash.
        if (Recording.RecordingOrchestrator.IsActive)
        {
            Recording.RecordingOrchestrator.RequestPrtScrAction();
            return;
        }

        // Honest trigger-based latency instrumentation (item 18, ported from the WPF reference):
        // stamp the moment the ACTUAL trigger happened — before the flash, before capture, before
        // anything else — so every downstream latency log measures what the user really
        // experienced, not from whenever the flash happened to start (or didn't, if it's
        // disabled/unavailable/fails). Must be the first statement in this method.
        Overlay.OverlayController.MarkTriggerTimestamp();

        // Instant-response flash (item 18, Windows only — see Overlay/FlashDimmer.cs): dim every
        // monitor within milliseconds of the trigger, BEFORE the capture+tonemap stretch inside
        // RunCaptureFlowAsync blocks this UI thread. TryShowFlash itself no-ops (returns false) on
        // non-Windows and when ROESNIP_NO_FLASH=1 is set, so this is unconditionally safe to call —
        // those cases simply fall straight through to the direct capture-then-show path, which is
        // also the PERMANENT behavior on Linux/macOS. The ReleaseFlash in ObserveCaptureTask's
        // finally is the backstop that guarantees the flash never outlives the flow on any exit path
        // (capture failed on every monitor, CaptureGate busy, an unexpected exception).
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

    private void OpenSettings()
    {
        if (_openSettingsWindow is not null)
        {
            _openSettingsWindow.Activate();
            return;
        }

        bool hotkeyUnavailableOnWayland = _hotkeyManager?.IsUnavailableOnWayland == true;
        var window = new SettingsWindow(
            _settings,
            hotkeyUnavailableOnWayland,
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
            FileLog.Write($"RoeSnip: failed to re-register the hotkey: {ex.Message}");
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

        var thread = new Thread(RunWarmup)
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "RoeSnip-Warmup",
        };
        thread.Start();
    }

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
    /// called after every capture flow (ObserveCaptureTask's finally). No display-change hook yet
    /// (unlike the WPF reference's SystemEvents.DisplaySettingsChanged subscription) — a monitor set
    /// change is instead picked up lazily by the next trigger's own TryShowFlash/FlashDimmer.ShowAll
    /// call, whose cold-build path (FlashDimmer.EnsureCreated's own set-comparison) already handles
    /// a stale cached list correctly, just without the pre-warm before that one trigger. Acceptable:
    /// docking/resolution changes are rare compared to the steady-state hot path this item optimizes
    /// for.</summary>
    private static void RefreshMonitorCacheInBackground()
    {
        _ = Task.Run(() =>
        {
            try
            {
                var monitors = new CaptureService().EnumerateMonitors();
                Volatile.Write(ref s_cachedMonitors, monitors);
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
            FileLog.Write($"RoeSnip: self-update failed: {ex.Message}");
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

    /// <summary>The periodic update loop (Windows only; only started when
    /// <see cref="UpdateManager.IsInstalled"/> is true — see the Start() call site's comment).
    /// Scheduling itself (iteration-zero startup check, 1-minute settings-poll wake, wall-clock
    /// elapsed, jitter, StartupOnly handling) is <see cref="UpdatePolling.RunPeriodicAsync"/> — see
    /// its doc comment for the full semantics, which are unchanged here. This method supplies only
    /// the two things that differ per app/platform: where the frequency setting lives
    /// (<c>_settings.UpdateCheckFrequency</c>, re-read fresh on every call so SettingsWindow's
    /// onSaved callback (ApplyUpdatedSettingsAsync) reassigning <c>_settings</c> takes effect on the
    /// very next wake) and what a tick means (CheckForUpdatesAndAutoApplyAsync). Named method (not
    /// run inline in Start()) purely so the platform-compat analyzer can see it is directly inside
    /// the IsWindows() guard — it cannot see through an anonymous method body (same trick as
    /// RunPendingUpdateFileCleanup).</summary>
    [SupportedOSPlatform("windows")]
    private Task RunUpdateLoopAsync() =>
        UpdatePolling.RunPeriodicAsync(() => _settings.UpdateCheckFrequency, CheckForUpdatesAndAutoApplyAsync);

    /// <summary>One check-and-apply cycle (Windows only). Says nothing when there is nothing to
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
    [SupportedOSPlatform("windows")]
    private async Task CheckForUpdatesAndAutoApplyAsync()
    {
        UpdateManager.UpdateInfo? update = await UpdateManager.CheckForUpdateAsync().ConfigureAwait(false);
        if (update is null)
        {
            UpdateManager.RecordLastCheckOutcome(version: null, outcome: "UpToDate");
            return;
        }

        FileLog.Write($"RoeSnip: auto-updating {UpdateManager.CurrentVersion} -> {update.Version}...");
        try
        {
            await UpdateManager.ApplyUpdateAsync(update, WaitForIdleAsync).ConfigureAwait(false);
            UpdateManager.RecordLastCheckOutcome(update.Version.ToString(), $"Applied {update.Version}");
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
            // A deliberate user click deserves a real network answer even if a periodic check
            // recently tripped the rate-limit backoff.
            update = await UpdateManager.CheckForUpdateAsync(bypassBackoff: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: update check failed (non-fatal): {ex.Message}");
            UpdateManager.RecordLastCheckOutcome(version: null, outcome: $"Failed: {ex.Message}");
            Dispatcher.UIThread.Post(() => ShowToast(
                "Could not check for updates - GitHub could not be reached.", isError: true, durationMs: 6000, onClick: null));
            return;
        }

        if (update is null)
        {
            UpdateManager.RecordLastCheckOutcome(version: null, outcome: "UpToDate");
        }
        // else: the outcome is still undecided (user hasn't answered Yes/No yet) -
        // ApplyUpdateFromToastAsync records Applied/Failed once they do.

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

    /// <summary>Shows the click-to-update toast (must already be on the UI thread). Every periodic
    /// tick of the update loop (see <see cref="CheckForUpdatesAndAutoApplyAsync"/>,
    /// <see cref="RunUpdateLoopAsync"/>) applies updates automatically without asking; this toast is
    /// only reached as that apply's failure fallback, so the user still has a way to update by hand
    /// if the automatic download/swap failed. Latched once per release version (see
    /// <see cref="_updateToastShownForVersion"/>) so an hourly retry against a persistently failing
    /// download doesn't spam a fresh toast every tick.</summary>
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
            UpdateManager.RecordLastCheckOutcome(info.Version.ToString(), $"Applied {info.Version}");
        }
        catch (Exception ex)
        {
            UpdateManager.RecordLastCheckOutcome(info.Version.ToString(), $"Failed: {ex.Message}");
            Dispatcher.UIThread.Post(() => ShowError($"Update to {info.Version} failed: {ex.Message}"));
        }
    }

    /// <summary>Linux/macOS periodic equivalent of <see cref="RunUpdateLoopAsync"/>. Scheduling is
    /// the same shared <see cref="UpdatePolling.RunPeriodicAsync"/> skeleton both Windows loops in
    /// this class use (this used to be a hand-duplicated third copy of that ~25-line Task.Delay
    /// skeleton, defended on the theory that staying textually parallel with the Windows loop was
    /// worth the duplication - a third near-identical copy appearing is exactly the evidence that
    /// argument was wrong: a future jitter/DST fix needs to land once, not three times, and Core is
    /// where framework-free logic shared by both apps already lives). The only thing that differs
    /// here is what a tick means: <see cref="CheckForNewVersionPassivelyAsync"/> instead of an
    /// auto-applying check — there is no install directory or exe-swap strategy on these platforms
    /// (item 13d), only a heads-up notice. No IsInstalled/IsWindows guard here: this is only ever
    /// started from the non-Windows branch of Start(), and there is no "installed copy" concept on
    /// Linux/macOS to gate on — every launch runs the loop.</summary>
    private Task RunPassiveNoticeLoopAsync() =>
        UpdatePolling.RunPeriodicAsync(() => _settings.UpdateCheckFrequency, CheckForNewVersionPassivelyAsync);

    /// <summary>Linux/macOS (item 13d): no install directory, no exe swap strategy that can be
    /// tested here (see docs/PARITY.md's accepted-limitations list) — just a passive heads-up that
    /// a newer release exists, linking straight to the GitHub release page so the user can grab it
    /// themselves (an AppImage/.dmg download, same as their first install). Never auto-applies
    /// anything. Latched to once per release version (<see cref="_passiveNoticeShownForVersion"/>)
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
        UpdateManager.UpdateInfo? update = await UpdateManager.CheckForUpdateAsync(commitEvenWhenUpdateFound: true).ConfigureAwait(false);
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
