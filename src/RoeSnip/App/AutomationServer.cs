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
using System.Windows.Threading;
using RoeSnip.Capture;
using RoeSnip.Interop;
using RoeSnip.Overlay;
using RoeSnip.Recording;
using RoeSnip.Recording.Gif;

namespace RoeSnip.App;

/// <summary>Wire contract for the dev-gated automation channel (App/AutomationServer.cs's own doc
/// comment has the full picture; TESTING.md's "Driving RoeSnip programmatically" section is the
/// user-facing reference). Split out into pure static functions — no live window, no Dispatcher, no
/// pipe — so the JSON parse/dispatch/serialize logic is unit-testable on its own, same as the rest
/// of this repo's convention of making the testable slice a plain public class instead of adding an
/// InternalsVisibleTo edit (see e.g. RecordingSizeEstimator's own doc comment).</summary>
public static class AutomationProtocol
{
    /// <summary>Named-pipe name shared by AutomationServer (listens) and AutomationClient
    /// (connects). Plain name, no session/user prefix — same convention TrayApp's own
    /// single-instance pipe ("RoeSnip-SingleInstance-Capture") already uses.</summary>
    public const string PipeName = "RoeSnip-Automation";

    /// <summary>Hard cap on one response line (spec: "ONE file write per response, 64k output
    /// cap") — a response that would exceed this is itself a protocol error, not a truncated line
    /// silently sent over the wire.</summary>
    public const int MaxResponseBytes = 64 * 1024;

