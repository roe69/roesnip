using System;
using System.Linq;
using RoeSnip.Core.Capture;
using RoeSnip.Core.Diagnostics;
using RoeSnip.Core.Recording;
using RoeSnip.Core.Settings;

namespace RoeSnip.App.Recording;

/// <summary>TEMPORARY: a hidden CLI verb exercising RecordingController end to end with no chrome
/// UI - the "temporary internal start/stop hook for testing" item 20's own brief calls for. Item 21
/// replaces the manual Start/Stop/Save sequence below with real chrome button clicks (and the
/// automation record/preset/fps/chrome commands); this file should be deleted once that lands and
/// the same coverage exists through the real UI + automation pipe instead.
///
/// Usage: <c>RoeSnip --record-smoketest gif|mp4 [seconds]</c> - captures a small fixed region of the
/// primary monitor for <paramref name="seconds"/> (default 3), then saves via
/// ROESNIP_RECORD_AUTOSAVE (must be set - see Program.cs's own dispatch of this verb) and prints the
/// resulting file path on success.</summary>
internal static class RecordingSmokeTest
{
    public static int Run(string[] args)
    {
        if (args.Length is < 2 or > 3 || (args[1] != "gif" && args[1] != "mp4"))
        {
            // Pure CLI usage text, not a diagnostic — see Program.PrintUsage's own doc comment.
            Console.Error.WriteLine("Usage: RoeSnip --record-smoketest gif|mp4 [seconds]");
            return 1;
        }
        var format = args[1] == "gif" ? RecordingFormat.Gif : RecordingFormat.Mp4;
        int seconds = args.Length == 3 && int.TryParse(args[2], out int s) ? s : 3;

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ROESNIP_RECORD_AUTOSAVE")))
        {
            FileLog.Write("RoeSnip: --record-smoketest requires ROESNIP_RECORD_AUTOSAVE to be set (this port has no save-file dialog yet).");
            return 1;
        }

        CaptureService captureService;
        try
        {
            captureService = new CaptureService();
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: no capture backend available: {ex.Message}");
            return 1;
        }

        var monitors = captureService.EnumerateMonitors();
        var monitor = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
        if (monitor is null)
        {
            FileLog.Write("RoeSnip: no monitor enumerated.");
            return 1;
        }

        int width = Math.Min(400, monitor.BoundsPx.Width);
        int height = Math.Min(300, monitor.BoundsPx.Height);
        var selection = RectPhysical.FromSize(0, 0, width, height);

        var settings = SettingsStore.Load();
        var session = RecordingController.StartNew(monitor, selection, format, settings);
        session.BeginCapture();
        if (!session.IsCapturing)
        {
            FileLog.Write("RoeSnip: recording failed to start capturing.");
            return 1;
        }

        FileLog.Write($"RoeSnip: recording {format} for {seconds}s ({width}x{height} @ {monitor.DeviceName})...");
        System.Threading.Thread.Sleep(seconds * 1000);

        session.StopCaptureToReview();
        string? finalPath = session.Save();
        if (finalPath is null)
        {
            FileLog.Write("RoeSnip: recording save failed.");
            return 1;
        }

        Console.WriteLine(finalPath);
        return 0;
    }
}
