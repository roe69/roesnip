using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using RoeSnip.Core.Capture;
using RoeSnip.Core.Diagnostics;
using RoeSnip.Core.Imaging;
using RoeSnip.Core.Recording;
using RoeSnip.Core.Recording.Gif;
using RoeSnip.App.Overlay;
using RoeSnip.App.Recording;

namespace RoeSnip.App.AppShell;

/// <summary>Wire contract for the dev-gated automation channel — ported from the frozen WPF app's
/// src/RoeSnip/App/AutomationServer.cs (that file's own doc comment has the full picture;
/// TESTING.md's "Driving RoeSnip.App programmatically" section is the user-facing reference here).
/// Split out into pure static functions — no live window, no Dispatcher, no pipe — so the JSON
/// parse/dispatch/serialize logic is unit-testable on its own, matching the WPF app's convention.
///
/// KEPT byte-identical to the WPF wire contract for every command's SHAPE (including record/preset/
/// fps/chrome, which this port does not yet implement live — see AutomationServer.HandleLine's own
/// comment for why they're still validated here rather than folded into "unknown command") so a
/// future recording port (tracked separately) only has to swap live handlers, never the protocol.
/// "confirm" accepts copy|save|share (Sharing/* subsystem, item 12), matching the WPF wire
/// contract exactly now that this port has a live Share path.</summary>
public static class AutomationProtocol
{
    /// <summary>Named-pipe name shared by AutomationServer (listens) and AutomationClient
    /// (connects). Deliberately DISTINCT from the WPF app's "RoeSnip-Automation" — both apps' own
    /// residents can run side by side (same rationale as AppShell/SingleInstance.cs's own pipe
    /// name split), and sharing a name would let whichever app's --auto client connects second
    /// silently drive the OTHER app's resident.</summary>
    public const string PipeName = "RoeSnip.App-Automation";

    /// <summary>Hard cap on one response line (spec: "ONE file write per response, 64k output
    /// cap") — a response that would exceed this is itself a protocol error, not a truncated line
    /// silently sent over the wire.</summary>
    public const int MaxResponseBytes = 64 * 1024;

    public static readonly IReadOnlyList<string> KnownCommands = new[]
    {
        "state", "trigger", "select", "record", "preset", "fps", "chrome", "escape", "screenshot",
        "confirm",
    };

