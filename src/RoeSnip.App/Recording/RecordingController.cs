using System;
using System.IO;
using System.Threading;
using RoeSnip.Core.Capture;
using RoeSnip.Core.Diagnostics;
using RoeSnip.Core.Recording;
using RoeSnip.Core.Recording.Gif;
using RoeSnip.Core.Settings;

namespace RoeSnip.App.Recording;

/// <summary>Owns the single active recording (if any) for the whole process - mirrors
/// RoeSnip.App's own OverlayController.IsSessionActive/s_activeSession shape, and the WPF app's
/// RoeSnip.Recording.RecordingController static class (same name, same role). Single-monitor takes
/// only in this item - spanning (multi-monitor) recording is a separate, not-yet-ported track, same
/// as the WPF app's own history (see docs/PARITY.md item 20's own note).</summary>
public static class RecordingController
{
    private static RecordingSession? s_active; // set/cleared on the UI thread only

    public static bool IsActive => s_active is not null;

    public static RecordingSession? Active => s_active;

    /// <summary>Opens a new Setup-phase session for <paramref name="selectionPx"/> (monitor-relative
    /// physical pixels) and marks it active. The selection is rounded down to even dimensions here
    /// (H.264 4:2:0 chroma subsampling requires it) - upstream of both encoders and RegionRecorder's
    /// own crop box, so nobody downstream ever sees an odd dimension. Throws if a recording is
    /// already active (mirrors the WPF app's own StartAsync contract) - the caller is expected to
    /// have already prevented that via whatever gate (CaptureGate on Windows, item 21's job) guards
    /// concurrent capture/recording.</summary>
    public static RecordingSession StartNew(
        MonitorInfo monitor, RectPhysical selectionPx, RecordingFormat format,
        RoeSnipSettings settings, ITrayNotifier? notifier = null)
    {
        if (s_active is not null)
        {
            throw new InvalidOperationException("A recording is already active.");
        }

        var evenSelection = RoundDownToEven(selectionPx);
        var session = new RecordingSession(monitor, evenSelection, format, settings, notifier);
        session.Start(); // Setup-phase bookkeeping only - throws on failure, nothing to clean up yet
        s_active = session;
        return session;
    }

    internal static void OnSessionEnded(RecordingSession session)
    {
        if (ReferenceEquals(s_active, session))
        {
            s_active = null;
        }
    }

    /// <summary>Public (this codebase's own "testable slice becomes a public member instead of an
    /// InternalsVisibleTo edit" convention) so it is directly unit-testable from RoeSnip.App.Tests.</summary>
    public static RectPhysical RoundDownToEven(RectPhysical selectionPx)
    {
        var n = selectionPx.Normalized();
        int width = Math.Max(2, n.Width - (n.Width % 2));
        int height = Math.Max(2, n.Height - (n.Height % 2));
        return RectPhysical.FromSize(n.Left, n.Top, width, height);
    }
}

/// <summary>Result of <see cref="RecordingSession.BeginShareHandoff"/> - the finished take's temp
/// file plus everything the orchestration layer's own upload needs (Sharing/ShareManager.UploadAsync
/// takes a stream, a file name and a content type, none of which this class needs to know about).</summary>
public sealed record RecordingShareHandoff(string TempPath, RecordingFormat Format, string FileName, string ContentType, MonitorInfo Monitor);

/// <summary>One in-progress recording flow - a three-phase state machine mirroring the WPF app's own
/// RecordingSession (Recording/RecordingController.cs):
///   Setup     - nothing is being captured yet. BeginCapture() (chrome's Start button, item 21) moves
///               to Capturing.
///   Capturing - <see cref="RegionRecorder"/> (capture) plus the chosen <see cref="IVideoEncoder"/>
///               (GIF everywhere, MP4 where RoeSnip.Core.Recording.RecordingCapabilitiesRegistry
///               reports support) are live, drained by a dedicated encoder thread. StopCaptureToReview
///               or Restart end this phase.
///   Reviewing - the take is fully encoded to its temp file. Save() finalizes it (File.Move, honoring
///               the ROESNIP_RECORD_AUTOSAVE test hook - item 21 wires a real save-file dialog for the
///               non-autosave path); CancelAndDiscard() discards it and closes the whole session.
/// No chrome/outline UI exists in this item - <see cref="RecordingController.StartNew"/> plus this
/// class's own public methods ARE the "temporary internal start/stop hook for testing" item 20's
/// brief calls for; item 21 wires real UI buttons onto these same methods with no shape change.</summary>
public sealed class RecordingSession
{
    private enum Phase { Setup, Capturing, Reviewing }

    /// <summary>Public mirror of the private <see cref="Phase"/> enum (item 21) - the chrome/outline
    /// orchestration layer (RoeSnip.App.Recording.RecordingOrchestrator) lives outside this class and
    /// needs a phase it can react to without this class exposing its own private enum.</summary>
    public enum RecordingSessionPhase { Setup, Capturing, Reviewing }