    public static readonly IReadOnlyList<string> KnownCommands = new[]
    {
        "state", "trigger", "select", "record", "preset", "fps", "chrome", "escape", "screenshot",
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

            case "preset":
                return TryGetString(request, "tier", out string? tier) && tier is "max" or "quality" or "balanced" or "compact" or "minimal"
                    ? null
                    : "preset requires \"tier\": one of max|quality|balanced|compact|minimal";

            case "fps":
                // Quality/fps expansion workstream: fps is a free integer slider now, not four fixed
                // chips per format, so this pure/format-agnostic layer can only range-check against
                // the UNION of both formats' ranges (5-60 — GifMinFps==Mp4MinFps==5, Mp4MaxFps==60 is
                // the broader ceiling since MP4's 5-60 range is a superset of GIF's 5-50) — agents
                // get an immediate error for outright garbage (e.g. "value":0 or "value":999) without
                // a live session. Whether it's valid for the CURRENT session's own format (e.g. 55
                // offered while recording GIF, whose own ceiling is 50) needs live state and is
                // rejected by RecordingSession.SetFpsForAutomation instead — see that method's own
                // doc comment for the exact split.
                if (!TryGetNumber(request, "value", out double rawFps))
                {
                    return "fps requires a numeric \"value\"";
                }
                if (rawFps != Math.Floor(rawFps))
                {
                    return "fps requires a whole-number \"value\"";
                }
                int fpsValue = (int)rawFps;
                if (fpsValue < RecordingSizeEstimator.GifMinFps || fpsValue > RecordingSizeEstimator.Mp4MaxFps)
                {
                    return $"fps \"{fpsValue}\" is outside the allowed {RecordingSizeEstimator.GifMinFps}-{RecordingSizeEstimator.Mp4MaxFps} range";
                }
                return null;

            case "chrome":
                return TryGetString(request, "action", out string? action)
                       && action is "start" or "stop" or "save" or "cancel" or "pause" or "resume"
                    ? null
                    : "chrome requires \"action\": one of start|stop|save|cancel|pause|resume";

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

    /// <summary>Quality/fps expansion workstream: replaces the old fixed `allowedFps` array (four
    /// per-format choices) now that fps is a free integer slider — see
    /// RecordingSizeEstimator.GifMinFps/GifMaxFps/Mp4MinFps/Mp4MaxFps for where the two numbers
    /// come from.</summary>
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
/// App/TrayApp.cs's RunInstance when the process was launched with ROESNIP_AUTOMATION=1 or
/// --automation (see <see cref="IsRequested"/>) — with the gate off, this type is never
/// instantiated: no pipe, no listener thread, no behavior change whatsoever for a normal launch.
///
/// Every command below marshals onto the UI thread via <see cref="InvokeOnUi{T}"/> (a
/// bounded-timeout wrapper around Dispatcher.BeginInvoke, not a plain Dispatcher.Invoke — a wedged
/// UI thread reports a clean timeout error back over the pipe instead of hanging the automation
/// client forever) and calls straight into the SAME production code a real click/drag runs:
/// RecordingChrome's own button Click events (raised via RaiseEvent, so that button's own
/// state-gating logic still applies), RegionOutline's own drag-finish bookkeeping, OverlayWindow's
/// own SetSelection/OnCommand. Nothing here re-implements that logic — the small `*ForAutomation`
/// methods this class calls into on OverlayController/OverlayWindow/RegionOutline/
/// RecordingController/RecordingSession/RecordingChrome are the minimal hooks that were needed
/// because the real step was private; each carries its own doc comment explaining why it exists.</summary>
internal sealed class AutomationServer
{
    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(5);

    private readonly Dispatcher _dispatcher;
    private readonly Action _triggerCapture;
    private CancellationTokenSource? _cts;

    public AutomationServer(Dispatcher dispatcher, Action triggerCapture)
    {
        _dispatcher = dispatcher;
        _triggerCapture = triggerCapture;
    }

    /// <summary>True when this launch should start the automation pipe. Checked by TrayApp.Run
    /// before RunInstance; AppComposition.RunTray's own unknown-argument guard separately allowlists
    /// --automation so a resident launched with it doesn't get rejected as a bad CLI argument.</summary>
    public static bool IsRequested(string[] args) =>
        Array.IndexOf(args, "--automation") >= 0 ||
        Environment.GetEnvironmentVariable("ROESNIP_AUTOMATION") == "1";

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = RunAsync(_cts.Token);
        Console.Error.WriteLine("RoeSnip: automation pipe enabled (ROESNIP_AUTOMATION=1 / --automation).");
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
                Console.Error.WriteLine($"RoeSnip: automation pipe listener error: {ex.Message}");
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
        // post-trigger state instead of a stale "idle". The predicate is deliberately "!= idle"
        // rather than any positive mode check: every non-idle Mode GetStateSnapshot can report is
        // derived directly from OverlayController.IsSessionActive / RecordingController.IsActive —
        // the exact flags a follow-up command depends on — so this can never return ok:true before
        // one of them is actually, observably set. Contrast with HandleRecord's own predicate,
        // where "idle" is NOT a safe stand-in for "settled" (see that method's doc comment) — this
        // one has no equivalent gap because a trigger's only possible live end states are "the
        // overlay came up" or "still idle", never a transient hand-off THROUGH idle.
        return AutomationProtocol.SerializeState(PollUntil(s => s.Mode != "idle"));
    }

    private string HandleSelect(JsonObject request)
    {
        var rect = RectPhysical.FromSize(
            AutomationProtocol.GetInt(request, "x"), AutomationProtocol.GetInt(request, "y"),
            AutomationProtocol.GetInt(request, "w"), AutomationProtocol.GetInt(request, "h"));

        string? error = InvokeOnUi<string?>(() =>
        {
            if (RecordingController.IsActive)
            {
                return RecordingController.SetSelectionForAutomation(rect);
            }
            if (OverlayController.IsSessionActive)
            {
                // NOTE: OverlayController.SetSelectionForAutomation (Overlay/OverlayController.cs,
                // out of scope for this fix — see HandleRecord's own doc comment for the full
                // writeup) has a live-verified null-coalescing bug that reports this as ok:false
                // even when IsSessionActive is true here and the selection is applied successfully.
                return OverlayController.SetSelectionForAutomation(rect);
            }
            return "idle: nothing to select (use trigger first)";
        });
        return error is not null
            ? AutomationProtocol.BuildError(error)
            : AutomationProtocol.SerializeState(InvokeOnUi(GetStateSnapshot));
    }

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

        // Belt-and-braces for a genuine (if secondary) timing gap: RecordForAutomation's
        // OverlaySession.Finish() (called synchronously above, inside the InvokeOnUi that just
        // returned) clears OverlayController.s_activeSession and completes a TaskCompletionSource
        // created with RunContinuationsAsynchronously. The continuation that resumes
        // RunCaptureFlowAsync past `await RunOverlay(...)` — and that eventually calls
        // RecordingController.StartAsync, which is what assigns RecordingController.s_active — is
        // POSTED to the UI dispatcher rather than run inline, so it is not guaranteed to have run
        // yet by the time this method continues. That is a real window where OverlayController.
        // IsSessionActive is already false and RecordingController.IsActive is not yet true, in
        // which GetStateSnapshot reports "idle". The OLD code here treated "idle" as an early-exit
        // success; only "setup" (RecordingController.IsActive, freshly in Setup) is treated as
        // settled now — a genuine start failure just rides out the full PollTimeout below instead
        // of being guessed at from an ambiguous "idle" snapshot.
        //
        // The DOMINANT bug live verification actually turned up, though, was upstream of this
        // poll entirely and is NOT fixable here: OverlayController.RecordForAutomation (Overlay/
        // OverlayController.cs) is `s_activeSession?.RecordForAutomation(format) ?? "no active
        // overlay session"` — the exact null-coalescing collapse documented on RecordingController
        // .InvokeChromeAction's own doc comment (that class's four automation wrappers had, and
        // now no longer have, the identical bug). When RecordForAutomation's underlying instance
        // method legitimately returns null for SUCCESS, "?." forwards that null indistinguishably
        // from "s_activeSession was null", so "?? "no active overlay session"" fires anyway —
        // `error` above comes back non-null and this method returns BuildError before ever
        // reaching the PollUntil below, even though the record request was accepted and a
        // recording then genuinely starts a moment later. Live-reproduced: `record` immediately
        // after a successful `trigger` reports ok:false every time (not just under a race — even
        // with an explicit multi-second delay first), while a following `state` call shows the
        // recording actually started. OverlayController.cs is owned by a different, concurrent
        // workstream (Overlay/* is off-limits here — see this fix's own task boundary) and needs
        // the same "read the field into a local, branch on IT" fix RecordingController's wrappers
        // just got; flagged here rather than silently left for the next person to rediscover.
        //
        // Pure layer: none of this reasoning is testable in AutomationProtocolTests — it depends
        // on OverlayController/RecordingController's live static state and the WPF dispatcher's
        // actual continuation scheduling, exactly the "live half" AutomationProtocolTests' own
        // class doc comment says is verified end-to-end against a resident process, not there.
        return AutomationProtocol.SerializeState(PollUntil(s => s.Mode == "setup"));
    }

