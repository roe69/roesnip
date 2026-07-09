using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Win32;
using RoeSnip.Capture;
using RoeSnip.Imaging;

namespace RoeSnip.Recording;

/// <summary>Owns the single active recording (if any) for the whole process — mirrors
/// OverlayController.IsSessionActive/s_activeSession's shape exactly. Wired into
/// AppComposition.StartRecording from Program.cs's RunCaptureFlowAsync (see that hook's doc
/// comment for the CaptureGate ownership-transfer contract this class must honor) and into
/// App/TrayApp.cs's TriggerCapture (PrtScr while recording stops+saves instead of starting a new
/// capture).</summary>
internal static class RecordingController
{
    private static RecordingSession? s_active; // set/cleared on the UI thread only

    /// <summary>True while a recording is actually running (chrome up, capture loop live) —
    /// TrayApp.TriggerCapture checks this BEFORE MarkTriggerTimestamp/flash/anything else.</summary>
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
            // one is live, and a recording holds the gate for its whole lifetime.
            throw new InvalidOperationException("A recording is already active.");
        }

        var evenSelection = RoundDownToEven(selectionPx);
        var session = new RecordingSession(monitor, evenSelection, format, settings, notifier);
        try
        {
            session.Start(); // synchronous WGC/MF/chrome setup — throws on failure, RecordingSession.Start cleans up partial state itself
        }
        catch
        {
            throw; // s_active was never set — RunCaptureFlowAsync's catch handles the CaptureGate non-transfer
        }

        s_active = session;
        return Task.CompletedTask;
    }

    /// <summary>TrayApp calls this when PrtScr is pressed while a recording is active. Stop+save,
    /// never a new capture flow.</summary>
    public static void RequestStopAndSave() => s_active?.RequestStop(save: true);

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

/// <summary>One in-progress recording: owns the <see cref="RegionRecorder"/> (capture), the chosen
/// encoder (<see cref="Mp4Encoder"/>/<see cref="GifEncoder"/>), the on-screen
/// <see cref="RecordingChrome"/>, and the dedicated encoder thread that drains frames from the
/// recorder and feeds the encoder. <see cref="Stop"/> is the single terminal path — mirrors
/// OverlayController.OverlaySession.Finish's "single terminal point" pattern.</summary>
internal sealed class RecordingSession
{
    // v1 hard cap for GIF recordings (PLAN.md §Flag-5): GifBitmapEncoder is a batch API with no
    // incremental write, so frames sit fully decoded in memory (~W*H*4 bytes each) until Stop() —
    // at 12fps a large selection can still reach roughly 1-1.5 GB resident before Save(). The chrome
    // shows a live countdown (SetElapsed's cap parameter) so hitting it is never a silent surprise.
    private const int GifCapSeconds = 60;

    private readonly MonitorInfo _monitor;
    private readonly RectPhysical _selectionPx;
    private readonly RecordingFormat _format;
    private readonly RoeSnipSettings _settings;
    private readonly ITrayNotifier? _notifier;

    private RegionRecorder? _recorder;
    private Mp4Encoder? _mp4Encoder;
    private GifEncoder? _gifEncoder;
    private RecordingChrome? _chrome;
    private Thread? _encoderThread;
    private DispatcherTimer? _uiTimer;
    private Dispatcher? _uiDispatcher;
    private EventHandler? _displayChangedHandler;
    private System.Diagnostics.Stopwatch? _stopwatch;
    private RoeSnip.Color.ToneMapOptions _fixedToneMapOpts;
    private int _targetFps;
    private string? _mp4TempPath;
    private int _stopped; // Interlocked guard — Stop() must run its body exactly once

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

