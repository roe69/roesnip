using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using RoeSnip.App.AppShell;
using RoeSnip.App.Overlay;
using RoeSnip.Core.Capture;
using RoeSnip.Core.Diagnostics;
using RoeSnip.Core.Recording;
using RoeSnip.Core.Recording.Gif;
using RoeSnip.Core.Settings;
using RoeSnip.Core.Sharing;

namespace RoeSnip.App.Recording;

/// <summary>Owns the UI layer of ONE recording (item 21): the RecordingChrome HUD, the (Windows-only)
/// RegionOutline, the elapsed-time ticker, and the Sharing/* integration - everything
/// RecordingController.RecordingSession (item 20, pure state-machine/encoder logic) deliberately has
/// no reference to. Wired into AppComposition.StartRecording via [ModuleInitializer], the same pattern
/// OverlayController.Init uses for AppComposition.RunOverlay. One instance per recording; replaced
/// (not reused) by the next Record click, mirroring OverlayController's own one-session-at-a-time
/// shape.</summary>
public sealed class RecordingOrchestrator
{
    private static RecordingOrchestrator? s_active; // UI thread only

    public static bool IsActive => s_active is not null;

    /// <summary>AppComposition.StartRecording's target - see that hook's own doc comment. Failure
    /// here (e.g. CaptureService/backend construction throwing) surfaces via the notifier and leaves
    /// no session behind, exactly like RecordingController.StartNew's own "throws on failure, nothing
    /// to clean up yet" contract.</summary>
    public static void Start(
        MonitorInfo monitor, RectPhysical selectionPx, RecordingFormat format,
        RoeSnipSettings settings, ITrayNotifier? notifier)
    {
        if (s_active is not null)
        {
            // CaptureGate already prevents a second overlay/capture stack while one is live, and a
            // recording holds the gate for its whole Setup-through-Reviewing lifetime (see
            // AppComposition.RunCaptureFlowAsync's own hand-off comment) - this should be unreachable.
            notifier?.ShowError("A recording is already active.");
            return;
        }

        RecordingSession session;
        try
        {
            session = RecordingController.StartNew(monitor, selectionPx, format, settings, notifier);
        }
        catch (Exception ex)
        {
            notifier?.ShowError($"Failed to start recording: {ex.Message}");
            CaptureGate.Exit();
            IdleMemoryTrimmer.Schedule();
            return;
        }

        s_active = new RecordingOrchestrator(session, settings, notifier);
    }

    private readonly RecordingSession _session;
    private readonly ITrayNotifier? _notifier;
    private readonly RecordingChrome _chrome;
    private readonly RegionOutline? _outline; // Windows-only - see that class's own doc comment
    private readonly DispatcherTimer _elapsedTimer;
    // Reviewing-only Ctrl+C hook (Windows only - see ReviewCopyHook), installed on entering
    // Reviewing and disposed by every path that leaves it.
    private ReviewCopyHook? _reviewCopyHook;
    // Reentrancy guard for the finished-take prompt's own Save: the picker is awaited, so a second
    // click (or automation call) would otherwise open a second one over the first.
    private bool _resavingFinishedTake;

