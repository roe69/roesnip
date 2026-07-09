using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RoeSnip;

/// <summary>Data-only result of one overlay session. The Overlay package (WP-B) produces this;
/// it performs Copy (clipboard) and Save (PNG dialog + file write) itself before returning, using
/// only WP-A leaf APIs (PngWriter) and its own ClipboardService — so those two actions need no
/// hook. Only the cross-cutting bits (HDR export, "saved" tray balloon) are threaded back through
/// AppComposition, because they need WP-C types (JxrWriter, ITrayNotifier) that Overlay must not
/// reference directly.</summary>
public sealed record OverlayResult(
    Capture.MonitorInfo Monitor,
    Capture.RectPhysical SelectionPx,      // selection rect, relative to Monitor.BoundsPx origin
    Imaging.SdrImage RenderedImage,         // tone-mapped crop with annotations burned in
    Capture.CapturedFrame SourceFrame,       // original (uncropped) frame for this monitor — for HDR export
    bool CopyPerformed,                      // true if Overlay already wrote PNG+CF_DIBV5 to the clipboard
    string? SavedPngPath,                    // non-null if the user used Save and it succeeded
    bool SaveHdrRequested                    // true if the user clicked "Save HDR" (independent of settings.AutoSaveHdrCopy)
);

/// <summary>Settings data shape (DESIGN.md §6). Persistence (JSON load/save, fail-closed-on-unreadable)
/// is WP-C's job in App/Settings.cs; this record is the pure shape so WP-A's composition root and
/// WP-B's overlay can reference it without depending on WP-C's persistence file.</summary>
public sealed record RoeSnipSettings
{
    public int SchemaVersion { get; init; } = 1;
    public uint HotkeyModifiers { get; init; } = 0;              // MOD_* flags (0 = PrintScreen alone)
    public uint HotkeyVirtualKey { get; init; } = 0x2C;          // VK_SNAPSHOT
    public string SaveDirectory { get; init; } = DefaultSaveDirectory();
    public bool AutoSaveHdrCopy { get; init; } = false;
    public double? ToneMapKneeOverride { get; init; } = null;     // null => ToneMapper default (0.90)
    public double? ToneMapPeakOverride { get; init; } = null;     // null => derive from monitor
    public bool RunAtStartup { get; init; } = false;
    public bool CopyOnSelect { get; init; } = false;              // confirming a selection also performs Copy
    public bool PrintScreenPromptAnswered { get; init; } = false; // one-time PrtScr/Snipping-Tool consent dialog already answered

    // ---------- UX round 2 (additive; see DESIGN.md addendum / Overlay/ColorPickerWindow etc.) ----------

    /// <summary>Last 8 picked colors from the standalone ColorPickerWindow's eyedropper ("Pick"),
    /// newest first, stored as "#RRGGBB". Deduplicated (a re-pick of an existing entry moves it to
    /// the front rather than adding a second copy) via Overlay/BoundedColorList.Push.</summary>
    public List<string> RecentPickedColors { get; init; } = new();

    /// <summary>Which format rows the ColorPickerWindow shows, toggled from its gear popover. All
    /// default true (every format visible out of the box).</summary>
    public bool ColorFormatShowHex { get; init; } = true;
    public bool ColorFormatShowRgb { get; init; } = true;
    public bool ColorFormatShowHsl { get; init; } = true;
    public bool ColorFormatShowNits { get; init; } = true;

    /// <summary>Last-used text-annotation style (toolbar's text-style group), applied to new text
    /// annotations and carried across overlay sessions.</summary>
    public string TextFontFamily { get; init; } = "Segoe UI";
    public double TextFontSize { get; init; } = 20.0;
    public bool TextBold { get; init; } = false;
    public bool TextItalic { get; init; } = false;

    /// <summary>Custom colors chosen via the toolbar's "+" swatch (System.Windows.Forms.ColorDialog),
    /// stored as "#RRGGBB", newest first, capped at 8 (LRU — see Overlay/BoundedColorList.Push),
    /// rendered as extra swatches in the toolbar after the built-in presets.
    /// LEGACY as of UX round 5: superseded by <see cref="PaletteColors"/> (the whole palette is
    /// editable now); kept — read once as migration seed, never written or displayed separately —
    /// so a downgrade to an older build still sees its custom colors.</summary>
    public List<string> CustomColors { get; init; } = new();

    // ---------- UX round 5 (additive) ----------