    /// <summary>Fires every time <see cref="_phase"/> actually changes (never on a stray re-entry
    /// into the same phase) - item 21's RecordingOrchestrator drives RecordingChrome's
    /// EnterSetup/EnterRecording/EnterReviewing off this instead of polling, so it can never miss a
    /// transition that originates from inside this class itself (e.g. OnRecorderFaulted's own
    /// StopCaptureToReview call, not just an explicit chrome-button-driven one).</summary>
    public event Action<RecordingSessionPhase>? PhaseChanged;

    /// <summary>Fires from Pause()/Resume() after <see cref="_paused"/> is updated.</summary>
    public event Action<bool>? PausedChanged;

    /// <summary>Fires exactly once, from <see cref="TeardownSession"/> - the orchestrator's own
    /// signal to close the chrome/outline windows and release CaptureGate (item 21;
    /// AppComposition.RunCaptureFlowAsync hands the gate to a recording instead of exiting it itself
    /// once <see cref="Program.OverlayResult.RecordingRequested"/> is set - see that call site's own
    /// comment).</summary>
    public event Action? Ended;

    private readonly MonitorInfo _monitor;
    private RectPhysical _selectionPx; // monitor-relative physical pixels, fixed size, position can slide (SetOrigin)
    private readonly RecordingFormat _format;
    private readonly RoeSnipSettings _settings;
    private readonly ITrayNotifier? _notifier;

    private RegionRecorder? _recorder;
    private IVideoEncoder? _encoder;
    private IAudioCaptureDevice? _audio;
    private Thread? _encoderThread;

    private RoeSnip.Core.Color.ToneMapOptions _fixedToneMapOpts;
    private int _targetFps;
    private string? _tempPath;

    // GIF downscaled render (Compact/Minimal tiers - see GifEncoderOptions.RenderScale's own doc
    // comment): the SCALED canvas dimensions the encoder was actually opened with, and the scale
    // that produced them. 1.0/selection-sized for every other tier and for MP4.
    private int _gifCanvasWidth;
    private int _gifCanvasHeight;
    private double _gifRenderScale = 1.0;

    private int _ended; // Interlocked guard - TeardownSession must run its body exactly once
    private Phase _phase = Phase.Setup;
    private bool _mic;
    private bool _systemAudio;
    private GifSizePreset _gifSizePreset; // GIF takes; read fresh into the encoder at the NEXT BeginCapture after a Setup-phase change
    private GifSizePreset _mp4SizePreset; // MP4 takes; own field, own settings key
    private RoeSnipSettings _liveSettings = null!; // set in Start(); tracks audio-toggle/preset edits for persistence

    // Pause/resume of a live take: a SINGLE accumulator drives both the excluded-time media clock
    // (EncoderLoop/DrainAudio) and any pause-aware elapsed readout a future chrome might show.
    // _paused/_pausedTicks are read from the encoder thread (Volatile.Read for the latter) and
    // written only from the UI thread (Pause/Resume) - never written from the encoder thread, so no
    // lock is needed for the read side. Ported verbatim from the WPF app's RecordingController
    // (~1344): frames captured DURING a pause never reach RegionRecorder's own queue at all (dropped
    // pre-readback, see IRegionCaptureSource.Paused's own doc comment), so the only thing left to
    // correct for at encode time is the GAP the pause left behind in the raw capture timestamps -
    // the review span (Reviewing phase, soft-stopped but still Resumable) accumulates into this same
    // clock exactly like an ordinary Pause does.
    private volatile bool _paused;
    private long _pausedTicks;      // total paused Stopwatch ticks accumulated across all pauses this take
    private long _pauseStartTicks;  // Stopwatch.GetTimestamp() when the current pause began (valid only while _paused)
    private bool _audioTrackEnabled; // this take's MP4 has an AAC track (fixed at BeginCapture)
    private bool _saving; // reentrancy guard against a double Save()/CancelAndDiscard() race
    private long _captureStartTicks; // Stopwatch.GetTimestamp() at the first BeginCapture of this take - RecordingOrchestrator's elapsed-time HUD reads Elapsed off this

    public RecordingSession(
        MonitorInfo monitor, RectPhysical selectionPx, RecordingFormat format,
        RoeSnipSettings settings, ITrayNotifier? notifier)
    {
        _monitor = monitor;
        _selectionPx = selectionPx;
        _format = format;
        _settings = settings;
        _notifier = notifier;
    }

    public bool IsSetup => _phase == Phase.Setup;
    public bool IsCapturing => _phase == Phase.Capturing;
    public bool IsReviewing => _phase == Phase.Reviewing;
    public bool IsPaused => _paused;

