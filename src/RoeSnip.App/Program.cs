using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using RoeSnip.Core.Capture;
using RoeSnip.Core.Imaging;
using RoeSnip.Core.Settings;

namespace RoeSnip.App;

/// <summary>Data-only result of one overlay session — identical fields to the WPF app's
/// OverlayResult (PLAN.md §2.4 / PLAN-XPLAT.md §2.8). The Overlay package (WP-X3) produces this;
/// it performs Copy (clipboard) and Save (PNG dialog + file write) itself before returning. Only
/// the cross-cutting bits (HDR export, "saved" balloon) are threaded back through AppComposition.</summary>
public sealed record OverlayResult(
    MonitorInfo Monitor,
    RectPhysical SelectionPx,      // selection rect, relative to Monitor.BoundsPx origin
    SdrImage RenderedImage,         // tone-mapped crop with annotations burned in
    CapturedFrame SourceFrame,       // original (uncropped) frame for this monitor — for HDR export
    bool CopyPerformed,              // true if Overlay already wrote the image to the clipboard
    string? SavedPngPath,            // non-null if the user used Save and it succeeded
    bool SaveHdrRequested            // true if the user clicked "Save HDR" (independent of settings.AutoSaveHdrCopy)
);

/// <summary>Implemented by AppShell/TrayApp.cs (WP-X2). Passed into
/// <see cref="AppComposition.RunCaptureFlowAsync"/> so the interactive flow can surface
/// notifications without Program.cs referencing TrayApp directly.</summary>
public interface ITrayNotifier
{
    void ShowSavedBalloon(string filePath);
    void ShowError(string message);
}

/// <summary>CliMode gains two new verbs beyond the WPF app's CliOptions (PLAN-XPLAT.md §2.8):
/// TriggerCapture ("RoeSnip capture" — signal a running instance to run the interactive overlay
/// flow, or become the resident instance and do so if none is running) and TriggerSettings
/// ("RoeSnip settings" — open the settings window in the running/new instance). Diag/Capture
/// (headless, dash-prefixed) are UNCHANGED from the WPF app.</summary>
public enum CliMode { None, Diag, Capture, TriggerCapture, TriggerSettings }

public sealed record CliOptions(CliMode Mode, int? Monitor, string? Out, bool Jxr)
{
    // Grammar: --diag | --capture [--monitor N] [--out path] [--jxr] | capture | settings
    // "capture"/"settings" are bare positional verbs (no leading --) — distinct from the headless
    // "--capture" flag. Unknown/malformed args => Mode=None, Program.Main prints usage, exit 1.
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

        if (args[0] == "capture")
        {
            return args.Length == 1
                ? new CliOptions(CliMode.TriggerCapture, null, null, false)
                : Invalid();
        }