    /// <summary>UI thread only. Synchronous end to end (WGC device/item creation, MF SinkWriter
    /// setup, chrome window creation) — throws on any failure, having cleaned up whatever it
    /// already constructed, so RecordingController never records a partially-started session as
    /// active.</summary>
    public void Start()
    {
        try
        {
            _uiDispatcher = Dispatcher.CurrentDispatcher;
            _targetFps = _format == RecordingFormat.Gif ? 12 : 30;

            // Fixed tone-map options, computed ONCE here from monitor metadata — never re-derived
            // per frame. Per-frame auto-derived peak (ToneMapper's normal path also folds in the
            // CURRENT frame's own max) would visibly pump exposure on fluctuating HDR content, an
            // auto-exposure-style flicker; pinning PeakOverride up front is the fix. Mirrors
            // ToneMapper.ComputeCurveParams's own derivation formula but computed from monitor
            // photometrics alone, never from a frame's content.
            double peak = _settings.ToneMapPeakOverride
                ?? Math.Clamp(_monitor.MaxLuminanceNits / _monitor.SdrWhiteNits, 2.0, double.MaxValue);
            double knee = _settings.ToneMapKneeOverride ?? 0.90;
            _fixedToneMapOpts = new RoeSnip.Color.ToneMapOptions(Knee: knee, PeakOverride: peak);

            _recorder = new RegionRecorder(_monitor, _selectionPx, _targetFps);
            _recorder.Faulted += OnRecorderFaulted;

            if (_format == RecordingFormat.Mp4)
            {
                _mp4TempPath = Path.Combine(Path.GetTempPath(), $"roesnip_rec_{Guid.NewGuid():N}.mp4");
                _mp4Encoder = Mp4Encoder.Create(_mp4TempPath, _selectionPx.Width, _selectionPx.Height, _targetFps);
            }
            else
            {
                _gifEncoder = new GifEncoder();
            }

            _chrome = new RecordingChrome(_monitor, _selectionPx);
            _chrome.StopRequested += () => RequestStop(save: true);
            _chrome.CancelRequested += () => RequestStop(save: false);
            _chrome.Show();

            // Started last (after the encoder is ready to receive frames) so no captured frame is
            // ever silently dropped for lack of a sink.
            _recorder.Start();

            _stopwatch = System.Diagnostics.Stopwatch.StartNew();
            // The 1-arg ctor binds to Dispatcher.CurrentDispatcher implicitly — safe here since
            // Start() itself only ever runs on the UI thread (see this method's own doc comment).
            _uiTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(1) };
            _uiTimer.Tick += OnTimerTick;
            _uiTimer.Start();

            _displayChangedHandler = (_, _) => RequestStop(save: true);
            SystemEvents.DisplaySettingsChanged += _displayChangedHandler;

            _encoderThread = new Thread(EncoderLoop) { IsBackground = true, Name = "RoeSnip-Recording-Encoder" };
            _encoderThread.Start();
            Console.Error.WriteLine($"RoeSnip: recording started ({_format}, {_selectionPx.Width}x{_selectionPx.Height}, {_targetFps}fps)");
        }
        catch
        {
            // Whatever partially came up must be torn down — otherwise a failed Start() leaks a WGC
            // device or leaves a chrome window stranded topmost with nothing to close it.
            try { _recorder?.Dispose(); } catch { /* best-effort */ }
            try { _chrome?.Close(); } catch { /* best-effort */ }
            try { _mp4Encoder?.Dispose(); } catch { /* best-effort */ }
            try { if (_mp4TempPath is not null && File.Exists(_mp4TempPath)) File.Delete(_mp4TempPath); } catch { /* best-effort */ }
            throw;
        }
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        var elapsed = _stopwatch!.Elapsed;
        TimeSpan? cap = _format == RecordingFormat.Gif ? TimeSpan.FromSeconds(GifCapSeconds) : null;
        _chrome!.SetElapsed(elapsed, cap);
        if (_format == RecordingFormat.Gif && elapsed.TotalSeconds >= GifCapSeconds)
        {
            RequestStop(save: true);
        }
    }

    private void OnRecorderFaulted(Exception ex)
    {
        Console.Error.WriteLine($"RoeSnip: recording capture failed mid-session (non-fatal — stopping with what was captured): {ex.Message}");
        RequestStop(save: true);
    }

    /// <summary>Marshals onto the UI thread if called from elsewhere (the encoder thread's own
    /// GIF-cap check, RegionRecorder.Faulted, which can fire on the WGC callback thread) before
    /// running <see cref="Stop"/> — Stop touches the WPF chrome window and must only ever run on
    /// the thread that created it.</summary>
    internal void RequestStop(bool save)
    {
        var dispatcher = _uiDispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Stop(save);
        }
        else
        {
            dispatcher.BeginInvoke(new Action(() => Stop(save)));
        }
    }

    /// <summary>The single terminal path every end-of-recording route funnels through (Stop button,
    /// Cancel button, PrtScr-while-recording, GIF cap, display change, a mid-recording fault). Runs
    /// on the UI thread (see <see cref="RequestStop"/>). Idempotent via <see cref="_stopped"/>.</summary>
    private void Stop(bool save)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }
        Console.Error.WriteLine($"RoeSnip: recording stopping (save={save}, elapsed={_stopwatch?.Elapsed})");

        if (_displayChangedHandler is not null)
        {
            SystemEvents.DisplaySettingsChanged -= _displayChangedHandler;
            _displayChangedHandler = null;
        }
        _uiTimer?.Stop();

        _recorder!.Stop(); // stops feeding the queue and completes it

        // Bounded join: a wedged encoder (stuck MF call, disk stall) must never hang the UI thread
        // forever. If it doesn't finish in time, we must NOT touch _mp4Encoder or the temp file from
        // here on — the abandoned thread can still be inside IMFSinkWriter.WriteSample/Finalize (its
        // own EncoderLoop finally block), and that COM interface is not safe to call concurrently
        // from two threads. A File.Move/Dispose racing it could crash the process or corrupt output
        // while telling the user the recording "saved" successfully. Deliberately leak the temp
        // file/writer in that case rather than race it.
        bool joined = _encoderThread!.Join(TimeSpan.FromSeconds(5));
        if (!joined)
        {
            Console.Error.WriteLine("RoeSnip: recording encoder thread did not finish within 5s; abandoning it without touching its output.");
        }

        string? finalPath = null;
        bool cancelled = false;
        if (joined)
        {
            try
            {
                if (save)
                {
                    // The chrome is Hidden (not yet Closed) so it can still own the SaveFileDialog —
                    // a WPF window needs to exist as a valid HWND for Dialog.ShowDialog(owner) to
                    // parent correctly; closing it first would leave the dialog unowned (it could
                    // appear behind other windows on a multi-monitor setup).
                    _chrome!.Hide();
                    finalPath = SaveOutput();
                    cancelled = finalPath is null; // SaveOutput returns null only when the user cancelled the dialog
                }
            }
            catch (Exception ex)
            {
                _notifier?.ShowError($"Failed to save recording: {ex.Message}");
            }
            finally
            {
                // Also clean up on a cancelled dialog, not just !save — otherwise a cancelled save
                // leaves the temp mp4 orphaned under %TEMP% with no file ever produced and no
                // indication anything was lost.
                if (!save || cancelled)
                {
                    CleanupTempFile();
                }
            }
        }
        _chrome!.CloseChrome();

        if (finalPath is not null)
        {
            _notifier?.ShowSavedBalloon(finalPath);
        }
        else if (joined && cancelled)
        {
            _notifier?.ShowError("Recording discarded — save was cancelled.");
        }
        else if (!joined)
        {
            _notifier?.ShowError("Recording could not be saved — the encoder did not stop in time.");
        }

        // The recorder's own device/item are never shared with the encoder thread (which only reads
        // from RegionRecorder.Frames), so disposing it is safe regardless of the join outcome above.
        _recorder.Dispose();
        if (joined)
        {
            _mp4Encoder?.Dispose(); // only safe once the encoder thread — the only other owner — has actually finished
        }

        RoeSnip.CaptureGate.Exit(); // exactly once, last — see AppComposition.StartRecording's doc comment
        RecordingController.OnSessionEnded(this);
    }

    /// <summary>UI thread (called from <see cref="Stop"/>). Test hook: when
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

        if (_format == RecordingFormat.Mp4)
        {
            File.Move(_mp4TempPath!, finalPath, overwrite: true);
        }
        else
        {
            _gifEncoder!.SaveTo(finalPath);
        }
        return finalPath;
    }

    private void CleanupTempFile()
    {
        if (_mp4TempPath is null)
        {
            return; // GIF never writes a temp file — SaveTo only ever runs when save=true
        }
        try
        {
            if (File.Exists(_mp4TempPath))
            {
                File.Delete(_mp4TempPath);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: failed to delete abandoned recording temp file: {ex.Message}");
        }
    }

    /// <summary>Encoder thread only (never the WGC callback thread, never the UI thread): drains
    /// RegionRecorder.Frames, tone-maps each raw CapturedFrame with the FIXED options computed once
    /// in Start(), and feeds the chosen encoder. Exits when the channel completes (RegionRecorder.
    /// Stop() was called) or on an unrecoverable encoder exception.</summary>
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
            while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
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
                        if (_gifEncoder.AtCap(GifCapSeconds, _targetFps))
                        {
                            RequestStop(save: true);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: recording encoder failed mid-session after {framesWritten} frame(s): {ex}");
            // Best-effort: keep whatever encoded cleanly so far rather than losing the whole
            // recording to one bad frame/disk hiccup.
            RequestStop(save: true);
        }
        finally
        {
            Console.Error.WriteLine($"RoeSnip: encoder loop exiting, {framesWritten} frame(s) processed");
            if (_format == RecordingFormat.Mp4)
            {
                try { _mp4Encoder?.FinalizeAndClose(); }
                catch (Exception ex) { Console.Error.WriteLine($"RoeSnip: MP4 finalize failed: {ex}"); }
            }
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