    /// <summary>Exposed for item 21's RecordingChrome/RegionOutline construction and repositioning -
    /// this class itself never reads these back from the orchestration layer.</summary>
    public MonitorInfo Monitor => _monitor;
    public RectPhysical SelectionPx => _selectionPx;
    public RecordingFormat Format => _format;

    /// <summary>Wall-clock time actually recorded so far (pause/review spans excluded, same
    /// accumulator EncoderLoop's own timestamps use) - RecordingOrchestrator's HUD timer reads this
    /// on a UI-thread poll rather than this class raising a tick event itself, mirroring the WPF
    /// reference's own System.Threading.Timer + RecordingChrome.SetElapsed poll shape.</summary>
    public TimeSpan Elapsed
    {
        get
        {
            if (_phase == Phase.Setup)
            {
                return TimeSpan.Zero;
            }
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            long paused = Volatile.Read(ref _pausedTicks) + (_paused ? now - _pauseStartTicks : 0);
            long elapsedTicks = now - _captureStartTicks - paused;
            return TimeSpan.FromSeconds(Math.Max(0, elapsedTicks) / (double)System.Diagnostics.Stopwatch.Frequency);
        }
    }

    private void SetPhase(Phase phase)
    {
        if (_phase == phase)
        {
            return;
        }
        _phase = phase;
        PhaseChanged?.Invoke(phase switch
        {
            Phase.Capturing => RecordingSessionPhase.Capturing,
            Phase.Reviewing => RecordingSessionPhase.Reviewing,
            _ => RecordingSessionPhase.Setup,
        });
    }

    /// <summary>UI thread only. Opens the Setup phase: seeds live audio-toggle/preset state and
    /// computes the fixed tone-map options (used by whichever take eventually gets captured). No
    /// capture pipeline exists yet - that is <see cref="BeginCapture"/>'s job.</summary>
    public void Start()
    {
        _liveSettings = _settings;
        _mic = _settings.RecordMicrophone;
        _systemAudio = _settings.RecordSystemAudio;
        _gifSizePreset = GifSizePresets.Parse(_settings.GifSizePreset);
        _mp4SizePreset = GifSizePresets.Parse(_settings.Mp4SizePreset);

        int requestedFps = _format == RecordingFormat.Gif ? _settings.GifFps : _settings.Mp4Fps;
        _targetFps = _format == RecordingFormat.Gif
            ? RecordingSizeEstimator.ClampFps(requestedFps, RecordingSizeEstimator.GifMinFps, RecordingSizeEstimator.GifMaxFps)
            : RecordingSizeEstimator.ClampFps(requestedFps, RecordingSizeEstimator.Mp4MinFps, RecordingSizeEstimator.Mp4MaxFps);

        // Fixed tone-map options, computed ONCE here from monitor metadata - never re-derived per
        // frame (a per-frame auto-derived peak would visibly pump exposure on fluctuating HDR
        // content, an auto-exposure-style flicker).
        _fixedToneMapOpts = ComputeFixedToneMapOpts(_monitor);

        FileLog.Write($"RoeSnip: recording setup opened ({_format}, {_selectionPx.Width}x{_selectionPx.Height})");
    }

    private RoeSnip.Core.Color.ToneMapOptions ComputeFixedToneMapOpts(MonitorInfo monitor)
    {
        double peak = _settings.ToneMapPeakOverride
            ?? Math.Clamp(monitor.MaxLuminanceNits / monitor.SdrWhiteNits, 2.0, double.MaxValue);
        double knee = _settings.ToneMapKneeOverride ?? 0.90;
        return new RoeSnip.Core.Color.ToneMapOptions(Knee: knee, PeakOverride: peak);
    }

    /// <summary>Setup-panel audio toggles changed. Persists immediately via SettingsStore
    /// (best-effort; a disk hiccup here must not interrupt the recording).</summary>
    public void SetAudioToggle(bool? mic, bool? systemAudio)
    {
        if (mic is { } m) _mic = m;
        if (systemAudio is { } s) _systemAudio = s;

        _liveSettings = _liveSettings with { RecordMicrophone = _mic, RecordSystemAudio = _systemAudio };
        TrySavePersistedSettings();
    }