    /// <summary>The toolbar's entire swatch palette (UX round 5, item 3): "#RRGGBB" strings in
    /// display order, fully user-editable (right-click Replace/Remove, "+" appends), capped at
    /// Overlay/SwatchPalette.MaxColors (12). Empty (the default) means "not migrated yet": consumers
    /// seed from SwatchPalette.DefaultColors + any legacy <see cref="CustomColors"/> via
    /// SwatchPalette.EffectivePalette and only persist the list once the user first edits it.</summary>
    public List<string> PaletteColors { get; init; } = new();

    public static RoeSnipSettings Default { get; } = new();

    private static string DefaultSaveDirectory() =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "RoeSnip");
}

/// <summary>Implemented by App/TrayApp.cs (WP-C). Passed into AppComposition.RunCaptureFlowAsync
/// so the interactive flow can surface balloons without Program.cs referencing TrayApp directly.</summary>
public interface ITrayNotifier
{
    void ShowSavedBalloon(string filePath);
    void ShowError(string message);
}

public enum CliMode { None, Diag, Capture }

public sealed record CliOptions(CliMode Mode, int? Monitor, string? Out, bool Jxr)
{
    // Grammar: --diag | --capture [--monitor N] [--out path] [--jxr]
    // Unknown/malformed args => Mode=None and Program.Main prints usage to stderr, exit 1.
    public static CliOptions Parse(string[] args)
    {
        if (args.Length == 0)
        {
            return new CliOptions(CliMode.None, null, null, false);
        }

        if (args[0] == "--diag")
        {
            return args.Length == 1
                ? new CliOptions(CliMode.Diag, null, null, false)
                : Invalid();
        }

        if (args[0] == "--capture")
        {
            int? monitor = null;
            string? outPath = null;
            bool jxr = false;

            int i = 1;
            while (i < args.Length)
            {
                switch (args[i])
                {
                    case "--monitor":
                        if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out int m))
                        {
                            return Invalid();
                        }
                        monitor = m;
                        i += 2;
                        break;

                    case "--out":
                        if (i + 1 >= args.Length)
                        {
                            return Invalid();
                        }
                        outPath = args[i + 1];
                        i += 2;
                        break;

                    case "--jxr":
                        jxr = true;
                        i += 1;
                        break;

                    default:
                        return Invalid();
                }
            }

            return new CliOptions(CliMode.Capture, monitor, outPath, jxr);
        }

        return Invalid();

        static CliOptions Invalid() => new(CliMode.None, null, null, false);
    }
}

/// <summary>Composition root. WP-A owns this class and file. WP-B/WP-C register their
/// capability into the hooks via [ModuleInitializer] in their own files; they never call
/// anything here except by having Program.cs's own logic invoke the hooks.</summary>
public static class AppComposition
{
    // Set by App/Settings.cs (WP-C). Null => RoeSnipSettings.Default is used everywhere.
    public static Func<RoeSnipSettings>? LoadSettings { get; set; }

    // Set by Overlay/OverlayController.cs (WP-B).
    public static Func<
        IReadOnlyList<(Capture.CapturedFrame Frame, Imaging.SdrImage Preview)>,
        RoeSnipSettings,
        Task<OverlayResult?>>? RunOverlay { get; set; }

    // Set by Imaging/JxrWriter.cs (WP-C). Writes frame (cropped to cropPx) as .jxr to path.
    public static Action<string, Capture.CapturedFrame, Capture.RectPhysical>? WriteJxr { get; set; }

    // Set by App/TrayApp.cs (WP-C). Runs the tray message loop; returns process exit code on quit.
    public static Func<string[], int>? RunTrayApp { get; set; }

    // Set by Overlay/OverlayController.cs (WP-B). Review fix (r5-latency S2): TrayApp's
    // MarkTriggerTimestamp call stamps a pending trigger BEFORE RunCaptureFlowAsync is invoked, so
    // every dead-end exit path below that never reaches RunOverlay must discard it here — otherwise
    // it sits stale until whatever OverlaySession runs next (which may be an entirely unrelated
    // pick-mode session, or a much-later hotkey press) silently inherits it. Routed through this hook
    // rather than a direct RoeSnip.Overlay.OverlayController reference so Program.cs (WP-A) stays
    // free of a WP-B dependency, matching RunOverlay's own hook pattern above.
    public static Action? ClearPendingOverlayTrigger { get; set; }

