using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Win32;
using RoeSnip.App;
using RoeSnip.Capture;
using RoeSnip.Core.Diagnostics;
using RoeSnip.Imaging;
using RoeSnip.Core.Recording.Gif;
// Recording-core-extraction: RecordingSizeEstimator moved to RoeSnip.Core.Recording. A plain
// `using RoeSnip.Core.Recording;` would also pull in that namespace's own MultiMonitorRecording,
// which collides with this file's enclosing RoeSnip.Recording.MultiMonitorRecording (the WPF app's
// own copy, kept separate — see that class's doc comment) — an alias avoids the ambiguity.
using RecordingSizeEstimator = RoeSnip.Core.Recording.RecordingSizeEstimator;

namespace RoeSnip.Recording;

/// <summary>Point-in-time automation snapshot of the active recording session — see
/// RecordingSession.GetAutomationSnapshot. SelectionVirtualDesktopPx is virtual-desktop-physical
/// pixels (the convention AutomationServer's wire protocol uses everywhere) — a straight passthrough
/// of RecordingSession's own internal selection state as of the multi-monitor recording phase 1
/// coordinate migration (PLAN-MULTIMON-RECORDING.md §4/§6); it used to require folding a single
/// monitor's own origin in at this boundary, before that state was natively absolute.</summary>
internal readonly record struct RecordingAutomationSnapshot(
    string Phase, RecordingFormat Format, RectPhysical SelectionVirtualDesktopPx,
    string EstimateText, GifSizePreset Preset, int Fps);

/// <summary>Owns the single active recording (if any) for the whole process — mirrors
/// OverlayController.IsSessionActive/s_activeSession's shape exactly. Wired into
/// AppComposition.StartRecording from Program.cs's RunCaptureFlowAsync (see that hook's doc
/// comment for the CaptureGate ownership-transfer contract this class must honor) and into
/// App/TrayApp.cs's TriggerCapture (PrtScr while recording advances the setup/preview state
/// machine instead of starting a new capture — see RecordingSession.RequestPrtScrAction).</summary>
internal static class RecordingController
{
    private static RecordingSession? s_active; // set/cleared on the UI thread only

    /// <summary>True for the WHOLE recording flow — Setup (chrome shown, nothing captured yet),
    /// Recording, and Reviewing (stopped, take not yet saved) — not just while frames are actually
    /// being captured. TrayApp.TriggerCapture checks this BEFORE MarkTriggerTimestamp/flash/
    /// anything else, and CaptureGate is held by RecordingController across all three phases.</summary>
    public static bool IsActive => s_active is not null;

    /// <summary>Called via the AppComposition.StartRecording hook from RunCaptureFlowAsync, on the
    /// UI thread (WinForms message-loop thread — same thread OverlaySession itself runs on). The
    /// selection is rounded down to even dimensions here (H.264 4:2:0 chroma subsampling requires
    /// it) — upstream of both encoders and RegionRecorder's own crop box, so nobody downstream ever
    /// sees an odd dimension.</summary>
    internal static Task StartAsync(
        MonitorInfo monitor, RectPhysical selectionPx, RecordingFormat format,
        RoeSnipSettings settings, ITrayNotifier? notifier)
    {
        if (s_active is not null)
        {
            // Shouldn't happen — CaptureGate already prevents a second overlay/capture stack while
            // one is live, and a recording holds the gate for its whole lifetime (Setup through
            // Reviewing).
            throw new InvalidOperationException("A recording is already active.");
        }

        var evenSelection = RoundDownToEven(selectionPx);
        var session = new RecordingSession(monitor, evenSelection, format, settings, notifier);
        try
        {
            session.Start(); // synchronous chrome/outline setup — throws on failure, cleans up partial state itself
        }
        catch
        {
            throw; // s_active was never set — RunCaptureFlowAsync's catch handles the CaptureGate non-transfer
        }

        s_active = session;
        return Task.CompletedTask;
    }

    /// <summary>TrayApp calls this when PrtScr is pressed while a recording is active. Name kept
    /// for compatibility with TrayApp.cs's existing call site (outside this workstream); behavior
    /// now advances the setup/preview state machine one step instead of always stop+save — see
    /// RecordingSession.RequestPrtScrAction for the exact per-phase mapping.</summary>
    public static void RequestStopAndSave() => s_active?.RequestPrtScrAction();

    internal static void OnSessionEnded(RecordingSession session)
    {
        if (ReferenceEquals(s_active, session))
        {
            s_active = null;
        }
    }

    // ---------- Automation hooks (App/AutomationServer.cs) ----------
    //
    // Thin static wrappers routing to the one active session, mirroring RequestStopAndSave's own
    // "s_active?." pattern above.
    //
    // Real bug found via live automation verification (session-assignment race investigation):
    // these four used to be one-line "s_active?.Method(...) ?? "no active recording session""
    // expressions — mirroring RequestStopAndSave's own harmless "s_active?." pattern above, which
    // is safe there ONLY because RequestStopAndSave's return type is void, so there is no return
    // value for "?." to forward. Every method here returns "null on success, else an error string"
    // (see e.g. RecordingSession.InvokeChromeAction's own doc comment), and "?." simply FORWARDS
    // whatever the call returns — including a legitimate null success. "??" then can't tell "the
    // receiver was null" apart from "the receiver was NOT null and its method returned null to mean
    // success": both look like a null left-hand side, so "?? "no active recording session""
    // clobbered every successful mutation into a false error. Live-verified: `chrome`/`preset`/
    // `fps`/`select` commands against an active recording session reported ok:false on every call
    // while the state they were supposed to change visibly changed anyway (confirmed via a
    // following `state` snapshot) — the mutation always landed; only the reported result lied.
    // Reading s_active into a local once and branching on IT (not on the method's own return
    // value) is the fix: the fallback string is now used exactly when there is no session, never
    // as a stand-in for "the session's own answer happened to be null".

    internal static RecordingAutomationSnapshot? GetAutomationSnapshot() => s_active?.GetAutomationSnapshot();

    internal static string? InvokeChromeAction(string action)
    {
        var session = s_active;
        return session is null ? "no active recording session" : session.InvokeChromeAction(action);
    }

    internal static string? SetSelectionForAutomation(RectPhysical virtualDesktopPx)
    {
        var session = s_active;
        return session is null ? "no active recording session" : session.SetSelectionForAutomation(virtualDesktopPx);
    }

    internal static string? SetSizePresetForAutomation(GifSizePreset preset)
    {
        var session = s_active;
        return session is null ? "no active recording session" : session.SetSizePresetForAutomation(preset);
    }

    internal static string? SetFpsForAutomation(int fps)
    {
        var session = s_active;
        return session is null ? "no active recording session" : session.SetFpsForAutomation(fps);
    }

    internal static RectPhysical RoundDownToEven(RectPhysical selectionPx)
    {
        var n = selectionPx.Normalized();
        int width = Math.Max(2, n.Width - (n.Width % 2));
        int height = Math.Max(2, n.Height - (n.Height % 2));
        return RectPhysical.FromSize(n.Left, n.Top, width, height);
    }
}

/// <summary>One in-progress recording flow — a three-phase state machine (feature 10 redesign):
///   Setup     - RegionOutline + RecordingChrome are up, nothing is being captured. The user can
///               flip the MP4-only audio toggles and either Start or Cancel.
///   Capturing - BeginCapture() built the <see cref="RegionRecorder"/> (capture), the chosen
///               encoder (<see cref="Mp4Encoder"/>/<see cref="GifEncoder"/>) and started the
///               dedicated encoder thread that drains frames from the recorder and feeds the
///               encoder. Stop or Restart end this phase.
///   Reviewing - the take is fully encoded to its temp file. Save finalizes it (SaveFileDialog +
///               File.Move); Restart discards it and returns to Setup; Cancel discards it and
///               closes the whole session.
/// <see cref="TeardownSession"/> is the single terminal path (mirrors OverlayController.
/// OverlaySession.Finish's "single terminal point" pattern) — reached from SaveAndFinish,
/// CancelAndDiscard, or an unrecoverable failure partway through Setup/Capturing.</summary>
internal sealed class RecordingSession
{
    private enum Phase { Setup, Capturing, Reviewing }

    // The monitor the ACTIVE RegionRecorder is currently reading from — not readonly: a cross-
    // monitor drag handoff (HandoffToMonitor, multi-monitor recording phase 1 — see
    // PLAN-MULTIMON-RECORDING.md §3) reassigns this when the region leaves its bounds. Every other
    // consumer (tone-map recompute, RegionRecorder construction/relative-coordinate conversion)
    // always reads the CURRENT value, never caches its own copy.
    private MonitorInfo _monitor;
    // Snapshot of every enumerated monitor, taken once in Start() — the set a handoff/BeginCapture
    // snap picks a destination from (MultiMonitorRecording.FindOwningMonitor). Deliberately NOT
    // re-enumerated live: SystemEvents.DisplaySettingsChanged already force-stops the whole take on
    // ANY topology change once Capturing (see BeginCapture's own subscription), so a stale snapshot
    // only matters for the narrow Setup-phase window before that handler is even wired up — an
    // accepted phase-1 gap, not a correctness issue for the take itself.
    private IReadOnlyList<MonitorInfo> _allMonitors = Array.Empty<MonitorInfo>();
    private RectPhysical _selectionPx; // position slides when the user drags RegionOutline; size fixed. Virtual-desktop-absolute physical pixels (multi-monitor recording phase 1, PLAN-MULTIMON-RECORDING.md §4) — NOT monitor-relative, despite the ctor parameter of the same name being monitor-relative (converted once, at the top of Start()).
    private readonly RecordingFormat _format;
    private readonly RoeSnipSettings _settings;
    private readonly ITrayNotifier? _notifier;

    private RegionRecorder? _recorder;
    // Spanning mode (multi-monitor recording phase 2, PLAN-MULTIMON-RECORDING.md §1): non-null
    // exactly when the ACTIVE take spans 2+ monitors — mutually exclusive with _recorder above,
    // never both set at once. Slots and Recorders are always the same length and index-aligned
    // (slot[i] describes recorder[i]'s own monitor/crop/canvas-destination/tone-map).
    //
    // Bundled into ONE record published behind ONE Volatile reference — deliberately NOT two
    // parallel fields (Slots array, Recorders array) written/read independently. Two independent
    // Volatile.Write calls (slots then recorders) paired with EncoderLoopSpanning reading them back
    // in the OPPOSITE order (recorders then slots) let the encoder thread observe a half-published
    // state: new slots against still-old recorders, or vice versa. A geometry/recorder-count
    // mismatch then indexes stale recorders against fresh slots (or the reverse), which can throw
    // (IndexOutOfRangeException on a shorter array, a length-mismatched SdrImage/Buffer.BlockCopy
    // throw from the resulting garbled canvas) well after the two writes/reads that actually caused
    // it. A single reference swap has no such gap — the whole pair becomes visible atomically, so
    // "new slots, new recorders" and "old slots, old recorders" are the only two states EncoderLoopSpanning
    // can ever observe.
    //
    // Published via Volatile.Write from the UI thread (BeginCapture/OnSpanningRegionMoved's rebuild
    // paths) and read via Volatile.Read from EncoderLoopSpanning — same cross-thread-publish
    // discipline HandoffToMonitor already established for the single-recorder case, generalized to
    // a bundled pair. A session that starts spanning STAYS in spanning mode (EncoderLoopSpanning) for
    // its whole take, even if a drag later reduces it to one intersected monitor — see
    // OnSpanningRegionMoved's own doc comment for why (SpanningCanvasCompositor's N==1 case is
    // exactly as cheap as the dedicated single-recorder path, so there is no reason to tear down and
    // restart the encoder thread just to hop back to EncoderLoop).
    private sealed record SpanningTopology(SpanningCanvasCompositor.MonitorSlot[] Slots, RegionRecorder[] Recorders);
    private SpanningTopology? _spanTopology;
    private Mp4Encoder? _mp4Encoder;
    private GifEncoder? _gifEncoder;
    private RecordingChrome? _chrome;
    private RegionOutline? _outline;
    private AudioCaptureEngine? _audio;
    private Thread? _encoderThread;
    private System.Threading.Timer? _elapsedTimer;
    // Spanning-mode drag-rebuild debounce (see RestartSpanningRebuildDebounce's own doc comment) —
    // UI thread only, created lazily on the first qualifying drag tick of a spanning take. Stopped
    // by HardStopCaptureIfNeeded when the take ends, purely so a Tick already queued at that moment
    // can't fire against a torn-down session (FireSpanningRebuildDebounce's own null-topology guard
    // would no-op it anyway — the Stop() is belt-and-braces, not load-bearing).
    private DispatcherTimer? _spanningRebuildDebounce;
    private RectPhysical _pendingSpanSelection;
    private IReadOnlyList<MonitorInfo>? _pendingSpanIntersecting;
    private Dispatcher? _uiDispatcher;
    private EventHandler? _displayChangedHandler;
    private System.Diagnostics.Stopwatch? _stopwatch;
    private RoeSnip.Color.ToneMapOptions _fixedToneMapOpts;
    private int _targetFps;
    private string? _mp4TempPath;
    private string? _gifTempPath;
    // GIF downscaled render (quality/fps expansion workstream; Compact 0.75 / Minimal 0.5 — see
    // GifEncoderOptions.RenderScale's own doc comment): the SCALED canvas dimensions GifEncoder.Create
    // was actually opened with, and the scale that produced them. Fixed at BeginCapture, read by
    // EncoderLoop every frame; 1.0/selection-sized is every other tier's (and MP4's) unchanged
    // behavior.
    private int _gifCanvasWidth;
    private int _gifCanvasHeight;
    private double _gifRenderScale = 1.0;
    private int _ended; // Interlocked guard — TeardownSession must run its body exactly once

    private Phase _phase = Phase.Setup;
    private bool _mic;
    private bool _systemAudio;
    private GifSizePreset _gifSizePreset; // GIF takes; read fresh into GifEncoder.Create at the NEXT BeginCapture after a Setup-phase change
    private GifSizePreset _mp4SizePreset; // MP4 takes; own field, own settings key (Mp4SizePreset) - format-specific memory, see SetSizePreset
    private RoeSnipSettings _liveSettings = null!; // set in Start(); tracks audio-toggle/preset edits for persistence

    // Pause/resume of a live take (CHANGE 2): a single accumulator drives both the excluded-time
    // media clock (EncoderLoop/DrainAudio) and the pause-aware elapsed HUD. _paused/_pausedTicks are
    // read from the encoder thread (Volatile.Read for the latter) and written only from the UI
    // thread (Pause/Resume, both only reachable from chrome button clicks) — never written from the
    // encoder thread, so no lock is needed for the read side.
    private volatile bool _paused;
    private long _pausedTicks; // total paused Stopwatch ticks accumulated across all pauses this take
    private long _pauseStartTicks; // Stopwatch.GetTimestamp() when the current pause began (valid only while _paused)
    private bool _audioTrackEnabled; // this take's MP4 has an AAC track (fixed at BeginCapture)
    private bool _saving; // reentrancy guard: the SaveFileDialog pumps messages, so a PrtScr press mid-dialog re-enters SaveAndFinish
    // Sharing/* subsystem: same reentrancy role as _saving (a hard stop must never run twice
    // concurrently against one pipeline), checked alongside it in both RequestShare and
    // SaveAndFinish's own entry guards — see RequestShare's own doc comment for why Share needs a
    // SEPARATE flag rather than reusing _saving outright (their guard windows don't nest the same
    // way: Share never re-enters itself via a nested message pump the way Save's SaveFileDialog does).
    private bool _sharing;