    private string HandlePreset(JsonObject request)
    {
        var preset = ParsePreset((string)request["tier"]!);

        string? error = InvokeOnUi<string?>(() => RecordingController.IsActive
            ? RecordingController.SetSizePresetForAutomation(preset)
            : "preset requires an active recording session in Setup");
        return error is not null
            ? AutomationProtocol.BuildError(error)
            : AutomationProtocol.SerializeState(InvokeOnUi(GetStateSnapshot));
    }

    private string HandleFps(JsonObject request)
    {
        int fps = AutomationProtocol.GetInt(request, "value");

        string? error = InvokeOnUi<string?>(() => RecordingController.IsActive
            ? RecordingController.SetFpsForAutomation(fps)
            : "fps requires an active recording session in Setup");
        return error is not null
            ? AutomationProtocol.BuildError(error)
            : AutomationProtocol.SerializeState(InvokeOnUi(GetStateSnapshot));
    }

    private string HandleChrome(JsonObject request)
    {
        string action = (string)request["action"]!;

        string? error = InvokeOnUi<string?>(() => RecordingController.IsActive
            ? RecordingController.InvokeChromeAction(action)
            : $"chrome \"{action}\" requires an active recording session");
        return error is not null
            ? AutomationProtocol.BuildError(error)
            : AutomationProtocol.SerializeState(InvokeOnUi(GetStateSnapshot));
    }