    // Set by App/ElevationManager.cs (WP-C). Hidden CLI verbs used only for the UAC round-trip when
    // SettingsWindow's "Run as administrator" checkbox is toggled from a non-elevated process — see
    // Program.Main. Both require the process to already be elevated (Verb=runas got it there) and
    // print a result instead of showing any UI.
    public static Func<int>? RunEnableElevatedStartupCli { get; set; }
    public static Func<int>? RunDisableElevatedStartupCli { get; set; }

    /// <summary>WP-A only. Implements --diag.</summary>
    public static int RunDiagCli()
    {
        var monitors = Capture.MonitorEnumerator.Enumerate();
        if (monitors.Count == 0)
        {
            Console.Error.WriteLine("RoeSnip: no monitors enumerated.");
            return 1;
        }

        foreach (var m in monitors)
        {
            Console.WriteLine(
                $"[{m.Index}] {m.DeviceName}  {m.BoundsPx.Width}x{m.BoundsPx.Height}  " +
                $"primary={m.IsPrimary}  advancedColor={m.AdvancedColorActive}  " +
                $"sdrWhite={m.SdrWhiteNits:0}nits  maxLuminance={m.MaxLuminanceNits:0}nits  " +
                $"dpi={m.DpiX}x{m.DpiY}");
        }

        return 0;
    }

    /// <summary>WP-A only for the PNG path. If cli.Jxr and WriteJxr is null, prints a warning to
    /// stderr but still writes the PNG and returns 0 (graceful degrade, so this method is fully
    /// testable before WP-C exists) — a bare `--capture --jxr` with WriteJxr unavailable is NOT
    /// treated as a hard failure.</summary>
    public static int RunCaptureCli(CliOptions cli)
    {
        var captureService = new Capture.CaptureService();
        var captureWatch = System.Diagnostics.Stopwatch.StartNew();
        var frames = captureService.CaptureAll(monitors: null, onlyMonitorIndex: cli.Monitor);
        captureWatch.Stop();
        // Latency instrumentation (stderr, like RunCaptureFlowAsync's capture-to-overlay line) so
        // hotkey-feel regressions are measurable from the CLI without launching the tray app.
        Console.Error.WriteLine($"RoeSnip: capture {captureWatch.ElapsedMilliseconds} ms");
        if (frames.Count == 0)
        {
            Console.Error.WriteLine("RoeSnip: capture failed on every monitor.");
            return 1;
        }

        var toneMapOpts = new Color.ToneMapOptions();
        bool anyWriteFailed = false;

        foreach (var frame in frames)
        {
            var (min, max, avg) = ComputeNitsStats(frame);
            Console.WriteLine(
                $"[{frame.Monitor.Index}] {frame.Monitor.DeviceName}  {frame.Width}x{frame.Height}  " +
                $"advancedColor={frame.Monitor.AdvancedColorActive}  format={frame.Format}  " +
                $"sdrWhite={frame.Monitor.SdrWhiteNits:0}nits  min={min:0.0}nits  max={max:0.0}nits  avg={avg:0.0}nits");

            var image = Imaging.SdrImage.FromCapturedFrame(frame, toneMapOpts);
            string outPath = ResolveCaptureOutPath(cli.Out, frame.Monitor.Index, frames.Count);
            try
            {
                string? outDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outPath));
                if (!string.IsNullOrEmpty(outDir))
                {
                    System.IO.Directory.CreateDirectory(outDir);
                }

                Imaging.PngWriter.WriteFile(outPath, image);
                Console.WriteLine($"  wrote {outPath}");
            }
            catch (Exception ex)
            {
                anyWriteFailed = true;
                Console.Error.WriteLine($"RoeSnip: failed to write {outPath}: {ex.Message}");
            }

            if (cli.Jxr)
            {
                if (WriteJxr is not null)
                {
                    try
                    {
                        string jxrPath = System.IO.Path.ChangeExtension(outPath, ".jxr");
                        var fullFrameRect = new Capture.RectPhysical(0, 0, frame.Width, frame.Height);
                        WriteJxr(jxrPath, frame, fullFrameRect);
                        Console.WriteLine($"  wrote {jxrPath}");
                    }
                    catch (Exception ex)
                    {
                        anyWriteFailed = true;
                        Console.Error.WriteLine($"RoeSnip: failed to write HDR copy: {ex.Message}");
                    }
                }
                else
                {
                    anyWriteFailed = true;
                    Console.Error.WriteLine("RoeSnip: HDR export unavailable — App package not present in this build.");
                }
            }

