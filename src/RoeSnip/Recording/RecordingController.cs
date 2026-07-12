using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Win32;
using RoeSnip.App;
using RoeSnip.Capture;
using RoeSnip.Imaging;
using RoeSnip.Recording.Gif;

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
    private Mp4Encoder? _mp4Encoder;
    private GifEncoder? _gifEncoder;
    private RecordingChrome? _chrome;
    private RegionOutline? _outline;
    private AudioCaptureEngine? _audio;
    private Thread? _encoderThread;
    private System.Threading.Timer? _elapsedTimer;
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
            _chrome.CancelRequested += CancelAndDiscard;
            _chrome.MicToggled += v => SetAudioToggle(mic: v, systemAudio: null);
            _chrome.SystemAudioToggled += v => SetAudioToggle(mic: null, systemAudio: v);
            _chrome.SizePresetChanged += SetSizePreset;
            _chrome.FpsChanged += SetFps;
            _chrome.Show();

            Console.Error.WriteLine($"RoeSnip: recording setup opened ({_format}, {_selectionPx.Width}x{_selectionPx.Height})");
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
            Console.Error.WriteLine($"RoeSnip: failed to persist recording audio toggle: {ex.Message}");
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
            Console.Error.WriteLine($"RoeSnip: failed to persist recording size preset: {ex.Message}");
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
            Console.Error.WriteLine($"RoeSnip: failed to persist recording fps: {ex.Message}");
        }
    }

    /// <summary>The user moved or resized the recorded region (RegionOutline), OR (via
    /// SetSelectionForAutomation) an automation caller drove the same production path. UI thread.
    /// Re-anchor the HUD to the region's new spot and, if a recorder is live (Capturing OR
    /// Reviewing — a soft-stopped take is a paused take, its recorder stays alive so Resume can
    /// continue it, see <see cref="Resume"/>), keep it following: same-monitor moves slide the
    /// existing recorder's crop origin (lock-free — <see cref="RegionRecorder.SetOrigin"/>'s own
    /// doc); a region that stops being fully contained in its current monitor's bounds triggers a
    /// cross-monitor HANDOFF instead (multi-monitor recording phase 1 — snap to the destination
    /// monitor, not true spanning; see PLAN-MULTIMON-RECORDING.md §3). <paramref name="selectionPx"/>
    /// is virtual-desktop-absolute (§4) — the outline itself is what already computed/clamped it;
    /// this only propagates it to the recorder/chrome.</summary>
    private void OnRegionMoved(RectPhysical selectionPx)
    {
        _selectionPx = selectionPx;
        _chrome?.UpdateSelection(selectionPx);

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
            Console.Error.WriteLine($"RoeSnip: cross-monitor recording handoff to {destMonitor.DeviceName} failed, staying on {_monitor.DeviceName}: {ex.Message}");
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

        Console.Error.WriteLine($"RoeSnip: recording handed off {fromDevice} -> {destMonitor.DeviceName} at ({clamped.Left},{clamped.Top}), size {clamped.Width}x{clamped.Height}");
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
            // Setup-phase resize/move can have pushed the selection onto (or across) a different
            // monitor than the one this session started on — RegionOutline now clamps drags to the
            // UNION of every monitor's bounds, not just one (plan §4). Re-derive which monitor
            // actually owns it and re-pin the fixed tone-map options to THAT monitor before building
            // the recorder, using the exact same "snap to majority overlap, never split" rule the
            // live cross-monitor drag handoff (HandoffToMonitor) uses once already capturing.
            var owningMonitor = MultiMonitorRecording.FindOwningMonitor(_selectionPx, _allMonitors) ?? _monitor;
            if (owningMonitor.DeviceName != _monitor.DeviceName)
            {
                _monitor = owningMonitor;
                _fixedToneMapOpts = ComputeFixedToneMapOpts(_monitor);
            }
            var clampedSelection = MultiMonitorRecording.ClampToMonitor(_selectionPx, _monitor);
            if (clampedSelection != _selectionPx)
            {
                _selectionPx = clampedSelection;
                _outline?.SyncExternalSelection(clampedSelection);
                _chrome?.UpdateSelection(clampedSelection);
            }

            _recorder = new RegionRecorder(_monitor, MultiMonitorRecording.ToMonitorRelative(_selectionPx, _monitor), _targetFps);
            _recorder.Faulted += OnRecorderFaulted;

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
            _recorder.Start();

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
            _encoderThread = new Thread(EncoderLoop)
            {
                IsBackground = true,
                Name = "RoeSnip-Recording-Encoder",
                Priority = ThreadPriority.BelowNormal,
            };
            _encoderThread.Start();

            _phase = Phase.Capturing;
            _chrome!.EnterRecording();
            _outline?.SetInteractionMode(allowResize: false); // size is locked to the encoder now - band moves, not resizes
            Console.Error.WriteLine($"RoeSnip: recording capture started ({_format}, {_selectionPx.Width}x{_selectionPx.Height}, {_targetFps}fps)");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: failed to start recording capture: {ex.Message}");
            _notifier?.ShowError($"Failed to start recording: {ex.Message}");

            try { _elapsedTimer?.Dispose(); } catch { /* best-effort */ }
            _elapsedTimer = null;
            try { _recorder?.Dispose(); } catch { /* best-effort */ }
            try { _audio?.Dispose(); } catch { /* best-effort */ }
            try { _mp4Encoder?.Dispose(); } catch { /* best-effort */ }
            try { _gifEncoder?.Dispose(); } catch { /* best-effort */ }
            try { if (_mp4TempPath is not null && File.Exists(_mp4TempPath)) File.Delete(_mp4TempPath); } catch { /* best-effort */ }
            try { if (_gifTempPath is not null && File.Exists(_gifTempPath)) File.Delete(_gifTempPath); } catch { /* best-effort */ }
            _recorder = null;
            _audio = null;
            _mp4Encoder = null;
            _gifEncoder = null;

            // The specific error was already surfaced above — TeardownSession's own messaging stays
            // silent for this path.
            TeardownSession(finalPath: null, dialogCancelled: false, encoderAbandoned: false);
        }
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
        _recorder!.Paused = true;
        _chrome!.SetPaused(true);
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
        _recorder!.Paused = false;
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
        Console.Error.WriteLine($"RoeSnip: recording capture failed mid-session (non-fatal, stopping with what was captured): {ex.Message}");
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
        if (_recorder is null)
        {
            return true;
        }

        if (_displayChangedHandler is not null)
        {
            SystemEvents.DisplaySettingsChanged -= _displayChangedHandler;
            _displayChangedHandler = null;
        }
        _elapsedTimer?.Dispose();
        _elapsedTimer = null;

        _audio?.Dispose();
        _audio = null;
        _recorder!.Stop(); // stops feeding the queue and completes it

        bool joined = _encoderThread!.Join(TimeSpan.FromSeconds(5));
        if (!joined)
        {
            Console.Error.WriteLine("RoeSnip: recording encoder thread did not finish within 5s; abandoning it without touching its output.");
            return false;
        }

        _mp4Encoder?.Dispose(); // only safe once the encoder thread — the only other owner — has actually finished
        _mp4Encoder = null;
        _gifEncoder?.Dispose(); // same ownership rule for the GIF temp-file stream
        _gifEncoder = null;
        // The recorder's own device/item are never shared with the encoder thread (which only reads
        // from RegionRecorder.Frames), so disposing it is safe regardless.
        _recorder.Dispose();
        _recorder = null;
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
        Console.Error.WriteLine($"RoeSnip: recording capture soft-stopping for review (elapsed={_stopwatch?.Elapsed})");

        if (!_paused)
        {
            _pauseStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            _paused = true;
            _recorder!.Paused = true;
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
        _chrome!.EnterReviewing();
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
        if (_phase != Phase.Reviewing || _saving)
        {
            // _saving: the save dialog below runs a nested message pump on this thread, so a
            // PrtScr press while it is open dispatches straight back into this method with _phase
            // still Reviewing — without this guard the reentrant call could save/re-arm first and
            // the suspended outer call would then tear the fresh session down when it resumed.
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

    /// <summary>The single terminal path every end-of-recording route funnels through (Save,
    /// Cancel, an unrecoverable failure). Idempotent via <see cref="_ended"/>.</summary>
    private void TeardownSession(string? finalPath, bool dialogCancelled, bool encoderAbandoned)
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0)
        {
            return;
        }
        Console.Error.WriteLine($"RoeSnip: recording session ending (saved={finalPath is not null})");

        try { _outline?.CloseOutline(); }
        catch (Exception ex) { Console.Error.WriteLine($"RoeSnip: closing the recording outline failed (non-fatal): {ex.Message}"); }
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
                Console.Error.WriteLine($"RoeSnip: failed to delete abandoned recording temp file: {ex.Message}");
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
            Console.Error.WriteLine($"RoeSnip: recording encoder failed mid-session after {framesWritten} frame(s): {ex}");
            // Best-effort: keep whatever encoded cleanly so far rather than losing the whole
            // recording to one bad frame/disk hiccup.
            RequestForceStopAndSave();
        }
        finally
        {
            Console.Error.WriteLine($"RoeSnip: encoder loop exiting, {framesWritten} frame(s) processed");
            prevGifRaw?.Dispose();
            prevMp4Raw?.Dispose();
            if (_format == RecordingFormat.Mp4)
            {
                // Whatever audio was still queued when the video channel completed belongs in the
                // file — Stop() halts the audio engine BEFORE completing the channel, so this final
                // drain is bounded.
                try { DrainAudio(freq, epochTicks); }
                catch (Exception ex) { Console.Error.WriteLine($"RoeSnip: final audio drain failed: {ex}"); }
                try { _mp4Encoder?.FinalizeAndClose(); }
                catch (Exception ex) { Console.Error.WriteLine($"RoeSnip: MP4 finalize failed: {ex}"); }
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
                catch (Exception ex) { Console.Error.WriteLine($"RoeSnip: GIF finalize failed: {ex}"); }
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