    /// <summary>Parses one line of the wire protocol: must be a JSON object with a non-empty string
    /// "cmd" field (every other field is command-specific and read directly off this same object —
    /// there is no separate "args" wrapper). Never throws; returns null and sets
    /// <paramref name="error"/> on anything else.</summary>
    public static JsonObject? TryParseRequest(string line, out string? error)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(line);
        }
        catch (JsonException ex)
        {
            error = $"malformed JSON: {ex.Message}";
            return null;
        }

        if (node is not JsonObject obj)
        {
            error = "request must be a JSON object";
            return null;
        }
        if (obj["cmd"] is not JsonValue cmdValue || !cmdValue.TryGetValue(out string? cmd) || string.IsNullOrWhiteSpace(cmd))
        {
            error = "request is missing a non-empty \"cmd\" string field";
            return null;
        }

        error = null;
        return obj;
    }

    public static string CommandName(JsonObject request) => (string)request["cmd"]!;

    /// <summary>Validates a request's command-specific fields WITHOUT touching any live app state —
    /// the pure half of "command JSON parse/dispatch". Returns null when the request is well-formed
    /// for its command, else an error string. Unknown commands are rejected here too, so
    /// AutomationServer's live dispatch switch never has to define its own "unknown command"
    /// fallback message.</summary>
    public static string? ValidateArgs(string cmd, JsonObject request)
    {
        switch (cmd)
        {
            case "state":
            case "trigger":
            case "escape":
                return null;

            case "select":
                foreach (string field in SelectFields)
                {
                    if (!TryGetNumber(request, field, out _))
                    {
                        return $"select requires a numeric \"{field}\"";
                    }
                }
                return null;

            case "record":
                return TryGetString(request, "format", out string? format) && format is "gif" or "mp4"
                    ? null
                    : "record requires \"format\": \"gif\" or \"mp4\"";

            case "confirm":
                // Sharing/* subsystem (item 12): "share" is accepted alongside copy|save, matching
                // the WPF app — it needs no "path" (the upload result is a URL, not a local file),
                // same as "copy".
                if (!TryGetString(request, "action", out string? confirmAction) || confirmAction is not ("copy" or "save" or "share"))
                {
                    return "confirm requires \"action\": one of copy|save|share";
                }
                if (confirmAction == "save"
                    && (!TryGetString(request, "path", out string? confirmPath) || string.IsNullOrWhiteSpace(confirmPath)))
                {
                    return "confirm \"save\" requires a non-empty \"path\"";
                }
                return null;

            case "preset":
                return TryGetString(request, "tier", out string? tier) && tier is "max" or "quality" or "balanced" or "compact" or "minimal"
                    ? null
                    : "preset requires \"tier\": one of max|quality|balanced|compact|minimal";

            case "fps":
                // Ported verbatim from the WPF app even though recording isn't live here yet: kept
                // format-agnostic and range-only so a future recording port can reuse this line
                // untouched (see this class's own doc comment).
                if (!TryGetNumber(request, "value", out double rawFps))
                {
                    return "fps requires a numeric \"value\"";
                }
                if (rawFps != Math.Floor(rawFps))
                {
                    return "fps requires a whole-number \"value\"";
                }
                int fpsValue = (int)rawFps;
                if (fpsValue < 5 || fpsValue > 60)
                {
                    return $"fps \"{fpsValue}\" is outside the allowed 5-60 range";
                }
                return null;

            case "chrome":
                return TryGetString(request, "action", out string? action)
                       && action is "start" or "stop" or "save" or "share" or "cancel" or "pause" or "resume"
                    ? null
                    : "chrome requires \"action\": one of start|stop|save|share|cancel|pause|resume";

            case "screenshot":
                if (!TryGetString(request, "path", out string? path) || string.IsNullOrWhiteSpace(path))
                {
                    return "screenshot requires a non-empty \"path\"";
                }
                if (request["rect"] is JsonObject rect)
                {
                    foreach (string field in SelectFields)
                    {
                        if (!TryGetNumber(rect, field, out _))
                        {
                            return $"screenshot's \"rect\" requires a numeric \"{field}\"";
                        }
                    }
                }
                return null;

            default:
                return $"unknown command \"{cmd}\"";
        }
    }

    private static readonly string[] SelectFields = { "x", "y", "w", "h" };

    private static bool TryGetString(JsonObject obj, string field, out string? value)
    {
        if (obj[field] is JsonValue v && v.TryGetValue(out value))
        {
            return true;
        }
        value = null;
        return false;
    }

    private static bool TryGetNumber(JsonObject obj, string field, out double value)
    {
        if (obj[field] is JsonValue v && v.TryGetValue(out value))
        {
            return true;
        }
        value = 0;
        return false;
    }

    public static int GetInt(JsonObject obj, string field) => (int)obj[field]!.AsValue().GetValue<double>();

    /// <summary>Shorthand expansion for the CLI client: `--auto state` == `--auto '{"cmd":"state"}'`.
    /// An argument that already looks like a JSON object (starts with '{') is passed through
    /// untouched, so a caller can always fall back to full JSON even for a zero-arg command.</summary>
    public static string NormalizeClientArgument(string arg)
    {
        string trimmed = arg.Trim();
        if (trimmed.StartsWith('{'))
        {
            return trimmed;
        }
        return new JsonObject { ["cmd"] = trimmed }.ToJsonString();
    }

    public static string BuildError(string message) =>
        new JsonObject { ["ok"] = false, ["error"] = message }.ToJsonString();

    // ---------- `state` response shape (also reused by every other command's trailing snapshot) ----------

    public readonly record struct SelectionDto(int X, int Y, int W, int H);

    public readonly record struct MonitorDto(string DeviceName, int Left, int Top, int Right, int Bottom, bool IsPrimary);

    public readonly record struct FpsRangeDto(int Min, int Max);

    public readonly record struct StateSnapshot(
        string Mode,
        SelectionDto? Selection,
        string? RecordingFormat,
        string? Preset,
        string? EstimateText,
        int? Fps,
        FpsRangeDto? FpsRange,
        IReadOnlyList<MonitorDto> Monitors);

    /// <summary>Pure DTO-to-JSON step of the `state` response (and the trailing snapshot every
    /// other command's response ends with) — no live app state read here, just serialization, so
    /// this is unit-testable by constructing a StateSnapshot by hand.</summary>
    public static string SerializeState(StateSnapshot state)
    {
        var obj = new JsonObject
        {
            ["ok"] = true,
            ["mode"] = state.Mode,
            ["selection"] = state.Selection is { } s
                ? new JsonObject { ["x"] = s.X, ["y"] = s.Y, ["w"] = s.W, ["h"] = s.H }
                : null,
            ["recordingFormat"] = state.RecordingFormat,
            ["preset"] = state.Preset,
            ["estimateText"] = state.EstimateText,
            ["fps"] = state.Fps,
        };

        obj["fpsRange"] = state.FpsRange is { } fr
            ? new JsonObject { ["min"] = fr.Min, ["max"] = fr.Max }
            : null;

        var monitors = new JsonArray();
        foreach (var m in state.Monitors)
        {
            monitors.Add(new JsonObject
            {
                ["deviceName"] = m.DeviceName,
                ["left"] = m.Left,
                ["top"] = m.Top,
                ["right"] = m.Right,
                ["bottom"] = m.Bottom,
                ["isPrimary"] = m.IsPrimary,
            });
        }
        obj["monitors"] = monitors;

        return obj.ToJsonString();
    }
}