            frame.Dispose();
        }

        return anyWriteFailed ? 1 : 0;
    }

    /// <summary>Entry point for launching the tray app (no CLI args). If RunTrayApp is null
    /// (App/* not built yet), prints an error and returns 1.</summary>
    public static int RunTray(string[] args)
    {
        if (args.Length > 0)
        {
            PrintUsage();
            return 1;
        }

        if (RunTrayApp is null)
        {
            Console.Error.WriteLine("RoeSnip: the tray app is unavailable in this build (App package not present).");
            return 1;
        }

        return RunTrayApp(args);
    }

    // Reentrancy guard (audit finding B): TriggerCapture can be invoked from the hotkey, the tray
    // menu, icon double-click, and the second-instance pipe. All four funnel into this one method.
    // The gate is shared with OverlayController's pick-mode capture (CaptureGate) so a hotkey press
    // can't stack an overlay set over a pick-mode session or vice versa — two overlay stacks must
    // never coexist (one would screenshot the other's UI).

    /// <summary>The interactive capture flow: capture all monitors, run the overlay, then handle
    /// the cross-cutting follow-ups (HDR auto-save / Save-HDR button, "saved" balloon). Called by
    /// WP-C's HotkeyManager (on hotkey) and TrayApp's "Capture" menu item, passing itself as
    /// notifier. If RunOverlay is null, calls notifier.ShowError and returns.</summary>
    public static async Task RunCaptureFlowAsync(RoeSnipSettings settings, ITrayNotifier? notifier)
    {
        if (RunOverlay is null)
        {
            notifier?.ShowError("Overlay unavailable in this build (Overlay package not present).");
            // Review fix: this trigger will never reach RunOverlay/OverlaySession — discard its
            // timestamp rather than leaving it for some later, unrelated session to inherit.
            ClearPendingOverlayTrigger?.Invoke();
            return;
        }

        // Cold-start CaptureGate race fix (r5-latency, S5): if the startup warmup thread is still
        // running its Warmup* steps but hasn't reached WarmupCaptureSessions' TryEnter yet, the gate
        // itself is still free — a trigger landing in exactly that window would WIN CaptureGate
        // against the warmup and then pay first-time D3D/WGC device init itself (measured: 292-1003
        // ms for the real capture) instead of riding the warmup's own init (measured: ~20 ms once the
        // warmup has actually provisioned everything). Waiting here is strictly better: the flash
        // (S1, now on by default) has already dimmed the screen by this point, so the wait is
        // invisible, and a wedged/slow warmup still can't brick the hotkey — the poll gives up after
        // 5 seconds and proceeds regardless.
        if (CaptureGate.WarmupPending)
        {
            var warmupWait = System.Diagnostics.Stopwatch.StartNew();
            while (CaptureGate.WarmupPending && warmupWait.ElapsedMilliseconds < 5000)
            {
                await Task.Delay(25);
            }
        }

        bool entered = CaptureGate.TryEnter();
        if (!entered && CaptureGate.HeldByWarmup)
        {
            // The gate is held by the sub-second startup warmup capture, not a real session. A
            // PrtScr press landing in that window is a deliberate user action — wait the warmup
            // out (bounded) instead of silently dropping the press, which made an early press
            // right after app launch do nothing at all.
            var wait = System.Diagnostics.Stopwatch.StartNew();
            while (!entered && wait.ElapsedMilliseconds < 3000)
            {
                await Task.Delay(25);
                entered = CaptureGate.TryEnter();
            }
        }
        if (!entered)
        {
            Console.Error.WriteLine("RoeSnip: capture already in progress; ignoring trigger.");
            // Review fix: same reasoning as the RunOverlay-null branch above — this trigger is being
            // dropped and will never construct an OverlaySession.
            ClearPendingOverlayTrigger?.Invoke();
            return;
        }

        try
        {
            // Hotkey-to-overlay latency instrumentation: this whole stretch (capture + tone-map)
            // is what the user perceives as "the overlay appearing", so it is timed and logged to
            // stderr just before the overlay is shown.
            var totalWatch = System.Diagnostics.Stopwatch.StartNew();

            var captureService = new Capture.CaptureService();
            var frames = captureService.CaptureAll();
            long captureMs = totalWatch.ElapsedMilliseconds;
            if (frames.Count == 0)
            {
                notifier?.ShowError("Capture failed on every monitor.");
                // Review fix: same reasoning as above — no OverlaySession will ever be constructed
                // for this trigger.
                ClearPendingOverlayTrigger?.Invoke();
                return;
            }

            // Frames must stay alive (undisposed) through the HDR-export branch below, which reads
            // result.SourceFrame — only dispose them once every post-overlay action (HDR export,
            // "saved" balloon) has completed, on every path (cancel, exception).
            try
            {
                var toneMapOpts = new Color.ToneMapOptions(
                    Knee: settings.ToneMapKneeOverride ?? 0.90,
                    PeakOverride: settings.ToneMapPeakOverride);

                // Tone-map the per-monitor previews in parallel — each frame is independent, and
                // even though ToneMapper already parallelizes over rows internally, overlapping
                // the frames still shaves the per-frame scan/setup serialization on a
                // multi-monitor machine. Fixed slots keep preview order == frame order.
                var previews = new Imaging.SdrImage[frames.Count];
                Parallel.For(0, frames.Count, i =>
                {
                    previews[i] = Imaging.SdrImage.FromCapturedFrame(frames[i], toneMapOpts);
                });

                var monitorsWithPreview = new List<(Capture.CapturedFrame Frame, Imaging.SdrImage Preview)>(frames.Count);
                for (int i = 0; i < frames.Count; i++)
                {
                    monitorsWithPreview.Add((frames[i], previews[i]));
                }

                totalWatch.Stop();
                Console.Error.WriteLine(
                    $"RoeSnip: capture-to-overlay {totalWatch.ElapsedMilliseconds} ms " +
                    $"(capture {captureMs} ms, tonemap {totalWatch.ElapsedMilliseconds - captureMs} ms)");

                OverlayResult? result = await RunOverlay(monitorsWithPreview, settings);

                if (result is null)
                {
                    return; // user cancelled
                }

                if (result.SaveHdrRequested || settings.AutoSaveHdrCopy)
                {
                    if (WriteJxr is not null)
                    {
                        try
                        {
                            string hdrPath = BuildHdrPath(settings, result.SavedPngPath);
                            WriteJxr(hdrPath, result.SourceFrame, result.SelectionPx);
                        }
                        catch (Exception ex)
                        {
                            notifier?.ShowError($"Failed to save HDR copy: {ex.Message}");
                        }
                    }
                    else if (result.SaveHdrRequested)
                    {
                        // Only surface this when the user explicitly asked for it; a silent auto-save
                        // setting shouldn't nag on every capture in a WP-A-only build.
                        notifier?.ShowError("HDR export unavailable — App package not present in this build.");
                    }
                }

                if (result.SavedPngPath is not null)
                {
                    notifier?.ShowSavedBalloon(result.SavedPngPath);
                }
            }
            finally
            {
                foreach (var frame in frames)
                {
                    frame.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            // Guards the overlay/export flow: an unhandled exception here would otherwise strand
            // topmost overlay windows and silently drop the failure (audit finding C).
            notifier?.ShowError($"Capture failed: {ex.Message}");
            // Review fix: covers an exception thrown before RunOverlay was ever reached (e.g. inside
            // CaptureAll/tonemap) — idempotent no-op if a session already consumed the timestamp.
            ClearPendingOverlayTrigger?.Invoke();
        }
        finally
        {
            CaptureGate.Exit();
        }
    }

    private static string ResolveCaptureOutPath(string? explicitOut, int monitorIndex, int frameCount)
    {
        if (explicitOut is null)
        {
            return $"roesnip_capture_monitor{monitorIndex}.png";
        }

        if (frameCount == 1)
        {
            return explicitOut;
        }

        // Multiple monitors captured with an explicit --out: avoid every monitor overwriting the
        // same file by inserting a per-monitor suffix (not explicitly specified in PLAN.md).
        string dir = System.IO.Path.GetDirectoryName(explicitOut) ?? string.Empty;
        string name = System.IO.Path.GetFileNameWithoutExtension(explicitOut);
        string ext = System.IO.Path.GetExtension(explicitOut);
        string fileName = $"{name}_monitor{monitorIndex}{ext}";
        return dir.Length == 0 ? fileName : System.IO.Path.Combine(dir, fileName);
    }

    private static string BuildHdrPath(RoeSnipSettings settings, string? savedPngPath)
    {
        if (savedPngPath is not null)
        {
            return System.IO.Path.ChangeExtension(savedPngPath, ".jxr");
        }

        System.IO.Directory.CreateDirectory(settings.SaveDirectory);
        string fileName = $"roesnip_{DateTime.Now:yyyyMMdd_HHmmss}.jxr";
        return System.IO.Path.Combine(settings.SaveDirectory, fileName);
    }

    private static (double Min, double Max, double Avg) ComputeNitsStats(Capture.CapturedFrame frame)
    {
        double min = double.MaxValue, max = double.MinValue, sum = 0;
        long count = 0;
        for (int y = 0; y < frame.Height; y++)
        {
            for (int x = 0; x < frame.Width; x++)
            {
                double nits = frame.ReadPixelNits(x, y);
                if (nits < min) min = nits;
                if (nits > max) max = nits;
                sum += nits;
                count++;
            }
        }
        return count == 0 ? (0, 0, 0) : (min, max, sum / count);
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage: RoeSnip.exe [--diag | --capture [--monitor N] [--out path] [--jxr]]");
        Console.Error.WriteLine("  --diag           Print per-monitor diagnostics and exit.");
        Console.Error.WriteLine("  --capture        Capture and save a screenshot without showing any UI.");
        Console.Error.WriteLine("    --monitor N    Capture only monitor N (0-based).");
        Console.Error.WriteLine("    --out path     Output PNG path (default: roesnip_capture_monitorN.png).");
        Console.Error.WriteLine("    --jxr          Also save the untouched HDR original as .jxr.");
        Console.Error.WriteLine("  (no arguments)   Launch the tray app.");
    }
}

/// <summary>Single process-wide capture gate shared by the hotkey/tray/pipe flow
/// (AppComposition.RunCaptureFlowAsync) AND the color picker's pick-mode capture
/// (OverlayController.TriggerPickModeCapture) — any overlay session in progress blocks every
/// other trigger, so two overlay stacks can never coexist.</summary>
internal static class CaptureGate
{
    private static int s_busy; // 0 = idle, 1 = busy; only touched via Interlocked

    /// <summary>True from just before TrayApp.StartWarmup launches the warmup thread until
    /// RunWarmup's finally clears it on every exit path (success, exception, or a benign
    /// zero-monitors return) — a wider window than <see cref="HeldByWarmup"/>, which only covers
    /// the sub-second stretch WarmupCaptureSessions actually holds the gate for. RunCaptureFlowAsync
    /// polls this BEFORE its TryEnter to close the race where a trigger lands after StartWarmup but
    /// before WarmupCaptureSessions has entered the gate (see the poll loop below for the
    /// measurements that make waiting worth it). Defaults to false, so CLI/test callers of
    /// RunCaptureFlowAsync that never touch TrayApp are unaffected.</summary>
    public static volatile bool WarmupPending;

    /// <summary>True while the STARTUP WARMUP capture holds the gate (set/cleared by TrayApp's
    /// WarmupCaptureSessions, strictly inside its TryEnter/Exit pair). Lets RunCaptureFlowAsync
    /// tell "busy because the sub-second warmup is running" (a real hotkey press should wait that
    /// out) apart from "busy because another overlay session is live" (drop the trigger).</summary>
    public static volatile bool HeldByWarmup;

    public static bool TryEnter() => Interlocked.CompareExchange(ref s_busy, 1, 0) == 0;
    public static void Exit() => Interlocked.Exchange(ref s_busy, 0);
}

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Hidden verbs (deliberately undocumented — not in CliOptions/PrintUsage): the target of the
        // single UAC round-trip SettingsWindow performs when the "Run as administrator" checkbox is
        // toggled from a non-elevated process. Never invoked unattended.
        if (args.Length == 1 && args[0] == "--enable-elevated-startup")
        {
            return AppComposition.RunEnableElevatedStartupCli?.Invoke() ?? 1;
        }
        if (args.Length == 1 && args[0] == "--disable-elevated-startup")
        {
            return AppComposition.RunDisableElevatedStartupCli?.Invoke() ?? 1;
        }

        var cli = CliOptions.Parse(args);
        return cli.Mode switch
        {
            CliMode.Diag => AppComposition.RunDiagCli(),
            CliMode.Capture => AppComposition.RunCaptureCli(cli),
            _ => AppComposition.RunTray(args), // includes single-instance signalling, see §3.3
        };
    }
}