    /// <summary>Setup-panel size row changed - persists to whichever settings key matches THIS
    /// session's own format (GifSizePreset for a GIF take, Mp4SizePreset for an MP4 take - the two
    /// stay independent) AND updates the matching field so it's exactly what
    /// <see cref="BeginCapture"/> reads from at the NEXT Start.</summary>
    public void SetSizePreset(GifSizePreset preset)
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
        TrySavePersistedSettings();
    }

    /// <summary>Setup-panel FPS row changed - mirrors <see cref="SetSizePreset"/> exactly: persists
    /// to whichever settings key matches THIS session's own format (GifFps/Mp4Fps).</summary>
    public void SetFps(int fps)
    {
        _targetFps = fps;
        _liveSettings = _format == RecordingFormat.Gif
            ? _liveSettings with { GifFps = fps }
            : _liveSettings with { Mp4Fps = fps };
        TrySavePersistedSettings();
    }

    private void TrySavePersistedSettings()
    {
        try
        {
            SettingsStore.Save(_liveSettings);
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: failed to persist recording setting: {ex.Message}");
        }
    }

    /// <summary>UI thread only (chrome's Start button, or the smoke-test hook). Builds the actual
    /// capture + encoder pipeline and starts it. On failure, tears the whole session down - nothing
    /// was capturing yet, so there is nothing to save.</summary>
    public void BeginCapture()
    {
        if (_phase != Phase.Setup)
        {
            return; // stray double-invoke
        }

        try
        {
            _recorder = new RegionRecorder(_monitor, _selectionPx, _targetFps);
            _recorder.Faulted += OnRecorderFaulted;

            if (_format == RecordingFormat.Mp4)
            {
                // Audio first: the encoder only gets an AAC track if a capture source genuinely came
                // up, so a device failure can never leave a sample-less audio stream for FinishAsync
                // to choke on. Engine failure is non-fatal (video-only recording).
                if (_mic || _systemAudio)
                {
                    _audio = AudioCaptureDeviceRegistry.TryStart(_mic, _systemAudio);
                    if (_audio is null)
                    {
                        _notifier?.ShowError("Audio capture unavailable; recording video only.");
                    }
                }
                _tempPath = Path.Combine(Path.GetTempPath(), $"roesnip_rec_{Guid.NewGuid():N}.mp4");
                _encoder = Mp4VideoEncoderRegistry.Create(
                    _tempPath, _selectionPx.Width, _selectionPx.Height, _targetFps,
                    withAudio: _audio is not null, preset: _mp4SizePreset);
                _audioTrackEnabled = _encoder.HasAudio;
            }
            else
            {
                _tempPath = Path.Combine(Path.GetTempPath(), $"roesnip_rec_{Guid.NewGuid():N}.gif");
                var gifOptions = GifSizePresets.ForPreset(_gifSizePreset);
                _gifRenderScale = gifOptions.RenderScale;
                _gifCanvasWidth = _gifRenderScale < 1.0
                    ? GifRegionDownsample.ScaledCanvasDimension(_selectionPx.Width, _gifRenderScale)
                    : _selectionPx.Width;
                _gifCanvasHeight = _gifRenderScale < 1.0
                    ? GifRegionDownsample.ScaledCanvasDimension(_selectionPx.Height, _gifRenderScale)
                    : _selectionPx.Height;
                _encoder = GifVideoEncoder.Create(
                    _tempPath, _gifCanvasWidth, _gifCanvasHeight, _targetFps, withAudio: false, preset: _gifSizePreset);
            }

            // Started last (after the encoder is ready to receive frames) so no captured frame is
            // ever silently dropped for lack of a sink.
            _recorder.Start();

            _paused = false;
            _pausedTicks = 0;

            // BelowNormal, not Normal: RegionRecorder's own queue is bounded DropOldest, so falling
            // behind under contention sheds load gracefully - a dropped stale frame, not a stall.
            // There is no such graceful degradation for whatever the user is doing in the foreground
            // while a recording runs; giving this thread lower scheduling priority means the OS
            // favors those over the encoder on a contended CPU instead of the other way around.
            _encoderThread = new Thread(EncoderLoop)
            {
                IsBackground = true,
                Name = "RoeSnip-Recording-Encoder",
                Priority = ThreadPriority.BelowNormal,
            };
            _encoderThread.Start();

            _captureStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            SetPhase(Phase.Capturing);
            FileLog.Write($"RoeSnip: recording capture started ({_format}, {_selectionPx.Width}x{_selectionPx.Height}, {_targetFps}fps)");
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: failed to start recording capture: {ex.Message}");
            _notifier?.ShowError($"Failed to start recording: {ex.Message}");

            try { _recorder?.Dispose(); } catch { /* best-effort */ }
            try { _audio?.Dispose(); } catch { /* best-effort */ }
            try { _encoder?.Dispose(); } catch { /* best-effort */ }
            try { if (_tempPath is not null && File.Exists(_tempPath)) File.Delete(_tempPath); } catch { /* best-effort */ }
            _recorder = null;
            _audio = null;
            _encoder = null;

            TeardownSession(finalPath: null, encoderAbandoned: false);
        }
    }

    /// <summary>The user moved the recorded region - only meaningful while <see cref="_recorder"/>
    /// is alive (Capturing or Reviewing; a soft-stopped take is a paused take, its recorder stays
    /// alive so Resume can continue it). Size never changes mid-take.</summary>
    public void UpdateSelection(RectPhysical newSelectionPx)
    {
        _selectionPx = RectPhysical.FromSize(newSelectionPx.Left, newSelectionPx.Top, _selectionPx.Width, _selectionPx.Height);
        _recorder?.SetOrigin(_selectionPx.Left, _selectionPx.Top);
    }

    public void Pause()
    {
        if (_phase != Phase.Capturing || _paused)
        {
            return;
        }
        _pauseStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        _paused = true;
        if (_recorder is not null) _recorder.Paused = true;
        PausedChanged?.Invoke(true);
    }

    /// <summary>Folds the just-ended pause into the running total BEFORE flipping _paused/
    /// _recorder.Paused back off, so the very next frame the encoder thread sees already has the
    /// enlarged _pausedTicks available. Also reachable from Reviewing (a soft-stopped take is just a
    /// paused take with its recorder/encoder still alive) - resuming there steps back into
    /// Capturing and the same take keeps recording, with the whole review span excluded from the
    /// media timeline like any other pause.</summary>
    public void Resume()
    {
        if (!_paused || (_phase != Phase.Capturing && _phase != Phase.Reviewing))
        {
            return;
        }
        _pausedTicks += System.Diagnostics.Stopwatch.GetTimestamp() - _pauseStartTicks;
        _paused = false;
        if (_recorder is not null) _recorder.Paused = false;
        PausedChanged?.Invoke(false);

        if (_phase == Phase.Reviewing)
        {
            if (_audioTrackEnabled)
            {
                _audio = AudioCaptureDeviceRegistry.TryStart(_mic, _systemAudio); // replaces the disposed engine
                if (_audio is null)
                {
                    _notifier?.ShowError("Audio capture unavailable; resuming video only.");
                }
            }
            SetPhase(Phase.Capturing);
        }
    }

    private void OnRecorderFaulted(Exception ex)
    {
        FileLog.Write($"RoeSnip: recording capture failed mid-session (non-fatal, stopping with what was captured): {ex.Message}");
        if (_phase == Phase.Capturing)
        {
            StopCaptureToReview();
        }
    }

    /// <summary>Ends the active capture pass if one is running: stops the recorder and joins the
    /// encoder thread. Audio shuts down fully before the video channel completes: the encoder
    /// thread's terminal DrainAudio runs almost immediately after Stop(), so a fire-and-forget audio
    /// stop would race the capture thread's final flush. Bounded join: a wedged encoder must never
    /// hang the caller forever - if it doesn't finish in time this deliberately leaks the temp file/
    /// writer rather than race it (the abandoned thread may still be mid-write). Returns false only
    /// when the encoder thread had to be abandoned.</summary>
    private bool HardStopCaptureIfNeeded()
    {
        if (_recorder is null)
        {
            return true; // nothing capturing
        }

        _audio?.Dispose();
        _audio = null;

        _recorder.Stop();

        bool joined = _encoderThread!.Join(TimeSpan.FromSeconds(5));
        if (!joined)
        {
            FileLog.Write("RoeSnip: recording encoder thread did not finish within 5s; abandoning it without touching its output.");
            return false;
        }

        _encoder?.Dispose();
        _encoder = null;
        _recorder.Dispose();
        _recorder = null;
        return true;
    }

    /// <summary>Chrome's Stop button (or the smoke-test hook). SOFT stop: pauses the pipeline
    /// (identical bookkeeping to Pause - frames drop pre-readback, the review span accumulates as
    /// paused time) and moves to Reviewing with the recorder and encoder still alive, so Resume can
    /// step back into Capturing with a seamless cut. The hard stop (encoder join + finalize) happens
    /// on the way out of Reviewing - Save/CancelAndDiscard each call
    /// <see cref="HardStopCaptureIfNeeded"/> themselves.</summary>
    public void StopCaptureToReview()
    {
        if (_phase != Phase.Capturing)
        {
            return;
        }
        FileLog.Write("RoeSnip: recording capture soft-stopping for review");

        if (!_paused)
        {
            _pauseStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            _paused = true;
            if (_recorder is not null) _recorder.Paused = true;
        }

        // Release the microphone/loopback for the review span (a review can last minutes and must
        // not keep the OS "microphone in use" indicator lit). The reference is deliberately KEPT
        // alive on the object graph via _audioTrackEnabled - Dispose's final flush enqueues the last
        // pre-stop chunks, and the encoder thread's periodic DrainAudio still drains that queue.
        // Resume re-creates the engine; the hard stop disposes again (idempotent).
        _audio?.Dispose();

        SetPhase(Phase.Reviewing);
    }

    /// <summary>Chrome's Save button (or the smoke-test hook) - only valid once a take has actually
    /// been stopped into review. Hard-stops the still-alive pipeline, finalizes/moves the temp file
    /// to a real path, then - on a successful save - returns to Setup with the same region so
    /// another take can be recorded immediately (<paramref name="rearmAfterSave"/> = true, the
    /// default); every other outcome tears the session down. <paramref name="explicitTargetPath"/>
    /// lets a caller with a real save-file dialog (item 21) supply the chosen path directly; with it
    /// null, this falls back to the ROESNIP_RECORD_AUTOSAVE test hook (see
    /// <see cref="SaveOutput"/>'s own doc comment) - this port has no native save dialog of its own
    /// yet, so a caller with neither returns null (a documented "not saved" result, not an
    /// exception).</summary>
    public string? Save(string? explicitTargetPath = null, bool rearmAfterSave = true)
    {
        if (_phase != Phase.Reviewing || _saving)
        {
            return null; // same reentrancy reasoning as the WPF app's own SaveAndFinish guard
        }
        _saving = true;

        bool joined = HardStopCaptureIfNeeded();
        if (!joined)
        {
            _saving = false;
            _notifier?.ShowError("Recording could not be saved: the encoder did not stop in time.");
            TeardownSession(finalPath: null, encoderAbandoned: true);
            return null;
        }

        string? finalPath = null;
        try
        {
            finalPath = SaveOutput(explicitTargetPath);
        }
        catch (Exception ex)
        {
            _notifier?.ShowError($"Failed to save recording: {ex.Message}");
            finalPath = null;
        }
        finally
        {
            if (finalPath is null)
            {
                CleanupTempFile();
            }
            _saving = false;
        }

        if (finalPath is not null && rearmAfterSave)
        {
            _notifier?.ShowSavedBalloon(finalPath);
            RearmForAnotherTake();
            return finalPath;
        }

        TeardownSession(finalPath, encoderAbandoned: false);
        return finalPath;
    }

    /// <summary>Test hook: when <paramref name="explicitTargetPath"/> is null and
    /// ROESNIP_RECORD_AUTOSAVE is set, saves straight there instead of needing a native save dialog -
    /// this is how the automated smoke/verify harness (and item 21's own automation commands) drive
    /// Record without UIA against a native Win32 file-picker. Returns null when neither an explicit
    /// path nor the env var is available - this port has no save-file dialog of its own (item 21).</summary>
    private string? SaveOutput(string? explicitTargetPath)
    {
        string finalPath;
        if (explicitTargetPath is not null)
        {
            finalPath = explicitTargetPath;
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath) is { Length: > 0 } dir ? dir : ".");
        }
        else
        {
            string ext = _format == RecordingFormat.Mp4 ? ".mp4" : ".gif";
            string fileName = $"roesnip_{DateTime.Now:yyyyMMdd_HHmmss}{ext}";
            string? autosaveDir = Environment.GetEnvironmentVariable("ROESNIP_RECORD_AUTOSAVE");
            if (string.IsNullOrEmpty(autosaveDir))
            {
                FileLog.Write(
                    "RoeSnip: recording save requested with no explicit path and no ROESNIP_RECORD_AUTOSAVE set - this port has no save-file dialog yet (item 21).");
                return null;
            }
            Directory.CreateDirectory(autosaveDir);
            finalPath = Path.Combine(autosaveDir, fileName);
        }

        File.Move(_tempPath!, finalPath, overwrite: true);
        return finalPath;
    }

    /// <summary>After a successful save: back to Setup with the SAME selected region instead of
    /// tearing down, so another take can be recorded immediately.</summary>
    private void RearmForAnotherTake()
    {
        _tempPath = null;
        SetPhase(Phase.Setup);
    }

    public void CancelAndDiscard()
    {
        bool joined = HardStopCaptureIfNeeded();
        if (joined)
        {
            CleanupTempFile();
        }
        TeardownSession(finalPath: null, encoderAbandoned: !joined);
    }

    /// <summary>Chrome's confirmed Restart (item 21 - the inline "discard and start over?" prompt
    /// itself lives in RecordingChrome). Discards whatever the current take is - mid-capture or
    /// already stopped and under review - and re-arms for a fresh <see cref="BeginCapture"/> with a
    /// new temp file, WITHOUT tearing the session down (same region, same session identity - mirrors
    /// the WPF reference's RestartTake). A hard-stop failure (wedged encoder) is handled exactly like
    /// <see cref="Save"/>'s own failure branch: error balloon, tear the whole session down.</summary>
    public void Restart()
    {
        if (_phase == Phase.Setup)
        {
            return; // nothing captured yet - Restart is a no-op until something has been recorded
        }

        bool joined = HardStopCaptureIfNeeded();
        if (!joined)
        {
            _notifier?.ShowError("Could not restart: the recording did not stop in time.");
            TeardownSession(finalPath: null, encoderAbandoned: true);
            return;
        }

        CleanupTempFile();
        _tempPath = null;
        SetPhase(Phase.Setup);
    }

    /// <summary>Reviewing-state Share handoff (item 21, RequestShare contract ported verbatim from
    /// the WPF reference's Recording/RecordingController.cs, commit 422e87a). The CALLER (item 21's
    /// RecordingOrchestrator, which owns Sharing/ShareManager/ITrayNotifier/SettingsStore - this
    /// class has none of those) is responsible for resolving a share provider from a FRESH settings
    /// read and rejecting the whole request BEFORE ever calling this method: this method's own job
    /// starts only once a provider is already known to resolve, exactly like RequestShare's WPF
    /// original hard-stops only after that same check. Hard-stops the still-alive pipeline (same
    /// irreversible join <see cref="Save"/> uses) and, on success, rearms for another take (mirrors
    /// Save's own successful-save branch) - the caller then uploads the returned temp file path
    /// completely independently of this session (which may already be recording a brand-new take by
    /// the time the upload finishes). Returns null (with the session already torn down and the error
    /// balloon already shown) only when the encoder failed to stop in time; the reentrancy guard
    /// mirrors <see cref="Save"/>'s own <see cref="_saving"/> check (this port's Save has no modal
    /// save-dialog nested pump, unlike the WPF reference, so one shared flag is enough - no separate
    /// _sharing flag is needed here).</summary>
    public RecordingShareHandoff? BeginShareHandoff()
    {
        if (_phase != Phase.Reviewing || _saving)
        {
            return null;
        }
        _saving = true;

        bool joined = HardStopCaptureIfNeeded();
        if (!joined)
        {
            _saving = false;
            _notifier?.ShowError("Recording could not be shared: the encoder did not stop in time.");
            TeardownSession(finalPath: null, encoderAbandoned: true);
            return null;
        }

        string tempPath = _tempPath!;
        string ext = _format == RecordingFormat.Mp4 ? ".mp4" : ".gif";
        string contentType = _format == RecordingFormat.Mp4 ? "video/mp4" : "image/gif";
        string fileName = $"roesnip_{DateTime.Now:yyyyMMdd_HHmmss}{ext}";

        _saving = false;
        RearmForAnotherTake(); // chrome is back in Setup from here - mirrors RequestShare's own doc comment

        // _monitor is readonly on this port (no cross-monitor handoff mid-take), so unlike the WPF
        // app's RequestShare this needs no local captured before rearming.
        return new RecordingShareHandoff(tempPath, _format, fileName, contentType, _monitor);
    }

    private void CleanupTempFile()
    {
        if (_tempPath is null)
        {
            return;
        }
        try
        {
            if (File.Exists(_tempPath)) File.Delete(_tempPath);
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: failed to clean up recording temp file: {ex.Message}");
        }
    }

    /// <summary>The single terminal path every end-of-recording route funnels through. Idempotent
    /// via <see cref="_ended"/>.</summary>
    private void TeardownSession(string? finalPath, bool encoderAbandoned)
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0)
        {
            return;
        }
        FileLog.Write($"RoeSnip: recording session ending (saved={finalPath is not null})");

        if (finalPath is not null)
        {
            _notifier?.ShowSavedBalloon(finalPath);
        }
        else if (encoderAbandoned)
        {
            _notifier?.ShowError("Recording could not be saved: the encoder did not stop in time.");
        }

        RecordingController.OnSessionEnded(this);
        Ended?.Invoke();
    }

    // ---------- Encoder thread ----------

    private void EncoderLoop()
    {
        double freq = System.Diagnostics.Stopwatch.Frequency;
        var reader = _recorder!.Frames;
        int framesWritten = 0;
        // Epoch is the FIRST frame actually dequeued here, not an independent Stopwatch sample taken
        // on this thread's own first line - RegionRecorder.Start() (and thus frame production)
        // begins before this thread is even created/scheduled/JITed, so a timestamp sampled here
        // could land AFTER one or more frames already sitting in the queue, producing a negative-
        // then-clamped-to-0 timestamp. Deriving the epoch from the first real frame makes timestamp
        // 0 always correspond to an actual frame and every later one strictly larger.
        long? epochTicks = null;
        // The raw pixels of the last frame actually handed to the encoder - comparing raw bytes here
        // skips a frame whose recorded crop is byte-identical to the previous one (WGC's dirty
        // tracking is monitor-wide, so a change anywhere on the monitor can deliver a frame even when
        // the recorded crop itself is untouched) before paying the much more expensive tone-map.
        CapturedFrame? prevRaw = null;
        // GIF only: two persistent exactly-canvas-sized tone-map targets so the recording-cadence
        // path never allocates a fresh (LOH-sized) output array per frame (LOH-avoidance buffer
        // reuse - item 20's own required item, via SdrImage.FromCapturedFrame's reuseOutput param).
        byte[]? gifTonemapA = null, gifTonemapB = null;
        bool gifUseA = true;
        // GIF downscaled render (Compact/Minimal tiers): a THIRD persistent exactly-scaled-canvas
        // buffer, allocated once on first use.
        byte[]? gifScaledScratch = null;

        try
        {
            var waitTask = reader.WaitToReadAsync().AsTask();
            while (true)
            {
                // Bounded wait rather than a plain block: audio must keep flowing into the writer
                // even when the screen is static and the source delivers no video frame - otherwise
                // queued audio would pile up until the next pixel moved.
                if (!waitTask.Wait(50))
                {
                    DrainAudio(freq, epochTicks);
                    continue;
                }
                if (!waitTask.Result)
                {
                    break; // the channel completed - real end of take (no handoff feature in this item)
                }

                while (reader.TryRead(out var queued))
                {
                    var frame = queued.Frame;
                    bool frameKeptAsRawBaseline = false;
                    try
                    {
                        // Subtract the accumulated paused time so the media clock is continuous
                        // across a pause - see this class's own _pausedTicks doc comment.
                        long paused = Volatile.Read(ref _pausedTicks);
                        long effectiveTicks = queued.TimestampTicks - paused;

                        if (prevRaw is not null && RawFrameEquality.RawFramesEqual(prevRaw, frame))
                        {
                            continue; // crop unchanged (monitor was dirty elsewhere) - skip pre-tone-map
                        }

                        epochTicks ??= effectiveTicks;
                        framesWritten++;

                        if (_format == RecordingFormat.Mp4)
                        {
                            var sdr = RoeSnip.Core.Imaging.SdrImage.FromCapturedFrame(frame, _fixedToneMapOpts);
                            long timestamp100ns = (long)((effectiveTicks - epochTicks.Value) / freq * 10_000_000.0);
                            _encoder!.WriteFrame(sdr, timestamp100ns);
                        }
                        else
                        {
                            byte[] target = gifUseA
                                ? (gifTonemapA ??= new byte[frame.Width * 4 * frame.Height])
                                : (gifTonemapB ??= new byte[frame.Width * 4 * frame.Height]);
                            var sdr = RoeSnip.Core.Imaging.SdrImage.FromCapturedFrame(frame, _fixedToneMapOpts, target);

                            var encodeSource = sdr;
                            if (_gifRenderScale < 1.0)
                            {
                                gifScaledScratch ??= new byte[_gifCanvasWidth * 4 * _gifCanvasHeight];
                                GifRegionDownsample.BoxDownsample(sdr.Pixels, sdr.Width, sdr.Height, gifScaledScratch, _gifCanvasWidth, _gifCanvasHeight);
                                encodeSource = new RoeSnip.Core.Imaging.SdrImage(_gifCanvasWidth, _gifCanvasHeight, gifScaledScratch);
                            }

                            // GIF's encoder owns the timing: it diffs against the last emitted frame,
                            // skips no-change frames, crops to the changed region, and patches each
                            // frame's delay to its real display duration once the next frame lands
                            // (the "patch-behind delta delay" this item is required to preserve).
                            if (_encoder!.WriteFrame(encodeSource, effectiveTicks))
                            {
                                gifUseA = !gifUseA; // harmless housekeeping only
                            }
                        }

                        prevRaw?.Dispose();
                        prevRaw = frame; // becomes the raw-skip baseline for the next frame
                        frameKeptAsRawBaseline = true;
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
        }
        finally
        {
            FileLog.Write($"RoeSnip: encoder loop exiting, {framesWritten} frame(s) processed");
            prevRaw?.Dispose();

            try { DrainAudio(freq, epochTicks); }
            catch (Exception ex) { FileLog.Write($"RoeSnip: final audio drain failed: {ex}"); }

            // Finalize with the take's stop moment on the pause-adjusted clock so the LAST frame's
            // delay (GIF) covers the static tail the user held before stopping; MP4 ignores the
            // timestamp argument entirely (see IVideoEncoder.FinishAsync's own doc comment).
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            long pausedTotal = Volatile.Read(ref _pausedTicks);
            if (_paused)
            {
                pausedTotal += now - Volatile.Read(ref _pauseStartTicks); // stopped mid-pause
            }
            try { _encoder?.FinishAsync(now - pausedTotal).GetAwaiter().GetResult(); }
            catch (Exception ex) { FileLog.Write($"RoeSnip: recording finalize failed: {ex}"); }
        }
    }

    private void DrainAudio(double freq, long? epochTicks)
    {
        var audio = _audio;
        var encoder = _encoder;
        if (audio is null || encoder is null || !encoder.HasAudio)
        {
            return;
        }

        while (audio.TryDequeue(out var chunk))
        {
            // Judge by when the chunk was CAPTURED, not by the flag at drain time: at the moment of
            // a Pause or soft Stop there is a small backlog of genuinely-recorded audio still queued
            // (plus the engine's final flush, for a soft stop), and a flag-only check would silently
            // chop that tail off the take. Chunks captured during the pause itself drop.
            if (_paused && chunk.QpcTicks >= Volatile.Read(ref _pauseStartTicks))
            {
                continue;
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