        if (args[0] == "settings")
        {
            return args.Length == 1
                ? new CliOptions(CliMode.TriggerSettings, null, null, false)
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

/// <summary>Composition root — direct analog of Phase 1's AppComposition (PLAN.md §2.4), per
/// PLAN-XPLAT.md §2.8. WP-X3 registers <see cref="RunOverlay"/> from Overlay/OverlayController.cs
/// via [ModuleInitializer]; the Windows HDR-export hook is wired by
/// <see cref="Program.RegisterPlatformHooks"/> (Platform.Windows cannot itself reference this
/// assembly — the plan's "set by JxrWriter" note is realized as App-side wiring on the windows
/// TFM). Settings are loaded via <see cref="SettingsStore"/> directly (the plan-sanctioned
/// simplification of the LoadSettings hook); the hook property is kept for structural parity.</summary>
public static class AppComposition
{
    // Kept for drop-in structural parity with Phase 1 (PLAN-XPLAT.md §2.8's own note allows the
    // direct SettingsStore.Load() route, which the shell uses; this hook stays null unless a test
    // wants to inject settings).
    public static Func<RoeSnipSettings>? LoadSettings { get; set; }

    // Set by Overlay/OverlayController.cs (WP-X3) via [ModuleInitializer].
    public static Func<
        IReadOnlyList<(CapturedFrame Frame, SdrImage Preview)>,
        RoeSnipSettings,
        Task<OverlayResult?>>? RunOverlay { get; set; }

    // Wired by Program.RegisterPlatformHooks on Windows builds — null on non-Windows builds/RIDs,
    // exactly like the WPF app's WriteJxr being null before WP-C landed.
    public static Action<string, CapturedFrame, RectPhysical>? WriteHdrExport { get; set; }

    // Set by AppShell/TrayApp.cs (WP-X2) via [ModuleInitializer].
    public static Func<string[], int>? RunTrayApp { get; set; }

    // Set by AppShell/TrayApp.cs alongside RunTrayApp: runs the resident tray app and performs
    // an initial action (capture/settings) once startup completes — the "become the resident
    // instance" half of the bare CLI verbs (PLAN-XPLAT.md §3.2 / §6 flag 4).
    internal static Func<AppShell.InstanceSignal, int>? RunResidentWithInitialAction { get; set; }

    internal static RoeSnipSettings LoadSettingsOrDefault()
    {
        try
        {
            return LoadSettings?.Invoke() ?? SettingsStore.Load();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: failed to load settings, using defaults: {ex.Message}");
            return RoeSnipSettings.Default;
        }
    }

    /// <summary>Implements --diag. Same output shape as the WPF app's RunDiagCli (a direct A/B
    /// check against src/RoeSnip's own --diag on the same machine, PLAN-XPLAT.md §4), plus the
    /// active backend name on stderr.</summary>
    public static int RunDiagCli()
    {
        CaptureService captureService;
        try
        {
            captureService = new CaptureService();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: no capture backend available: {ex.Message}");
            return 1;
        }

        Console.Error.WriteLine($"RoeSnip: backend = {captureService.BackendName}");

        var monitors = captureService.EnumerateMonitors();
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

    /// <summary>Implements the headless --capture verb — same shape as the WPF app's
    /// RunCaptureCli, with the HDR branch now gated on the backend's SupportsHdrExport capability
    /// flag in addition to the WriteHdrExport hook being present.</summary>
    public static int RunCaptureCli(CliOptions cli)
    {
        CaptureService captureService;
        try
        {
            captureService = new CaptureService();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: no capture backend available: {ex.Message}");
            return 1;
        }

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

        var toneMapOpts = new Core.Color.ToneMapOptions();
        bool anyWriteFailed = false;

        foreach (var frame in frames)
        {
            var (min, max, avg) = ComputeNitsStats(frame);
            Console.WriteLine(
                $"[{frame.Monitor.Index}] {frame.Monitor.DeviceName}  {frame.Width}x{frame.Height}  " +
                $"advancedColor={frame.Monitor.AdvancedColorActive}  format={frame.Format}  " +
                $"sdrWhite={frame.Monitor.SdrWhiteNits:0}nits  min={min:0.0}nits  max={max:0.0}nits  avg={avg:0.0}nits");

            var image = SdrImage.FromCapturedFrame(frame, toneMapOpts);
            string outPath = ResolveCaptureOutPath(cli.Out, frame.Monitor.Index, frames.Count);
            try
            {
                string? outDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outPath));
                if (!string.IsNullOrEmpty(outDir))
                {
                    System.IO.Directory.CreateDirectory(outDir);
                }

                PngWriter.WriteFile(outPath, image);
                Console.WriteLine($"  wrote {outPath}");
            }
            catch (Exception ex)
            {
                anyWriteFailed = true;
                Console.Error.WriteLine($"RoeSnip: failed to write {outPath}: {ex.Message}");
            }

            if (cli.Jxr)
            {
                if (WriteHdrExport is not null && captureService.SupportsHdrExport)
                {
                    try
                    {
                        string jxrPath = System.IO.Path.ChangeExtension(outPath, ".jxr");
                        var fullFrameRect = new RectPhysical(0, 0, frame.Width, frame.Height);
                        WriteHdrExport(jxrPath, frame, fullFrameRect);
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
                    Console.Error.WriteLine("RoeSnip: HDR export is not available on this platform/build.");
                }
            }

            frame.Dispose();
        }

        return anyWriteFailed ? 1 : 0;
    }

    /// <summary>Entry point for launching the tray app (no CLI args). If RunTrayApp is null
    /// (AppShell not present in this build), prints an error and returns 1.</summary>
    public static int RunTray(string[] args)
    {
        if (args.Length > 0)
        {
            PrintUsage();
            return 1;
        }

        if (RunTrayApp is null)
        {
            Console.Error.WriteLine("RoeSnip: the tray app is unavailable in this build (AppShell not present).");
            return 1;
        }

        return RunTrayApp(args);
    }

    /// <summary>Implements the bare "capture" verb: signal an already-running instance over
    /// AppShell/SingleInstance.cs (the resident instance does the work and this process exits 0),
    /// or — if none is running — become the resident instance, complete normal tray startup, and
    /// then immediately trigger the capture flow (PLAN-XPLAT.md §3.2 / §6 flag 4: the process
    /// stays resident afterwards; it does NOT exit after one shot).</summary>
    public static int RunTriggerCapture() => RunTrigger(AppShell.InstanceSignal.TriggerCapture);

    /// <summary>Implements the bare "settings" verb — same signal-or-become-resident semantics as
    /// <see cref="RunTriggerCapture"/>, opening the settings window instead.</summary>
    public static int RunTriggerSettings() => RunTrigger(AppShell.InstanceSignal.TriggerSettings);

    private static int RunTrigger(AppShell.InstanceSignal signal)
    {
        if (RunResidentWithInitialAction is null)
        {
            Console.Error.WriteLine("RoeSnip: the tray app is unavailable in this build (AppShell not present).");
            return 1;
        }

        return RunResidentWithInitialAction(signal);
    }

    // Reentrancy guard (audit finding B, ported from the WPF app): TriggerCapture can be invoked
    // from the hotkey, the tray menu, icon click, and the second-instance pipe. All funnel into
    // this one method, so a single Interlocked flag here covers every trigger. While a capture is
    // in progress, a new trigger is ignored (logged to stderr) rather than stacking a second
    // overlay set on top of the first (which would screenshot the first overlay's own UI).
    private static int s_captureInProgress; // 0 = idle, 1 = busy; only touched via Interlocked

    /// <summary>The interactive capture flow: capture all monitors, run the overlay, then handle
    /// the cross-cutting follow-ups (HDR auto-save / Save-HDR button, "saved" balloon). Called by
    /// WP-X2's HotkeyManager (on hotkey) and TrayApp's "Capture" menu item, passing itself as
    /// notifier. If RunOverlay is null (WP-X3 not landed), calls notifier.ShowError and returns.</summary>
    public static async Task RunCaptureFlowAsync(RoeSnipSettings settings, ITrayNotifier? notifier)
    {
        if (RunOverlay is null)
        {
            notifier?.ShowError("Overlay unavailable in this build (Overlay package not present).");
            return;
        }

        if (Interlocked.CompareExchange(ref s_captureInProgress, 1, 0) != 0)
        {
            Console.Error.WriteLine("RoeSnip: capture already in progress; ignoring trigger.");
            return;
        }

        try
        {
            // Hotkey-to-overlay latency instrumentation: this whole stretch (capture + tone-map)
            // is what the user perceives as "the overlay appearing", so it is timed and logged to
            // stderr just before the overlay is shown.
            var totalWatch = System.Diagnostics.Stopwatch.StartNew();

            var captureService = new CaptureService();
            var frames = captureService.CaptureAll();
            long captureMs = totalWatch.ElapsedMilliseconds;
            if (frames.Count == 0)
            {
                notifier?.ShowError("Capture failed on every monitor.");
                return;
            }

            // Frames must stay alive (undisposed) through the HDR-export branch below, which reads
            // result.SourceFrame — only dispose them once every post-overlay action (HDR export,
            // "saved" balloon) has completed, on every path (cancel, exception).
            try
            {
                var toneMapOpts = new Core.Color.ToneMapOptions(
                    Knee: settings.ToneMapKneeOverride ?? 0.90,
                    PeakOverride: settings.ToneMapPeakOverride);

                // Tone-map the per-monitor previews in parallel — each frame is independent, and
                // even though ToneMapper already parallelizes over rows internally, overlapping
                // the frames still shaves the per-frame scan/setup serialization on a
                // multi-monitor machine. Fixed slots keep preview order == frame order.
                var previews = new SdrImage[frames.Count];
                Parallel.For(0, frames.Count, i =>
                {
                    previews[i] = SdrImage.FromCapturedFrame(frames[i], toneMapOpts);
                });

                var monitorsWithPreview = new List<(CapturedFrame Frame, SdrImage Preview)>(frames.Count);
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
                    if (WriteHdrExport is not null && captureService.SupportsHdrExport)
                    {
                        try
                        {
                            string hdrPath = BuildHdrPath(settings, result.SavedPngPath);
                            WriteHdrExport(hdrPath, result.SourceFrame, result.SelectionPx);
                        }
                        catch (Exception ex)
                        {
                            notifier?.ShowError($"Failed to save HDR copy: {ex.Message}");
                        }
                    }
                    else if (result.SaveHdrRequested)
                    {
                        // Only surface this when the user explicitly asked for it; a silent
                        // auto-save setting shouldn't nag on every capture on a platform whose
                        // backend has no HDR export path (SupportsHdrExport == false, v1
                        // non-Windows — DESIGN-XPLAT.md).
                        notifier?.ShowError("HDR export is not available on this platform/build.");
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
        }
        finally
        {
            Interlocked.Exchange(ref s_captureInProgress, 0);
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
        // same file by inserting a per-monitor suffix (matches the WPF app).
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

    private static (double Min, double Max, double Avg) ComputeNitsStats(CapturedFrame frame)
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

    internal static void PrintUsage()
    {
        Console.Error.WriteLine("Usage: RoeSnip [--diag | --capture [--monitor N] [--out path] [--jxr] | capture | settings]");
        Console.Error.WriteLine("  --diag           Print per-monitor diagnostics and exit.");
        Console.Error.WriteLine("  --capture        Capture and save a screenshot without showing any UI.");
        Console.Error.WriteLine("    --monitor N    Capture only monitor N (0-based).");
        Console.Error.WriteLine("    --out path     Output PNG path (default: roesnip_capture_monitorN.png).");
        Console.Error.WriteLine("    --jxr          Also save the untouched HDR original as .jxr (Windows only).");
        Console.Error.WriteLine("  capture          Trigger the interactive capture flow in the running instance");
        Console.Error.WriteLine("                   (or start a resident instance and capture).");
        Console.Error.WriteLine("  settings         Open the settings window in the running/new instance.");
        Console.Error.WriteLine("  (no arguments)   Launch the tray app.");
    }
}

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // OutputType is WinExe on Windows (orchestrator-approved override to PLAN-XPLAT.md §6
        // flag 7) — reattach to the invoking terminal's console so CLI verbs still print.
        TryAttachParentConsole();

        // Platform assemblies self-register their ICaptureBackend via [ModuleInitializer], which
        // only runs when the assembly is LOADED — and .NET loads assemblies lazily on first type
        // reference. Force-load every referenced Platform.* assembly up front so
        // CaptureBackendRegistry selection sees all candidates (PLAN.md §4's ModuleInitializer
        // ordering caveat, resolved deterministically here).
        EnsurePlatformAssembliesLoaded();
        RegisterPlatformHooks();

        var cli = CliOptions.Parse(args);
        return cli.Mode switch
        {
            CliMode.Diag => AppComposition.RunDiagCli(),
            CliMode.Capture => AppComposition.RunCaptureCli(cli),
            CliMode.TriggerCapture => AppComposition.RunTriggerCapture(),
            CliMode.TriggerSettings => AppComposition.RunTriggerSettings(),
            _ => AppComposition.RunTray(args),
        };
    }

    /// <summary>Avalonia entry point used by AppShell/TrayApp.cs when this process becomes the
    /// resident tray instance (and by the Avalonia designer/previewer, which looks for exactly
    /// this method by convention).</summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static void EnsurePlatformAssembliesLoaded()
    {
        foreach (var name in new[] { "RoeSnip.Platform.Windows", "RoeSnip.Platform.MacOS", "RoeSnip.Platform.Linux" })
        {
            try
            {
                System.Reflection.Assembly.Load(name);
            }
            catch
            {
                // Not part of this TFM/RID's build — expected (§1.7's conditional references).
            }
        }
    }

    private static void RegisterPlatformHooks()
    {
#if WINDOWS
        // Realizes PLAN-XPLAT.md §2.8's "WriteHdrExport set by RoeSnip.Platform.Windows's
        // JxrWriter": Platform.Windows cannot reference this assembly (App references it, not
        // vice versa), so the App wires the hook on the windows TFM instead. Compiled only when
        // Platform.Windows is referenced (windows TFM, win/empty RID).
        AppComposition.WriteHdrExport = RoeSnip.Platform.Windows.JxrWriter.Write;
#endif
    }

    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    private static void TryAttachParentConsole()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            // Fails harmlessly when double-clicked (no parent console) or when std handles are
            // already redirected; must run before any Console output so .NET binds the attached
            // console's handles.
            AttachConsole(AttachParentProcess);
        }
        catch
        {
            // best-effort
        }
    }
}