    public RecordingSession(
        MonitorInfo monitor, RectPhysical selectionPx, RecordingFormat format,
        RoeSnipSettings settings, ITrayNotifier? notifier)
    {
        _monitor = monitor;
        // selectionPx arrives MONITOR-RELATIVE here (OverlayResult.SelectionPx's own documented
        // convention — Program.cs/OverlayController are untouched by this workstream, see plan §6's
        // "recommend deriving inside StartAsync" note, reconciled: the field converts to
        // virtual-desktop-absolute at the top of Start() instead, one line, no delegate-signature
        // ripple needed). Stored as given for that one line to convert from.
        _selectionPx = selectionPx;
        _format = format;
        _settings = settings;
        _notifier = notifier;
    }

    /// <summary>UI thread only. Opens the Setup phase: computes the fixed tone-map options (used by
    /// whichever take eventually gets captured), shows the region outline + chrome, and wires the
    /// chrome's events. Synchronous and throws on failure (cleaning up whatever it already
    /// constructed) so RecordingController never records a partially-started session as active. No
    /// capture pipeline exists yet — that is <see cref="BeginCapture"/>'s job, run only once the
    /// user presses Start in the chrome.</summary>
    public void Start()
    {
        try
        {
            _uiDispatcher = Dispatcher.CurrentDispatcher;

            // Multi-monitor recording phase 1 (PLAN-MULTIMON-RECORDING.md §4): from here on,
            // _selectionPx is virtual-desktop-absolute physical pixels, never monitor-relative — the
            // ctor's own selectionPx parameter is the one and only monitor-relative value this class
            // ever sees (OverlayResult.SelectionPx's documented convention), converted exactly once,
            // right here, before RegionOutline/RecordingChrome/anything else reads it.
            var homeBounds = _monitor.BoundsPx;
            _selectionPx = new RectPhysical(
                homeBounds.Left + _selectionPx.Left, homeBounds.Top + _selectionPx.Top,
                homeBounds.Left + _selectionPx.Right, homeBounds.Top + _selectionPx.Bottom);

            // Snapshot every monitor once for the whole session's handoff/clamp decisions (see the
            // field's own doc comment). Never empty even if enumeration genuinely fails — falling
            // back to just the home monitor keeps FindOwningMonitor/UnionBounds well-defined instead
            // of degrading to a magic (0,0,0,0) union.
            var enumerated = MonitorEnumerator.Enumerate();
            _allMonitors = enumerated.Count > 0 ? enumerated : new[] { _monitor };

            // Target fps comes from the format's own persisted setting (GifFps/Mp4Fps), clamped
            // fail-safe into the format's allowed range (RecordingSizeEstimator.ClampFps) so a
            // garbled or future-schema settings value never crashes the recording flow - same
            // fail-closed-at-use-time convention SetSizePreset's Parse call already follows below.
            // 50fps is still GIF's format ceiling: delays are whole centiseconds and browsers clamp
            // 0-1cs to 10cs, so 2cs is the smallest honest frame time (60fps is unrepresentable) -
            // see RecordingSizeEstimator.GifMaxFps's own doc comment for why every OTHER integer fps
            // up to that ceiling is legal now (the patch-behind carry makes non-divisor rates average
            // out exactly, not just the four old fixed divisor choices). Size is kept down by
            // GifEncoder's skip-identical + changed-region delta encoding, not by a low fps -
            // static pixels cost nothing at any capture rate, and if the encoder can't keep up
            // with heavy motion the bounded DropOldest queue sheds frames while the patch-behind
            // timestamps keep playback speed exact.
            int requestedFps = _format == RecordingFormat.Gif ? _settings.GifFps : _settings.Mp4Fps;
            _targetFps = _format == RecordingFormat.Gif
                ? RecordingSizeEstimator.ClampFps(requestedFps, RecordingSizeEstimator.GifMinFps, RecordingSizeEstimator.GifMaxFps)
                : RecordingSizeEstimator.ClampFps(requestedFps, RecordingSizeEstimator.Mp4MinFps, RecordingSizeEstimator.Mp4MaxFps);

            // Fixed tone-map options, computed ONCE here from monitor metadata — never re-derived
            // per frame or per take (a Restart reuses these). Per-frame auto-derived peak
            // (ToneMapper's normal path also folds in the CURRENT frame's own max) would visibly
            // pump exposure on fluctuating HDR content, an auto-exposure-style flicker; pinning
            // PeakOverride up front is the fix. Mirrors ToneMapper.ComputeCurveParams's own
            // derivation formula but computed from monitor photometrics alone, never from a frame's
            // content. Factored into ComputeFixedToneMapOpts (multi-monitor recording phase 1) so
            // BeginCapture's Setup-drag-onto-another-monitor snap and HandoffToMonitor's live
            // cross-monitor handoff can both re-pin it against a DIFFERENT monitor later without
            // duplicating this formula — see PLAN-MULTIMON-RECORDING.md §2 ("recomputed per monitor,
            // never blended").
            _fixedToneMapOpts = ComputeFixedToneMapOpts(_monitor);

            _liveSettings = _settings;
            _mic = _settings.RecordMicrophone;
            _systemAudio = _settings.RecordSystemAudio;
            _gifSizePreset = GifSizePresets.Parse(_settings.GifSizePreset);
            _mp4SizePreset = GifSizePresets.Parse(_settings.Mp4SizePreset);

            // The visible "this area will be recorded" frame (both formats): dashed outline just
            // OUTSIDE the region; the inner is unpainted (true click-through) except while Shift/
            // Ctrl is held for a region move — see RegionOutline's doc. Shown for the whole session
            // (Setup through Reviewing), not just while actually capturing.
            _outline = new RegionOutline(_allMonitors, _selectionPx);
            _outline.RegionChanged += OnRegionMoved;
            _outline.Show(); // Setup mode by default: band resizes, Shift/Ctrl inside moves

            // Chrome's size row shows the CURRENT FORMAT's own persisted preset - GIF and MP4 keep
            // independent memory (RoeSnipSettings.GifSizePreset / Mp4SizePreset), so a take never
            // inherits the other format's last-picked tier.
            GifSizePreset initialSizePreset = _format == RecordingFormat.Gif ? _gifSizePreset : _mp4SizePreset;
            _chrome = new RecordingChrome(_monitor, _allMonitors, _selectionPx, _format, _mic, _systemAudio, initialSizePreset, _targetFps);
            _chrome.StartRequested += BeginCapture;
            _chrome.StopRequested += StopCaptureToReview;
            _chrome.PauseRequested += Pause;
            _chrome.ResumeRequested += Resume;
            _chrome.RestartConfirmed += RestartTake;
            _chrome.SaveRequested += () => SaveAndFinish();
            // Sharing/* subsystem: RecordingChrome.ShareRequested's own doc comment documents this
            // exact subscription as the missing wire — see RequestShare's own doc comment for the
            // full hard-stop/upload/re-arm design.
            _chrome.ShareRequested += RequestShare;
            _chrome.CancelRequested += CancelAndDiscard;
            _chrome.MicToggled += v => SetAudioToggle(mic: v, systemAudio: null);
            _chrome.SystemAudioToggled += v => SetAudioToggle(mic: null, systemAudio: v);
            _chrome.SizePresetChanged += SetSizePreset;
            _chrome.FpsChanged += SetFps;
            _chrome.Show();

            FileLog.Write($"RoeSnip: recording setup opened ({_format}, {_selectionPx.Width}x{_selectionPx.Height})");
        }
        catch
        {
            try { _chrome?.Close(); } catch { /* best-effort */ }
            try { _outline?.CloseOutline(); } catch { /* best-effort */ }
            throw;
        }
    }

    /// <summary>The fixed tone-map formula (Start()'s own original comment has the "why fixed, not
    /// per-frame" reasoning) factored out so it can be re-run against a DIFFERENT monitor's own
    /// photometrics — multi-monitor recording phase 1's BeginCapture snap and HandoffToMonitor both
    /// need this, and per PLAN-MULTIMON-RECORDING.md §2 it is always a fresh computation against the
    /// new monitor alone, never blended with the old one (the resulting exposure "pop" across a
    /// handoff is documented as correct, not a bug).</summary>
    private RoeSnip.Color.ToneMapOptions ComputeFixedToneMapOpts(MonitorInfo monitor)
    {
        double peak = _settings.ToneMapPeakOverride
            ?? Math.Clamp(monitor.MaxLuminanceNits / monitor.SdrWhiteNits, 2.0, double.MaxValue);
        double knee = _settings.ToneMapKneeOverride ?? 0.90;
        return new RoeSnip.Color.ToneMapOptions(Knee: knee, PeakOverride: peak);
    }