    private RecordingOrchestrator(RecordingSession session, RoeSnipSettings settings, ITrayNotifier? notifier)
    {
        _session = session;
        _notifier = notifier;

        var capabilities = RecordingCapabilitiesRegistry.ForCurrentPlatform();
        bool micSupported = capabilities.SupportsMicrophone;
        bool systemAudioSupported = capabilities.SupportsLoopback;
        int fps = session.Format == RecordingFormat.Gif ? settings.GifFps : settings.Mp4Fps;
        int clampedFps = session.Format == RecordingFormat.Gif
            ? RecordingSizeEstimator.ClampFps(fps, RecordingSizeEstimator.GifMinFps, RecordingSizeEstimator.GifMaxFps)
            : RecordingSizeEstimator.ClampFps(fps, RecordingSizeEstimator.Mp4MinFps, RecordingSizeEstimator.Mp4MaxFps);
        var preset = GifSizePresets.Parse(session.Format == RecordingFormat.Gif ? settings.GifSizePreset : settings.Mp4SizePreset);

        _chrome = new RecordingChrome(
            session.Monitor, session.SelectionPx, session.Format,
            settings.RecordMicrophone && micSupported, settings.RecordSystemAudio && systemAudioSupported,
            micSupported, systemAudioSupported, preset, clampedFps);

        _chrome.StartRequested += () => _session.BeginCapture();
        _chrome.StopRequested += () => _session.StopCaptureToReview();
        _chrome.PauseRequested += () => _session.Pause();
        _chrome.ResumeRequested += () => _session.Resume();
        _chrome.RestartConfirmed += () => _session.Restart();
        _chrome.SaveRequested += () => Save();
        _chrome.ShareRequested += () => RequestShare();
        _chrome.CopyRequested += () => RequestCopyToClipboard();
        _chrome.RecordAnotherRequested += () => _session.RecordAnotherTake();
        _chrome.DoneRequested += () => _session.FinishAfterTake();
        _chrome.CopyAgainRequested += () => CopyFinishedTakeAgain();
        _chrome.SaveAgainRequested += () => SaveFinishedTakeCopy();
        _chrome.CancelRequested += () => _session.CancelAndDiscard();
        _chrome.MicToggled += on => _session.SetAudioToggle(on, null);
        _chrome.SystemAudioToggled += on => _session.SetAudioToggle(null, on);
        _chrome.SizePresetChanged += preset2 => _session.SetSizePreset(preset2);
        _chrome.FpsChanged += fps2 => _session.SetFps(fps2);

        _session.PhaseChanged += OnPhaseChanged;
        _session.TakeFinished += message =>
            _chrome.ShowDonePrompt(message, takeAvailable: _session.FinishedTakePath is not null);
        _session.PausedChanged += _chrome.SetPaused;
        _session.Ended += OnEnded;

        if (OperatingSystem.IsWindows())
        {
            _outline = new RegionOutline(session.Monitor, session.SelectionPx);
            _outline.RegionChanged += rect =>
            {
                _session.UpdateSelection(rect);
                _chrome.UpdateSelection(rect);
            };
        }

        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _elapsedTimer.Tick += (_, _) => _chrome.SetElapsed(_session.Elapsed, cap: null);
        _elapsedTimer.Start();

        _chrome.Show();
        _outline?.Show();
    }

    private void OnPhaseChanged(RecordingSession.RecordingSessionPhase phase)
    {
        switch (phase)
        {
            case RecordingSession.RecordingSessionPhase.Setup:
                // Re-armed for a new take: the finished one is out of reach, so Ctrl+C has nothing
                // left to copy. The hook deliberately SURVIVES the finished-take prompt, which is
                // not a phase change - see CopyOnCtrlC.
                DisposeReviewCopyHook();
                _chrome.HideDonePrompt();
                _chrome.EnterSetup();
                _outline?.SetInteractionMode(allowResize: true);
                break;
            case RecordingSession.RecordingSessionPhase.Capturing:
                DisposeReviewCopyHook(); // a live take has nothing finished to copy
                _chrome.EnterRecording();
                _outline?.SetInteractionMode(allowResize: false);
                break;
            case RecordingSession.RecordingSessionPhase.Reviewing:
                // Senior-review fix ported verbatim from the WPF reference (RecordingController.cs,
                // commit 422e87a): re-evaluate the chrome's Share gate from a FRESH disk read every
                // time Reviewing is (re-)entered, BEFORE EnterReviewing applies it - see
                // RequestShare's own doc comment for why "fresh" (not this orchestrator's own
                // _settingsAtStart snapshot) matters here specifically.
                _chrome.SetShareAvailable(ResolveFreshDefaultShareProvider() is not null);
                _chrome.EnterReviewing();
                // Ctrl+C copies the take while it waits here (Windows only - see ReviewCopyHook).
                DisposeReviewCopyHook();
                _reviewCopyHook = ReviewCopyHook.TryInstall(() =>
                    Dispatcher.UIThread.Post(CopyOnCtrlC));
                break;
        }
    }

    private void DisposeReviewCopyHook()
    {
        _reviewCopyHook?.Dispose();
        _reviewCopyHook = null;
    }