/// <summary>The dev-gated automation pipe server. ONLY constructed and started by
/// AppShell/TrayApp.cs when the process was launched with ROESNIP_AUTOMATION=1 or --automation
/// (see <see cref="IsRequested"/>) — with the gate off, this type is never instantiated: no pipe,
/// no listener thread, no behavior change whatsoever for a normal launch.
///
/// Every command below marshals onto the UI thread via <see cref="InvokeOnUi{T}"/> (a
/// bounded-timeout wrapper around Dispatcher.UIThread.InvokeAsync, not a plain synchronous call —
/// a wedged UI thread reports a clean timeout error back over the pipe instead of hanging the
/// automation client forever) and calls straight into the SAME production code a real click/drag
/// runs: OverlayWindow's own SetSelection/OnCommand via the `*ForAutomation` hooks on
/// OverlayController/OverlayWindow. Nothing here re-implements that logic.
///
/// Recording (item 21) is live: "record"/"preset"/"fps"/"chrome" dispatch to
/// OverlayController.RecordForAutomation / RecordingOrchestrator's own automation surface exactly
/// like the WPF app's own AutomationServer does against its RecordingController.</summary>
internal sealed class AutomationServer
{
    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(5);

    private readonly Action _triggerCapture;
    private CancellationTokenSource? _cts;

    public AutomationServer(Action triggerCapture)
    {
        _triggerCapture = triggerCapture;
    }

