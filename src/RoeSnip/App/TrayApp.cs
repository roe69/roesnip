using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using RoeSnip.Core.Updates;
using RoeSnip.Interop;

namespace RoeSnip.App;

/// <summary>The tray-app shell (DESIGN.md §1-2, PLAN.md §3.3): a WinForms <see cref="NotifyIcon"/>
/// with a context menu, the global hotkey, single-instance enforcement (named mutex + a named-pipe
/// signal so a second launch triggers the first instance's capture flow and exits 0), and the
/// <see cref="ITrayNotifier"/> balloons for the cross-cutting "saved"/"error" notifications that
/// <see cref="AppComposition.RunCaptureFlowAsync"/> surfaces back through this class.</summary>
public sealed class TrayApp : ITrayNotifier
{
    private const string MutexName = @"Global\RoeSnip-SingleInstance";
    private const string PipeName = "RoeSnip-SingleInstance-Capture";
    // Single-instance pipe commands: a normal launch tells the running instance to EXIT so the new
    // one can take over (running the exe = the latest build); --signal-capture asks it to snip.
    private const byte SignalCapture = 1;
    private const byte SignalExit = 2;
    private const string PrintScreenRegistryKeyPath = @"Control Panel\Keyboard";
    private const string PrintScreenValueName = "PrintScreenKeyForSnippingEnabled";

    private NotifyIcon? _notifyIcon;
    private HotkeyManager? _hotkeyManager;
    private System.Windows.Forms.Control? _uiThreadMarshal;
    private CancellationTokenSource? _pipeListenerCts;
    private RoeSnipSettings _settings = RoeSnipSettings.Default;
    private EventHandler? _activeBalloonClickHandler;
    private SettingsWindow? _settingsWindow;
    private AutomationServer? _automationServer;
    // Latches the click-to-update fallback balloon to once per release version: a persistently
    // failing download is retried every periodic tick (desirable - the network hiccup or the
    // asset could be transient), but re-showing the balloon on every one of those retries would
    // be hourly notification spam for a problem the user has already been told about once.
    private Version? _updateBalloonShownForVersion;

    /// <summary>Runs the tray app: NotifyIcon + context menu + message loop. A normal launch while
    /// another instance is already running (named mutex held) REPLACES that instance with this one -
    /// it asks the old one to exit, waits for it to release the mutex (force-terminating it as a last
    /// resort), then takes over. That way a user only has to run the exe to get the latest build; no
    /// need to hunt down and kill the old process first. The hidden --signal-capture flag keeps the
    /// old "just poke the running instance to snip" behavior for scripting/tests.</summary>
    public static int Run(string[] args)
    {
        if (Array.IndexOf(args, "--self-update-now") >= 0)
        {
            // One-shot "force update now": check GitHub and, if there is a newer release, download +
            // swap it and let the new build take over via replace-on-run, then exit - no tray icon,
            // no single-instance mutex (so it never fights the running instance the new build
            // replaces). Drives the self-updater headlessly for scripts/tests and doubles as a manual
            // force-update. See RunSelfUpdateNow.
            return RunSelfUpdateNow();
        }

        bool signalCaptureOnly = Array.IndexOf(args, "--signal-capture") >= 0;
        // Dev-gated automation channel (App/AutomationServer.cs): only checked here, never inside
        // RunInstance's normal flow, so a launch without --automation/ROESNIP_AUTOMATION=1 behaves
        // byte-for-byte like before this feature existed.
        bool automationEnabled = AutomationServer.IsRequested(args);
        var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);

        if (signalCaptureOnly)
        {
            if (!createdNew)
            {
                SignalExistingInstance(SignalCapture);
            }
            mutex.Dispose();
            return 0;
        }

        if (!createdNew)
        {
            // Ask the running instance to exit, then wait for it to drop the mutex so we can own it.
            SignalExistingInstance(SignalExit);
            bool acquired = TryAcquire(mutex, TimeSpan.FromSeconds(3));
            if (!acquired)
            {
                // It would not go quietly (hung, mid-recording, or the pipe never landed) - force it.
                KillOtherInstances();
                acquired = TryAcquire(mutex, TimeSpan.FromSeconds(2));
            }
            if (!acquired)
            {
                Console.Error.WriteLine("RoeSnip: could not take over from the running instance; leaving it in place.");
                mutex.Dispose();
                return 0;
            }
        }