    private void OnEnded()
    {
        _elapsedTimer.Stop();
        DisposeReviewCopyHook(); // a system-wide keyboard hook must never outlive its session
        _chrome.CloseChrome();
        _outline?.CloseOutline();
        if (ReferenceEquals(s_active, this))
        {
            s_active = null;
        }
        // The whole point of AppComposition.RunCaptureFlowAsync handing CaptureGate off instead of
        // exiting it itself (see that call site's own comment) - only NOW, once the whole recording
        // flow (Setup through Reviewing, however it ended) is truly over, is the gate released for
        // the next hotkey trigger.
        CaptureGate.Exit();
        IdleMemoryTrimmer.Schedule();
    }

    /// <summary>Chrome's Save button (item 21e - no native save-file dialog yet, same documented gap
    /// RecordingSession.Save's own doc comment already carries; ROESNIP_RECORD_AUTOSAVE is the only
    /// way this port can save outside automation's explicit-path route).</summary>
    private void Save()
    {
        string? path = _session.Save();
        if (path is null && !_session.IsReviewing)
        {
            // Save() already reported (balloon) and tore the session down itself on a real failure;
            // a null result while still Reviewing means "no path available" (ROESNIP_RECORD_AUTOSAVE
            // unset, no automation path) - RecordingSession.SaveOutput already logs that to stderr.
        }
    }

    /// <summary>Reviewing-state Share (item 21e). The provider is resolved FIRST from a FRESH
    /// SettingsStore.Load() (never this orchestrator's own settings snapshot, which can be minutes
    /// stale by the time a Reviewing take gets around to sharing) and checked BEFORE the session's
    /// own irreversible hard-stop: if nothing resolves, this is a plain NO-OP - an honest error
    /// balloon, the take left exactly as it was (still soft-stopped, still fully Resumable). Only
    /// once a provider resolves does RecordingSession.BeginShareHandoff hard-stop and rearm; the
    /// upload itself is handed to <see cref="RoeSnip.App.Sharing.ShareFlowPresenter"/> - its own
    /// ShareResultWindow now owns progress/result reporting, and the temp file is deleted ONLY via
    /// the presenter's onSuccess callback (the DATA-LOSS RULE), kept with its full path named in the
    /// Failure state on any other outcome.</summary>
    /// <summary>Chrome's Copy button (and, on Windows, Ctrl+C): the session hard-stops and stages
    /// the finished take, this puts it on the platform clipboard, and only then does the session
    /// park on its "Record another?" prompt - a failed clipboard write must not claim success.
    /// The take is never discarded on failure: it has already been staged out of the temp path, and
    /// the path is named in the error so it can be recovered by hand.</summary>
    private void RequestCopyToClipboard()
    {
        string? staged = _session.BeginClipboardHandoff();
        if (staged is null)
        {
            return; // reentrant call, or the encoder failed to stop (already messaged by the session)
        }

        _ = CopyStagedFileAsync(staged);
    }

    private async Task CopyStagedFileAsync(string staged)
    {
        try
        {
            await ClipboardService.CopyFileAsync(_chrome, staged);
            FileLog.Write($"RoeSnip: recording copied to the clipboard as {staged}");
            _session.CompleteClipboardHandoff(staged);
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: copying the recording to the clipboard failed: {ex}");
            _notifier?.ShowError($"Could not copy the recording to the clipboard: {ex.Message} It was kept at: {staged}");
            // Still park on the prompt WITH the staged path: the file is real and already out of
            // the temp path, so "Copy again" is exactly the retry this failure needs.
            _session.CompleteClipboardHandoff(staged);
        }
    }

    /// <summary>Where the review Ctrl+C hook lands (Windows only). Reviewing means "copy this
    /// take"; the finished-take prompt means "put it back on the clipboard" - the same key, because
    /// to the user it is the same take and the same intent.</summary>
    private void CopyOnCtrlC()
    {
        if (_session.AwaitingAnotherTakeChoice)
        {
            CopyFinishedTakeAgain();
            return;
        }
        RequestCopyToClipboard();
    }

    /// <summary>"Copy again" on the finished-take prompt, and Ctrl+C while it is up. The clipboard
    /// is shared state - anything else that copies between finishing a take and pasting it wipes it
    /// out - so the take stays re-copyable for as long as the prompt is on screen.</summary>
    private void CopyFinishedTakeAgain()
    {
        if (!_session.AwaitingAnotherTakeChoice || _session.FinishedTakePath is not { } path)
        {
            return;
        }
        if (!File.Exists(path))
        {
            _notifier?.ShowError($"The recording is no longer at {path}.");
            return;
        }

        _ = CopyFinishedTakeAgainAsync(path);
    }