    /// <summary>PrtScr while a recording flow is active: advances the state machine by one logical
    /// step rather than the old unconditional "stop and save" — Setup begins capturing (that is the
    /// one pending action while the setup panel is up), Capturing stops into review (matching the
    /// old PrtScr-stops-a-recording expectation), Reviewing saves and finishes (the one pending
    /// action left once a take is done). Marshals to the UI thread since TrayApp's hotkey path can
    /// call this from off-thread.</summary>
    internal void RequestPrtScrAction()
    {
        var dispatcher = _uiDispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            AdvanceOnPrtScr();
        }
        else
        {
            dispatcher.BeginInvoke(new Action(AdvanceOnPrtScr));
        }
    }

    /// <summary>RecordingChrome's Setup-panel audio toggles changed (feature 10 redesign moved
    /// these off the toolbar's record menu). Persists immediately via SettingsStore, the same
    /// split every other settings-editing flow in the app uses (see OverlayWindow.
    /// TrySaveLiveSettings) — best-effort; a disk hiccup here must not interrupt the recording.</summary>
    private void SetAudioToggle(bool? mic, bool? systemAudio)
    {
        if (mic is { } m)
        {
            _mic = m;
        }
        if (systemAudio is { } s)
        {
            _systemAudio = s;
        }

        _liveSettings = _liveSettings with { RecordMicrophone = _mic, RecordSystemAudio = _systemAudio };
        try
        {
            SettingsStore.Save(_liveSettings);
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: failed to persist recording audio toggle: {ex.Message}");
        }
    }

    /// <summary>RecordingChrome's Setup-panel size row changed (shown for BOTH formats as of the
    /// recording-size-tiers workstream). Persists immediately, same best-effort split as
    /// <see cref="SetAudioToggle"/>, to whichever settings key matches THIS session's own format
    /// (GifSizePreset for a GIF take, Mp4SizePreset for an MP4 take — the two stay independent, see
    /// RoeSnipSettings.Mp4SizePreset's own doc comment) AND updates the matching field so it's
    /// exactly what <see cref="BeginCapture"/> reads from at the NEXT Start — the row is disabled
    /// outside Setup (see RecordingChrome.ApplyState), so there is no live take whose already-built
    /// encoder this could retroactively affect.</summary>
    private void SetSizePreset(GifSizePreset preset)
    {
        if (_format == RecordingFormat.Gif)
        {
            _gifSizePreset = preset;
            _liveSettings = _liveSettings with { GifSizePreset = preset.ToString() };
        }
        else
        {
            _mp4SizePreset = preset;
            _liveSettings = _liveSettings with { Mp4SizePreset = preset.ToString() };
        }

        try
        {
            SettingsStore.Save(_liveSettings);
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: failed to persist recording size preset: {ex.Message}");
        }
    }

    /// <summary>RecordingChrome's Setup-panel FPS row changed (quality/framerate decoupling
    /// workstream, stage 3) — mirrors <see cref="SetSizePreset"/> exactly: persists to whichever
    /// settings key matches THIS session's own format (GifFps for a GIF take, Mp4Fps for an MP4
    /// take — see <see cref="RoeSnip.RoeSnipSettings.Mp4Fps"/>'s own doc comment for why the two
    /// stay independent) and updates <see cref="_targetFps"/> so it's exactly what
    /// <see cref="BeginCapture"/> reads from at the NEXT Start — the row is disabled outside Setup
    /// (see RecordingChrome.ApplyState), so there is no live take whose already-built recorder/
    /// encoder this could retroactively affect.</summary>
    private void SetFps(int fps)
    {
        _targetFps = fps;
        _liveSettings = _format == RecordingFormat.Gif
            ? _liveSettings with { GifFps = fps }
            : _liveSettings with { Mp4Fps = fps };

        try
        {
            SettingsStore.Save(_liveSettings);
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: failed to persist recording fps: {ex.Message}");
        }
    }

    /// <summary>The user moved or resized the recorded region (RegionOutline), OR (via
    /// SetSelectionForAutomation) an automation caller drove the same production path. UI thread.
    /// Re-anchor the HUD to the region's new spot and, if a recorder is live (Capturing OR
    /// Reviewing — a soft-stopped take is a paused take, its recorder stays alive so Resume can
    /// continue it, see <see cref="Resume"/>), keep it following.
    ///
    /// Multi-monitor recording phase 2 (PLAN-MULTIMON-RECORDING.md §1/§3) branches here on which
    /// mode the ACTIVE take is already in — a take's mode, once chosen at BeginCapture, never
    /// changes for the rest of that take (see <see cref="_spanTopology"/>'s own doc comment for
    /// why): a SPANNING take (<see cref="_spanTopology"/> non-null) always routes through
    /// <see cref="OnSpanningRegionMoved"/>, including a drag that would momentarily reduce it to one
    /// intersected monitor. A SINGLE-monitor take keeps phase 1's exact original behavior below
    /// unchanged — same-monitor moves slide the existing recorder's crop origin (lock-free —
    /// <see cref="RegionRecorder.SetOrigin"/>'s own doc); a region that stops being fully contained
    /// in its current monitor's bounds triggers a cross-monitor HANDOFF (snap to the destination
    /// monitor, never a live promotion to spanning mid-take — drawing the selection spanning up
    /// front in Setup, so BeginCapture picks spanning mode from the start, is the supported way to
    /// get a spanning take; see <see cref="OnSpanningRegionMoved"/>'s doc for the full reasoning).
    /// <paramref name="selectionPx"/> is virtual-desktop-absolute (§4) — the outline itself is what
    /// already computed/clamped it; this only propagates it to the recorder(s)/chrome.</summary>
    private void OnRegionMoved(RectPhysical selectionPx)
    {
        _selectionPx = selectionPx;
        _chrome?.UpdateSelection(selectionPx);

        if (_spanTopology is { } topology)
        {
            OnSpanningRegionMoved(selectionPx, topology);
            return;
        }

        var recorder = _recorder;
        if (recorder is null)
        {
            return; // Setup phase — nothing capturing yet; outline/chrome are already in sync above
        }

        if (MultiMonitorRecording.Contains(_monitor.BoundsPx, selectionPx))
        {
            var rel = MultiMonitorRecording.ToMonitorRelative(selectionPx, _monitor);
            recorder.SetOrigin(rel.Left, rel.Top);
            return;
        }

        // The region left its current monitor's bounds. Find whichever monitor now claims the
        // majority of it — "snap, don't split" (plan §3).
        var dest = MultiMonitorRecording.FindOwningMonitor(selectionPx, _allMonitors);
        if (dest is null || dest.DeviceName == _monitor.DeviceName)
        {
            // No OTHER monitor claims it (dragged into a dead gap between non-adjacent monitors, or
            // it's still mostly overlapping the current one at a boundary sliver) — stay on the
            // current monitor. RegionRecorder.SetOrigin's own internal clamp keeps the crop box
            // inside the captured surface regardless of how far outside its bounds selectionPx now
            // reads.
            var rel = MultiMonitorRecording.ToMonitorRelative(selectionPx, _monitor);
            recorder.SetOrigin(rel.Left, rel.Top);
            return;
        }

        HandoffToMonitor(dest, selectionPx);
    }

    /// <summary>Tears down the recorder on the monitor the region just left and builds a fresh one
    /// on the monitor it now mostly overlaps — the phase-1 "snap, not spanning" handoff
    /// (PLAN-MULTIMON-RECORDING.md §3). UI thread only (called from <see cref="OnRegionMoved"/>,
    /// itself always UI-thread — RegionOutline's own WM_MOUSEMOVE handler or an automation
    /// `select`). Synchronous, per the plan's own "measure before committing to async" note:
    /// <see cref="RegionRecorder.Start"/> does not block waiting for a first frame (see that
    /// method's own doc comment), so this is bounded by device/session construction cost, not a
    /// capture wait — if that assumption ever stops holding on real hardware, the plan's documented
    /// fallback is an async handoff that pauses the recorder for the duration (not implemented here;
    /// not needed per this workstream's own live-hardware verification, see the E2E notes).</summary>
    private void HandoffToMonitor(MonitorInfo destMonitor, RectPhysical desiredSelectionAbs)
    {
        var clamped = MultiMonitorRecording.ClampToMonitor(desiredSelectionAbs, destMonitor);

        RegionRecorder newRecorder;
        try
        {
            newRecorder = new RegionRecorder(destMonitor, MultiMonitorRecording.ToMonitorRelative(clamped, destMonitor), _targetFps);
            newRecorder.Faulted += OnRecorderFaulted;
            // Carry the session's own pause state onto the new recorder BEFORE Start() — a handoff
            // can happen while Reviewing (the recorder stays alive, paused, so Resume can continue
            // the same take; see this method's own class-level doc note on OnRegionMoved). Without
            // this a handoff mid-pause would silently start capturing frames the paused UI promised
            // it wasn't.
            newRecorder.Paused = _paused;
            newRecorder.Start();
        }
        catch (Exception ex)
        {
            // A failed handoff must not kill an otherwise-healthy recording. The outline/chrome
            // already reflect the dragged position (OnRegionMoved's caller updated _selectionPx/
            // chrome before calling in); the recorder itself just keeps recording its OLD monitor's
            // edge, clamped sane by SetOrigin's own internal clamp, until the user drags back or the
            // take ends. No user-visible error surfaced for this narrow failure — an accepted phase-1
            // gap (see the plan's own "measure before committing" flag).
            FileLog.Write($"RoeSnip: cross-monitor recording handoff to {destMonitor.DeviceName} failed, staying on {_monitor.DeviceName}: {ex.Message}");
            return;
        }

        // Per-monitor fixed tone-map, recomputed fresh for the destination — NOT blended with the
        // old monitor's (plan §2). The resulting exposure "pop" across the seam is documented as
        // correct: it matches exactly what a fresh take started here from scratch would show.
        _fixedToneMapOpts = ComputeFixedToneMapOpts(destMonitor);

        string fromDevice = _monitor.DeviceName;
        var oldRecorder = _recorder;
        _monitor = destMonitor;
        // Published via Volatile.Write BEFORE the old recorder's Stop() below: EncoderLoop's
        // Volatile.Read(ref _recorder), on the completion of the OLD recorder's channel, is what
        // lets it notice the swap instead of treating that completion as the end of the take — see
        // EncoderLoop's own comment for the full happens-before argument (this is the load-bearing
        // fix PLAN-MULTIMON-RECORDING.md §3 calls out: without it the encoder thread would exit as
        // if the whole take had ended, mid-drag).
        Volatile.Write(ref _recorder, newRecorder);
        _selectionPx = clamped;

        oldRecorder?.Stop();    // completes the OLD channel — EncoderLoop drains whatever was still
                                // queued on it, then (per its own fix) picks up the new recorder
                                // instead of breaking out as if Stop() had ended the take for real
        oldRecorder?.Dispose();

        // Re-sync the outline/chrome to the (possibly re-clamped) destination rect WITHOUT
        // re-raising RegionChanged — this method IS a RegionChanged handler already (called from
        // OnRegionMoved); re-raising it here would recurse straight back into that same handler.
        _outline?.SyncExternalSelection(clamped);
        _chrome?.UpdateSelection(clamped);

        FileLog.Write($"RoeSnip: recording handed off {fromDevice} -> {destMonitor.DeviceName} at ({clamped.Left},{clamped.Top}), size {clamped.Width}x{clamped.Height}");
    }

    /// <summary>The spanning-mode counterpart of <see cref="OnRegionMoved"/>'s single-recorder
    /// branch above — multi-monitor recording phase 2 (PLAN-MULTIMON-RECORDING.md §1/§3's "or
    /// re-derives intersections when the monitor set changes mid-drag - handle it" note). A take
    /// that entered spanning mode at BeginCapture ALWAYS stays in spanning mode for the rest of
    /// that take, even if a drag temporarily reduces it to a single intersected monitor:
    /// <see cref="SpanningCanvasCompositor"/>'s own N==1 case is exactly as cheap as the dedicated
    /// single-recorder path (see <see cref="SpanningCanvasCompositor.CompositeSlot"/>'s own doc
    /// comment — it tone-maps straight into the canvas with no extra scratch/copy whenever a slot's
    /// sub-rect is the whole canvas), so there is no correctness or perf reason to ever drop back to
    /// <see cref="EncoderLoop"/> mid-take. Doing so WOULD require stopping and restarting the
    /// encoder thread itself (EncoderLoop and EncoderLoopSpanning are two different thread entry
    /// points, and a .NET Thread cannot be redirected mid-flight) with a "does the outgoing thread
    /// finalize the encoder, or does an incoming thread keep feeding it?" signaling problem this
    /// workstream deliberately avoids by never needing to ask it. The one accepted consequence:
    /// starting on a single monitor and then dragging INTO a second one still uses phase 1's
    /// existing snap-to-destination-monitor handoff above, not a live promotion to spanning —
    /// drawing the selection spanning up front in Setup (so BeginCapture's own topology derivation
    /// picks spanning mode from the very first frame) is the supported way to get a spanning
    /// take.</summary>
    private void OnSpanningRegionMoved(RectPhysical selectionAbs, SpanningTopology topology)
    {
        var intersecting = MultiMonitorRecording.IntersectingMonitors(selectionAbs, _allMonitors);
        if (intersecting.Count == 0)
        {
            // Dragged into a dead gap between non-adjacent monitors — same "stay put" rule phase 1
            // already has for the single-recorder case (OnRegionMoved above): every recorder keeps
            // capturing wherever its own crop last was.
            return;
        }

        if (SameMonitorSet(intersecting, topology.Slots) && TrySlideSpanningInPlace(selectionAbs, intersecting, topology))
        {
            return;
        }

        // A rebuild is needed (either the monitor SET changed, or sliding in place would have to
        // resize some slot's crop — see TrySlideSpanningInPlace's own doc for why that always falls
        // through here). Debounced, never called synchronously from this handler: see
        // RestartSpanningRebuildDebounce's own doc comment for why a rebuild is the single most
        // expensive thing this class does (fresh D3D11 device + WGC session per monitor, then a
        // GPU-synchronizing Stop() of the old set) and this handler runs on the UI thread inside
        // WM_MOUSEMOVE, up to 100+ times a second during a drag.
        _pendingSpanSelection = selectionAbs;
        _pendingSpanIntersecting = intersecting;
        RestartSpanningRebuildDebounce();
    }

    /// <summary>Coalesces every <see cref="OnSpanningRegionMoved"/> rebuild request that arrives
    /// while the drag is still moving into ONE actual <see cref="RebuildSpanningRecorders"/> call,
    /// fired only once ticks stop arriving for a whole <see cref="SpanningRebuildDebounceMs"/>
    /// window — the same debounce shape <see cref="RecordingChrome"/>'s own fps-slider persistence
    /// already uses for an analogous problem (an expensive operation, driven by a high-frequency UI
    /// event, where only the FINAL value in a burst actually matters — see that class's own
    /// RestartFpsDebounce doc comment). Without this, dragging a spanning selection along the axis a
    /// seam straddles rebuilds N D3D11 devices + WGC capture sessions on literally every qualifying
    /// mouse-move tick (RegionOutline's own WM_MOUSEMOVE handler fires RegionChanged unthrottled) —
    /// each rebuild's Stop() of the OLD set does a GPU-synchronizing ring drain under RegionRecorder's
    /// own _gpuLock, on the UI thread, and the freshly-built recorders need TWO WGC callbacks before
    /// their first frame (RegionRecorder.OnFrameArrived's own copy-#0 comment) — so a set that lives
    /// only a few milliseconds between rebuilds may never emit a single frame, freezing the recording
    /// for the whole drag while the UI stutters and the GPU churns capture sessions underneath it.
    ///
    /// Between debounce ticks the OLD recorder set just keeps capturing at its last-known split — a
    /// momentary, bounded-by-the-debounce-window inaccuracy in exactly where the seam falls, never a
    /// stall and never a dropped frame — which is a strictly better trade than briefly freezing the
    /// whole recording on every pixel of mouse movement. The outline/chrome themselves are NOT
    /// debounced (OnRegionMoved already moved them synchronously before calling in here), so the
    /// visible selection band still tracks the cursor exactly; only the actual capture-crop split
    /// lags, and only while the pointer keeps moving.</summary>
    private const int SpanningRebuildDebounceMs = 150;

    private void RestartSpanningRebuildDebounce()
    {
        if (_spanningRebuildDebounce is null)
        {
            _spanningRebuildDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SpanningRebuildDebounceMs) };
            _spanningRebuildDebounce.Tick += (_, _) => FireSpanningRebuildDebounce();
        }
        _spanningRebuildDebounce.Stop();
        _spanningRebuildDebounce.Start();
    }

    /// <summary>The debounce timer's own landing point — always reads the LATEST pending selection/
    /// monitor set (whatever the most recent <see cref="RestartSpanningRebuildDebounce"/> call
    /// recorded), never a value captured back when the timer was first (re)started, so a whole burst
    /// of ticks collapses into exactly one rebuild against the drag's FINAL settled position. Guards
    /// against firing after the take already ended — <see cref="HardStopCaptureIfNeeded"/> stops this
    /// same timer and nulls <see cref="_spanTopology"/> as part of tearing a spanning take down, so a
    /// Tick that was already queued when that happened lands here with nothing left to rebuild.</summary>
    private void FireSpanningRebuildDebounce()
    {
        _spanningRebuildDebounce!.Stop();
        if (_pendingSpanIntersecting is not { } intersecting || _spanTopology is not { } topology)
        {
            return; // take ended, or nothing actually pending
        }
        _pendingSpanIntersecting = null;
        RebuildSpanningRecorders(_pendingSpanSelection, intersecting, topology.Recorders);
    }

    /// <summary>True when <paramref name="intersecting"/> and <paramref name="slots"/> name the
    /// exact same monitors in the exact same order — both are independently produced by
    /// <see cref="MultiMonitorRecording.IntersectingMonitors"/>'s own Index-sorted order (once at
    /// BeginCapture/a rebuild for <paramref name="slots"/>, fresh every drag tick for
    /// <paramref name="intersecting"/>), so a positional compare is sufficient and cheaper than a
    /// set compare — no need to sort/hash either side again here.</summary>
    private static bool SameMonitorSet(IReadOnlyList<MonitorInfo> intersecting, IReadOnlyList<SpanningCanvasCompositor.MonitorSlot> slots)
    {
        if (intersecting.Count != slots.Count)
        {
            return false;
        }
        for (int i = 0; i < intersecting.Count; i++)
        {
            if (intersecting[i].DeviceName != slots[i].Monitor.DeviceName)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>The cheap path for a spanning drag that keeps the SAME monitor set: tries to just
    /// slide every slot's own crop origin (<see cref="RegionRecorder.SetOrigin"/>, lock-free) rather
    /// than tearing anything down. Bails out (returns false, changes nothing) the instant ANY slot's
    /// own crop SIZE would need to change — dragging a spanning selection along an axis that shifts
    /// how much of it falls on each side of a seam (e.g. sliding horizontally while straddling a
    /// vertical monitor boundary) changes one monitor's share and shrinks the other's, and
    /// <see cref="RegionRecorder"/>'s crop width/height is fixed for that recorder's whole lifetime
    /// (see its own class doc) — there is no such thing as "resize this recorder's crop in place",
    /// only "build a new one". Falling through to <see cref="RebuildSpanningRecorders"/> for ANY
    /// size change (not just the specific slot whose size changed) is a deliberate simplification:
    /// one rebuild pass beats trying to rebuild only the affected slot(s) while lock-free-sliding the
    /// rest, for a case (spanning drags specifically along the straddled axis) that is already the
    /// less common one — see <see cref="RestartSpanningRebuildDebounce"/>'s own doc comment for why
    /// falling through to a rebuild no longer means an immediate, synchronous, per-tick D3D/WGC
    /// rebuild the way it originally did: it is debounced, so the actual expensive work only ever
    /// runs once a drag settles.</summary>
    private bool TrySlideSpanningInPlace(RectPhysical selectionAbs, IReadOnlyList<MonitorInfo> intersecting, SpanningTopology topology)
    {
        var oldSlots = topology.Slots;
        var oldRecorders = topology.Recorders;
        var newSlots = new SpanningCanvasCompositor.MonitorSlot[oldSlots.Length];

        for (int i = 0; i < oldSlots.Length; i++)
        {
            var monitor = oldSlots[i].Monitor;
            var cropAbs = MultiMonitorRecording.Intersect(selectionAbs, monitor.BoundsPx);
            if (cropAbs.Width != oldSlots[i].MonitorRelativeCrop.Width || cropAbs.Height != oldSlots[i].MonitorRelativeCrop.Height)
            {
                return false; // a size change anywhere in the set — full rebuild instead (see doc)
            }
            var monitorRelativeCrop = MultiMonitorRecording.ToMonitorRelative(cropAbs, monitor);
            var canvasSubRect = new RectPhysical(
                cropAbs.Left - selectionAbs.Left, cropAbs.Top - selectionAbs.Top,
                cropAbs.Right - selectionAbs.Left, cropAbs.Bottom - selectionAbs.Top);
            newSlots[i] = oldSlots[i] with { MonitorRelativeCrop = monitorRelativeCrop, CanvasSubRect = canvasSubRect };
        }

        // Geometry only ever moved, never resized (checked above) — every recorder's own crop box
        // just slides in place; SetOrigin is lock-free and UI-thread-safe by design (see its own doc
        // comment), so this whole method costs nothing more than N cheap volatile writes.
        for (int i = 0; i < oldRecorders.Length; i++)
        {
            oldRecorders[i].SetOrigin(newSlots[i].MonitorRelativeCrop.Left, newSlots[i].MonitorRelativeCrop.Top);
        }

        // Recorders are UNCHANGED (same array reference) — only Slots differs. Publish a new bundled
        // pair anyway rather than mutating the old one in place, so the single-Volatile-reference
        // publish discipline (see _spanTopology's own doc comment) stays uniform for every spanning-
        // topology change, slide or rebuild alike: EncoderLoopSpanning's own topology resync (which
        // compares the Recorders array by reference each tick) correctly treats this as "no rebuild
        // needed" since that reference is unchanged, and just picks up the new canvas offsets from
        // this fresh Slots array on its very next tick.
        Volatile.Write(ref _spanTopology, topology with { Slots = newSlots });
        return true;
    }

    /// <summary>The spanning-mode generalization of <see cref="HandoffToMonitor"/>: builds a fresh
    /// <see cref="RegionRecorder"/> set for the NEW intersected-monitor geometry, publishes it, THEN
    /// stops the old set — mirroring HandoffToMonitor's own publish-before-stop ordering so
    /// EncoderLoopSpanning's topology resync (comparing <see cref="_spanTopology"/>'s Recorders array
    /// by reference each tick) sees a clean swap instead of ever having to distinguish "the old
    /// channels completed because of a real Stop()" from "...because of a rebuild" the way the
    /// single-recorder EncoderLoop has to (see that method's own comment) — spanning's tick-based
    /// drain never blocks on channel completion in the first place, so there is nothing to
    /// disambiguate. UI thread only (called from <see cref="FireSpanningRebuildDebounce"/>). On
    /// failure, leaves the OLD topology running untouched (same "a failed handoff must not kill an
    /// otherwise-healthy recording" rule <see cref="HandoffToMonitor"/>'s own doc comment states).
    /// Unlike a single→spanning or spanning→single MODE change, this never touches
    /// <see cref="_encoderThread"/> at all — the same thread just keeps running
    /// <see cref="EncoderLoopSpanning"/>, reading whatever <see cref="_spanTopology"/> says on its
    /// next tick.</summary>
    private void RebuildSpanningRecorders(RectPhysical selectionAbs, IReadOnlyList<MonitorInfo> intersecting, RegionRecorder[] oldRecorders)
    {
        RegionRecorder[]? newRecorders = null;
        SpanningCanvasCompositor.MonitorSlot[] newSlots;
        try
        {
            var builtSlots = SpanningCanvasCompositor.BuildSlots(selectionAbs, intersecting, ComputeFixedToneMapOpts).ToArray();
            newRecorders = new RegionRecorder[builtSlots.Length];
            for (int i = 0; i < builtSlots.Length; i++)
            {
                var r = new RegionRecorder(builtSlots[i].Monitor, builtSlots[i].MonitorRelativeCrop, _targetFps);
                r.Faulted += OnRecorderFaulted;
                r.Paused = _paused; // carry the session's own pause state, same reason HandoffToMonitor does
                newRecorders[i] = r;
            }
            foreach (var r in newRecorders)
            {
                r.Start();
            }
            newSlots = builtSlots;
        }
        catch (Exception ex)
        {
            // Tear down whatever of the NEW set already came up (constructed and/or Start()ed)
            // before giving up and staying on the OLD topology — StartSpanningRecorders' own catch
            // follows the same "no half-built set left running" rule. Without this, a recorder that
            // got far enough to Start() its WGC session before a LATER sibling's construction/Start
            // threw would leak a live capture session (its own D3D11 device + WGC session, doing GPU
            // readback at target fps) with nothing left holding a reference to Stop() it — and
            // because a rebuild retries on every subsequent qualifying drag tick, a persistent
            // failure (monitor unplugged mid-drag, driver pressure) would leak a fresh set of these
            // on every retry.
            if (newRecorders is not null)
            {
                foreach (var r in newRecorders)
                {
                    if (r is null) continue;
                    try { r.Stop(); r.Dispose(); } catch { /* best-effort teardown of a partial rebuild */ }
                }
            }
            FileLog.Write($"RoeSnip: spanning recording topology rebuild failed, staying on the previous monitor set: {ex.Message}");
            return;
        }

        // ONE Volatile.Write of the bundled pair — see _spanTopology's own doc comment for why this
        // must never be two independent writes (slots, then recorders) again.
        Volatile.Write(ref _spanTopology, new SpanningTopology(newSlots, newRecorders));

        foreach (var old in oldRecorders)
        {
            old.Stop();
            old.Dispose();
        }

        _outline?.SyncExternalSelection(selectionAbs);
        _chrome?.UpdateSelection(selectionAbs);

        FileLog.Write($"RoeSnip: spanning recording topology rebuilt at ({selectionAbs.Left},{selectionAbs.Top}) {selectionAbs.Width}x{selectionAbs.Height}, {newRecorders.Length} monitor(s)");
    }

    private void AdvanceOnPrtScr()
    {
        switch (_phase)
        {
            case Phase.Setup:
                BeginCapture();
                break;
            case Phase.Capturing:
                StopCaptureToReview();
                break;
            case Phase.Reviewing:
                SaveAndFinish();
                break;
        }
    }

    /// <summary>UI thread only (chrome's Start button, or PrtScr in Setup). Builds the actual WGC
    /// capture + encoder pipeline and starts it — everything the old single-shot Start() used to do
    /// immediately. On failure, tears the whole session down (nothing was capturing yet, so there
    /// is nothing to save).</summary>
    private void BeginCapture()
    {
        if (_phase != Phase.Setup)
        {
            return; // stray double-invoke (e.g. Start clicked twice before the first click applied)
        }

        try
        {
            // Setup-phase resize/move can have pushed the selection onto (or across) any number of
            // monitors — re-derive the FULL intersected set fresh right before building the capture
            // pipeline (multi-monitor recording phase 2, PLAN-MULTIMON-RECORDING.md §1): 2+ monitors
            // means a genuine SPANNING take (one RegionRecorder per monitor feeding a
            // SpanningCanvasCompositor — see StartSpanningRecorders below); exactly 1 is phase 1's
            // existing single-recorder path, unchanged; 0 (dragged into a dead gap between
            // non-adjacent monitors) falls back to phase 1's own "snap to nearest, never resize" rule
            // so a take can still start somewhere sane.
            var intersecting = MultiMonitorRecording.IntersectingMonitors(_selectionPx, _allMonitors);
            if (intersecting.Count == 0)
            {
                var owningMonitor = MultiMonitorRecording.FindOwningMonitor(_selectionPx, _allMonitors) ?? _monitor;
                var clampedSelection = MultiMonitorRecording.ClampToMonitor(_selectionPx, owningMonitor);
                if (clampedSelection != _selectionPx)
                {
                    _selectionPx = clampedSelection;
                    _outline?.SyncExternalSelection(clampedSelection);
                    _chrome?.UpdateSelection(clampedSelection);
                }
                intersecting = new[] { owningMonitor };
            }

            bool spanning = intersecting.Count > 1;
            if (!spanning)
            {
                _monitor = intersecting[0];
                _fixedToneMapOpts = ComputeFixedToneMapOpts(_monitor);
                _recorder = new RegionRecorder(_monitor, MultiMonitorRecording.ToMonitorRelative(_selectionPx, _monitor), _targetFps);
                _recorder.Faulted += OnRecorderFaulted;
            }
            // Spanning's own RegionRecorder set is built further down (StartSpanningRecorders),
            // AFTER the encoder is created — same "encoder ready before any recorder starts
            // producing frames" ordering the single-recorder branch already follows below.

            if (_format == RecordingFormat.Mp4)
            {
                // Audio first: the encoder only gets an AAC track if a capture source genuinely
                // came up, so a device failure can never leave a sample-less audio stream for
                // Finalize to choke on. Engine failure is non-fatal (video-only recording).
                if (_mic || _systemAudio)
                {
                    _audio = AudioCaptureEngine.TryStart(_mic, _systemAudio);
                    if (_audio is null)
                    {
                        _notifier?.ShowError("Audio capture unavailable; recording video only.");
                    }
                }
                _mp4TempPath = Path.Combine(Path.GetTempPath(), $"roesnip_rec_{Guid.NewGuid():N}.mp4");
                // The preset is whatever the Setup-phase size row last selected for MP4 (or the
                // persisted Mp4SizePreset default, if it was never touched this session) — same
                // "next take" contract as the GIF branch's own preset read below.
                _mp4Encoder = Mp4Encoder.Create(
                    _mp4TempPath, _selectionPx.Width, _selectionPx.Height, _targetFps,
                    withAudio: _audio is not null, preset: _mp4SizePreset);
                // Whether THIS take's MP4 carries an AAC track is fixed here forever — a soft stop
                // releases the engine for the review span, and resume-from-review consults this to
                // know whether to bring it back.
                _audioTrackEnabled = _audio is not null;
            }
            else
            {
                _gifTempPath = Path.Combine(Path.GetTempPath(), $"roesnip_rec_{Guid.NewGuid():N}.gif");
                // The preset is whatever the Setup-phase GIF row last selected (or the persisted
                // settings default, if it was never touched this session) — see SetGifSizePreset's
                // doc comment for why reading the field here is the correct "next take" contract.
                var gifOptions = GifSizePresets.ForPreset(_gifSizePreset);
                _gifRenderScale = gifOptions.RenderScale;
                // Compact (0.75) and Minimal (0.5) set RenderScale < 1.0 today; every other tier keeps
                // the encoder's canvas exactly the selection size (already even — RoundDownToEven
                // upstream). Below 1.0, floor each dimension to even AFTER scaling (scaling an even
                // selection can still land on an odd number, e.g. 802 -> 401 at 0.5, or 402 -> 301 at
                // 0.75) — GifEncoder.Create has no tolerance for odd dimensions any more than the
                // unscaled path does.
                _gifCanvasWidth = _gifRenderScale < 1.0 ? ScaledCanvasDimension(_selectionPx.Width, _gifRenderScale) : _selectionPx.Width;
                _gifCanvasHeight = _gifRenderScale < 1.0 ? ScaledCanvasDimension(_selectionPx.Height, _gifRenderScale) : _selectionPx.Height;
                _gifEncoder = GifEncoder.Create(_gifTempPath, _gifCanvasWidth, _gifCanvasHeight, options: gifOptions);
            }

            // Started last (after the encoder is ready to receive frames) so no captured frame is
            // ever silently dropped for lack of a sink.
            if (spanning)
            {
                StartSpanningRecorders(intersecting);
            }
            else
            {
                _recorder!.Start();
            }

            _stopwatch = System.Diagnostics.Stopwatch.StartNew();
            _paused = false;
            _pausedTicks = 0;
            // Threadpool timer, NOT a DispatcherTimer: a WM_TIMER-driven tick is starved by the
            // recording's own CPU/message-queue load, producing multi-second jumps in the HUD (the
            // bug this fixes). This timer's own tick is independent of the UI thread's message
            // queue; only the resulting text update is marshalled onto it, via BeginInvoke below.
            _elapsedTimer = new System.Threading.Timer(OnElapsedTimerTick, null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));

            _displayChangedHandler = (_, _) => RequestForceStopAndSave();
            SystemEvents.DisplaySettingsChanged += _displayChangedHandler;

            // BelowNormal, not Normal: this thread's own queue (RegionRecorder.Frames) is bounded
            // DropOldest (see that channel's own doc comment), so falling behind under contention
            // sheds load gracefully — a dropped stale frame, not a stall. There is no such graceful
            // degradation for whatever the user is doing in the FOREGROUND while a recording runs
            // (a game, a video call); giving this thread lower scheduling priority means the OS
            // favors those over the encoder on a contended CPU instead of the other way around.
            // Which method this thread runs is fixed for the take's WHOLE lifetime — see
            // _spanTopology's own doc comment for why a take never switches between these two
            // bodies mid-take (a .NET Thread can't be redirected once started).
            ThreadStart threadBody = spanning ? new ThreadStart(EncoderLoopSpanning) : new ThreadStart(EncoderLoop);
            _encoderThread = new Thread(threadBody)
            {
                IsBackground = true,
                Name = "RoeSnip-Recording-Encoder",
                Priority = ThreadPriority.BelowNormal,
            };
            _encoderThread.Start();

            _phase = Phase.Capturing;
            _chrome!.EnterRecording();
            _outline?.SetInteractionMode(allowResize: false); // size is locked to the encoder now - band moves, not resizes
            FileLog.Write($"RoeSnip: recording capture started ({_format}, {_selectionPx.Width}x{_selectionPx.Height}, {_targetFps}fps, {intersecting.Count} monitor(s){(spanning ? " [spanning]" : "")})");
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: failed to start recording capture: {ex.Message}");
            _notifier?.ShowError($"Failed to start recording: {ex.Message}");

            try { _elapsedTimer?.Dispose(); } catch { /* best-effort */ }
            _elapsedTimer = null;
            try { _recorder?.Dispose(); } catch { /* best-effort */ }
            if (_spanTopology is { } failedTopology)
            {
                foreach (var r in failedTopology.Recorders)
                {
                    try { r.Stop(); r.Dispose(); } catch { /* best-effort */ }
                }
            }
            try { _audio?.Dispose(); } catch { /* best-effort */ }
            try { _mp4Encoder?.Dispose(); } catch { /* best-effort */ }
            try { _gifEncoder?.Dispose(); } catch { /* best-effort */ }
            try { if (_mp4TempPath is not null && File.Exists(_mp4TempPath)) File.Delete(_mp4TempPath); } catch { /* best-effort */ }
            try { if (_gifTempPath is not null && File.Exists(_gifTempPath)) File.Delete(_gifTempPath); } catch { /* best-effort */ }
            _recorder = null;
            _spanTopology = null;
            _audio = null;
            _mp4Encoder = null;
            _gifEncoder = null;

            // The specific error was already surfaced above — TeardownSession's own messaging stays
            // silent for this path.
            TeardownSession(finalPath: null, dialogCancelled: false, encoderAbandoned: false);
        }
    }

    /// <summary>Builds and starts one <see cref="RegionRecorder"/> per intersected monitor plus the
    /// compositor slot geometry that ties them together — multi-monitor recording phase 2's spanning
    /// capture entry point (PLAN-MULTIMON-RECORDING.md §1). UI thread only (<see cref="BeginCapture"/>).
    /// On a partial failure (one monitor's RegionRecorder throws mid-construction/start), tears down
    /// whatever DID start and rethrows — a half-spanning take (some monitors capturing, others not)
    /// is not a state this class supports; BeginCapture's own catch handles the resulting
    /// cleanup/messaging exactly like a single-recorder start failure already does.</summary>
    private void StartSpanningRecorders(IReadOnlyList<MonitorInfo> intersecting)
    {
        var slots = SpanningCanvasCompositor.BuildSlots(_selectionPx, intersecting, ComputeFixedToneMapOpts).ToArray();
        var recorders = new RegionRecorder[slots.Length];
        try
        {
            for (int i = 0; i < slots.Length; i++)
            {
                var r = new RegionRecorder(slots[i].Monitor, slots[i].MonitorRelativeCrop, _targetFps);
                r.Faulted += OnRecorderFaulted;
                recorders[i] = r;
            }
            foreach (var r in recorders)
            {
                r.Start();
            }
        }
        catch
        {
            foreach (var r in recorders)
            {
                if (r is null) continue;
                try { r.Stop(); r.Dispose(); } catch { /* best-effort teardown of a partial spanning start */ }
            }
            throw;
        }

        Volatile.Write(ref _spanTopology, new SpanningTopology(slots, recorders));
    }

    /// <summary>Threadpool timer thread (never the UI thread) — ticks every ~250ms regardless of
    /// what the UI thread's message queue is doing. Computes the pause-aware media elapsed here and
    /// marshals only the resulting text update onto the UI thread. Guards against a tick landing
    /// after Stop()/teardown already disposed the timer (Timer.Dispose does not block for an
    /// in-flight callback) by re-checking phase/chrome once the BeginInvoke actually runs.</summary>
    private void OnElapsedTimerTick(object? state)
    {
        var dispatcher = _uiDispatcher;
        var stopwatch = _stopwatch;
        if (dispatcher is null || stopwatch is null)
        {
            return;
        }

        dispatcher.BeginInvoke(new Action(() =>
        {
            if (_phase != Phase.Capturing || _chrome is null)
            {
                return; // stopped/torn down between the tick firing and this running on the UI thread
            }

            // Pause-aware media clock: exclude all accumulated paused time, plus the in-progress
            // pause (if any is currently open), from the stopwatch's raw elapsed. Frozen while
            // paused (both terms grow at the same rate), continuous across a pause (CHANGE 4).
            long paused = _pausedTicks + (_paused ? System.Diagnostics.Stopwatch.GetTimestamp() - _pauseStartTicks : 0);
            var elapsed = TimeSpan.FromSeconds((double)(stopwatch.ElapsedTicks - paused) / System.Diagnostics.Stopwatch.Frequency);

            // No duration cap for either format: GIF frames stream to a temp file now (see
            // GifEncoder), so the old 60-second in-memory-buffering cap has no reason to exist.
            _chrome!.SetElapsed(elapsed, cap: null);
        }));
    }

    /// <summary>Chrome's Pause button — UI thread only. Valid only mid-take (Capturing) and not
    /// already paused. Marks the pause start; frames stop entering the queue immediately
    /// (RegionRecorder.Paused) and DrainAudio starts dropping queued chunks on the encoder thread.</summary>
    private void Pause()
    {
        if (_phase != Phase.Capturing || _paused)
        {
            return;
        }
        _pauseStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        _paused = true;
        SetAllRecordersPaused(true);
        _chrome!.SetPaused(true);
    }

    /// <summary>Fans a pause/resume toggle out to whatever is currently capturing — the single
    /// recorder (phase 1) or every recorder in a spanning take's set (phase 2, PLAN-MULTIMON-
    /// RECORDING.md §7's "trivial fan-out" call). Same field, same semantics
    /// (<see cref="RegionRecorder.Paused"/>), just iterated for the spanning case.</summary>
    private void SetAllRecordersPaused(bool paused)
    {
        if (_spanTopology is { } topology)
        {
            foreach (var r in topology.Recorders)
            {
                r.Paused = paused;
            }
        }
        else
        {
            _recorder!.Paused = paused;
        }
    }

    /// <summary>Chrome's Resume button — UI thread only. Folds the just-ended pause into the
    /// running total BEFORE flipping _paused/_recorder.Paused back off, so the very next frame the
    /// encoder thread sees already has the enlarged _pausedTicks available (see the EncoderLoop/
    /// DrainAudio doc comments for why a per-frame Volatile.Read of it is safe here).
    ///
    /// Also reachable from REVIEWING (a soft-stopped take is just a paused take with review
    /// chrome — see StopCaptureToReview): resuming there steps back into Capturing and the same
    /// take keeps recording, with the whole review span excluded from the media timeline like any
    /// other pause — that is what makes stop-review-resume cuts seamless in the output.</summary>
    private void Resume()
    {
        if (!_paused || (_phase != Phase.Capturing && _phase != Phase.Reviewing))
        {
            return;
        }
        _pausedTicks += System.Diagnostics.Stopwatch.GetTimestamp() - _pauseStartTicks;
        _paused = false;
        SetAllRecordersPaused(false);
        if (_phase == Phase.Reviewing)
        {
            // The soft stop released the audio engine (mic indicator off during review) — bring it
            // back for takes whose MP4 already carries an AAC track. A device failure here keeps
            // the video going; the track just has a silent gap.
            if (_audioTrackEnabled)
            {
                _audio = AudioCaptureEngine.TryStart(_mic, _systemAudio); // replaces the disposed engine
                if (_audio is null)
                {
                    _notifier?.ShowError("Audio capture unavailable; resuming video only.");
                }
            }
            _phase = Phase.Capturing;
            _chrome!.EnterRecording(); // resets the paused visuals itself
        }
        else
        {
            _chrome!.SetPaused(false);
        }
    }

    private void OnRecorderFaulted(Exception ex)
    {
        FileLog.Write($"RoeSnip: recording capture failed mid-session (non-fatal, stopping with what was captured): {ex.Message}");
        RequestForceStopAndSave();
    }

    /// <summary>Safety-net path for triggers that have no user present to click through Setup ->
    /// Reviewing -> Save themselves (a mid-recording capture fault, or a display-settings change
    /// while capturing — e.g. a monitor was unplugged). Marshals onto the UI thread first (both
    /// triggers can fire off-thread) then stops the current take into review and immediately saves
    /// it, matching the old single-step "stop and save" behavior for these automatic cases.</summary>
    private void RequestForceStopAndSave()
    {
        var dispatcher = _uiDispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            ForceStopAndSave();
        }
        else
        {
            dispatcher.BeginInvoke(new Action(ForceStopAndSave));
        }
    }

    private void ForceStopAndSave()
    {
        if (_phase == Phase.Setup)
        {
            // Nothing was ever captured — there is nothing to save, just close quietly.
            CancelAndDiscard();
            return;
        }
        if (_phase == Phase.Capturing)
        {
            StopCaptureToReview(); // soft stop: always lands in Reviewing with the pipeline alive
        }
        if (_phase == Phase.Reviewing)
        {
            // Never re-arm from this path: it runs because the pipeline faulted or the display
            // topology changed, so "record another take on the same region" may not even be
            // possible — save what exists and end the session.
            SaveAndFinish(rearmAfterSave: false);
        }
    }

    /// <summary>Ends the active capture pass if one is running: unsubscribes the display-change
    /// safety net, stops the UI timer, shuts audio down, stops the recorder, and joins the encoder
    /// thread. Audio shuts down FULLY (flag + bounded thread join, which is what Dispose does)
    /// before the video channel completes: the encoder thread's terminal DrainAudio runs almost
    /// immediately after _recorder.Stop(), so a fire-and-forget audio stop would race the capture
    /// thread's final flush and silently drop the take's last ~10ms of audio. Bounded join: a
    /// wedged encoder (stuck MF call, disk stall) must never hang the UI thread forever — if it
    /// doesn't finish in time we must NOT touch _mp4Encoder/_gifEncoder or the temp file from here
    /// on (the abandoned thread can still be inside IMFSinkWriter.WriteSample/Finalize, and that COM
    /// interface is not safe to call concurrently from two threads), so this deliberately leaks the
    /// temp file/writer rather than race it. Disposes the encoder + recorder only once actually
    /// joined. A no-op (returns true) when nothing is capturing. Returns false only when the
    /// encoder thread had to be abandoned.</summary>
    private bool HardStopCaptureIfNeeded()
    {
        // Gate on the pipeline, not the phase: since Stop became a soft stop, the recorder/encoder
        // stay alive through Reviewing (so Resume can continue the take) and the hard stop happens
        // from there — Save, Restart, and Cancel all funnel through here with _phase == Reviewing.
        // Spanning mode (multi-monitor recording phase 2): _recorder and _spanTopology are mutually
        // exclusive (see _spanTopology's own doc comment), so checking both null is exactly "is
        // anything capturing" for either mode.
        if (_recorder is null && _spanTopology is null)
        {
            return true;
        }

        // Belt-and-braces: stop the drag-rebuild debounce before it can fire a rebuild against a
        // topology this method is about to tear down — see the timer field's own doc comment.
        _spanningRebuildDebounce?.Stop();
        _pendingSpanIntersecting = null;

        if (_displayChangedHandler is not null)
        {
            SystemEvents.DisplaySettingsChanged -= _displayChangedHandler;
            _displayChangedHandler = null;
        }
        _elapsedTimer?.Dispose();
        _elapsedTimer = null;

        _audio?.Dispose();
        _audio = null;

        if (_spanTopology is { } topology)
        {
            foreach (var r in topology.Recorders)
            {
                r.Stop(); // stops feeding the queue and completes it, same as the single-recorder branch
            }
        }
        else
        {
            _recorder!.Stop();
        }

        bool joined = _encoderThread!.Join(TimeSpan.FromSeconds(5));
        if (!joined)
        {
            FileLog.Write("RoeSnip: recording encoder thread did not finish within 5s; abandoning it without touching its output.");
            return false;
        }

        _mp4Encoder?.Dispose(); // only safe once the encoder thread — the only other owner — has actually finished
        _mp4Encoder = null;
        _gifEncoder?.Dispose(); // same ownership rule for the GIF temp-file stream
        _gifEncoder = null;
        // The recorder(s)' own device/item are never shared with the encoder thread (which only
        // reads from RegionRecorder.Frames), so disposing them is safe regardless.
        if (_spanTopology is { } topologyToDispose)
        {
            foreach (var r in topologyToDispose.Recorders)
            {
                r.Dispose();
            }
            _spanTopology = null;
        }
        else
        {
            _recorder!.Dispose();
            _recorder = null;
        }
        return true;
    }

    /// <summary>Chrome's Stop button (or PrtScr while Capturing). SOFT stop: pauses the pipeline
    /// (identical bookkeeping to Pause — frames drop pre-readback, the review span accumulates as
    /// paused time) and moves to Reviewing with the recorder and encoders still alive, so the
    /// Reviewing chrome's Resume can step back into Capturing and CONTINUE the same take with a
    /// seamless cut. The hard stop (encoder join + finalize) happens on the way out of Reviewing:
    /// Save, Restart, and Cancel each call <see cref="HardStopCaptureIfNeeded"/> themselves.</summary>
    private void StopCaptureToReview()
    {
        if (_phase != Phase.Capturing)
        {
            return;
        }
        FileLog.Write($"RoeSnip: recording capture soft-stopping for review (elapsed={_stopwatch?.Elapsed})");

        if (!_paused)
        {
            _pauseStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            _paused = true;
            SetAllRecordersPaused(true);
        }
        // else: stopped while already paused — the running pause span simply continues.

        // Release the microphone/loopback for the review span (unlike a quick Pause, a review can
        // last minutes and must not keep the OS "microphone in use" indicator lit). The reference
        // is deliberately KEPT: Dispose's final flush enqueues the last pre-stop chunks, and the
        // encoder thread's periodic DrainAudio still drains that queue — its timestamp check
        // writes everything captured before the stop and drops the rest. Resume re-creates the
        // engine; the hard stop disposes again, which is documented-idempotent.
        _audio?.Dispose();

        _phase = Phase.Reviewing;
        _chrome!.SetPaused(true); // Reviewing's Pause/Resume button must read (and route as) Resume
        // Senior-review fix (Finding 1c): re-evaluate the chrome's Share button gate every time
        // Reviewing is (re-)entered, from the same fresh-disk-read helper RequestShare itself uses
        // — mirrors ToolbarControl.SetShareProviders' own "no clickable-but-broken button" rule
        // instead of RecordingChrome's old check (whether ShareRequested has ANY subscriber, which
        // Start() always satisfies unconditionally and so never actually reflected whether Share
        // could ever succeed).
        _chrome!.SetShareAvailable(ResolveFreshDefaultShareProvider() is not null);
        _chrome!.EnterReviewing();
    }

    /// <summary>Sharing/* subsystem: the single fresh-disk-state provider lookup shared by
    /// <see cref="RequestShare"/> (deciding whether Share is a no-op) and
    /// <see cref="StopCaptureToReview"/> (deciding whether the chrome's Share button should even be
    /// enabled) — see RequestShare's own doc comment for why "fresh" (a plain <see
    /// cref="SettingsStore.Load()"/>, not this session's own <see cref="_liveSettings"/> snapshot)
    /// matters here specifically: a provider configured via the tray's Settings window while a take
    /// sits in Reviewing must be picked up without needing a new take.</summary>
    private static RoeSnip.Core.Sharing.ShareProviderConfig? ResolveFreshDefaultShareProvider()
    {
        try
        {
            var settings = SettingsStore.Load();
            return RoeSnip.Core.Sharing.ShareManager.ResolveDefault(settings.ShareProviders, settings.DefaultShareProviderId);
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: fresh settings load for share availability check failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Chrome's confirmed Restart (the confirmation prompt itself lives in
    /// RecordingChrome). Discards whatever the current take is — mid-capture or already stopped and
    /// under review — and re-arms for a fresh <see cref="BeginCapture"/> with new temp file paths.
    /// Reuses the session's fixed tone-map options and target fps; everything else about the
    /// capture pipeline is rebuilt from scratch on the next Start.</summary>
    private void RestartTake()
    {
        bool joined = HardStopCaptureIfNeeded();
        if (!joined)
        {
            _notifier?.ShowError("Could not restart: the recording did not stop in time.");
            TeardownSession(finalPath: null, dialogCancelled: false, encoderAbandoned: false); // already messaged above
            return;
        }

        CleanupTempFile();
        _mp4TempPath = null;
        _gifTempPath = null;

        _phase = Phase.Setup;
        _chrome!.EnterSetup();
        _outline?.SetInteractionMode(allowResize: true); // back to setup - the region can be reshaped again
    }

    /// <summary>Available in every phase — aborts the whole recording without saving. Discards any
    /// in-progress or already-stopped take, then tears the session down.</summary>
    private void CancelAndDiscard()
    {
        bool joined = HardStopCaptureIfNeeded();
        if (joined)
        {
            CleanupTempFile();
        }
        // else: encoder thread abandoned — deliberately leak rather than race it, same rule as
        // HardStopCaptureIfNeeded's own doc comment.
        TeardownSession(finalPath: null, dialogCancelled: false, encoderAbandoned: !joined);
    }

    /// <summary>Chrome's Save button (or PrtScr while Reviewing) — only valid once a take has
    /// actually been stopped into review. Hard-stops the still-alive pipeline (Stop is a soft stop
    /// now), finalizes/moves the temp file to a real path, then — on a successful save — returns
    /// to Setup with the same region so another take can be recorded immediately; every other
    /// outcome tears the session down as before.</summary>
    private void SaveAndFinish(bool rearmAfterSave = true)
    {
        if (_phase != Phase.Reviewing || _saving || _sharing)
        {
            // _saving: the save dialog below runs a nested message pump on this thread, so a
            // PrtScr press while it is open dispatches straight back into this method with _phase
            // still Reviewing — without this guard the reentrant call could save/re-arm first and
            // the suspended outer call would then tear the fresh session down when it resumed.
            // _sharing: belt-and-braces (RequestShare never re-enters via a nested pump the way Save
            // does, and it always resets _sharing/re-arms to Setup before returning, so in practice
            // _phase is already back to Setup by the time this could fire concurrently) — see
            // RequestShare's own doc comment.
            return;
        }
        _saving = true;

        bool joined = HardStopCaptureIfNeeded();
        if (!joined)
        {
            _saving = false;
            _notifier?.ShowError("Recording could not be saved: the encoder did not stop in time.");
            TeardownSession(finalPath: null, dialogCancelled: false, encoderAbandoned: false); // already messaged above
            return;
        }

        string? finalPath = null;
        bool dialogCancelled = false;
        try
        {
            // The chrome is Hidden (not yet Closed) so it can still own the SaveFileDialog — a WPF
            // window needs to exist as a valid HWND for Dialog.ShowDialog(owner) to parent correctly;
            // closing it first would leave the dialog unowned (it could appear behind other windows
            // on a multi-monitor setup).
            _chrome!.Hide();
            finalPath = SaveOutput();
            dialogCancelled = finalPath is null; // SaveOutput returns null only when the user cancelled the dialog
        }
        catch (Exception ex)
        {
            _notifier?.ShowError($"Failed to save recording: {ex.Message}");
        }
        finally
        {
            if (dialogCancelled)
            {
                CleanupTempFile();
            }
            _saving = false;
        }

        if (finalPath is not null && rearmAfterSave)
        {
            _notifier?.ShowSavedBalloon(finalPath);
            RearmForAnotherTake();
            return;
        }

        TeardownSession(finalPath, dialogCancelled, encoderAbandoned: false);
    }

    /// <summary>After a successful save: back to Setup with the SAME selected region instead of
    /// tearing down, so the user can immediately record another take (chrome Start, or PrtScr).
    /// Mirrors RestartTake's re-arm minus the discard — the finished file was already moved to its
    /// final path, so only the stale temp-path fields need clearing. Cancel (chrome button) remains
    /// the way to dismiss the session from here.</summary>
    private void RearmForAnotherTake()
    {
        _mp4TempPath = null;
        _gifTempPath = null;

        _phase = Phase.Setup;
        _chrome!.EnterSetup();
        _chrome!.Show(); // was Hidden to own the save dialog
        _outline?.SetInteractionMode(allowResize: true); // new take - the region can be reshaped again
    }

    /// <summary>Reviewing state's Share button (Sharing/* subsystem — RecordingChrome.ShareRequested's
    /// own doc comment names this class as the owner responsible for wiring it). DESIGN, spelled out
    /// because the integration brief specifically asked for it:
    ///
    /// Senior-review fix (HIGH, data loss): the provider is resolved FIRST, from a FRESH
    /// <see cref="SettingsStore.Load()"/> — not this session's own <see cref="_liveSettings"/>
    /// snapshot, which can be minutes stale by the time a Reviewing take gets around to sharing —
    /// so a provider added via the tray's Settings window WHILE this take sits in Reviewing is seen.
    /// If nothing resolves, this is a plain NO-OP: an honest balloon ("configure a share provider in
    /// Settings first") and an early return, before <see cref="HardStopCaptureIfNeeded"/> ever runs.
    /// The take is left exactly as it was — still soft-stopped, still fully Resumable — instead of
    /// the old behavior, which hard-stopped and rearmed regardless, then discovered "no provider"
    /// only inside a detached upload task, by which point its own finally-block had already deleted
    /// the only copy of a take the user could never get back.
    ///
    /// Only once a provider resolves does this go on to do what Share always did:
    ///
    /// Stop is a SOFT stop (see StopCaptureToReview's own doc comment) — the recorder/encoder stay
    /// alive through Reviewing so Resume can continue the SAME take. The temp file is therefore NOT
    /// necessarily complete/stable yet when Share is clicked; uploading it mid-write would race the
    /// encoder thread. So Share's next step is exactly what Save's first step already is: call
    /// HardStopCaptureIfNeeded() to join the encoder thread and finalize the container. That call is
    /// irreversible (it's the same hard stop Save/Restart/Cancel all use) — once it succeeds, Resume
    /// is gone regardless of what Share does next, exactly as if the user had clicked Save. The
    /// finalized file's size is exactly what ShareManager.UploadAsync's own offline MaxUploadBytes
    /// pre-flight check (run against the real FileStream this method opens, before any network call)
    /// judges — a preflight rejection surfaces through the exact same Success=false result an
    /// ordinary upload failure would, so it needs no separate code path here.
    ///
    /// Unlike Save, this never calls SaveOutput/File.Move — the point is to upload the file WITHOUT
    /// moving it, so the temp path stays exactly where BeginCapture wrote it and a plain FileStream
    /// reads it for the upload.
    ///
    /// "Re-arm or teardown consistent with Save's flow" (the brief's own phrasing): this chooses
    /// re-arm, unconditionally, immediately after a successful hard stop — mirroring SaveAndFinish's
    /// OWN successful-save branch (RearmForAnotherTake), not its dialog-cancelled/failure branches.
    /// The reasoning: once the pipeline is hard-stopped, sitting in Reviewing has nothing left to
    /// offer (Resume is impossible; Restart/Cancel would just discard a take the user explicitly
    /// asked to share) — the take is DONE, precisely like a completed Save. Re-arming immediately
    /// also happens to be the only option that doesn't block the UI thread on the network: the actual
    /// upload is handed to <see cref="RoeSnip.Sharing.ShareFlowPresenter"/> (never awaited here)
    /// against a few plain local values captured before rearming (a FileStream opened on the temp
    /// path, a friendly filename, content type, the resolved provider config) — none of them alias
    /// the session's own mutable fields, so a user who immediately starts a brand-new take (fresh
    /// _mp4TempPath/_gifTempPath) can never race the in-flight upload of the OLD one. The upload's
    /// own progress/result is now reported via the presenter's own ShareResultWindow (a small
    /// always-on-top toast) rather than a tray balloon — there is deliberately no "please wait" UI on
    /// the chrome itself for it, since the chrome has already moved on to Setup by the time the
    /// network call even starts.
    ///
    /// FAILURE = DATA LOSS, confronted honestly: once HardStopCaptureIfNeeded succeeds, Resume is
    /// gone for good — there is no path back to Reviewing. So the temp file is deleted ONLY via the
    /// presenter's onSuccess callback below; every failure (network error, timeout, an offline
    /// MaxUploadBytes rejection, cancellation, a provider that stopped resolving between the check
    /// above and the call) leaves the finished mp4/gif sitting in the temp directory and the
    /// ShareResultWindow's Failure state names its FULL PATH, so the user can still go rescue it by
    /// hand. A leaked temp file only if the process exits mid-upload remains the one accepted gap —
    /// everything else that can go wrong now preserves the recording instead of silently discarding
    /// it.
    ///
    /// A hard-stop failure (wedged encoder) is handled exactly like SaveAndFinish's own failure
    /// branch: error balloon, tear the whole session down, nothing to upload.</summary>
    private void RequestShare()
    {
        if (_phase != Phase.Reviewing || _saving || _sharing)
        {
            return; // same reentrancy reasoning as SaveAndFinish's own guard — see _sharing's own doc comment
        }

        // Resolve the provider FIRST and from FRESH disk state — see this method's own doc comment
        // for why both of those must happen before the irreversible hard stop below, not after it.
        var config = ResolveFreshDefaultShareProvider();
        if (config is null)
        {
            // No-op: the take stays exactly as it was — still soft-stopped, still fully Resumable.
            _notifier?.ShowError("Recording not shared: configure a share provider in Settings first.");
            return;
        }

        _sharing = true;

        bool joined = HardStopCaptureIfNeeded();
        if (!joined)
        {
            _sharing = false;
            _notifier?.ShowError("Recording could not be shared: the encoder did not stop in time.");
            TeardownSession(finalPath: null, dialogCancelled: false, encoderAbandoned: false); // already messaged above
            return;
        }

        string tempPath = _format == RecordingFormat.Mp4 ? _mp4TempPath! : _gifTempPath!;
        string ext = _format == RecordingFormat.Mp4 ? ".mp4" : ".gif";
        string contentType = _format == RecordingFormat.Mp4 ? "video/mp4" : "image/gif";
        string fileName = $"roesnip_{DateTime.Now:yyyyMMdd_HHmmss}{ext}";

        _sharing = false;
        RearmForAnotherTake(); // chrome is back in Setup from here — see this method's own doc comment for why

        Stream stream;
        try
        {
            stream = new FileStream(
                tempPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 16, useAsync: true);
        }
        catch (Exception ex)
        {
            // Could not even open the finalized file for reading — report the same way an upload
            // failure would, naming the (still-present) path so the user can go rescue it by hand.
            _notifier?.ShowError($"Recording share failed: {ex.Message} The recording was kept at: {tempPath}");
            return;
        }

        var request = new RoeSnip.Core.Sharing.ShareUploadRequest(stream, fileName, contentType);
        RoeSnip.Sharing.ShareFlowPresenter.StartUpload(
            config,
            request,
            keptFilePathOnFailure: tempPath,
            onSuccess: () =>
            {
                // The DATA-LOSS RULE's enforcement point: delete ONLY here, on a genuine upload
                // success — never in the presenter's own generic plumbing. See this method's own doc
                // comment ("FAILURE = DATA LOSS").
                try { if (File.Exists(tempPath)) File.Delete(tempPath); }
                catch (Exception ex) { FileLog.Write($"RoeSnip: failed to delete shared recording temp file: {ex.Message}"); }
            },
            onFailure: null,
            notifier: _notifier);
    }

    /// <summary>The single terminal path every end-of-recording route funnels through (Save,
    /// Cancel, an unrecoverable failure). Idempotent via <see cref="_ended"/>.</summary>
    private void TeardownSession(string? finalPath, bool dialogCancelled, bool encoderAbandoned)
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0)
        {
            return;
        }
        FileLog.Write($"RoeSnip: recording session ending (saved={finalPath is not null})");

        try { _outline?.CloseOutline(); }
        catch (Exception ex) { FileLog.Write($"RoeSnip: closing the recording outline failed (non-fatal): {ex.Message}"); }
        _chrome?.CloseChrome();

        if (finalPath is not null)
        {
            _notifier?.ShowSavedBalloon(finalPath);
        }
        else if (dialogCancelled)
        {
            _notifier?.ShowError("Recording discarded: the save was cancelled.");
        }
        else if (encoderAbandoned)
        {
            _notifier?.ShowError("Recording could not be saved: the encoder did not stop in time.");
        }
        // else: a plain user Cancel — silent, matching the toolbar Cancel button's own "close
        // without capturing" semantics elsewhere.

        RoeSnip.CaptureGate.Exit(); // exactly once, last — see AppComposition.StartRecording's doc comment
        RecordingController.OnSessionEnded(this);

        // A recording is the app's biggest allocation burst (GifEncoder buffers every frame in
        // memory until SaveTo — its own doc comment cites ~1-1.5 GB peak for a 60 s GIF). Without
        // an explicit trim that peak stays committed for the rest of the process's life; this is
        // the recording-path counterpart of TrayApp.ObserveCaptureTask's post-snip schedule.
        RoeSnip.IdleMemoryTrimmer.Schedule(Dispatcher.CurrentDispatcher);
    }

    /// <summary>UI thread (called from <see cref="SaveAndFinish"/>). Test hook: when
    /// ROESNIP_RECORD_AUTOSAVE is set, saves straight there instead of showing the SaveFileDialog —
    /// this is how the automated smoke/verify harness drives Record without needing UIA against a
    /// native Win32 file-picker. Returns null if the user cancelled the dialog (temp file already
    /// cleaned up by the caller's CleanupTempFile in that case).</summary>
    private string? SaveOutput()
    {
        string ext = _format == RecordingFormat.Mp4 ? ".mp4" : ".gif";
        string fileName = $"roesnip_{DateTime.Now:yyyyMMdd_HHmmss}{ext}";

        string? autosaveDir = Environment.GetEnvironmentVariable("ROESNIP_RECORD_AUTOSAVE");
        string finalPath;
        if (!string.IsNullOrEmpty(autosaveDir))
        {
            Directory.CreateDirectory(autosaveDir);
            finalPath = Path.Combine(autosaveDir, fileName);
        }
        else
        {
            try
            {
                Directory.CreateDirectory(_settings.SaveDirectory);
            }
            catch (Exception)
            {
                // Fall through and let SaveFileDialog itself surface a directory problem, if any —
                // same pattern as OverlayController.TryShowSaveDialog.
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                InitialDirectory = _settings.SaveDirectory,
                FileName = fileName,
                DefaultExt = ext,
                Filter = _format == RecordingFormat.Mp4 ? "MP4 video (*.mp4)|*.mp4" : "GIF image (*.gif)|*.gif",
                AddExtension = true,
            };
            bool? result = dialog.ShowDialog(_chrome);
            if (result != true)
            {
                return null; // user cancelled — caller's CleanupTempFile removes the temp mp4, if any
            }
            finalPath = dialog.FileName;
        }

        // Both formats stream to a temp file now; saving is one atomic move either way.
        File.Move(_format == RecordingFormat.Mp4 ? _mp4TempPath! : _gifTempPath!, finalPath, overwrite: true);
        return finalPath;
    }

    private void CleanupTempFile()
    {
        foreach (var tempPath in new[] { _mp4TempPath, _gifTempPath })
        {
            if (tempPath is null)
            {
                continue;
            }
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (Exception ex)
            {
                FileLog.Write($"RoeSnip: failed to delete abandoned recording temp file: {ex.Message}");
            }
        }
    }

    /// <summary>Encoder thread only (never the WGC callback thread, never the UI thread): drains
    /// RegionRecorder.Frames, tone-maps each raw CapturedFrame with the FIXED options computed once
    /// in Start() (or re-pinned per monitor at a handoff — see <see cref="ComputeFixedToneMapOpts"/>),
    /// and feeds the chosen encoder. Exits when the LIVE recorder's own channel completes
    /// (RegionRecorder.Stop() was called on the recorder that is STILL the session's current one) or
    /// on an unrecoverable encoder exception. A channel completing because a cross-monitor drag
    /// handoff swapped <see cref="_recorder"/> out from under this loop (multi-monitor recording
    /// phase 1, PLAN-MULTIMON-RECORDING.md §3 — the section's own "load-bearing" fix) does NOT end
    /// the take: see the reader-reacquisition branch below. Runs once per take — a Restart spins up
    /// a brand-new thread via the next BeginCapture.</summary>
    private void EncoderLoop()
    {
        double freq = System.Diagnostics.Stopwatch.Frequency;
        // recorderRef tracks WHICH recorder `reader` currently drains — compared against the live
        // _recorder (Volatile.Read) whenever the channel completes, so a handoff's swap can be told
        // apart from a real Stop() without a separate pending-handoff flag (see the completion
        // branch below for the full reasoning).
        RegionRecorder recorderRef = _recorder!;
        var reader = recorderRef.Frames;
        int framesWritten = 0;
        // Epoch is the FIRST frame actually dequeued here, not an independent Stopwatch sample taken
        // on this thread's own first line. RegionRecorder.Start() (and thus frame production) begins
        // before this thread is even created/scheduled/JITed, so a Stopwatch.GetTimestamp() sampled
        // here could land AFTER one or more frames already sitting in the queue — those would get a
        // negative-then-clamped-to-0 SampleTime, producing duplicate/non-monotonic timestamps that
        // Media Foundation's sink writer can reject. Deriving the epoch from the first real frame
        // makes timestamp 0 always correspond to an actual frame and every later one strictly larger.
        long? epochTicks = null;
        // The raw pixels of the last frame actually handed to the encoder, for WHICHEVER format
        // this take is (only one of the two is ever non-null for the session's whole lifetime).
        // WGC's dirty tracking is monitor-wide, so a change anywhere on the monitor delivers a
        // frame even when the recorded crop is untouched — comparing raw bytes here skips those
        // before paying the (much more expensive) tone-map. Applies to BOTH formats: GIF also has
        // GifEncoder's own SDR diff as a second, finer-grained gate underneath this one, but MP4
        // has no such second gate — WriteFrame is a straight encode, so this raw check is the ONLY
        // thing standing between a static region and paying full tone-map + H.264 encode on every
        // single WGC callback (this was the "MP4 pays full CPU while the user works elsewhere on
        // the same monitor" bug). Each starts null, so the FIRST frame of a take is always kept —
        // there is nothing yet to compare it against.
        CapturedFrame? prevGifRaw = null;
        CapturedFrame? prevMp4Raw = null;
        // GIF only: two persistent exactly-canvas-sized tone-map targets so the 50fps path never
        // allocates a fresh (LOH-sized) output array per frame. GifEncoder.AddFrame(SdrImage, long)
        // never retains a reference to the buffer it's handed, on EITHER outcome — it copies
        // whatever it paints into its own internal diff baseline synchronously before returning
        // (see that method's doc comment) — so there is no aliasing hazard for this rotation to
        // protect against in the first place. The alternation below is kept anyway as cheap,
        // harmless housekeeping, not because it's required for correctness.
        byte[]? gifTonemapA = null, gifTonemapB = null;
        bool gifUseA = true;
        // GIF downscaled render (Compact/Minimal tiers, _gifRenderScale < 1.0 — see
        // GifEncoderOptions.RenderScale's own doc comment): a THIRD persistent exactly-scaled-canvas
        // buffer, allocated once on first use, that the box-filtered downsample writes into before
        // AddFrame ever sees it. Kept separate from gifTonemapA/B (which stay full selection size,
        // the tone-mapper's own output shape) rather than tone-mapping directly into a smaller
        // buffer, since ToneMapper has no notion of a destination size different from its source.
        byte[]? gifScaledScratch = null;

        try
        {
            var waitTask = reader.WaitToReadAsync().AsTask();
            while (true)
            {
                // Bounded wait rather than a plain block: audio must keep flowing into the writer
                // even when the screen is static and WGC delivers no video frame (it only fires on
                // dirty updates) — otherwise queued audio would pile up until the next pixel moved.
                if (!waitTask.Wait(50))
                {
                    DrainAudio(freq, epochTicks);
                    continue;
                }
                if (!waitTask.Result)
                {
                    // The channel we were draining (recorderRef's) completed. Two possible reasons:
                    //   1. A REAL Stop() (HardStopCaptureIfNeeded) — the take genuinely ended.
                    //   2. A cross-monitor drag HANDOFF (HandoffToMonitor) — it deliberately calls
                    //      Stop() on the OLD recorder after already swapping _recorder to the NEW
                    //      one, so this channel completing is expected and does NOT mean the take
                    //      is over.
                    // Distinguish them by comparing the recorder we were draining against the LIVE
                    // one: HardStopCaptureIfNeeded never reassigns _recorder before calling Stop()
                    // on it (it nulls _recorder only AFTER Join()ing this very thread, i.e. after
                    // this loop has already returned) — so a real stop always sees them equal.
                    // HandoffToMonitor's Volatile.Write(ref _recorder, newRecorder) happens strictly
                    // BEFORE its oldRecorder.Stop() call (same UI thread, program order), and that
                    // Stop() is what makes waitTask.Result observably false here — so by the time we
                    // get here after a handoff, this Volatile.Read is guaranteed to already see the
                    // new recorder (release/acquire pair), never null and never the old one.
                    var live = Volatile.Read(ref _recorder);
                    if (ReferenceEquals(live, recorderRef))
                    {
                        break; // real end of take
                    }

                    recorderRef = live!;
                    reader = recorderRef.Frames;
                    // Dispose-and-null the raw-skip baselines at the handoff boundary: avoids
                    // comparing a stale monitor-A frame against monitor-B's first frame. Harmless
                    // either way (dimensions can't mismatch — region size is fixed for the whole
                    // take, plan §3), just cheap correctness housekeeping.
                    prevGifRaw?.Dispose();
                    prevGifRaw = null;
                    prevMp4Raw?.Dispose();
                    prevMp4Raw = null;
                    waitTask = reader.WaitToReadAsync().AsTask();
                    continue;
                }

                while (reader.TryRead(out var queued))
                {
                    var frame = queued.Frame;
                    bool frameKeptAsRawBaseline = false;
                    try
                    {
                        // Subtract the accumulated paused time so the media clock is continuous across
                        // a pause — frames captured DURING a pause never reach this queue at all
                        // (RegionRecorder.Paused drops them before GPU readback), so the only thing left
                        // to correct for is the gap the pause left behind in the raw QPC timestamps.
                        // _pausedTicks only ever grows and Resume() updates it before any post-resume
                        // frame is produced, so a per-frame Volatile.Read is safe here.
                        long paused = Volatile.Read(ref _pausedTicks);
                        long effectiveTicks = queued.TimestampTicks - paused;

                        CapturedFrame? prevRaw = _format == RecordingFormat.Gif ? prevGifRaw : prevMp4Raw;
                        if (prevRaw is not null && RawFramesEqual(prevRaw, frame))
                        {
                            continue; // crop unchanged (monitor was dirty elsewhere) — skip pre-tone-map
                        }

                        epochTicks ??= effectiveTicks;
                        framesWritten++;

                        if (_format == RecordingFormat.Mp4)
                        {
                            var sdr = SdrImage.FromCapturedFrame(frame, _fixedToneMapOpts);
                            long timestamp100ns = (long)((effectiveTicks - epochTicks.Value) / freq * 10_000_000.0);
                            _mp4Encoder!.WriteFrame(sdr, timestamp100ns);

                            // Same raw-skip baseline discipline as the GIF branch below: keep THIS
                            // frame (not a copy) as next iteration's comparison target instead of
                            // disposing it, and tell the finally block not to dispose it out from
                            // under that reference. MF's sink writer is fine with sparse/VFR
                            // samples — each WriteFrame call already carries its own explicit
                            // timestamp100ns, so a skipped stretch just means fewer, further-apart
                            // samples rather than any timestamp math changing.
                            prevMp4Raw?.Dispose();
                            prevMp4Raw = frame;
                            frameKeptAsRawBaseline = true;
                        }
                        else
                        {
                            byte[] target = gifUseA
                                ? (gifTonemapA ??= new byte[frame.Width * 4 * frame.Height])
                                : (gifTonemapB ??= new byte[frame.Width * 4 * frame.Height]);
                            var sdr = SdrImage.FromCapturedFrame(frame, _fixedToneMapOpts, target);

                            // Compact/Minimal tiers: box-filter the full-size tone-mapped frame down
                            // into the reused scaled buffer BEFORE GifEncoder ever sees it — the
                            // encoder's own canvas was opened at the scaled size in BeginCapture, so
                            // every frame handed to AddFrame from here on must already match it.
                            SdrImage encodeSource = sdr;
                            if (_gifRenderScale < 1.0)
                            {
                                gifScaledScratch ??= new byte[_gifCanvasWidth * 4 * _gifCanvasHeight];
                                BoxDownsampleGifFrame(sdr.Pixels, sdr.Width, sdr.Height, gifScaledScratch, _gifCanvasWidth, _gifCanvasHeight);
                                encodeSource = new SdrImage(_gifCanvasWidth, _gifCanvasHeight, gifScaledScratch);
                            }

                            // GifEncoder owns the timing: it diffs against the last emitted frame,
                            // skips no-change frames, crops to the changed region, and patches each
                            // frame's delay to its real display duration once the next frame lands.
                            if (_gifEncoder!.AddFrame(encodeSource, effectiveTicks))
                            {
                                gifUseA = !gifUseA; // harmless housekeeping only — see the comment above
                            }
                            prevGifRaw?.Dispose();
                            prevGifRaw = frame; // becomes the raw-skip baseline for the next frame
                            frameKeptAsRawBaseline = true;
                        }
                    }
                    finally
                    {
                        if (!frameKeptAsRawBaseline)
                        {
                            frame.Dispose();
                        }
                    }
                }

                DrainAudio(freq, epochTicks);
                waitTask = reader.WaitToReadAsync().AsTask();
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: recording encoder failed mid-session after {framesWritten} frame(s): {ex}");
            // Best-effort: keep whatever encoded cleanly so far rather than losing the whole
            // recording to one bad frame/disk hiccup.
            RequestForceStopAndSave();
        }
        finally
        {
            FileLog.Write($"RoeSnip: encoder loop exiting, {framesWritten} frame(s) processed");
            prevGifRaw?.Dispose();
            prevMp4Raw?.Dispose();
            if (_format == RecordingFormat.Mp4)
            {
                // Whatever audio was still queued when the video channel completed belongs in the
                // file — Stop() halts the audio engine BEFORE completing the channel, so this final
                // drain is bounded.
                try { DrainAudio(freq, epochTicks); }
                catch (Exception ex) { FileLog.Write($"RoeSnip: final audio drain failed: {ex}"); }
                try { _mp4Encoder?.FinalizeAndClose(); }
                catch (Exception ex) { FileLog.Write($"RoeSnip: MP4 finalize failed: {ex}"); }
            }
            else
            {
                // Finalize with the take's stop moment on the pause-adjusted clock so the LAST
                // frame's delay covers the static tail the user held before stopping (without it,
                // the final frame would flash for its 3cs provisional delay on every loop).
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                long pausedTotal = Volatile.Read(ref _pausedTicks);
                if (_paused)
                {
                    pausedTotal += now - Volatile.Read(ref _pauseStartTicks); // stopped mid-pause
                }
                try { _gifEncoder?.FinalizeAndClose(now - pausedTotal); }
                catch (Exception ex) { FileLog.Write($"RoeSnip: GIF finalize failed: {ex}"); }
            }
        }
    }

    /// <summary>Encoder thread only — the spanning-mode counterpart of <see cref="EncoderLoop"/>
    /// (multi-monitor recording phase 2, PLAN-MULTIMON-RECORDING.md §1). Structurally different from
    /// EncoderLoop by necessity: EncoderLoop blocks on ONE recorder's <c>WaitToReadAsync</c> and
    /// forwards every frame it produces (subject to that recorder's own throttle) as soon as it
    /// arrives; spanning has N independent, unsynchronized WGC delivery streams (mixed refresh rates,
    /// no common vsync across monitors — see the plan's own "OBS-style latest-per-monitor" rationale),
    /// so this loop instead runs its OWN fixed-cadence tick (paced at 1/_targetFps, the same
    /// schedule-advance-not-since-last-forward throttle idea <see cref="RegionRecorder.
    /// OnFrameArrived"/> already uses) and, each tick, drains every recorder's channel down to its
    /// OWN latest frame — a monitor with nothing new this tick simply leaves its own canvas sub-rect
    /// as whatever it last composited (WGC only fires on a dirty update, so this is expected, not a
    /// bug — same reasoning EncoderLoop's own RawFramesEqual skip already relies on, just now
    /// explicit per monitor instead of implicit for the whole frame).
    ///
    /// Termination: unlike EncoderLoop (which treats its one channel completing as "the take ended,"
    /// then has to disambiguate a real Stop() from a phase-1 handoff swap — see that method's own
    /// comment), this loop never blocks on channel completion at all, so there is nothing to
    /// disambiguate: it simply re-reads <see cref="_spanTopology"/> fresh every tick (a UI-thread
    /// rebuild — RebuildSpanningRecorders/TrySlideSpanningInPlace — publishes a new bundled Slots+
    /// Recorders pair at any time, as ONE Volatile reference — see that field's own doc comment for
    /// why never two independent ones) and exits only once EVERY currently-tracked recorder's own
    /// channel has both completed AND been fully drained (<c>ChannelReader.Completion.IsCompleted</c>)
    /// — which only happens once <see cref="HardStopCaptureIfNeeded"/> has called Stop() on the whole
    /// set and left <see cref="_spanTopology"/> pointing at that same (now all-stopping) pair, i.e. a
    /// real end of take, never a mid-take rebuild (a rebuild always publishes a NEW pair before
    /// stopping the old recorders, so this loop's own local <c>recorders</c> reference would already
    /// have moved on to the new array by the time the old one's channels finish) — EXCEPT for one
    /// narrow timing window this loop closes explicitly: a rebuild's publish-then-Stop can land
    /// entirely WITHIN a single iteration, after this iteration's own top-of-loop read already
    /// committed to the (now-superseded) old array for the rest of that iteration. If every recorder
    /// in that stale old array happens to have zero queued frames (typical for a set that has been
    /// alive only a few milliseconds — see RegionRecorder.OnFrameArrived's own copy-#0 comment for
    /// why a brand-new recorder needs a second WGC callback before it can emit anything), Stop()
    /// completing all of them mid-iteration would otherwise make `allCompleted` read true for an
    /// array that is NOT actually the take's final one — see the re-check right before the break
    /// below for how this loop tells that apart from a genuine end of take.</summary>
    private void EncoderLoopSpanning()
    {
        double freq = System.Diagnostics.Stopwatch.Frequency;
        long minIntervalTicks = Math.Max(1, (long)(freq / _targetFps));

        SpanningTopology initialTopology = Volatile.Read(ref _spanTopology)!;
        RegionRecorder[] recorders = initialTopology.Recorders;
        SpanningCanvasCompositor.MonitorSlot[] slots = initialTopology.Slots;

        // Selection size is fixed for the take's whole lifetime once Capturing begins (BeginCapture's
        // own "encoder canvas stays fixed" note) — hoisted into locals ONCE here rather than reading
        // the _selectionPx field's Width/Height repeatedly through the loop below. _selectionPx's
        // POSITION does change live (OnRegionMoved reassigns the whole struct on the UI thread on
        // every drag tick), and a 16-byte struct field read on this thread has no atomicity guarantee
        // against a concurrent whole-struct write on that one — a torn read could pair a stale Left
        // with a fresh Right (or vice versa) and compute a transiently wrong width/height, which then
        // feeds CopyIntoCanvas' row-stride math or SdrImage's exact-length constructor. Reading
        // Width/Height exactly once, before either thread's per-tick/per-drag mutation can interleave
        // with anything else in this method, removes the race entirely — same reason EncoderLoop's
        // own single-recorder path already keys off frame.Width instead of re-reading shared state
        // every tick.
        int canvasWidth = _selectionPx.Width;
        int canvasHeight = _selectionPx.Height;

        // Persistent, canvas-sized composite target — allocated ONCE for the whole take, never
        // per-tick (the f7aa9a3 LOH/Gen2 lesson applies here exactly as it does to EncoderLoop's own
        // gifTonemapA/B buffers).
        byte[] canvas = new byte[canvasWidth * 4 * canvasHeight];
        // Per-slot tone-map scratch + raw-skip baseline, index-aligned with `recorders`/`slots`
        // above. Resynced (not reallocated wholesale) on a topology change — see the resync block
        // inside the loop below for how entries are carried over by monitor identity.
        byte[]?[] scratch = new byte[recorders.Length][];
        CapturedFrame?[] prevRaw = new CapturedFrame?[recorders.Length];
        // Per-slot "have I already tried the one-shot stuck-first-frame flush" guard — see
        // RegionRecorder.TryFlushPendingInitialFrame's own doc comment for what this fixes (a
        // monitor whose content never changes after its very first WGC callback would otherwise
        // never contribute a single composited pixel for the whole take, leaving its canvas sub-rect
        // at the zero-initialized black `canvas` started life as). Resynced alongside scratch/prevRaw
        // on a topology change, same carried-over-by-DeviceName rule.
        bool[] initialFlushAttempted = new bool[recorders.Length];
        byte[]? gifScaledScratch = null;
        int framesWritten = 0;
        long? epochTicks = null;
        long nextDueTicks = System.Diagnostics.Stopwatch.GetTimestamp();

        try
        {
            while (true)
            {
                var liveTopology = Volatile.Read(ref _spanTopology);
                if (liveTopology is null)
                {
                    break; // HardStopCaptureIfNeeded already stopped everything and cleared the field
                }
                var liveRecorders = liveTopology.Recorders;
                var liveSlots = liveTopology.Slots;

                if (!ReferenceEquals(liveRecorders, recorders))
                {
                    // Topology changed (RebuildSpanningRecorders published a new pair) — resize the
                    // per-slot scratch/raw-baseline/flush-attempted arrays, carrying over entries for
                    // monitors that are STILL present (matched by DeviceName against the OLD `slots`,
                    // which at this point in the method still refers to the pre-resync geometry) so a
                    // rebuild that only adds/removes one monitor doesn't throw away every other
                    // slot's tone-map scratch/skip-baseline for nothing.
                    byte[]?[] newScratch = new byte[liveSlots.Length][];
                    var newPrevRaw = new CapturedFrame?[liveSlots.Length];
                    var newInitialFlushAttempted = new bool[liveSlots.Length];
                    for (int i = 0; i < liveSlots.Length; i++)
                    {
                        int oldIndex = Array.FindIndex(slots, s => s.Monitor.DeviceName == liveSlots[i].Monitor.DeviceName);
                        if (oldIndex >= 0)
                        {
                            newScratch[i] = scratch[oldIndex];
                            newPrevRaw[i] = prevRaw[oldIndex];
                            prevRaw[oldIndex] = null; // ownership moved — don't let the disposal loop below touch it
                            newInitialFlushAttempted[i] = initialFlushAttempted[oldIndex];
                        }
                        // else: a brand-new slot (monitor just entered the spanning set) — starts at
                        // false, so the loop below gets exactly one shot at its own stuck-first-frame
                        // flush too, same as a slot that has been there since BeginCapture.
                    }
                    foreach (var stale in prevRaw)
                    {
                        stale?.Dispose(); // baseline for a monitor that dropped out of the new set entirely
                    }
                    scratch = newScratch;
                    prevRaw = newPrevRaw;
                    initialFlushAttempted = newInitialFlushAttempted;
                    recorders = liveRecorders;
                }
                slots = liveSlots;

                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (now < nextDueTicks)
                {
                    // Not due yet — keep audio flowing (same reason EncoderLoop's own bounded wait
                    // does: audio must not pile up just because the screen is static) and check again
                    // shortly rather than busy-spinning the encoder thread.
                    DrainAudio(freq, epochTicks);
                    Thread.Sleep(2);
                    continue;
                }
                nextDueTicks = Math.Max(nextDueTicks + minIntervalTicks, now - minIntervalTicks);

                bool anyComposited = false;
                bool allCompleted = true;
                for (int i = 0; i < recorders.Length; i++)
                {
                    var reader = recorders[i].Frames;
                    allCompleted &= reader.Completion.IsCompleted;

                    // Drain to LATEST: everything but the last dequeued frame this tick is stale by
                    // definition once a newer one exists (plan §1's own "drain that monitor's channel
                    // with TryRead in a loop, keeping only the LAST" instruction).
                    CapturedFrame? latest = null;
                    while (reader.TryRead(out var queued))
                    {
                        latest?.Dispose();
                        latest = queued.Frame;
                    }
                    if (latest is null)
                    {
                        // Nothing new from this monitor this tick. If it has NEVER composited
                        // anything yet (still showing zero-initialized black for its whole canvas
                        // sub-rect), try exactly once — ever, per recorder instance — to force its
                        // stuck first frame out; see TryFlushPendingInitialFrame's own doc comment.
                        if (prevRaw[i] is null && !initialFlushAttempted[i])
                        {
                            initialFlushAttempted[i] = true;
                            if (recorders[i].TryFlushPendingInitialFrame())
                            {
                                while (reader.TryRead(out var queued))
                                {
                                    latest?.Dispose();
                                    latest = queued.Frame;
                                }
                            }
                        }
                        if (latest is null)
                        {
                            continue; // still nothing — this monitor's canvas sub-rect stays as-is
                        }
                    }

                    bool keptAsBaseline = false;
                    try
                    {
                        if (prevRaw[i] is { } prev && RawFramesEqual(prev, latest))
                        {
                            continue; // this monitor's own crop is byte-identical to last tick — skip its tone-map (plan §5)
                        }
                        SpanningCanvasCompositor.CompositeSlot(canvas, canvasWidth, canvasHeight, slots[i], latest, ref scratch[i]);
                        anyComposited = true;
                        prevRaw[i]?.Dispose();
                        prevRaw[i] = latest;
                        keptAsBaseline = true;
                    }
                    finally
                    {
                        if (!keptAsBaseline)
                        {
                            latest.Dispose();
                        }
                    }
                }

                if (anyComposited)
                {
                    // Timestamped by the TICK's own schedule, not any one monitor's raw capture time
                    // — the whole point of fixed-cadence compositing is decoupling the composed
                    // frame's timing from N independently-jittering WGC delivery streams (plan §1).
                    long paused = Volatile.Read(ref _pausedTicks);
                    long effectiveTicks = now - paused;
                    epochTicks ??= effectiveTicks;
                    framesWritten++;

                    if (_format == RecordingFormat.Mp4)
                    {
                        long timestamp100ns = (long)((effectiveTicks - epochTicks.Value) / freq * 10_000_000.0);
                        _mp4Encoder!.WriteFrame(new SdrImage(canvasWidth, canvasHeight, canvas), timestamp100ns);
                    }
                    else
                    {
                        SdrImage encodeSource;
                        if (_gifRenderScale < 1.0)
                        {
                            gifScaledScratch ??= new byte[_gifCanvasWidth * 4 * _gifCanvasHeight];
                            BoxDownsampleGifFrame(canvas, canvasWidth, canvasHeight, gifScaledScratch, _gifCanvasWidth, _gifCanvasHeight);
                            encodeSource = new SdrImage(_gifCanvasWidth, _gifCanvasHeight, gifScaledScratch);
                        }
                        else
                        {
                            encodeSource = new SdrImage(canvasWidth, canvasHeight, canvas);
                        }
                        _gifEncoder!.AddFrame(encodeSource, effectiveTicks);
                    }
                }

                DrainAudio(freq, epochTicks);

                if (allCompleted)
                {
                    // Re-check the topology reference before actually ending the take: a rebuild can
                    // publish a NEW pair and then Stop() the OLD `recorders` array entirely WITHIN
                    // this same iteration's window (see this method's own class doc for the exact
                    // race) — if every recorder in that now-stale array had zero frames queued (the
                    // common case for a set that only just got rebuilt), `allCompleted` reads true
                    // for an array that was already superseded, not for a real end of take.
                    // `liveTopology` is what THIS iteration read at its own top, before the drain
                    // above ran — comparing a FRESH read against that same value confirms nothing
                    // published a newer pair while this iteration's drain was in flight. Only break
                    // if they still match; otherwise a rebuild landed mid-iteration — loop again, and
                    // the top-of-loop resync will pick up the new array on the very next pass.
                    if (ReferenceEquals(Volatile.Read(ref _spanTopology), liveTopology))
                    {
                        break; // every currently-tracked recorder is done and nobody swapped the pair under us — real end of take
                    }
                }
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: spanning recording encoder failed mid-session after {framesWritten} frame(s): {ex}");
            // Best-effort: keep whatever encoded cleanly so far rather than losing the whole
            // recording to one bad frame/disk hiccup — same policy EncoderLoop's own catch uses.
            RequestForceStopAndSave();
        }
        finally
        {
            FileLog.Write($"RoeSnip: spanning encoder loop exiting, {framesWritten} frame(s) processed");
            foreach (var f in prevRaw)
            {
                f?.Dispose();
            }
            if (_format == RecordingFormat.Mp4)
            {
                try { DrainAudio(freq, epochTicks); }
                catch (Exception ex) { FileLog.Write($"RoeSnip: final audio drain failed: {ex}"); }
                try { _mp4Encoder?.FinalizeAndClose(); }
                catch (Exception ex) { FileLog.Write($"RoeSnip: MP4 finalize failed: {ex}"); }
            }
            else
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                long pausedTotal = Volatile.Read(ref _pausedTicks);
                if (_paused)
                {
                    pausedTotal += now - Volatile.Read(ref _pauseStartTicks);
                }
                try { _gifEncoder?.FinalizeAndClose(now - pausedTotal); }
                catch (Exception ex) { FileLog.Write($"RoeSnip: GIF finalize failed: {ex}"); }
            }
        }
    }

    /// <summary>UI thread (BeginCapture). Scales <paramref name="fullSize"/> by <paramref name="scale"/>
    /// and floors the result to the nearest even number, minimum 2 — GifEncoder.Create's canvas
    /// dimensions must be even for the same reason RecordingController.RoundDownToEven already
    /// floors the UNSCALED selection (H.264 4:2:0 chroma subsampling on the MP4 path; kept even here
    /// too purely for symmetry with that convention, GIF itself has no such constraint).</summary>
    private static int ScaledCanvasDimension(int fullSize, double scale)
    {
        int scaled = (int)(fullSize * scale);
        return Math.Max(2, scaled - (scaled % 2));
    }

    /// <summary>Encoder thread only. Box-filter downsample of a tone-mapped BGRA8 buffer into a
    /// smaller, caller-owned destination buffer (GIF downscaled render, Compact 0.75 / Minimal 0.5
    /// — see GifEncoderOptions.RenderScale's own doc comment). Each destination pixel averages the
    /// axis-aligned source rect that maps to it under uniform (srcSize/dstSize) scaling — at
    /// Minimal's 0.5 that rect is always a plain 2x2 block; at Compact's 0.75 (non-integer ratio)
    /// the integer-floored boundaries produce a repeating 1,1,2-pixel rect pattern per axis, i.e.
    /// an integer-snapped area average in which every source pixel contributes wholly to exactly
    /// one destination pixel (no fractional edge weights — a deliberate approximation of the exact
    /// fractional-coverage box filter, accepted because the per-pixel weight error is at most one
    /// source pixel's worth and GifSizeBenchmarkTests' scale-aware visual gates measure the whole
    /// 0.75 path at meanErr 1.26 on scroll text, nowhere near a legibility problem, without the
    /// per-pixel weight state an exact filter would need in this alloc-free hot loop). The general
    /// ratio math below works for any RenderScale in the validated 0.25-1.0 range without a
    /// separate code path. Alloc-free: <paramref name="dst"/> is the caller's reused scratch buffer
    /// (see EncoderLoop's own LOH/Gen2 discipline note), never allocated here.</summary>
    private static void BoxDownsampleGifFrame(byte[] src, int srcWidth, int srcHeight, byte[] dst, int dstWidth, int dstHeight)
    {
        for (int dy = 0; dy < dstHeight; dy++)
        {
            int sy0 = dy * srcHeight / dstHeight;
            int sy1 = Math.Min(srcHeight, Math.Max(sy0 + 1, (dy + 1) * srcHeight / dstHeight));
            for (int dx = 0; dx < dstWidth; dx++)
            {
                int sx0 = dx * srcWidth / dstWidth;
                int sx1 = Math.Min(srcWidth, Math.Max(sx0 + 1, (dx + 1) * srcWidth / dstWidth));

                long sumB = 0, sumG = 0, sumR = 0;
                int count = 0;
                for (int sy = sy0; sy < sy1; sy++)
                {
                    int rowOffset = sy * srcWidth * 4;
                    for (int sx = sx0; sx < sx1; sx++)
                    {
                        int so = rowOffset + sx * 4;
                        sumB += src[so];
                        sumG += src[so + 1];
                        sumR += src[so + 2];
                        count++;
                    }
                }

                int doff = (dy * dstWidth + dx) * 4;
                dst[doff] = (byte)(sumB / count);
                dst[doff + 1] = (byte)(sumG / count);
                dst[doff + 2] = (byte)(sumR / count);
                dst[doff + 3] = 255;
            }
        }
    }

    /// <summary>Encoder thread only. True when two raw captured frames carry identical pixel bytes
    /// (row-wise, padding excluded). Dimension/format mismatches count as changed.</summary>
    private static bool RawFramesEqual(CapturedFrame a, CapturedFrame b)
    {
        if (a.Format != b.Format || a.Width != b.Width || a.Height != b.Height)
        {
            return false;
        }
        for (int y = 0; y < a.Height; y++)
        {
            if (!a.Row(y).SequenceEqual(b.Row(y)))
            {
                return false;
            }
        }
        return true;
    }

    // ---------- Automation hooks (App/AutomationServer.cs) — UI thread only, same as every other
    // method above driven by a chrome button click. ----------

    internal RecordingAutomationSnapshot GetAutomationSnapshot()
    {
        // _selectionPx is already virtual-desktop-absolute (multi-monitor recording phase 1,
        // PLAN-MULTIMON-RECORDING.md §4/§6) — a straight passthrough now; this used to fold in
        // _monitor.BoundsPx's own origin here (a single-monitor-relative _selectionPx needed that
        // conversion at the wire boundary). Simpler, not more complex, per the plan's own note.
        return new RecordingAutomationSnapshot(_phase.ToString(), _format, _selectionPx, _chrome!.EstimateText, _chrome!.CurrentSizePreset, _chrome!.CurrentFps);
    }

    /// <summary>AutomationServer's `select` command: applies the rect to RegionOutline through the
    /// exact band-drag code path (RegionChanged -> OnRegionMoved -> chrome UpdateSelection, and (if
    /// a recorder is live) the SAME same-monitor-slide/cross-monitor-handoff logic a real drag gets
    /// — all fire, same as a real drag). No phase gate: RegionOutline shows and accepts a drag in
    /// every phase this session shows it (Setup through Reviewing — see RegionOutline's own ctor
    /// doc), so a real mouse drag already isn't restricted to Setup either (a Capturing/Reviewing
    /// drag just moves the region, per RegionOutline's own size-locked-once-capturing rule); this
    /// automation hook now matches that instead of rejecting it, which is what lets AutomationServer
    /// drive the cross-monitor drag-handoff path headlessly during Capturing (see
    /// RegionOutline.SetSelectionForAutomation's own doc comment for the size-locked-move-only
    /// semantics it applies once a take exists).</summary>
    internal string? SetSelectionForAutomation(RectPhysical virtualDesktopPx)
    {
        _outline?.SetSelectionForAutomation(virtualDesktopPx);
        return null;
    }

    /// <summary>AutomationServer's `preset` command — only valid in Setup, same as a real chip
    /// click (see RecordingChrome.ApplyState's IsEnabled gating on the size row).</summary>
    internal string? SetSizePresetForAutomation(GifSizePreset preset)
    {
        if (_phase != Phase.Setup)
        {
            return $"cannot change the size preset while recording is {_phase}";
        }
        _chrome!.InvokeSizePreset(preset);
        return null;
    }

    /// <summary>AutomationServer's `fps` command — only valid in Setup, same as a real slider drag
    /// (see RecordingChrome.ApplyState's IsEnabled gating on the FPS row). Unlike
    /// <see cref="SetSizePresetForAutomation"/>, the value isn't a fixed enum: AutomationProtocol.
    /// ValidateArgs can only pure-validate up front that it's an integer somewhere in the UNION of
    /// both formats' ranges (5-60, see that method's own doc comment) — so this live half
    /// additionally rejects a value outside THIS session's own format's range, e.g. 55 offered
    /// while recording GIF (whose ceiling is 50), giving the caller a real error instead of
    /// RecordingChrome.InvokeFps's belt-and-braces exception.</summary>
    internal string? SetFpsForAutomation(int fps)
    {
        if (_phase != Phase.Setup)
        {
            return $"cannot change fps while recording is {_phase}";
        }
        int min = _format == RecordingFormat.Gif ? RecordingSizeEstimator.GifMinFps : RecordingSizeEstimator.Mp4MinFps;
        int max = _format == RecordingFormat.Gif ? RecordingSizeEstimator.GifMaxFps : RecordingSizeEstimator.Mp4MaxFps;
        if (fps < min || fps > max)
        {
            return $"fps must be {min}-{max} for {_format}";
        }
        _chrome!.InvokeFps(fps);
        return null;
    }

    /// <summary>AutomationServer's `chrome`/`escape` commands — raises the same button Click a real
    /// mouse click on that chrome button would (see RecordingChrome's Invoke* methods), after
    /// rejecting an action that button isn't valid for right now as an explicit error instead of
    /// the silent no-op a disabled button would give.</summary>
    internal string? InvokeChromeAction(string action)
    {
        switch (action)
        {
            case "start":
                if (_phase != Phase.Setup) return $"cannot start while recording is {_phase}";
                _chrome!.InvokeStartStop();
                return null;
            case "stop":
                if (_phase != Phase.Capturing) return $"cannot stop while recording is {_phase}";
                _chrome!.InvokeStartStop();
                return null;
            case "pause":
                if (_phase != Phase.Capturing || _paused) return "cannot pause: not capturing, or already paused";
                _chrome!.InvokePauseResume();
                return null;
            case "resume":
                if (!_paused) return "cannot resume: not paused";
                _chrome!.InvokePauseResume();
                return null;
            case "save":
                if (_phase != Phase.Reviewing) return $"cannot save while recording is {_phase}";
                _chrome!.InvokeSave();
                return null;
            case "share":
                // Same phase/reentrancy guard RequestShare itself enforces (Reviewing, not already
                // mid hard-stop) - checked here too so a bad automation call gets an explicit error
                // instead of RequestShare's own silent no-op.
                if (_phase != Phase.Reviewing) return $"cannot share while recording is {_phase}";
                if (_saving || _sharing) return "cannot share: a save or share is already in progress";
                _chrome!.InvokeShare();
                return null;
            case "cancel":
                _chrome!.InvokeCancel();
                return null;
            default:
                return $"unknown chrome action \"{action}\"";
        }
    }

    /// <summary>Encoder thread only. Moves every queued audio chunk into the MP4 writer, mapping
    /// each chunk's QPC timestamp onto the video timeline (same epoch: the first dequeued video
    /// frame, itself paused-time-adjusted). Chunks captured before that epoch are dropped — the
    /// track starts with the video. Chunks captured while paused are dropped outright (not written)
    /// so the audio track stays silent-free across the cut rather than baking in dead air.</summary>
    private void DrainAudio(double freq, long? epochTicks)
    {
        var audio = _audio;
        var encoder = _mp4Encoder;
        if (audio is null || encoder is null)
        {
            return;
        }

        while (audio.TryDequeue(out var chunk))
        {
            // Judge by when the chunk was CAPTURED, not by the flag at drain time: at the moment
            // of a Pause or soft Stop there is a small backlog of genuinely-recorded audio still
            // queued (plus the engine's final flush, for a soft stop), and a flag-only check would
            // silently chop that tail off the take. Chunks captured during the pause itself drop.
            if (_paused && chunk.QpcTicks >= Volatile.Read(ref _pauseStartTicks))
            {
                continue; // captured while paused — never written
            }
            long paused = Volatile.Read(ref _pausedTicks);
            long effectiveTicks = chunk.QpcTicks - paused;
            if (epochTicks is null || effectiveTicks < epochTicks.Value)
            {
                continue; // pre-roll audio from before the first video frame
            }
            long timestamp100ns = (long)((effectiveTicks - epochTicks.Value) / freq * 10_000_000.0);
            encoder.WriteAudioSamples(chunk.Pcm, chunk.Pcm.Length, timestamp100ns);
        }
    }
}

file static class ModuleInit
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Init() => AppComposition.StartRecording = RecordingController.StartAsync;
}