        using (mutex)
        {
            var app = new TrayApp();
            return app.RunInstance(automationEnabled);
        }
    }

    /// <summary>Waits to own the single-instance mutex. AbandonedMutexException (the previous owner
    /// died without releasing) still hands us ownership, so it counts as success.</summary>
    private static bool TryAcquire(Mutex mutex, TimeSpan timeout)
    {
        try { return mutex.WaitOne(timeout); }
        catch (AbandonedMutexException) { return true; }
    }

    /// <summary>Force-terminates every OTHER RoeSnip process (last resort when a running instance
    /// will not exit on request). Best-effort per process; never throws out.</summary>
    private static void KillOtherInstances()
    {
        int self = Environment.ProcessId;
        foreach (var p in Process.GetProcessesByName("RoeSnip"))
        {
            try
            {
                if (p.Id != self)
                {
                    p.Kill();
                    p.WaitForExit(2000);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"RoeSnip: could not terminate a stale instance (pid {p.Id}): {ex.Message}");
            }
            finally
            {
                p.Dispose();
            }
        }
    }

    private int RunInstance(bool automationEnabled)
    {
        Application.EnableVisualStyles();

        _settings = AppComposition.LoadSettings?.Invoke() ?? RoeSnipSettings.Default;

        // Bug 4: make sure the save directory exists before anything (Browse, first save) touches
        // it. Save-time paths already create it (OverlayController, RecordingController, Program's
        // BuildOutputPath) so this is first-run polish, not load-bearing - never fatal.
        try
        {
            Directory.CreateDirectory(_settings.SaveDirectory);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"RoeSnip: could not create save directory '{_settings.SaveDirectory}' at startup (non-fatal): {ex.Message}");
        }

        // A hidden Control purely so the pipe-listener background task can marshal the capture
        // trigger back onto this (UI/message-loop) thread via BeginInvoke.
        _uiThreadMarshal = new System.Windows.Forms.Control();
        _uiThreadMarshal.CreateControl();

        using var trayIcon = CreateTrayIcon();
        _notifyIcon = trayIcon;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Capture", null, (_, _) => TriggerCapture());
        menu.Items.Add("Settings...", null, (_, _) => OpenSettings());
        menu.Items.Add("About", null, (_, _) => ShowAbout());
        menu.Items.Add(new ToolStripSeparator());
        if (!UpdateManager.InstallExists)
        {
            // Only offered until an install actually exists on disk. Gating on InstallExists (not
            // IsInstalled) means a rebuilt dev copy / fresh download / replace-on-run takeover of an
            // already-installed app does NOT re-offer "Install" - to the user it is already
            // installed, and the self-updater keeps that installed copy current from here.
            menu.Items.Add("Install RoeSnip", null, (_, _) => InstallRoeSnip());
        }
        menu.Items.Add("Check for updates", null, (_, _) => _ = CheckForUpdatesFromMenuAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Application.Exit());

        _notifyIcon.ContextMenuStrip = menu;
        _notifyIcon.Text = $"RoeSnip {UpdateManager.CurrentVersionText}";
        _notifyIcon.Visible = true;
        _notifyIcon.DoubleClick += (_, _) => TriggerCapture();

        _pipeListenerCts = new CancellationTokenSource();
        _ = ListenForSignalAsync(_pipeListenerCts.Token);

        if (automationEnabled)
        {
            // Dev-gated automation channel (App/AutomationServer.cs) for driving this app
            // deterministically from agents/E2E instead of synthetic mouse/UIA. TriggerCapture is
            // passed by reference (not re-implemented) so a `trigger` command runs the exact same
            // path --signal-capture/the tray icon/the hotkey already do.
            _automationServer = new AutomationServer(System.Windows.Threading.Dispatcher.CurrentDispatcher, TriggerCapture);
            _automationServer.Start();
        }

        // Self-update (CHANGE 2, periodic in this later pass): best-effort cleanup of any leftover
        // .old/.new from a prior install/update swap, then - only when this IS the installed copy
        // (a portable/dev run has nothing sensible to update itself into) - a background loop that
        // checks GitHub for a newer release and, if found, downloads and applies it automatically
        // (no click required; see CheckForUpdatesAndAutoApplyAsync for the idle-wait guard on the
        // relaunch that follows), then repeats on a user-configurable cadence for as long as this
        // process lives. The original ship (v1.6.0/1.7.0) only ever checked once, at this exact
        // startup moment - which almost never actually delivers an update, because this app spends
        // its life as a tray resident running for weeks between launches, not being relaunched
        // often enough for a startup-only check to land on a fresh release. Reports of "updates
        // never fired" trace to that: an install that predates auto-apply, a portable/dev run
        // (checks nothing, by design), or an installed copy that just hadn't been relaunched since
        // the last release shipped. RunUpdateLoopAsync below is the fix - the startup check becomes
        // its first iteration, and the periodic re-checks are what actually keeps a long-lived
        // resident current. Fire-and-forget: CheckForUpdateAsync never throws and this must never
        // delay startup or block the UI thread.
        UpdateManager.CleanupStaleUpdateFiles();
        // Background cleanup that may need a bounded retry (a still-locked file must never stall
        // startup): the source exe a prior Install() "move" left behind (pending-source-cleanup
        // marker) AND the ".old" a just-applied update swapped out - right after an update hand-off
        // the replaced process can still be exiting and holding that renamed exe locked, so the
        // synchronous CleanupStaleUpdateFiles above can miss it. Retrying here frees the ~170 MB
        // artefact the same session instead of leaving it until the next launch.
        _ = Task.Run(() =>
        {
            UpdateManager.ProcessPendingSourceCleanup();
            UpdateManager.CleanupStaleExeWithRetry();
            if (UpdateManager.IsInstalled)
            {
                // Startup ensure (not just install-time): installs made before the shortcut feature
                // existed gain search findability on their first launch after updating, and a
                // user-deleted/corrupted shortcut heals itself. No-op when it already points right.
                StartMenuShortcut.EnsureFor(UpdateManager.InstalledExePath);
            }
        });
        if (UpdateManager.IsInstalled)
        {
            // Gating once here (rather than inside the loop, on every wake) is sound because
            // IsInstalled derives from Environment.ProcessPath, which cannot change for the
            // lifetime of this process - a portable/dev copy that is never installed keeps getting
            // zero checks for as long as it runs, exactly as before this feature.
            _ = RunUpdateLoopAsync();
        }

        // Cold-start warmup (item 6b, UX round 3): runs on its own background thread so it can
        // never delay the tray icon/pipe listener above coming up, and never blocks (or is blocked
        // by) the modal PrintScreen consent dialog below.
        StartWarmup();

        // The consent resolution below can show a MODAL MessageBox (first launch on a machine
        // where Windows intercepts PrtScr for Snipping Tool), so it must come AFTER the NotifyIcon
        // and pipe listener are live — otherwise an unanswered dialog on an auto-start-at-login app
        // leaves it with no tray icon and second-instance signals going nowhere until answered.
        _settings = ResolvePrintScreenConsent(_settings);

        _hotkeyManager = new HotkeyManager(() => TriggerCapture());
        _hotkeyManager.Register(_settings);
        WarnIfPrintScreenConflict();

        // Bridge WinForms' message pump into WPF's keyboard stack — without this every WPF window
        // on this thread (overlay, settings, color picker) is keyboard-deaf even with real OS
        // focus, which is the root cause behind rounds 1-3's key/typing complaints. See
        // WpfKeyboardBridge's doc comment.
        Application.AddMessageFilter(new WpfKeyboardBridge());

        Application.Run();

        _pipeListenerCts.Cancel();
        _automationServer?.Stop();
        _notifyIcon.Visible = false;
        _hotkeyManager.Dispose();
        _uiThreadMarshal.Dispose();

        return 0;
    }

    private void TriggerCapture()
    {
        // A recording is live: PrtScr stops+saves it instead of starting a new capture flow. Direct
        // reference (not an AppComposition hook) — this file already calls
        // RoeSnip.Overlay.OverlayController's own statics the same way (MarkTriggerTimestamp,
        // TryShowFlash, ReleaseFlash below); the hook discipline in Program.cs exists to keep
        // Program.cs itself decoupled from Overlay/App/Recording, not between App and Recording.
        // Deliberately BEFORE MarkTriggerTimestamp: a stop-triggering PrtScr is not a new capture
        // trigger and must not stamp latency instrumentation or touch the flash.
        if (RoeSnip.Recording.RecordingController.IsActive)
        {
            RoeSnip.Recording.RecordingController.RequestStopAndSave();
            return;
        }

        // Honest trigger-based latency instrumentation (r5-latency, S2): stamp the moment the
        // ACTUAL trigger happened — before the flash, before capture, before anything else — so
        // every downstream latency log (hotkey-to-dim, first-overlay-visible, all-overlays-visible)
        // measures from what the user really experienced, not from whenever the flash happened to
        // start (or didn't, if it's disabled/fails). Must be the first statement in this method.
        RoeSnip.Overlay.OverlayController.MarkTriggerTimestamp();

        // Instant-response flash (r5-latency): dim every monitor within milliseconds of the
        // trigger, BEFORE the capture+tonemap stretch inside RunCaptureFlowAsync blocks this UI
        // thread. The frozen preview the real overlay later shows equals the live screen, so the
        // flash-to-overlay swap is visually seamless; OverlayController hides each monitor's flash
        // as that monitor's real overlay window renders. The ReleaseFlash in ObserveCaptureTask's
        // finally is the backstop that guarantees the flash never outlives the flow on ANY exit
        // path (capture failed on every monitor, CaptureGate busy, RunOverlay unavailable,
        // unexpected exception).
        bool flashShown = false;
        try
        {
            var flashWatch = Stopwatch.StartNew();
            var monitors = Volatile.Read(ref s_cachedMonitors) ?? RoeSnip.Capture.MonitorEnumerator.Enumerate();
            flashShown = RoeSnip.Overlay.OverlayController.TryShowFlash(monitors);
            if (flashShown)
            {
                Console.Error.WriteLine($"RoeSnip: hotkey-to-dim {flashWatch.ElapsedMilliseconds} ms");
            }
        }
        catch (Exception ex)
        {
            // The flash is a pure perceived-latency optimization — its failure must never block
            // the actual capture.
            Console.Error.WriteLine($"RoeSnip: flash dimmer failed (non-fatal): {ex.Message}");
        }

        // Fire-and-forget from a UI event handler: RunCaptureFlowAsync reports its own failures
        // via ITrayNotifier (this) internally, but that's belt-and-braces, not a guarantee — the
        // task itself must still be observed so an unexpected exception (e.g. thrown before its own
        // try/catch is entered) can't become an unobserved-task-exception crash later on the
        // finalizer thread. Route anything that slips through to the same error balloon.
        _ = ObserveCaptureTask(AppComposition.RunCaptureFlowAsync(_settings, this), flashShown);
    }

    private async Task ObserveCaptureTask(Task captureTask, bool flashShown)
    {
        try
        {
            // Deliberately NOT ConfigureAwait(false): ShowError touches the NotifyIcon, which this
            // class otherwise only ever touches from the UI/message-loop thread that's running
            // Application.Run — stay on the captured (WinForms) SynchronizationContext.
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
                // Still on the WinForms SynchronizationContext (UI thread) — see the await above.
                RoeSnip.Overlay.OverlayController.ReleaseFlash();
            }
            // Re-enumerate for the NEXT trigger's flash placement off the hot path (monitors may
            // have changed while the overlay was up).
            RefreshMonitorCacheInBackground(prewarmFlash: false);

            // Give the snip's burst allocations (~127 MB of LOH frame/preview buffers on this
            // class of machine) back to the OS once everything above — including the ContextIdle
            // pool reprovision — has run. If this flow handed the gate to a recording,
            // TrimNow's busy-check skips and RecordingSession.Stop schedules the trim instead.
            RoeSnip.IdleMemoryTrimmer.Schedule(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        }
    }

    private void OpenSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        // Bugs 2 & 5: SettingsWindow suspends the global hotkey only for the (brief) span it is
        // actively capturing a new key combination — RegisterHotKey has first claim on a
        // registered combo, so leaving it registered during capture is why re-binding PrintScreen
        // never reached the window's own key events (bug 2). The rest of the time this window is
        // open, the hotkey stays registered against _settings (whatever is currently saved), so
        // pressing it still triggers a normal snip with Settings open (bug 5).
        var window = new SettingsWindow(
            _settings,
            updated =>
            {
                // Same resolved path as startup: if the saved hotkey is bare PrintScreen, run it
                // through the consent resolution before registering. PrintScreenPromptAnswered will
                // already be true after the first-ever resolution, so this cannot re-trigger the
                // prompt then — it only fires the one-time dialog in the edge case where the user
                // never had bare PrtScr configured before (prompt never applied) and just switched
                // to it now.
                _settings = ResolvePrintScreenConsent(updated);
                _hotkeyManager?.Register(_settings);
            },
            suspendGlobalHotkey: () => _hotkeyManager?.Unregister(),
            resumeGlobalHotkey: () => _hotkeyManager?.Register(_settings));

        // Shown NON-modally (bug 5): ShowDialog disables every other top-level window on this thread,
        // including the parked overlay pool windows, so a snip triggered while Settings was open drew
        // an overlay that could not take mouse input. Show() keeps the overlay live. Track the single
        // instance so a second tray click focuses the existing window instead of stacking another.
        _settingsWindow = window;
        window.Closed += (_, _) => _settingsWindow = null;
        window.Show();
        window.Activate();
    }

    /// <summary>The one-time PrintScreen/Snipping-Tool consent flow (DESIGN.md §2), applicable only
    /// when the configured hotkey is bare PrintScreen (no modifiers). On Windows 11 an ABSENT
    /// <c>HKCU\Control Panel\Keyboard\PrintScreenKeyForSnippingEnabled</c> value means the Snipping
    /// Tool intercept is ON by default, so only an explicitly-present 0 counts as "no conflict";
    /// anything else (present nonzero, absent, unparseable) means Windows would swallow the key.
    /// In that case, unless the user has already answered the prompt once
    /// (<see cref="RoeSnipSettings.PrintScreenPromptAnswered"/>, persisted), ask via a blocking
    /// dialog: Yes writes 0 to that registry value (the write happens ONLY as the direct result of
    /// that interactive click — never unattended; no unit test exercises this path) and keeps bare
    /// PrtScr; No switches the hotkey to Ctrl+PrintScreen. Either answer is persisted with
    /// PrintScreenPromptAnswered=true so the dialog truly happens once ever. Any registry or dialog
    /// failure logs and returns the settings unchanged (fail open on the UX, not silently register
    /// nothing) without marking the prompt as answered.</summary>
    private static RoeSnipSettings ResolvePrintScreenConsent(RoeSnipSettings settings)
    {
        if (settings.HotkeyModifiers != 0 || settings.HotkeyVirtualKey != NativeMethods.VK_SNAPSHOT)
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
                // Asked once before; honor whatever the persisted answer produced (a previous "No"
                // saved HotkeyModifiers=MOD_CONTROL, a previous "Yes" wrote the registry value —
                // which the user has since re-enabled if we got here; don't nag again).
                return settings;
            }

            var result = MessageBox.Show(
                "Windows is currently set to open Snipping Tool when you press PrintScreen, which " +
                "would prevent RoeSnip's screenshot hotkey from receiving it.\n\n" +
                "Disable that Windows setting so PrintScreen triggers RoeSnip directly?\n\n" +
                "Choosing \"No\" leaves the Windows setting alone and makes RoeSnip use " +
                "Ctrl+PrintScreen instead.",
                "RoeSnip - PrintScreen hotkey",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            RoeSnipSettings updated;
            if (result == DialogResult.Yes)
            {
                try
                {
                    using var writableKey = Registry.CurrentUser.OpenSubKey(PrintScreenRegistryKeyPath, writable: true);
                    writableKey?.SetValue(PrintScreenValueName, 0, RegistryValueKind.DWord);

                    // The shell only reads PrintScreenKeyForSnippingEnabled at logon, so writing
                    // the value alone leaves it grabbing PrtScr for Snipping Tool for the rest of
                    // this session. Broadcast WM_SETTINGCHANGE (what the Settings toggle does) so
                    // it applies immediately and RegisterHotKey below actually receives PrtScr.
                    NativeMethods.SendMessageTimeout(
                        NativeMethods.HWND_BROADCAST,
                        NativeMethods.WM_SETTINGCHANGE,
                        IntPtr.Zero,
                        "Control Panel\\Keyboard",
                        NativeMethods.SMTO_ABORTIFHUNG,
                        1000,
                        out _);
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
                    HotkeyModifiers = NativeMethods.MOD_CONTROL,
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

    private static void ShowAbout()
    {
        MessageBox.Show(
            $"RoeSnip {UpdateManager.CurrentVersionText}\n\n" +
            "An HDR-correct screenshot tool. On HDR/Advanced-Color displays, RoeSnip captures the " +
            "true linear scRGB frame and tone-maps it properly (matching the SDR white level and " +
            "rolling off highlights) instead of producing the washed-out gray screenshots typical " +
            "of legacy capture tools.",
            "About RoeSnip",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    /// <inheritdoc/>
    public void ShowSavedBalloon(string filePath)
    {
        if (_notifyIcon is null) return;

        DetachActiveBalloonHandler();
        _activeBalloonClickHandler = (_, _) =>
        {
            DetachActiveBalloonHandler();
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"RoeSnip: failed to open folder for {filePath}: {ex.Message}");
            }
        };
        _notifyIcon.BalloonTipClicked += _activeBalloonClickHandler;
        _notifyIcon.BalloonTipTitle = "RoeSnip";
        _notifyIcon.BalloonTipText = $"Saved {Path.GetFileName(filePath)}. Click to open the folder.";
        _notifyIcon.ShowBalloonTip(4000);
    }

    /// <inheritdoc/>
    public void ShowError(string message)
    {
        if (_notifyIcon is null)
        {
            Console.Error.WriteLine($"RoeSnip: {message}");
            return;
        }

        DetachActiveBalloonHandler();
        _notifyIcon.BalloonTipTitle = "RoeSnip";
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.BalloonTipIcon = ToolTipIcon.Error;
        _notifyIcon.ShowBalloonTip(6000);
    }

    private void DetachActiveBalloonHandler()
    {
        if (_activeBalloonClickHandler is not null && _notifyIcon is not null)
        {
            _notifyIcon.BalloonTipClicked -= _activeBalloonClickHandler;
        }
        _activeBalloonClickHandler = null;
    }

    // ---------------- Self-update (CHANGE 2) ----------------

    /// <summary>Backs the hidden --self-update-now flag: synchronously checks GitHub for a newer
    /// release and, if there is one, downloads + swaps it (UpdateManager.ApplyUpdateAsync, which then
    /// launches the new build so replace-on-run hands off to it) before returning. No tray icon and
    /// no single-instance mutex are ever created here - this process's only job is to perform the
    /// swap and exit, leaving the freshly-launched new build as the running instance. Returns 0 on
    /// "updated" or "already current", 1 only if the check/download/swap itself errored; never
    /// throws.</summary>
    private static int RunSelfUpdateNow()
    {
        try
        {
            if (!UpdateManager.IsInstalled)
            {
                // Only the installed copy should force-update itself: CheckForUpdateAsync compares
                // the release against THIS process's version, but ApplyUpdateAsync swaps the installed
                // exe - run it from a portable/dev copy and that comparison is against the wrong
                // baseline (and with no install present it would create one with no run-at-startup
                // key). The installed copy is the only correct place to run this.
                Console.Out.WriteLine(@"RoeSnip: --self-update-now applies only to the installed copy (%LOCALAPPDATA%\RoeSnip\RoeSnip.exe).");
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

    /// <summary>The "Install RoeSnip" context-menu item (only shown when
    /// <see cref="UpdateManager.IsInstalled"/> is false): copies this portable/dev run into
    /// %LOCALAPPDATA%\RoeSnip and hands off to it, then exits this process so the installed copy
    /// takes over. Runs off the UI thread (file copy + registry write) and never lets a failure
    /// take the tray down with it: UpdateManager.Install rethrows on failure, so the catch here shows
    /// an error balloon and Application.Exit is skipped, leaving the tray running as it was.</summary>
    private void InstallRoeSnip()
    {
        _ = Task.Run(() =>
        {
            try
            {
                UpdateManager.Install();
                _uiThreadMarshal?.BeginInvoke(new Action(Application.Exit));
            }
            catch (Exception ex)
            {
                _uiThreadMarshal?.BeginInvoke(new Action(() => ShowError($"Install failed: {ex.Message}")));
            }
        });
    }

    /// <summary>The periodic update loop (only started when <see cref="UpdateManager.IsInstalled"/>
    /// is true - see the RunInstance call site's comment). Iteration zero runs immediately and IS
    /// the old startup check; after that it wakes every minute purely to re-read the current
    /// setting, and only actually performs a check once the configured interval (plus jitter) has
    /// elapsed since the last one. The 1-minute wake - not a timer re-armed to the configured
    /// interval - is what lets a Settings change take effect without any CancellationToken/re-arm
    /// plumbing: SettingsWindow's onSaved callback reassigns <c>_settings</c> synchronously, and the
    /// very next wake picks the new value up. Wall-clock elapsed (DateTime.UtcNow, never .Now - a
    /// DST jump must not double-fire or stall this) rather than counting Task.Delay iterations means
    /// a laptop resumed after sleep fires its overdue check on the next wake instead of drifting
    /// forever behind. The check is awaited INLINE, not fire-and-forgotten per tick: if an apply is
    /// parked for hours inside CheckForUpdatesAndAutoApplyAsync's idle-wait gate, this loop is parked
    /// with it, so checks can never pile up behind UpdateManager.ApplyUpdateLock. Jitter (up to
    /// interval/4, capped at 5 minutes) keeps a fleet of machines that all boot at the same moment
    /// from stampeding GitHub's API in lockstep.</summary>
    private async Task RunUpdateLoopAsync()
    {
        await CheckForUpdatesAndAutoApplyAsync().ConfigureAwait(false); // iteration zero = today's startup check
        DateTime lastCheckUtc = DateTime.UtcNow;
        TimeSpan currentInterval = UpdateCheckFrequencies.Interval(UpdateCheckFrequencies.Parse(_settings.UpdateCheckFrequency))
            ?? TimeSpan.FromHours(1);
        TimeSpan jitter = UpdateCheckFrequencies.Jitter(currentInterval, Random.Shared.NextDouble());
        while (true)
        {
            await Task.Delay(TimeSpan.FromMinutes(1)).ConfigureAwait(false); // settings-poll cadence, not network cadence
            var freq = UpdateCheckFrequencies.Parse(_settings.UpdateCheckFrequency); // FRESH read: live reconfigure
            TimeSpan? interval = UpdateCheckFrequencies.Interval(freq);
            if (interval is null)
            {
                continue; // StartupOnly: keep watching the setting, never check again on our own
            }
            if (DateTime.UtcNow - lastCheckUtc < interval + jitter)
            {
                continue;
            }
            lastCheckUtc = DateTime.UtcNow;
            jitter = UpdateCheckFrequencies.Jitter(interval.Value, Random.Shared.NextDouble());
            await CheckForUpdatesAndAutoApplyAsync().ConfigureAwait(false); // awaited INLINE: no pile-up possible
        }
    }

    /// <summary>One check-and-apply cycle: says nothing when there is nothing to offer (no update,
    /// no network, private-repo 404, a 304/rate-limit backoff - CheckForUpdateAsync already swallows
    /// all of that and returns null). When a newer release does exist, applies it automatically - no
    /// click required - but the beforeLaunch delegate passed to ApplyUpdateAsync holds the actual
    /// relaunch (and the replace-on-run kill that follows it) until CaptureGate and
    /// RecordingController both report idle, so an update landing mid-snip or mid-recording can
    /// never yank the app out from under the user; the download itself is allowed to proceed
    /// regardless since it does not touch anything live. Falls back to the old click-to-update
    /// balloon if the auto-apply throws, so a download/swap failure still leaves the user a way to
    /// update by hand - latched to once per release version (<see cref="_updateBalloonShownForVersion"/>)
    /// so a persistently failing download retried every periodic tick does not turn into hourly
    /// balloon spam; the retries themselves keep happening regardless, only the notification is
    /// latched. Called both as the loop's iteration zero (RunUpdateLoopAsync) and, on every
    /// subsequent tick, as the actual periodic recheck - never blocks its caller beyond its own
    /// await chain.</summary>
    private async Task CheckForUpdatesAndAutoApplyAsync()
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
            if (_updateBalloonShownForVersion != update.Version)
            {
                _updateBalloonShownForVersion = update.Version;
                _uiThreadMarshal?.BeginInvoke(new Action(() => ShowUpdateAvailableBalloon(update)));
            }
        }

        // Polled rather than event-driven: capture/recording sessions are short-lived (seconds to a
        // few minutes) and CaptureGate/RecordingController expose no completion signal, so a coarse
        // poll is the simplest thing that cannot miss a state change. The interval only needs to be
        // short relative to session length, not tight - the download already took far longer.
        static async Task WaitForIdleAsync()
        {
            while (CaptureGate.IsBusy || RoeSnip.Recording.RecordingController.IsActive)
            {
                await Task.Delay(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The "Check for updates" context-menu item: unlike the silent startup check, this
    /// always reports back via a MODAL MessageBox rather than a balloon - a deliberate, occasional,
    /// user-initiated click is exactly the case a MessageBox is right for, and unlike a balloon it
    /// cannot be silently eaten by Windows (Focus Assist, notification settings, or the Shell's own
    /// long-standing habit of just not showing NotifyIcon balloons are all real and common - "I
    /// clicked Check for updates and nothing happened" is the textbook symptom). Reports: not
    /// installed (nothing to update itself into), up to date, an update available (offered inline as
    /// Yes/No instead of requiring the user to notice and click a balloon), or a failure.
    /// CheckForUpdateAsync never throws in practice (it swallows network/parse failures and returns
    /// null the same as "no update"), but this still wraps the await so a check can never crash the
    /// tray if that ever changed.</summary>
    private async Task CheckForUpdatesFromMenuAsync()
    {
        if (!UpdateManager.IsInstalled)
        {
            _uiThreadMarshal?.BeginInvoke(new Action(() => MessageBox.Show(
                $"You're running RoeSnip {UpdateManager.CurrentVersionText} as a portable copy - " +
                "only the installed copy (Install RoeSnip in this menu) checks for and applies updates.",
                "RoeSnip", MessageBoxButtons.OK, MessageBoxIcon.Information)));
            return;
        }

        UpdateManager.UpdateInfo? update;
        try
        {
            // A deliberate user click deserves a real network answer even if a periodic check
            // recently tripped the rate-limit backoff.
            update = await UpdateManager.CheckForUpdateAsync(bypassBackoff: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: update check failed (non-fatal): {ex.Message}");
            _uiThreadMarshal?.BeginInvoke(new Action(() => MessageBox.Show(
                "Could not check for updates - GitHub could not be reached.",
                "RoeSnip", MessageBoxButtons.OK, MessageBoxIcon.Error)));
            return;
        }

        _uiThreadMarshal?.BeginInvoke(new Action(() =>
        {
            if (update is not null)
            {
                DialogResult choice = MessageBox.Show(
                    $"RoeSnip {update.Version} is available (you have {UpdateManager.CurrentVersionText}). Update now?",
                    "RoeSnip", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (choice == DialogResult.Yes)
                {
                    _ = ApplyUpdateFromBalloonAsync(update);
                }
            }
            else
            {
                MessageBox.Show(
                    $"RoeSnip {UpdateManager.CurrentVersionText} is up to date.",
                    "RoeSnip", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }));
    }

    /// <summary>Shows the click-to-update balloon (must already be on the UI thread). Startup checks
    /// now apply updates automatically without asking (see CheckForUpdatesOnStartupAsync); this
    /// balloon is only reached as its failure fallback, so the user still has a way to update by
    /// hand if the automatic download/swap failed. Uses the same
    /// DetachActiveBalloonHandler/_activeBalloonClickHandler pattern as ShowSavedBalloon.</summary>
    private void ShowUpdateAvailableBalloon(UpdateManager.UpdateInfo info)
    {
        if (_notifyIcon is null) return;

        DetachActiveBalloonHandler();
        _activeBalloonClickHandler = (_, _) =>
        {
            DetachActiveBalloonHandler();
            _ = ApplyUpdateFromBalloonAsync(info);
        };
        _notifyIcon.BalloonTipClicked += _activeBalloonClickHandler;
        _notifyIcon.BalloonTipTitle = "RoeSnip";
        _notifyIcon.BalloonTipText = $"RoeSnip {info.Version} is available. Click to update.";
        _notifyIcon.BalloonTipIcon = ToolTipIcon.Info;
        _notifyIcon.ShowBalloonTip(8000);
    }

    /// <summary>Runs off the UI thread (download + file swap); on success the new build takes over
    /// via the same replace-on-run signal every other launch relies on, so this instance simply
    /// gets told to exit shortly afterwards - nothing further to do here. On failure, surfaces it as
    /// an error balloon instead of letting it vanish into an unobserved task.</summary>
    private async Task ApplyUpdateFromBalloonAsync(UpdateManager.UpdateInfo info)
    {
        try
        {
            await UpdateManager.ApplyUpdateAsync(info).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _uiThreadMarshal?.BeginInvoke(new Action(() =>
                ShowError($"Update to {info.Version} failed: {ex.Message}")));
        }
    }

    // Well-known screenshot tools that grab the PrintScreen key (process name -> friendly name).
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
    /// screenshot tool (ShareX etc.). Only fires for a BARE PrintScreen hotkey - Ctrl+PrintScreen and
    /// other combos do not collide with the usual PrtScr grabs. Two signals: RegisterHotKey failed
    /// (another app already owns the exact combo via RegisterHotKey), or a known screenshot app is
    /// running (those usually grab PrtScr with a low-level keyboard hook that RegisterHotKey cannot
    /// see, so they can steal the key even though our RegisterHotKey succeeded). A non-blocking tray
    /// balloon, never a modal, so it can never gate startup.</summary>
    private void WarnIfPrintScreenConflict()
    {
        bool barePrintScreen = _settings.HotkeyModifiers == 0
            && _settings.HotkeyVirtualKey == NativeMethods.VK_SNAPSHOT;
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
        ShowWarning(
            $"{who} may be using the PrintScreen key, which can stop RoeSnip's screenshot hotkey from " +
            "working. Close it, or pick a different hotkey in RoeSnip's Settings.");
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

    private void ShowWarning(string message)
    {
        if (_notifyIcon is null)
        {
            Console.Error.WriteLine($"RoeSnip: {message}");
            return;
        }
        DetachActiveBalloonHandler();
        _notifyIcon.BalloonTipTitle = "RoeSnip";
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.BalloonTipIcon = ToolTipIcon.Warning;
        _notifyIcon.ShowBalloonTip(8000);
    }

    // ---------------- Cold-start warmup (item 6b, UX round 3; extended for r5-latency) ----------------

    /// <summary>The monitor list the instant-response flash uses (TriggerCapture must not pay a
    /// fresh DXGI/QueryDisplayConfig enumeration on the hot path). Seeded by warmup, refreshed in
    /// the background after every capture flow and on DisplaySettingsChanged; a stale list costs
    /// at worst one flash on outdated bounds (the real overlay always re-enumerates via
    /// CaptureService).</summary>
    private static IReadOnlyList<RoeSnip.Capture.MonitorInfo>? s_cachedMonitors;

    /// <summary>Pre-pays the heavy first-capture costs off the UI thread (r5-latency): JIT of the
    /// tonemap/encode path, monitor enumeration (cached for the flash), pre-creation of the
    /// per-monitor flash dimmer windows (marshalled to the UI thread — they are WPF windows), and
    /// one REAL throwaway capture per monitor through the exact CaptureService path a hotkey press
    /// takes. That throwaway capture may briefly show the OS capture border on WGC-fallback
    /// monitors — accepted at tray startup per the r5-latency brief (no session is left running,
    /// so no border persists). Fully try/caught and never fatal — a warmup failure just means the
    /// first real hotkey press is as slow as it always was, not a crash.</summary>
    private void StartWarmup()
    {
        // WM_DISPLAYCHANGE-style invalidation: keep the cached monitor list (and the pre-created
        // flash windows, via the prewarm) in sync with docking/undocking/resolution changes.
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) =>
            RefreshMonitorCacheInBackground(prewarmFlash: true);

        // Cold-start CaptureGate race fix (r5-latency, S5): flag the gate as "warmup pending" BEFORE
        // the warmup thread even starts, so a trigger that fires in the brief window between
        // StartWarmup returning and RunWarmup's first CaptureGate.TryEnter (WarmupCaptureSessions)
        // still sees WarmupPending=true and waits rather than racing straight past the not-yet-busy
        // gate into paying first-time D3D/WGC init itself (measured: winning that race cost the real
        // capture 292-1003 ms; losing it — i.e. waiting — cost ~20 ms). RunWarmup clears this in a
        // finally on every exit path (success, exception, zero monitors) — see RunWarmup below.
        RoeSnip.CaptureGate.WarmupPending = true;

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
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // Ordering (r5-latency, S4): the flash windows ARE the instant (<20 ms) response — every
            // other warmup step is slower and only matters once the flash is already up, so it comes
            // first. Monitor enumeration seeds s_cachedMonitors, which TriggerCapture's flash needs
            // before it can position itself, so it has to run before WarmupFlashWindows. Tonemap/
            // encode and the OverlayWindow type warmup both want a WPF Application to exist (the
            // latter for pack-URI resolution — see WarmupOverlayWindowType), which WarmupFlashWindows
            // is what creates (via PrewarmFlash -> EnsureApplication), so they're scheduled after it.
            // The real throwaway capture session warmup is last: it's the slowest step and holds the
            // process-wide CaptureGate, so it should delay everything above it as little as possible.
            var monitors = WarmupCaptureBackendInit();
            WarmupFlashWindows(monitors);
            WarmupOverlayPool(monitors);
            WarmupToneMapAndEncode();
            WarmupOverlayWindowType();
            WarmupCaptureSessions(monitors);
            stopwatch.Stop();
            Console.Error.WriteLine($"RoeSnip: warmup completed in {stopwatch.ElapsedMilliseconds} ms");

            // Warmup itself is a full-scale burst (throwaway full-res captures + tonemaps) — sweep
            // its garbage so the app starts its resident life at the small footprint, not at the
            // warmup high-water mark. NOT run inline here (review finding): a blocking collect on
            // this thread would fire at the exact moment the app becomes hotkey-armed and suspend
            // every managed thread — including the UI thread's 3-7 ms hotkey-to-dim path — for the
            // collect's duration. Marshal to the UI thread and take the same ApplicationIdle-
            // deferred Schedule() route as every other call site instead.
            _uiThreadMarshal?.BeginInvoke(new Action(() =>
                RoeSnip.IdleMemoryTrimmer.Schedule(System.Windows.Threading.Dispatcher.CurrentDispatcher)));
        }
        catch (Exception ex)
        {
            // Never let a warmup failure be visible as anything other than "the first real capture
            // is exactly as cold as before" — this is a pure optimization, not a requirement.
            Console.Error.WriteLine($"RoeSnip: warmup failed (non-fatal): {ex.Message}");
        }
        finally
        {
            // S5: unblock any RunCaptureFlowAsync call currently polling WarmupPending, on every
            // possible exit from this method — success, an exception caught above, or a benign
            // zero-monitors early return inside one of the Warmup* steps. A stuck WarmupPending=true
            // would otherwise make every hotkey press wait out the full 5-second poll deadline for
            // nothing.
            RoeSnip.CaptureGate.WarmupPending = false;
        }
    }

    /// <summary>Marshals the flash dimmer window pre-creation onto the UI thread (WPF windows must
    /// live there; the rest of warmup deliberately stays on this background thread).
    ///
    /// Known trade-off (review finding, r5-latency S3): PrewarmFlash primes every monitor's flash
    /// window back-to-back inside this ONE BeginInvoke callback (window creation is ~100 ms/monitor
    /// even before S3's own priming cost per window), so on a multi-monitor rig this can be a
    /// several-hundred-ms stretch of UI-thread work, and a hotkey press landing inside it is delayed
    /// until it returns. This is the same accepted "press during warmup pays a cost" trade-off
    /// WarmupCaptureSessions already documents for its own gate hold, just wider in scope than
    /// intended; PrewarmFlash's own "flash dimmer windows ready in N ms" log line (below) is the
    /// existing observability hook for measuring it. Not restructured into a chained/yielding
    /// per-monitor sequence here — that would meaningfully change EnsureCreated's atomic
    /// "does the monitor set match" semantics for comparatively low measured benefit.</summary>
    private void WarmupFlashWindows(IReadOnlyList<RoeSnip.Capture.MonitorInfo> monitors)
    {
        if (monitors.Count == 0)
        {
            return;
        }
        try
        {
            _uiThreadMarshal?.BeginInvoke(new Action(() =>
                RoeSnip.Overlay.OverlayController.PrewarmFlash(monitors)));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: flash dimmer warmup scheduling failed (non-fatal): {ex.Message}");
        }
    }

    /// <summary>Pre-builds the overlay window POOL (r5-latency round 2, D1/D4 — see
    /// OverlayWindowPool's own doc comment), scheduled right after the flash windows via a second,
    /// separately-queued _uiThreadMarshal.BeginInvoke (see RunWarmup's own comment for why flash
    /// comes first). "Scheduled after" rather than "guaranteed to run after": WarmupFlashWindows'
    /// own FlashWindow.PrepareHidden calls Dispatcher.Invoke(Loaded) per window, and that's a nested
    /// message pump on this same thread — exactly the reentrancy FlashDimmer's s_ensuringCreated
    /// guards against (see its doc comment) — so this callback can end up DISPATCHED (and even
    /// finish building) WHILE PrewarmFlash's own loop is still mid-flight, observed as this method's
    /// "overlay pool built" log line sometimes printing before "flash dimmer windows ready". Safe
    /// either way: OverlayWindowPool.EnsureBuilt has its own independent reentrancy guard and never
    /// touches FlashDimmer's state (or vice versa) — this note exists purely so a future reader
    /// isn't surprised by the interleaved log ordering. Same known trade-off as WarmupFlashWindows
    /// above: building one parked window per monitor costs real UI-thread time (each pays the same
    /// ~80-103 ms construction/Show/first-render this whole pool exists to move off the hotkey path
    /// — see this method's own "overlay pool built in N ms" log line), so a trigger landing during
    /// this stretch is delayed until it returns; accepted for the same reason WarmupFlashWindows
    /// accepts it.</summary>
    private void WarmupOverlayPool(IReadOnlyList<RoeSnip.Capture.MonitorInfo> monitors)
    {
        if (monitors.Count == 0)
        {
            return;
        }
        try
        {
            _uiThreadMarshal?.BeginInvoke(new Action(() =>
                RoeSnip.Overlay.OverlayController.PrewarmOverlayPool(monitors)));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: overlay pool warmup scheduling failed (non-fatal): {ex.Message}");
        }
    }

    /// <summary>One real throwaway capture per monitor at startup: warms the D3D/duplication/WGC
    /// machinery end to end, provisions WgcCapturer's per-monitor item/device cache, updates the
    /// DD-broken memo (so even the FIRST hotkey press skips a doomed Desktop Duplication attempt),
    /// and tone-maps each frame once so the per-monitor LUTs and the SIMD path are built/JITted at
    /// real frame sizes. Holds the process-wide CaptureGate: a concurrent real capture could
    /// otherwise race this one duplicating the same output and spuriously poison the persisted
    /// DD-broken memo. (A hotkey press inside this sub-second window logs "capture already in
    /// progress" and is dropped — the lesser evil.)</summary>
    private static void WarmupCaptureSessions(IReadOnlyList<RoeSnip.Capture.MonitorInfo> monitors)
    {
        if (monitors.Count == 0)
        {
            return;
        }
        if (!RoeSnip.CaptureGate.TryEnter())
        {
            Console.Error.WriteLine("RoeSnip: skipping warmup capture; a real capture is already in progress.");
            return;
        }
        // Flag the hold as warmup's so a real hotkey press in this window waits for the gate
        // (RunCaptureFlowAsync) instead of being dropped.
        RoeSnip.CaptureGate.HeldByWarmup = true;

        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var frames = new RoeSnip.Capture.CaptureService().CaptureAll(monitors);
            try
            {
                // Fixed-slot Parallel.For mirrors AppComposition.RunCaptureFlowAsync's real tonemap
                // step exactly (r5-latency, S7) — a plain sequential foreach only warms ToneMapper's
                // single-threaded internals, not the nested Parallel.For threadpool ramp + full-size
                // LUT/SIMD path a real hotkey press actually exercises. Measured: with the old
                // sequential warmup, the first REAL tonemap after launch still cost ~112 ms
                // (JIT/threadpool ramp) despite warmup already tonemapping real frames.
                //
                // Trigger-1/2 latency investigation (r5-latency): a single pass here still left
                // ToneMapper.ComputeGlobalMax's AVX2 max-scan running ~60-105 ms on the first TWO
                // real hotkey presses after launch (vs. ~1 ms steady-state) despite already running
                // here — repeating this loop more times did NOT fix it (tried 3 passes; measured no
                // change), ruling out a simple call-count/OSR promotion gap. A controlled
                // DOTNET_TieredCompilation=0 run collapsed the same trigger's scan time to 1-4 ms
                // immediately, isolating ordinary tiered-JIT promotion timing as the cause — fixed
                // process-wide via TieredCompilation=false in RoeSnip.csproj instead (see that
                // file's comment).
                var previews = new RoeSnip.Imaging.SdrImage[frames.Count];
                Parallel.For(0, frames.Count, i =>
                {
                    previews[i] = RoeSnip.Imaging.SdrImage.FromCapturedFrame(frames[i], new RoeSnip.Color.ToneMapOptions());
                });
            }
            finally
            {
                foreach (var frame in frames)
                {
                    frame.Dispose();
                }
            }

            // Monitors whose capture above went through (borderless) Desktop Duplication never
            // touched WGC — pre-provision WgcCapturer's cached item/device for them too (cheap,
            // and sessionless, so no border), covering the cost of a first-ever DD→WGC fallback.
            foreach (var monitor in monitors)
            {
                if (!RoeSnip.Capture.CaptureCache.Default.IsDesktopDuplicationBroken(monitor.DeviceName))
                {
                    try
                    {
                        RoeSnip.Capture.WgcCapturer.Prewarm(monitor, throwawayFrame: false);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine(
                            $"RoeSnip: WGC pre-provisioning failed for monitor {monitor.DeviceName} (non-fatal): {ex.Message}");
                    }
                }
            }
            watch.Stop();
            Console.Error.WriteLine(
                $"RoeSnip: warmup capture ({frames.Count}/{monitors.Count} monitors) completed in " +
                $"{watch.ElapsedMilliseconds} ms (the OS capture border may have flashed once on WGC monitors)");
        }
        finally
        {
            RoeSnip.CaptureGate.HeldByWarmup = false;
            RoeSnip.CaptureGate.Exit();
        }
    }

    /// <summary>Background monitor re-enumeration for <see cref="s_cachedMonitors"/>; optionally
    /// re-prewarns the flash windows on the UI thread (display-change path — PrewarmFlash itself
    /// refuses to touch windows while a flash/session is live).</summary>
    private void RefreshMonitorCacheInBackground(bool prewarmFlash)
    {
        _ = Task.Run(() =>
        {
            try
            {
                var monitors = RoeSnip.Capture.MonitorEnumerator.Enumerate();
                Volatile.Write(ref s_cachedMonitors, monitors);
                if (prewarmFlash && monitors.Count > 0)
                {
                    _uiThreadMarshal?.BeginInvoke(new Action(() =>
                    {
                        RoeSnip.Overlay.OverlayController.PrewarmFlash(monitors);
                        // D4: a real monitor-set change invalidates the pool the same way it
                        // invalidates the flash windows — EnsureBuilt's own set-comparison (see
                        // OverlayWindowPool.Matches) detects the mismatch and rebuilds wholesale.
                        RoeSnip.Overlay.OverlayController.PrewarmOverlayPool(monitors);
                    }));
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"RoeSnip: monitor cache refresh failed (non-fatal): {ex.Message}");
            }
        });
    }

    /// <summary>Exercises the exact code path a real capture's tone-map/encode stretch runs
    /// (SdrImage.FromCapturedFrame -> ToneMapper -> ToBitmapSource -> PngWriter.Encode) against a
    /// small synthetic frame with a fake MonitorInfo — no real WGC/duplication session, so nothing
    /// ever flashes.</summary>
    private static void WarmupToneMapAndEncode()
    {
        const int size = 256;
        using var frame = CreateSyntheticWarmupFrame(size);

        var sdr = RoeSnip.Imaging.SdrImage.FromCapturedFrame(frame, new RoeSnip.Color.ToneMapOptions());
        _ = sdr.ToBitmapSource();
        _ = RoeSnip.Imaging.PngWriter.Encode(sdr);
    }

    /// <summary>Builds the small synthetic Fp16 scRGB frame shared by <see
    /// cref="WarmupToneMapAndEncode"/> and <see cref="WarmupOverlayWindowType"/> — a fake MonitorInfo
    /// plus <paramref name="size"/>x<paramref name="size"/> pixel data alternating a plain-SDR value
    /// and an HDR-highlight value per column, so ToneMapper's pass-through AND shoulder/dither
    /// branches both actually execute (a uniform frame would only ever hit whichever single branch
    /// its one value falls into). Caller owns and must Dispose the returned frame.</summary>
    private static RoeSnip.Capture.CapturedFrame CreateSyntheticWarmupFrame(int size)
    {
        // Fully qualified deliberately (matching OverlayController.RunPickModeCaptureAsync's own
        // ToneMapOptions construction) — RoeSnip.Color is a sibling namespace whose "Color" type
        // would collide with System.Drawing.Color (already in scope via this file's own
        // NotifyIcon/Bitmap usage below) if brought in with a bare "using RoeSnip.Color;".
        var monitor = new RoeSnip.Capture.MonitorInfo(
            Index: -1, DeviceName: "RoeSnip-Warmup", HMonitor: 0,
            BoundsPx: new RoeSnip.Capture.RectPhysical(0, 0, size, size),
            DpiX: 96, DpiY: 96, AdvancedColorActive: true,
            SdrWhiteNits: 240.0, MaxLuminanceNits: 1000.0, IsPrimary: true);

        Span<byte> dim = stackalloc byte[2];
        Span<byte> bright = stackalloc byte[2];
        BitConverter.TryWriteBytes(dim, (Half)0.5f);   // plain SDR gray — exercises the pass-through branch
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

        return new RoeSnip.Capture.CapturedFrame(
            RoeSnip.Capture.FrameFormat.Fp16ScRgb, size, size, size * 8, pixels, monitor);
    }

    /// <summary>Warms the real OverlayWindow type (r5-latency, S6): the first-ever
    /// InitializeComponent for OverlayWindow.xaml pays BAML load for it AND its nested
    /// ToolbarControl.xaml (733 lines) plus custom-control JIT — measured as a large share of the
    /// 447 ms first flash-mode overlay. Must run on the UI thread (marshalled like
    /// WarmupFlashWindows), and deliberately NEVER Show()s the window: with no HWND,
    /// OnSourceInitialized never runs, so nothing built here can become visible or touch any real
    /// monitor/CaptureGate state — Measure+Arrange alone is enough to force the XAML visual tree to
    /// build and lay out for real, which is where the BAML/JIT cost actually lands.</summary>
    private void WarmupOverlayWindowType()
    {
        try
        {
            _uiThreadMarshal?.BeginInvoke(new Action(() =>
            {
                const int size = 256;
                RoeSnip.Capture.CapturedFrame? frame = null;
                try
                {
                    // Pack-URI resolution dependency: InitializeComponent below needs a live WPF
                    // Application to resolve pack://application:,,,/RoeSnip;component/... URIs, but
                    // this method doesn't call OverlayController.EnsureApplication itself — S4's
                    // RunWarmup ordering schedules WarmupFlashWindows (whose PrewarmFlash calls
                    // EnsureApplication) BEFORE this method, both marshalled onto the SAME
                    // _uiThreadMarshal queue via BeginInvoke, which dispatches in FIFO order — so the
                    // Application this callback needs already exists by the time it runs. If that
                    // scheduling were ever to fail (e.g. BeginInvoke itself throwing), InitializeComponent
                    // throws here and the catch below swallows it non-fatally, same as any other
                    // warmup step failure.
                    frame = CreateSyntheticWarmupFrame(size);
                    var sdr = RoeSnip.Imaging.SdrImage.FromCapturedFrame(frame, new RoeSnip.Color.ToneMapOptions());

                    var window = new RoeSnip.Overlay.OverlayWindow(
                        frame, sdr, RoeSnipSettings.Default,
                        static _ => { }, static _ => { }, static (_, _, _) => { }, static _ => { },
                        static _ => { }, static _ => { }, static _ => { },
                        pickOnlyMode: false);
                    window.Measure(new System.Windows.Size(size, size));
                    window.Arrange(new System.Windows.Rect(0, 0, size, size));
                    window.Close(); // never Show()n — see method doc.
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"RoeSnip: overlay window type warmup failed (non-fatal): {ex.Message}");
                }
                finally
                {
                    frame?.Dispose();
                }
            }));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: overlay window type warmup scheduling failed (non-fatal): {ex.Message}");
        }
    }

    /// <summary>Pre-creates the DXGI factory/adapter/output walk MonitorEnumerator does for the
    /// AdvancedColor/MaxLuminance/SDR-white-level lookups — otherwise-shared setup cost that's paid
    /// on the very first hotkey press — and seeds the cached monitor list the instant-response
    /// flash positions itself with (r5-latency).</summary>
    private static IReadOnlyList<RoeSnip.Capture.MonitorInfo> WarmupCaptureBackendInit()
    {
        var monitors = RoeSnip.Capture.MonitorEnumerator.Enumerate();
        Volatile.Write(ref s_cachedMonitors, monitors);
        return monitors;
    }

    // ---------------- Single-instance signalling (named mutex + named pipe) ----------------

    private static void SignalExistingInstance(byte command)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);
            client.WriteByte(command);
            client.Flush();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: another instance appears to be running, but signalling it (command {command}) failed: {ex.Message}");
        }
    }

    private async Task ListenForSignalAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                int command = server.ReadByte(); // the single signal byte
                if (command == SignalExit)
                {
                    // A newer instance is taking over: exit so it can own the single-instance mutex.
                    // Ends the WinForms message loop RunInstance is blocked on; its cleanup then
                    // releases the mutex the new instance is waiting on.
                    _uiThreadMarshal?.BeginInvoke(new Action(Application.Exit));
                }
                else
                {
                    _uiThreadMarshal?.BeginInvoke(new Action(TriggerCapture));
                }
            }
            catch (OperationCanceledException)
            {
                // shutting down
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                Console.Error.WriteLine($"RoeSnip: single-instance pipe listener error: {ex.Message}");
                await Task.Delay(1000, token).ContinueWith(_ => { }).ConfigureAwait(false);
            }
        }
    }

    // ---------------- Programmatic tray icon (no asset files needed) ----------------

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static NotifyIcon CreateTrayIcon()
    {
        // Prefer the on-brand app icon (Assets/roesnip.ico, embedded as a manifest resource so it
        // survives single-file publish) so the tray glyph matches the taskbar/titlebar icon. Falls
        // back to the original procedurally-drawn glyph below if the resource is ever missing or
        // fails to decode, so tray startup can never regress/fail because of the icon.
        try
        {
            using var iconStream = typeof(TrayApp).Assembly.GetManifestResourceStream("RoeSnip.Assets.roesnip.ico");
            if (iconStream is not null)
            {
                using var loaded = new Icon(iconStream, 16, 16);
                return new NotifyIcon { Icon = (Icon)loaded.Clone() };
            }
        }
        catch
        {
            // fall through to the procedural glyph below
        }

        using var bitmap = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);

            // Simple stylized camera glyph: dark body, a small "flash bump" on top, and a lens.
            using var bodyBrush = new SolidBrush(System.Drawing.Color.FromArgb(255, 74, 158, 255)); // accent blue
            using var darkBrush = new SolidBrush(System.Drawing.Color.FromArgb(255, 30, 30, 30));
            using var lensPen = new Pen(System.Drawing.Color.FromArgb(255, 30, 30, 30), 1.5f);

            g.FillRectangle(bodyBrush, 1, 4, 14, 10);
            g.FillRectangle(bodyBrush, 5, 1, 6, 3);
            g.FillEllipse(darkBrush, 4, 6, 8, 6);
            g.DrawEllipse(lensPen, 6, 8, 4, 3);
        }

        IntPtr hIcon = bitmap.GetHicon();
        try
        {
            using var tempIcon = Icon.FromHandle(hIcon);
            var icon = (Icon)tempIcon.Clone(); // Clone owns its own resources, safe to keep after DestroyIcon.
            return new NotifyIcon { Icon = icon };
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }
}

file static class ModuleInit
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Init() => AppComposition.RunTrayApp = TrayApp.Run;
}