    private async Task CopyFinishedTakeAgainAsync(string path)
    {
        try
        {
            await ClipboardService.CopyFileAsync(_chrome, path);
            FileLog.Write($"RoeSnip: recording re-copied to the clipboard from {path}");
            // Re-word the prompt: a button that deliberately does not dismiss it has no other way
            // to report back.
            _chrome.ShowDonePrompt(
                $"Copied {Path.GetFileName(path)} to the clipboard. Record another from this area?");
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: re-copying the recording to the clipboard failed: {ex}");
            _notifier?.ShowError($"Could not copy the recording to the clipboard: {ex.Message}");
        }
    }

    /// <summary>"Save" on the finished-take prompt. COPIES rather than moves: the same file is what
    /// Copy again hands to the clipboard (and, after a plain Save, is already the user's own file),
    /// so moving it would break the other button and could strand a clipboard entry pointing at
    /// nothing. A cancelled picker leaves the prompt exactly as it was.</summary>
    private void SaveFinishedTakeCopy()
    {
        if (!_session.AwaitingAnotherTakeChoice || _resavingFinishedTake
            || _session.FinishedTakePath is not { } path)
        {
            return;
        }
        if (!File.Exists(path))
        {
            _notifier?.ShowError($"The recording is no longer at {path}.");
            return;
        }

        _resavingFinishedTake = true;
        _ = SaveFinishedTakeCopyAsync(path);
    }

    private async Task SaveFinishedTakeCopyAsync(string path)
    {
        try
        {
            string? target = await PickSavePathAsync(Path.GetExtension(path));
            if (target is null)
            {
                return; // cancelled - the take is untouched and the prompt is still up
            }

            File.Copy(path, target, overwrite: true);
            _notifier?.ShowSavedBalloon(target);
            _chrome.ShowDonePrompt($"Saved {Path.GetFileName(target)}. Record another from this area?");
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: saving a second copy of the recording failed: {ex}");
            _notifier?.ShowError($"Failed to save recording: {ex.Message}");
        }
        finally
        {
            _resavingFinishedTake = false;
        }
    }