    /// <summary>True when this launch should start the automation pipe. Checked by TrayApp before
    /// it starts the Avalonia lifetime (via the real process args, Environment.GetCommandLineArgs())
    /// — AppComposition.RunTray's own hidden-flag allowlist separately makes sure a resident
    /// launched with --automation doesn't get rejected as an unknown CLI argument.</summary>
    public static bool IsRequested(string[] args) =>
        Array.IndexOf(args, "--automation") >= 0 ||
        Environment.GetEnvironmentVariable("ROESNIP_AUTOMATION") == "1";

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = RunAsync(_cts.Token);
        FileLog.Write("RoeSnip: automation pipe enabled (ROESNIP_AUTOMATION=1 / --automation).");
    }

    public void Stop() => _cts?.Cancel();

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    AutomationProtocol.PipeName, PipeDirection.InOut, maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                await ServeConnectionAsync(server, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // shutting down
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                FileLog.Write($"RoeSnip: automation pipe listener error: {ex.Message}");
                await Task.Delay(1000, token).ContinueWith(_ => { }).ConfigureAwait(false);
            }
        }
    }

    /// <summary>One client connection: a persistent line-in/line-out session (a script can send many
    /// commands over one connection) rather than connect-per-command — ends when the client closes
    /// its side, at which point the outer loop goes back to accepting the next connection.</summary>
    private async Task ServeConnectionAsync(NamedPipeServerStream server, CancellationToken token)
    {
        using var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        using var writer = new StreamWriter(server, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };

        while (!token.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(token).ConfigureAwait(false);
            if (line is null)
            {
                return; // client disconnected
            }
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            string response = HandleLine(line);
            if (Encoding.UTF8.GetByteCount(response) > AutomationProtocol.MaxResponseBytes)
            {
                response = AutomationProtocol.BuildError("response exceeded the 64k output cap");
            }
            await writer.WriteLineAsync(response).ConfigureAwait(false);
        }
    }

    private string HandleLine(string line)
    {
        var request = AutomationProtocol.TryParseRequest(line, out string? parseError);
        if (request is null)
        {
            return AutomationProtocol.BuildError(parseError!);
        }

        string cmd = AutomationProtocol.CommandName(request);
        string? validationError = AutomationProtocol.ValidateArgs(cmd, request);
        if (validationError is not null)
        {
            return AutomationProtocol.BuildError(validationError);
        }

        try
        {
            return cmd switch
            {
                "state" => HandleState(),
                "trigger" => HandleTrigger(),
                "select" => HandleSelect(request),
                "record" => HandleRecord(request),
                "preset" => HandlePreset(request),
                "fps" => HandleFps(request),
                "chrome" => HandleChrome(request),
                "escape" => HandleEscape(),
                "screenshot" => HandleScreenshot(request),
                "confirm" => HandleConfirm(request),
                _ => AutomationProtocol.BuildError($"unknown command \"{cmd}\""), // unreachable, ValidateArgs already rejected it
            };
        }
        catch (Exception ex)
        {
            return AutomationProtocol.BuildError($"command \"{cmd}\" failed: {ex.Message}");
        }
    }

    // ---------- Command handlers ----------

    private string HandleState() => AutomationProtocol.SerializeState(InvokeOnUi(GetStateSnapshot));

    private string HandleTrigger()
    {
        InvokeOnUi(() => { _triggerCapture(); return true; });
        // TriggerCapture is fire-and-forget internally (RunCaptureFlowAsync starts, then awaits the
        // overlay) — poll for the overlay to actually be up rather than racing it, so the response
        // (and thus anything the caller sends right after, e.g. `select`) reflects the real
        // post-trigger state instead of a stale "idle". "!= idle" is safe here (rather than a
        // positive mode check) because the only live modes GetStateSnapshot can report in this
        // port are "overlay" and "idle" — both derived directly from
        // OverlayController.IsSessionActive, the exact flag a follow-up command depends on.
        return AutomationProtocol.SerializeState(PollUntil(s => s.Mode != "idle"));
    }

    private string HandleSelect(JsonObject request)
    {
        var rect = RectPhysical.FromSize(
            AutomationProtocol.GetInt(request, "x"), AutomationProtocol.GetInt(request, "y"),
            AutomationProtocol.GetInt(request, "w"), AutomationProtocol.GetInt(request, "h"));

        string? error = InvokeOnUi<string?>(() =>
        {
            if (RecordingOrchestrator.IsActive)
            {
                return RecordingOrchestrator.SetSelectionForAutomation(rect);
            }
            if (OverlayController.IsSessionActive)
            {
                return OverlayController.SetSelectionForAutomation(rect);
            }
            return "idle: nothing to select (use trigger first)";
        });
        return error is not null
            ? AutomationProtocol.BuildError(error)
            : AutomationProtocol.SerializeState(InvokeOnUi(GetStateSnapshot));
    }

    /// <summary>Item 21f. Closes the overlay (RecordForAutomation raises the exact
    /// OverlayCommand.RecordMp4/RecordGif a toolbar menu pick would) and hands off to
    /// RecordingOrchestrator - polls for "setup" (a freshly opened recording session) rather than
    /// treating "idle" as settled, since there is a real timing gap between the overlay session's own
    /// completion and RecordingOrchestrator.Start actually assigning its active instance (both hop
    /// through the UI dispatcher independently) - mirrors the WPF reference's own HandleRecord.</summary>
    private string HandleRecord(JsonObject request)
    {
        var format = ((string)request["format"]!) == "gif" ? RecordingFormat.Gif : RecordingFormat.Mp4;

        string? error = InvokeOnUi<string?>(() => OverlayController.IsSessionActive
            ? OverlayController.RecordForAutomation(format)
            : "record requires an active overlay session with a selection");
        if (error is not null)
        {
            return AutomationProtocol.BuildError(error);
        }
        return AutomationProtocol.SerializeState(PollUntil(s => s.Mode == "setup"));
    }

    private string HandlePreset(JsonObject request)
    {
        var preset = ParsePreset((string)request["tier"]!);

        string? error = InvokeOnUi<string?>(() => RecordingOrchestrator.IsActive
            ? RecordingOrchestrator.SetSizePresetForAutomation(preset)
            : "preset requires an active recording session in Setup");
        return error is not null
            ? AutomationProtocol.BuildError(error)
            : AutomationProtocol.SerializeState(InvokeOnUi(GetStateSnapshot));
    }

    private string HandleFps(JsonObject request)
    {
        int fps = AutomationProtocol.GetInt(request, "value");

        string? error = InvokeOnUi<string?>(() => RecordingOrchestrator.IsActive
            ? RecordingOrchestrator.SetFpsForAutomation(fps)
            : "fps requires an active recording session in Setup");
        return error is not null
            ? AutomationProtocol.BuildError(error)
            : AutomationProtocol.SerializeState(InvokeOnUi(GetStateSnapshot));
    }

    private string HandleChrome(JsonObject request)
    {
        string action = (string)request["action"]!;

        string? error = InvokeOnUi<string?>(() => RecordingOrchestrator.IsActive
            ? RecordingOrchestrator.InvokeChromeAction(action)
            : $"chrome \"{action}\" requires an active recording session");
        return error is not null
            ? AutomationProtocol.BuildError(error)
            : AutomationProtocol.SerializeState(InvokeOnUi(GetStateSnapshot));
    }

    private static GifSizePreset ParsePreset(string tier) => tier switch
    {
        "max" => GifSizePreset.Max,
        "quality" => GifSizePreset.Quality,
        "balanced" => GifSizePreset.Balanced,
        "compact" => GifSizePreset.Compact,
        "minimal" => GifSizePreset.Minimal,
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "unreachable: ValidateArgs already rejected this"),
    };

    private string HandleEscape()
    {
        InvokeOnUi(() =>
        {
            if (RecordingOrchestrator.IsActive)
            {
                RecordingOrchestrator.InvokeChromeAction("cancel");
            }
            else if (OverlayController.IsSessionActive)
            {
                OverlayController.CancelForAutomation();
            }
            return true;
        });
        return AutomationProtocol.SerializeState(PollUntil(s => s.Mode == "idle"));
    }

    private string HandleScreenshot(JsonObject request)
    {
        string path = (string)request["path"]!;
        bool includeExcluded = request["includeExcluded"] is JsonValue v && v.TryGetValue(out bool b) && b;
        RectPhysical? explicitRect = request["rect"] is JsonObject r
            ? RectPhysical.FromSize(
                AutomationProtocol.GetInt(r, "x"), AutomationProtocol.GetInt(r, "y"),
                AutomationProtocol.GetInt(r, "w"), AutomationProtocol.GetInt(r, "h"))
            : null;

        var (width, height) = InvokeOnUi(() => CaptureScreenshot(path, explicitRect, includeExcluded));
        return new JsonObject { ["ok"] = true, ["path"] = path, ["width"] = width, ["height"] = height }.ToJsonString();
    }

    private string HandleConfirm(JsonObject request)
    {
        string action = (string)request["action"]!;
        string? path = request["path"] is JsonValue pv && pv.TryGetValue(out string? p) ? p : null;

        string? error = InvokeOnUi<string?>(() => OverlayController.IsSessionActive
            ? OverlayController.ConfirmForAutomation(action, path)
            : "confirm requires an active overlay session");
        return error is not null
            ? AutomationProtocol.BuildError(error)
            : AutomationProtocol.SerializeState(InvokeOnUi(GetStateSnapshot));
    }

    // ---------- Live state gathering (UI thread only) ----------

    private static AutomationProtocol.StateSnapshot GetStateSnapshot()
    {
        var monitors = new CaptureService().EnumerateMonitors()
            .Select(m => new AutomationProtocol.MonitorDto(
                m.DeviceName, m.BoundsPx.Left, m.BoundsPx.Top, m.BoundsPx.Right, m.BoundsPx.Bottom, m.IsPrimary))
            .ToList();

        if (RecordingOrchestrator.GetAutomationSnapshot() is { } snapshot)
        {
            string mode = snapshot.Phase switch
            {
                "Setup" => "setup",
                "Capturing" => "capturing",
                "Reviewing" => "reviewing",
                _ => snapshot.Phase.ToLowerInvariant(),
            };
            var sel = snapshot.SelectionVirtualDesktopPx;
            var fpsRange = snapshot.Format == RecordingFormat.Gif
                ? new AutomationProtocol.FpsRangeDto(RecordingSizeEstimator.GifMinFps, RecordingSizeEstimator.GifMaxFps)
                : new AutomationProtocol.FpsRangeDto(RecordingSizeEstimator.Mp4MinFps, RecordingSizeEstimator.Mp4MaxFps);
            return new AutomationProtocol.StateSnapshot(
                mode,
                new AutomationProtocol.SelectionDto(sel.Left, sel.Top, sel.Width, sel.Height),
                snapshot.Format == RecordingFormat.Gif ? "gif" : "mp4",
                snapshot.Preset.ToString().ToLowerInvariant(),
                snapshot.EstimateText,
                snapshot.Fps,
                fpsRange,
                monitors);
        }

        if (OverlayController.IsSessionActive)
        {
            var sel = OverlayController.GetSelectionForAutomation();
            return new AutomationProtocol.StateSnapshot(
                "overlay",
                sel is { } s ? new AutomationProtocol.SelectionDto(s.Left, s.Top, s.Width, s.Height) : null,
                null, null, null, null, null, monitors);
        }

        return new AutomationProtocol.StateSnapshot("idle", null, null, null, null, null, null, monitors);
    }

    /// <summary>Captures <paramref name="explicitRect"/> (or the primary monitor's full bounds) and
    /// writes a PNG. Routed through the app's OWN CaptureService/SdrImage/PngWriter pipeline (the
    /// exact same one --capture and the interactive overlay use) rather than a GDI
    /// CopyFromScreen-style raw desktop grab — that keeps this command fully portable (Windows/
    /// macOS/Linux all already implement ICaptureBackend) instead of adding a Windows-only
    /// System.Drawing dependency to a shared/App-level file. One simplification versus the WPF
    /// app's version: an explicit <paramref name="explicitRect"/> must fall within a SINGLE
    /// monitor's bounds (its top-left corner is used to pick the monitor, then the rect is clamped
    /// to that monitor's captured frame) — WPF's raw GDI capture could span monitors freely, but
    /// this port's capture pipeline is inherently per-monitor.
    /// <paramref name="includeExcluded"/> temporarily clears WDA_EXCLUDEFROMCAPTURE on this
    /// process's own windows (via WindowCaptureExclusion, item 02) for the duration of the capture
    /// — same caveat as the WPF version: the affinity change and the capture are not atomic with
    /// the compositor, so a window mid-repaint at the exact capture instant could show a stale or
    /// partially composited frame for that one screenshot; acceptable for automation/E2E, not
    /// something this method tries to eliminate.</summary>
    private static (int Width, int Height) CaptureScreenshot(string path, RectPhysical? explicitRect, bool includeExcluded)
    {
        var captureService = new CaptureService();
        var monitors = captureService.EnumerateMonitors();
        if (monitors.Count == 0)
        {
            throw new InvalidOperationException("no monitors enumerated");
        }

        MonitorInfo monitor;
        RectPhysical localRect;
        if (explicitRect is { } rect)
        {
            var normalized = rect.Normalized();
            monitor = monitors.FirstOrDefault(m =>
                normalized.Left >= m.BoundsPx.Left && normalized.Left < m.BoundsPx.Right
                && normalized.Top >= m.BoundsPx.Top && normalized.Top < m.BoundsPx.Bottom)
                ?? throw new InvalidOperationException("screenshot rect's top-left corner is not on any monitor");
            localRect = new RectPhysical(
                normalized.Left - monitor.BoundsPx.Left, normalized.Top - monitor.BoundsPx.Top,
                normalized.Right - monitor.BoundsPx.Left, normalized.Bottom - monitor.BoundsPx.Top);
        }
        else
        {
            monitor = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
            localRect = new RectPhysical(0, 0, monitor.BoundsPx.Width, monitor.BoundsPx.Height);
        }

        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        List<IntPtr>? restore = includeExcluded ? WindowCaptureExclusion.ClearOnOwnWindows() : null;
        try
        {
            var frames = captureService.CaptureAll(onlyMonitorIndex: monitor.Index);
            if (frames.Count == 0)
            {
                throw new InvalidOperationException($"capture failed for monitor {monitor.Index} ({monitor.DeviceName})");
            }

            var frame = frames[0];
            try
            {
                var image = SdrImage.FromCapturedFrame(frame, new RoeSnip.Core.Color.ToneMapOptions());
                var clamped = ClampRectToImage(localRect, image.Width, image.Height);
                var cropped = image.Crop(clamped);
                PngWriter.WriteFile(path, cropped);
                return (cropped.Width, cropped.Height);
            }
            finally
            {
                frame.Dispose();
            }
        }
        finally
        {
            if (restore is not null)
            {
                WindowCaptureExclusion.Restore(restore);
            }
        }
    }

    private static RectPhysical ClampRectToImage(RectPhysical rect, int imageWidth, int imageHeight)
    {
        var n = rect.Normalized();
        int width = Math.Clamp(n.Width, 1, imageWidth);
        int height = Math.Clamp(n.Height, 1, imageHeight);
        int left = Math.Clamp(n.Left, 0, Math.Max(0, imageWidth - width));
        int top = Math.Clamp(n.Top, 0, Math.Max(0, imageHeight - height));
        return RectPhysical.FromSize(left, top, width, height);
    }

    // ---------- UI-thread marshaling ----------

    /// <summary>Runs <paramref name="func"/> on the Avalonia UI thread and blocks the calling (pipe)
    /// thread for its result, with a 10s bound. Uses Dispatcher.UIThread.InvokeAsync +
    /// DispatcherOperation.GetTask().Wait(timeout) rather than a plain await so a timeout is
    /// unambiguous: if the UI thread never gets to it, this throws TimeoutException (turned into an
    /// ok:false response) instead of silently returning a default value that looks like a real (if
    /// empty) result. Direct analog of the WPF app's own InvokeOnUi (Dispatcher.BeginInvoke +
    /// Task.Wait(timeout)).</summary>
    private static T InvokeOnUi<T>(Func<T> func)
    {
        var task = Dispatcher.UIThread.InvokeAsync(func).GetTask();
        if (!task.Wait(UiTimeout))
        {
            throw new TimeoutException("the resident's UI thread did not respond within 10s");
        }
        if (task.IsFaulted)
        {
            throw task.Exception!.GetBaseException();
        }
        return task.Result;
    }

    private static AutomationProtocol.StateSnapshot PollUntil(Func<AutomationProtocol.StateSnapshot, bool> settled)
    {
        var deadline = DateTime.UtcNow + PollTimeout;
        AutomationProtocol.StateSnapshot snapshot;
        do
        {
            snapshot = InvokeOnUi(GetStateSnapshot);
            if (settled(snapshot))
            {
                return snapshot;
            }
            Thread.Sleep(PollInterval);
        } while (DateTime.UtcNow < deadline);
        return snapshot;
    }
}