    private string HandleEscape()
    {
        InvokeOnUi(() =>
        {
            if (RecordingController.IsActive)
            {
                RecordingController.InvokeChromeAction("cancel");
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

    private static GifSizePreset ParsePreset(string tier) => tier switch
    {
        "max" => GifSizePreset.Max,
        "quality" => GifSizePreset.Quality,
        "balanced" => GifSizePreset.Balanced,
        "compact" => GifSizePreset.Compact,
        "minimal" => GifSizePreset.Minimal,
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "unreachable: ValidateArgs already rejected this"),
    };

    // ---------- Live state gathering (UI thread only) ----------

    private static AutomationProtocol.StateSnapshot GetStateSnapshot()
    {
        var monitors = MonitorEnumerator.Enumerate()
            .Select(m => new AutomationProtocol.MonitorDto(
                m.DeviceName, m.BoundsPx.Left, m.BoundsPx.Top, m.BoundsPx.Right, m.BoundsPx.Bottom, m.IsPrimary))
            .ToList();

        if (RecordingController.IsActive)
        {
            var snapshot = RecordingController.GetAutomationSnapshot()!.Value;
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

    /// <summary>Captures <paramref name="explicitRect"/> (or the primary monitor) via
    /// System.Drawing.Graphics.CopyFromScreen, from inside this process, and writes a PNG. UI
    /// thread only (called from inside an InvokeOnUi callback): RegionOutline is not
    /// capture-excluded, so it appears in a plain screenshot like any other on-screen window; the
    /// overlay/chrome/flash windows ARE WDA_EXCLUDEFROMCAPTURE and so are invisible to
    /// CopyFromScreen unless <paramref name="includeExcluded"/> temporarily clears that affinity on
    /// this process's own windows for the duration of the capture. That's a real, if small, risk:
    /// the affinity change and the capture are not atomic with whatever the compositor is doing, so
    /// a window mid-repaint at the exact capture instant could show a stale or partially composited
    /// frame for that one screenshot — acceptable for an automation/E2E screenshot, not something
    /// this method tries to eliminate.</summary>
    private static (int Width, int Height) CaptureScreenshot(string path, RectPhysical? explicitRect, bool includeExcluded)
    {
        RectPhysical rect = explicitRect ?? PrimaryMonitorBounds();

        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        List<IntPtr>? restore = includeExcluded ? ClearCaptureExclusionOnOwnWindows() : null;
        try
        {
            using var bitmap = new System.Drawing.Bitmap(rect.Width, rect.Height);
            using (var g = System.Drawing.Graphics.FromImage(bitmap))
            {
                g.CopyFromScreen(rect.Left, rect.Top, 0, 0, new System.Drawing.Size(rect.Width, rect.Height));
            }
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            return (rect.Width, rect.Height);
        }
        finally
        {
            if (restore is not null)
            {
                RestoreCaptureExclusion(restore);
            }
        }
    }

    private static RectPhysical PrimaryMonitorBounds()
    {
        var monitors = MonitorEnumerator.Enumerate();
        var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
        return primary?.BoundsPx ?? new RectPhysical(0, 0, 1920, 1080);
    }

    /// <summary>Clears WDA_EXCLUDEFROMCAPTURE on every one of THIS process's own currently-excluded
    /// WPF windows (overlay, recording chrome, flash dimmers, color picker, settings — whichever
    /// happen to be up) and returns their handles so <see cref="RestoreCaptureExclusion"/> can put
    /// the flag back on exactly those windows, not force it onto ones that were never excluded.</summary>
    private static List<IntPtr> ClearCaptureExclusionOnOwnWindows()
    {
        var cleared = new List<IntPtr>();
        if (System.Windows.Application.Current is null)
        {
            return cleared;
        }
        foreach (System.Windows.Window window in System.Windows.Application.Current.Windows)
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero)
                {
                    continue;
                }
                if (NativeMethods.GetWindowDisplayAffinity(hwnd, out uint affinity)
                    && affinity == NativeMethods.WDA_EXCLUDEFROMCAPTURE
                    && NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_NONE))
                {
                    cleared.Add(hwnd);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"RoeSnip: automation screenshot could not inspect a window's capture affinity (non-fatal): {ex.Message}");
            }
        }
        return cleared;
    }

    private static void RestoreCaptureExclusion(List<IntPtr> handles)
    {
        foreach (var hwnd in handles)
        {
            try
            {
                NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"RoeSnip: automation screenshot failed to restore capture exclusion (non-fatal): {ex.Message}");
            }
        }
    }