    /// <summary>Avalonia's own save picker, built the same way OverlayController.TryPickSavePathAsync
    /// builds the screenshot one (same suggested directory, same overwrite prompt). Note this does
    /// NOT close item 21e: RecordingSession.SaveOutput still has no dialog, so the Reviewing-state
    /// Save button stays ROESNIP_RECORD_AUTOSAVE-only. The prompt's Save can have one because it
    /// only copies an already-finished file instead of finalizing a live take.</summary>
    private async Task<string?> PickSavePathAsync(string extension)
    {
        string ext = extension.TrimStart('.');
        var settings = SettingsStore.Load();
        try
        {
            Directory.CreateDirectory(settings.SaveDirectory);
        }
        catch (Exception)
        {
            // Fall through and let the picker itself surface a directory problem, if any.
        }

        IStorageFolder? startLocation = null;
        try
        {
            startLocation = await _chrome.StorageProvider.TryGetFolderFromPathAsync(settings.SaveDirectory);
        }
        catch (Exception)
        {
            // A missing/inaccessible start folder just means the picker opens at its default.
        }

        var file = await _chrome.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedStartLocation = startLocation,
            SuggestedFileName = $"roesnip_{DateTime.Now:yyyyMMdd_HHmmss}.{ext}",
            DefaultExtension = ext,
            ShowOverwritePrompt = true,
        });

        return file?.TryGetLocalPath();
    }

    private void RequestShare()
    {
        var config = ResolveFreshDefaultShareProvider();
        if (config is null)
        {
            _notifier?.ShowError("Recording not shared: configure a share provider in Settings first.");
            return;
        }

        var handoff = _session.BeginShareHandoff();
        if (handoff is null)
        {
            return; // reentrant call, or the encoder failed to stop (already messaged by the session itself)
        }

        Stream stream;
        try
        {
            stream = new FileStream(
                handoff.TempPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 16, useAsync: true);
        }
        catch (Exception ex)
        {
            // Could not even open the finalized file for reading - report the same way an upload
            // failure would, naming the (still-present) path so the user can go rescue it by hand.
            _notifier?.ShowError($"Recording share failed: {ex.Message} The recording was kept at: {handoff.TempPath}");
            return;
        }

        var request = new ShareUploadRequest(stream, handoff.FileName, handoff.ContentType);
        RoeSnip.App.Sharing.ShareFlowPresenter.StartUpload(
            config,
            request,
            handoff.Monitor,
            keptFilePathOnFailure: handoff.TempPath,
            onSuccess: () =>
            {
                // The DATA-LOSS RULE's enforcement point: delete ONLY here, on a genuine upload
                // success - never in the presenter's own generic plumbing.
                try { if (File.Exists(handoff.TempPath)) File.Delete(handoff.TempPath); }
                catch (Exception ex) { FileLog.Write($"RoeSnip: failed to delete shared recording temp file: {ex.Message}"); }
            },
            onFailure: null,
            notifier: _notifier);
    }

    /// <summary>The single fresh-disk-state provider lookup shared by <see cref="RequestShare"/> and
    /// <see cref="OnPhaseChanged"/>'s own Reviewing-entry gate - see RequestShare's own doc comment
    /// for why "fresh" matters here specifically.</summary>
    private static ShareProviderConfig? ResolveFreshDefaultShareProvider()
    {
        try
        {
            var settings = SettingsStore.Load();
            return ShareManager.ResolveDefault(settings.ShareProviders, settings.DefaultShareProviderId);
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: fresh settings load for share availability check failed: {ex.Message}");
            return null;
        }
    }

    // ---------- PrtScr state machine (item 21d) ----------

    /// <summary>TrayApp.TriggerCapture's is-recording-active branch (WPF reference TrayApp.cs:
    /// 255-260) - a PrtScr press while a recording is active advances the SAME chrome state machine a
    /// click would (Setup -> BeginCapture, Capturing -> StopCaptureToReview, Reviewing -> Save),
    /// never a new capture. Marshals onto the UI thread the same way the WPF reference's own
    /// RequestPrtScrAction does, though in practice TrayApp.TriggerCapture (this method's only
    /// caller) already runs on the UI thread.</summary>
    public static void RequestPrtScrAction()
    {
        var active = s_active;
        if (active is null)
        {
            return;
        }
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => active.AdvanceOnPrtScr());
            return;
        }
        active.AdvanceOnPrtScr();
    }

    private void AdvanceOnPrtScr()
    {
        if (_session.IsSetup)
        {
            _session.BeginCapture();
        }
        else if (_session.IsCapturing)
        {
            _session.StopCaptureToReview();
        }
        else if (_session.IsReviewing)
        {
            Save();
        }
    }

    // ---------- Automation hooks (item 21f, AppShell/AutomationServer.cs) ----------

    public readonly record struct AutomationSnapshot(
        string Phase, RecordingFormat Format, RectPhysical SelectionVirtualDesktopPx,
        string EstimateText, GifSizePreset Preset, int Fps);

    public static AutomationSnapshot? GetAutomationSnapshot()
    {
        var active = s_active;
        if (active is null)
        {
            return null;
        }
        var s = active._session;
        var b = s.Monitor.BoundsPx;
        var sel = s.SelectionPx;
        var virtualPx = new RectPhysical(b.Left + sel.Left, b.Top + sel.Top, b.Left + sel.Right, b.Top + sel.Bottom);
        string phase = s.IsCapturing ? "Capturing" : s.IsReviewing ? "Reviewing" : "Setup";
        return new AutomationSnapshot(phase, s.Format, virtualPx, active._chrome.EstimateText, active._chrome.CurrentSizePreset, active._chrome.CurrentFps);
    }

    /// <summary>AutomationServer's `select` command while a recording is active - virtualDesktopPx is
    /// the wire protocol's own absolute-physical-pixel convention; converted to this (single-)
    /// monitor-relative before reaching RegionOutline, which shares RecordingSession's own
    /// monitor-relative convention (see that class's field doc comment).</summary>
    public static string? SetSelectionForAutomation(RectPhysical virtualDesktopPx)
    {
        var active = s_active;
        if (active is null)
        {
            return "no active recording session";
        }
        if (active._outline is null)
        {
            return "no region outline available on this platform/build";
        }
        var b = active._session.Monitor.BoundsPx;
        var monitorRelative = new RectPhysical(
            virtualDesktopPx.Left - b.Left, virtualDesktopPx.Top - b.Top,
            virtualDesktopPx.Right - b.Left, virtualDesktopPx.Bottom - b.Top);
        active._outline.SetSelectionForAutomation(monitorRelative);
        return null;
    }

    public static string? SetSizePresetForAutomation(GifSizePreset preset)
    {
        var active = s_active;
        if (active is null)
        {
            return "no active recording session";
        }
        if (!active._session.IsSetup)
        {
            return $"cannot change the size preset while recording is not in Setup";
        }
        active._chrome.InvokeSizePreset(preset);
        return null;
    }

    public static string? SetFpsForAutomation(int fps)
    {
        var active = s_active;
        if (active is null)
        {
            return "no active recording session";
        }
        if (!active._session.IsSetup)
        {
            return "cannot change fps while recording is not in Setup";
        }
        int min = active._session.Format == RecordingFormat.Gif ? RecordingSizeEstimator.GifMinFps : RecordingSizeEstimator.Mp4MinFps;
        int max = active._session.Format == RecordingFormat.Gif ? RecordingSizeEstimator.GifMaxFps : RecordingSizeEstimator.Mp4MaxFps;
        if (fps < min || fps > max)
        {
            return $"fps must be {min}-{max} for {active._session.Format}";
        }
        active._chrome.InvokeFps(fps);
        return null;
    }

    /// <summary>AutomationServer's `chrome`/`escape` commands - drives the same handler a real chrome
    /// button click would, after rejecting an action that button isn't valid for right now as an
    /// explicit error instead of the silent no-op a disabled button would give.</summary>
    public static string? InvokeChromeAction(string action)
    {
        var active = s_active;
        if (active is null)
        {
            return "no active recording session";
        }
        var s = active._session;
        switch (action)
        {
            case "start":
                if (!s.IsSetup) return "cannot start while recording is not in Setup";
                active._chrome.InvokeStartStop();
                return null;
            case "stop":
                if (!s.IsCapturing) return "cannot stop while recording is not Capturing";
                active._chrome.InvokeStartStop();
                return null;
            case "pause":
                if (!s.IsCapturing || s.IsPaused) return "cannot pause: not capturing, or already paused";
                active._chrome.InvokePauseResume();
                return null;
            case "resume":
                if (!s.IsPaused) return "cannot resume: not paused";
                active._chrome.InvokePauseResume();
                return null;
            case "save":
                if (!s.IsReviewing) return "cannot save while recording is not Reviewing";
                active._chrome.InvokeSave();
                return null;
            case "share":
                if (!s.IsReviewing) return "cannot share while recording is not Reviewing";
                active._chrome.InvokeShare();
                return null;
            case "copy":
                if (!s.IsReviewing) return "cannot copy while recording is not Reviewing";
                active._chrome.InvokeCopy();
                return null;
            case "copyagain":
                if (!s.AwaitingAnotherTakeChoice) return "cannot copy again: no finished take is waiting on a choice";
                if (s.FinishedTakePath is null) return "cannot copy again: this take has no file left to copy";
                active._chrome.InvokeCopyAgain();
                return null;
            case "saveagain":
                if (!s.AwaitingAnotherTakeChoice) return "cannot save again: no finished take is waiting on a choice";
                if (s.FinishedTakePath is null) return "cannot save again: this take has no file left to save";
                if (active._resavingFinishedTake) return "cannot save again: a save is already in progress";
                active._chrome.InvokeSaveAgain();
                return null;
            case "another":
                if (!s.AwaitingAnotherTakeChoice) return "cannot record another: no finished take is waiting on a choice";
                active._chrome.InvokeRecordAnother();
                return null;
            case "done":
                if (!s.AwaitingAnotherTakeChoice) return "cannot finish: no finished take is waiting on a choice";
                active._chrome.InvokeDone();
                return null;
            case "cancel":
                active._chrome.InvokeCancel();
                return null;
            default:
                return $"unknown chrome action \"{action}\"";
        }
    }

    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Init() => AppComposition.StartRecording = Start;
}