/// <summary>`RoeSnip --auto '<json>'` (or `--auto state` shorthand for a zero-arg command):
/// connects to the resident's automation pipe, sends one line, prints the response line to STDOUT,
/// and exits 0 on ok:true / 1 on ok:false or any failure to connect/parse. Called from Program.Main
/// BEFORE CliOptions.Parse/AppComposition.RunTray — see that call site's own comment — so a client
/// invocation never touches TrayApp's single-instance mutex/pipe the way a bare launch does. Direct
/// port of the WPF app's App/AutomationServer.cs AutomationClient (including the 3ecb895
/// disposal-order fix documented inline below) against this port's own pipe name.</summary>
public static class AutomationClient
{
    public static int Run(string argument) => Run(argument, AutomationProtocol.PipeName);

    /// <summary>Pipe-name-parameterized overload. Exists so AutomationClientTests can drive this
    /// method against a private, per-test pipe instead of the real "RoeSnip.App-Automation" name —
    /// a resident running with --automation already owns that name with
    /// maxNumberOfServerInstances:1, and squatting on it from a test would both race a real
    /// resident's bind and let a stray real `--auto` invocation talk to the test's fake server
    /// mid-run. Matches this repo's "make the testable slice a plain public overload" convention.</summary>
    public static int Run(string argument, string pipeName)
    {
        string json;
        try
        {
            json = AutomationProtocol.NormalizeClientArgument(argument);
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: --auto argument is not valid JSON: {ex.Message}");
            return 1;
        }

        // Root cause of the old (WPF app) "prints the right JSON, then exits 1 with 'Cannot access
        // a closed pipe'" cosmetic bug, and why the fix has two parts:
        //
        // 1. `leaveOpen: true` on both wrappers below (the SERVER side of this same pipe — see
        //    AutomationServer's own read loop above — already does this). Without it, StreamReader/
        //    StreamWriter each close the shared `client` stream themselves on Dispose. That made the
        //    bug deterministic, not a rare race: with `using var` in declaration order
        //    client/writer/reader, disposal ran in REVERSE order — reader first, which silently
        //    closed `client` out from under the still-undisposed writer — then writer.Dispose() tried
        //    to flush an already-closed pipe and threw. `leaveOpen: true` means only the explicit
        //    `client.Dispose()` below ever actually closes the pipe, so the two wrappers disposing in
        //    either order can never race each other.
        // 2. Even with that fixed, the SERVER can still tear its own end down the instant after
        //    flushing the response (a fast request/response cycle) — a genuine remote race this
        //    process cannot prevent. `using var` would let a throw from that land inside this
        //    method's try block, get swallowed by the generic `catch (Exception)` below, and stomp
        //    the already-computed, already-printed, genuinely correct exit code with a spurious 1
        //    plus a misleading "--auto failed: Cannot access a closed pipe" — the response on
        //    STDOUT was right, only the exit code (and stderr noise) was wrong. So cleanup here is
        //    explicit and best-effort in `finally` instead, never allowed to change `exitCode` once
        //    it has been set from the parsed `ok` field.
        NamedPipeClientStream? client = null;
        StreamWriter? writer = null;
        StreamReader? reader = null;
        int exitCode;
        try
        {
            client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
            client.Connect(2000);

            writer = new StreamWriter(client, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
            reader = new StreamReader(client, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            writer.WriteLine(json);

            string? response = reader.ReadLine();
            if (response is null)
            {
                FileLog.Write("RoeSnip: automation pipe closed without a response.");
                return 1;
            }

            Console.WriteLine(response);
            using var doc = JsonDocument.Parse(response);
            bool ok = doc.RootElement.TryGetProperty("ok", out var okProp) && okProp.ValueKind == JsonValueKind.True;
            exitCode = ok ? 0 : 1;
        }
        catch (TimeoutException)
        {
            FileLog.Write(
                "RoeSnip: could not connect to the automation pipe - resident not started with ROESNIP_AUTOMATION=1 (or --automation).");
            return 1;
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: --auto failed: {ex.Message}");
            return 1;
        }
        finally
        {
            // Reverse acquisition order, each swallowed independently — a failure disposing the
            // reader must not skip disposing the writer/client, and none of these are allowed to
            // throw out of a finally (that would still replace exitCode's already-decided value
            // with an unhandled-exception exit, the exact bug this rewrite fixes).
            try { reader?.Dispose(); } catch { /* quiet: best-effort cleanup only, see comment above */ }
            try { writer?.Dispose(); } catch { /* quiet: best-effort cleanup only, see comment above */ }
            try { client?.Dispose(); } catch { /* quiet: best-effort cleanup only, see comment above */ }
        }
        return exitCode;
    }
}
