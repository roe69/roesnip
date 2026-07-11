using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Win32;
using RoeSnip.App;
using RoeSnip.Capture;
using RoeSnip.Imaging;

namespace RoeSnip.Recording;

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

    private readonly MonitorInfo _monitor;
    private RectPhysical _selectionPx; // position slides when the user drags RegionOutline; size fixed
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
    private DispatcherTimer? _uiTimer;
    private Dispatcher? _uiDispatcher;
    private EventHandler? _displayChangedHandler;
    private System.Diagnostics.Stopwatch? _stopwatch;
    private RoeSnip.Color.ToneMapOptions _fixedToneMapOpts;
    private int _targetFps;
    private string? _mp4TempPath;
    private string? _gifTempPath;
    private int _ended; // Interlocked guard — TeardownSession must run its body exactly once

    private Phase _phase = Phase.Setup;
    private bool _mic;
    private bool _systemAudio;
    private RoeSnipSettings _liveSettings = null!; // set in Start(); tracks audio-toggle edits for persistence

    private bool _hasGifPrevTimestamp;
    private long _lastGifTimestampTicks;

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
            _targetFps = _format == RecordingFormat.Gif ? 12 : 30;

            // Fixed tone-map options, computed ONCE here from monitor metadata — never re-derived
            // per frame or per take (a Restart reuses these). Per-frame auto-derived peak
            // (ToneMapper's normal path also folds in the CURRENT frame's own max) would visibly
            // pump exposure on fluctuating HDR content, an auto-exposure-style flicker; pinning
            // PeakOverride up front is the fix. Mirrors ToneMapper.ComputeCurveParams's own
            // derivation formula but computed from monitor photometrics alone, never from a frame's
            // content.
            double peak = _settings.ToneMapPeakOverride
                ?? Math.Clamp(_monitor.MaxLuminanceNits / _monitor.SdrWhiteNits, 2.0, double.MaxValue);
            double knee = _settings.ToneMapKneeOverride ?? 0.90;
            _fixedToneMapOpts = new RoeSnip.Color.ToneMapOptions(Knee: knee, PeakOverride: peak);

            _liveSettings = _settings;
            _mic = _settings.RecordMicrophone;
            _systemAudio = _settings.RecordSystemAudio;

            // The visible "this area will be recorded" frame (both formats): dashed outline just
            // OUTSIDE the region, capture-excluded, click-through — see RegionOutline's doc. Shown
            // for the whole session (Setup through Reviewing), not just while actually capturing.
            _outline = new RegionOutline(_monitor, _selectionPx);
            _outline.RegionChanged += OnRegionMoved;
            _outline.Show(); // Setup mode by default: band resizes, Shift/Ctrl inside moves

            _chrome = new RecordingChrome(_monitor, _selectionPx, _format, _mic, _systemAudio);
            _chrome.StartRequested += BeginCapture;
            _chrome.StopRequested += StopCaptureToReview;
            _chrome.RestartConfirmed += RestartTake;
            _chrome.SaveRequested += SaveAndFinish;
            _chrome.CancelRequested += CancelAndDiscard;
            _chrome.MicToggled += v => SetAudioToggle(mic: v, systemAudio: null);
            _chrome.SystemAudioToggled += v => SetAudioToggle(mic: null, systemAudio: v);
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

    /// <summary>The user moved or resized the recorded region (RegionOutline). UI thread. Re-anchor
    /// the HUD to the region's new spot and, if a take is actively capturing, slide the recorder's
    /// crop origin so the recording follows (only a MOVE reaches here while capturing - resize is
    /// Setup-only, before the recorder exists, so SetOrigin is a no-op then). The outline repositions
    /// itself; this only propagates to the other two moving parts.</summary>
    private void OnRegionMoved(RectPhysical selectionPx)
    {
        _selectionPx = selectionPx;
        _chrome?.UpdateSelection(selectionPx);
        _recorder?.SetOrigin(selectionPx.Left, selectionPx.Top);
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
            _recorder = new RegionRecorder(_monitor, _selectionPx, _targetFps);
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
                _mp4Encoder = Mp4Encoder.Create(
                    _mp4TempPath, _selectionPx.Width, _selectionPx.Height, _targetFps, withAudio: _audio is not null);
            }
            else
            {
                _gifTempPath = Path.Combine(Path.GetTempPath(), $"roesnip_rec_{Guid.NewGuid():N}.gif");
                _gifEncoder = GifEncoder.Create(_gifTempPath, _selectionPx.Width, _selectionPx.Height);
            }

            _hasGifPrevTimestamp = false; // fresh delay-timestamp sequence for this take

            // Started last (after the encoder is ready to receive frames) so no captured frame is
            // ever silently dropped for lack of a sink.
            _recorder.Start();

            _stopwatch = System.Diagnostics.Stopwatch.StartNew();
            _uiTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(1) };
            _uiTimer.Tick += OnTimerTick;
            _uiTimer.Start();

            _displayChangedHandler = (_, _) => RequestForceStopAndSave();
            SystemEvents.DisplaySettingsChanged += _displayChangedHandler;

            _encoderThread = new Thread(EncoderLoop) { IsBackground = true, Name = "RoeSnip-Recording-Encoder" };
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

    private void OnTimerTick(object? sender, EventArgs e)
    {
        // No duration cap for either format: GIF frames stream to a temp file now (see
        // GifEncoder), so the old 60-second in-memory-buffering cap has no reason to exist.
        _chrome!.SetElapsed(_stopwatch!.Elapsed, cap: null);
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
            StopCaptureToReview(); // may itself tear the session down if the encoder thread had to be abandoned
        }
        if (_phase == Phase.Reviewing)
        {
            SaveAndFinish();
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
        if (_phase != Phase.Capturing)
        {
            return true;
        }

        if (_displayChangedHandler is not null)
        {
            SystemEvents.DisplaySettingsChanged -= _displayChangedHandler;
            _displayChangedHandler = null;
        }
        _uiTimer?.Stop();
        _uiTimer = null;

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

    /// <summary>Chrome's Stop button (or PrtScr while Capturing). Ends the take and moves to
    /// Reviewing — does NOT save. If the encoder thread had to be abandoned there is nothing left
    /// to review, so the session tears down instead (message already shown here).</summary>
    private void StopCaptureToReview()
    {
        if (_phase != Phase.Capturing)
        {
            return;
        }
        Console.Error.WriteLine($"RoeSnip: recording capture stopping for review (elapsed={_stopwatch?.Elapsed})");

        bool joined = HardStopCaptureIfNeeded();
        if (!joined)
        {
            _notifier?.ShowError("Recording could not be saved: the encoder did not stop in time.");
            TeardownSession(finalPath: null, dialogCancelled: false, encoderAbandoned: false); // already messaged above
            return;
        }

        _phase = Phase.Reviewing;
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
        _hasGifPrevTimestamp = false;

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
    /// actually been stopped into review. Finalizes/moves the temp file to a real path, then tears
    /// the session down.</summary>
    private void SaveAndFinish()
    {
        if (_phase != Phase.Reviewing)
        {
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
        }

        TeardownSession(finalPath, dialogCancelled, encoderAbandoned: false);
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
    /// in Start(), and feeds the chosen encoder. Exits when the channel completes (RegionRecorder.
    /// Stop() was called) or on an unrecoverable encoder exception. Runs once per take — a Restart
    /// spins up a brand-new thread via the next BeginCapture.</summary>
    private void EncoderLoop()
    {
        double freq = System.Diagnostics.Stopwatch.Frequency;
        var reader = _recorder!.Frames;
        int framesWritten = 0;
        // Epoch is the FIRST frame actually dequeued here, not an independent Stopwatch sample taken
        // on this thread's own first line. RegionRecorder.Start() (and thus frame production) begins
        // before this thread is even created/scheduled/JITed, so a Stopwatch.GetTimestamp() sampled
        // here could land AFTER one or more frames already sitting in the queue — those would get a
        // negative-then-clamped-to-0 SampleTime, producing duplicate/non-monotonic timestamps that
        // Media Foundation's sink writer can reject. Deriving the epoch from the first real frame
        // makes timestamp 0 always correspond to an actual frame and every later one strictly larger.
        long? epochTicks = null;

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
                    break; // channel completed — RegionRecorder.Stop() was called
                }

                while (reader.TryRead(out var queued))
                {
                    using var frame = queued.Frame;
                    epochTicks ??= queued.TimestampTicks;
                    var sdr = SdrImage.FromCapturedFrame(frame, _fixedToneMapOpts);
                    framesWritten++;

                    if (_format == RecordingFormat.Mp4)
                    {
                        long timestamp100ns = (long)((queued.TimestampTicks - epochTicks.Value) / freq * 10_000_000.0);
                        _mp4Encoder!.WriteFrame(sdr, timestamp100ns);
                    }
                    else
                    {
                        ushort delayCs = ComputeGifDelayCentiseconds(queued.TimestampTicks, freq);
                        _gifEncoder!.AddFrame(sdr, delayCs);
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
                try { _gifEncoder?.FinalizeAndClose(); }
                catch (Exception ex) { Console.Error.WriteLine($"RoeSnip: GIF finalize failed: {ex}"); }
            }
        }
    }

    /// <summary>Encoder thread only. Moves every queued audio chunk into the MP4 writer, mapping
    /// each chunk's QPC timestamp onto the video timeline (same epoch: the first dequeued video
    /// frame). Chunks captured before that epoch are dropped — the track starts with the video.</summary>
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
            if (epochTicks is null || chunk.QpcTicks < epochTicks.Value)
            {
                continue; // pre-roll audio from before the first video frame
            }
            long timestamp100ns = (long)((chunk.QpcTicks - epochTicks.Value) / freq * 10_000_000.0);
            encoder.WriteAudioSamples(chunk.Pcm, chunk.Pcm.Length, timestamp100ns);
        }
    }

    private ushort ComputeGifDelayCentiseconds(long timestampTicks, double freq)
    {
        ushort delay;
        if (!_hasGifPrevTimestamp)
        {
            delay = (ushort)Math.Clamp(Math.Round(100.0 / _targetFps), 1, ushort.MaxValue);
            _hasGifPrevTimestamp = true;
        }
        else
        {
            double seconds = (timestampTicks - _lastGifTimestampTicks) / freq;
            delay = (ushort)Math.Clamp(Math.Round(seconds * 100.0), 1, ushort.MaxValue);
        }
        _lastGifTimestampTicks = timestampTicks;
        return delay;
    }
}

file static class ModuleInit
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Init() => AppComposition.StartRecording = RecordingController.StartAsync;
}