    // ---------- UI-thread marshaling ----------

    /// <summary>Runs <paramref name="func"/> on the UI thread and blocks the calling (pipe) thread
    /// for its result, with a 10s bound. Uses Dispatcher.BeginInvoke + Task.Wait(timeout) rather
    /// than Dispatcher.Invoke(Delegate, TimeSpan) so a timeout is unambiguous: if the UI thread
    /// never gets to it, this throws TimeoutException (turned into an ok:false response) instead of
    /// silently returning a default value that looks like a real (if empty) result.</summary>
    private T InvokeOnUi<T>(Func<T> func)
    {
        T? value = default;
        Exception? error = null;
        var operation = _dispatcher.BeginInvoke(new Action(() =>
        {
            try { value = func(); }
            catch (Exception ex) { error = ex; }
        }));

        if (!operation.Task.Wait(UiTimeout))
        {
            throw new TimeoutException("the resident's UI thread did not respond within 10s");
        }
        if (error is not null)
        {
            throw error;
        }
        return value!;
    }

    private AutomationProtocol.StateSnapshot PollUntil(Func<AutomationProtocol.StateSnapshot, bool> settled)
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

/// <summary>`RoeSnip.exe --auto '<json>'` (or `--auto state` shorthand for a zero-arg command):
/// connects to the resident's automation pipe, sends one line, prints the response line to STDOUT,
/// and exits 0 on ok:true / 1 on ok:false or any failure to connect/parse. Called from Program.Main
/// BEFORE CliOptions.Parse/AppComposition.RunTray — see that call site's own comment — so a client
/// invocation never touches TrayApp's single-instance mutex/pipe the way a bare launch does.</summary>
public static class AutomationClient
{
    public static int Run(string argument)
    {
        string json;
        try
        {
            json = AutomationProtocol.NormalizeClientArgument(argument);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: --auto argument is not valid JSON: {ex.Message}");
            return 1;
        }

        try
        {
            using var client = new NamedPipeClientStream(".", AutomationProtocol.PipeName, PipeDirection.InOut);
            client.Connect(2000);

            using var writer = new StreamWriter(client, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true, NewLine = "\n" };
            using var reader = new StreamReader(client, Encoding.UTF8);
            writer.WriteLine(json);

            string? response = reader.ReadLine();
            if (response is null)
            {
                Console.Error.WriteLine("RoeSnip: automation pipe closed without a response.");
                return 1;
            }

            Console.WriteLine(response);
            using var doc = JsonDocument.Parse(response);
            bool ok = doc.RootElement.TryGetProperty("ok", out var okProp) && okProp.ValueKind == JsonValueKind.True;
            return ok ? 0 : 1;
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine(
                "RoeSnip: could not connect to the automation pipe - resident not started with ROESNIP_AUTOMATION=1 (or --automation).");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: --auto failed: {ex.Message}");
            return 1;
        }
    }
}
