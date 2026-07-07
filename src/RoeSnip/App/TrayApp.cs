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
    private const string PrintScreenRegistryKeyPath = @"Control Panel\Keyboard";
    private const string PrintScreenValueName = "PrintScreenKeyForSnippingEnabled";

    private NotifyIcon? _notifyIcon;
    private HotkeyManager? _hotkeyManager;
    private System.Windows.Forms.Control? _uiThreadMarshal;
    private CancellationTokenSource? _pipeListenerCts;
    private RoeSnipSettings _settings = RoeSnipSettings.Default;
    private EventHandler? _activeBalloonClickHandler;

    /// <summary>Runs the tray app: NotifyIcon + context menu + message loop. If another instance is
    /// already running (named mutex held), signals it to run the capture flow instead and returns
    /// 0 immediately without creating a second tray icon.</summary>
    public static int Run(string[] args)
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            SignalExistingInstance();
            return 0;
        }

        var app = new TrayApp();
        return app.RunInstance();
    }

    private int RunInstance()
    {
        Application.EnableVisualStyles();

        _settings = AppComposition.LoadSettings?.Invoke() ?? RoeSnipSettings.Default;

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
        menu.Items.Add("Exit", null, (_, _) => Application.Exit());

        _notifyIcon.ContextMenuStrip = menu;
        _notifyIcon.Text = "RoeSnip";
        _notifyIcon.Visible = true;
        _notifyIcon.DoubleClick += (_, _) => TriggerCapture();

        _pipeListenerCts = new CancellationTokenSource();
        _ = ListenForSignalAsync(_pipeListenerCts.Token);

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

        // Bridge WinForms' message pump into WPF's keyboard stack — without this every WPF window
        // on this thread (overlay, settings, color picker) is keyboard-deaf even with real OS
        // focus, which is the root cause behind rounds 1-3's key/typing complaints. See
        // WpfKeyboardBridge's doc comment.
        Application.AddMessageFilter(new WpfKeyboardBridge());

        Application.Run();

        _pipeListenerCts.Cancel();
        _notifyIcon.Visible = false;
        _hotkeyManager.Dispose();
        _uiThreadMarshal.Dispose();

        return 0;
    }

    private void TriggerCapture()
    {
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
        }
    }

    private void OpenSettings()
    {
        var window = new SettingsWindow(_settings, updated =>
        {
            // Same resolved path as startup: if the saved hotkey is bare PrintScreen, run it
            // through the consent resolution before registering. PrintScreenPromptAnswered will
            // already be true after the first-ever resolution, so this cannot re-trigger the
            // prompt then — it only fires the one-time dialog in the edge case where the user
            // never had bare PrtScr configured before (prompt never applied) and just switched
            // to it now.
            _settings = ResolvePrintScreenConsent(updated);
            _hotkeyManager?.Register(_settings);
        });
        window.ShowDialog();
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
            "RoeSnip\n\n" +
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
        _notifyIcon.BalloonTipText = $"Saved {Path.GetFileName(filePath)} — click to open folder";
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
            WarmupToneMapAndEncode();
            var monitors = WarmupCaptureBackendInit();
            WarmupFlashWindows(monitors);
            WarmupCaptureSessions(monitors);
            stopwatch.Stop();
            Console.Error.WriteLine($"RoeSnip: warmup completed in {stopwatch.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            // Never let a warmup failure be visible as anything other than "the first real capture
            // is exactly as cold as before" — this is a pure optimization, not a requirement.
            Console.Error.WriteLine($"RoeSnip: warmup failed (non-fatal): {ex.Message}");
        }
    }

    /// <summary>Marshals the flash dimmer window pre-creation onto the UI thread (WPF windows must
    /// live there; the rest of warmup deliberately stays on this background thread).</summary>
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

        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var frames = new RoeSnip.Capture.CaptureService().CaptureAll(monitors);
            foreach (var frame in frames)
            {
                try
                {
                    _ = RoeSnip.Imaging.SdrImage.FromCapturedFrame(frame, new RoeSnip.Color.ToneMapOptions());
                }
                finally
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
                        RoeSnip.Overlay.OverlayController.PrewarmFlash(monitors)));
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
    /// ever flashes. The pixel data alternates a plain-SDR value and an HDR-highlight value per
    /// column so ToneMapper's pass-through AND shoulder/dither branches both actually execute
    /// (a uniform frame would only ever hit whichever single branch its one value falls into).</summary>
    private static void WarmupToneMapAndEncode()
    {
        const int size = 256;

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

        using var frame = new RoeSnip.Capture.CapturedFrame(
            RoeSnip.Capture.FrameFormat.Fp16ScRgb, size, size, size * 8, pixels, monitor);

        var sdr = RoeSnip.Imaging.SdrImage.FromCapturedFrame(frame, new RoeSnip.Color.ToneMapOptions());
        _ = sdr.ToBitmapSource();
        _ = RoeSnip.Imaging.PngWriter.Encode(sdr);
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

    private static void SignalExistingInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);
            client.WriteByte(1);
            client.Flush();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: another instance appears to be running, but signalling it failed: {ex.Message}");
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
                _ = server.ReadByte(); // drain the single signal byte
                _uiThreadMarshal?.BeginInvoke(new Action(TriggerCapture));
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
