using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using RoeSnip.Capture;
using RoeSnip.Core.Diagnostics;
using RoeSnip.Imaging;
// RoeSnip.csproj enables both UseWPF and UseWindowsForms, so System.Windows.Forms is in scope
// alongside System.Windows — alias the colliding name to WPF's Application.
using Application = System.Windows.Application;

namespace RoeSnip.Overlay;

/// <summary>Commands that can be raised from any monitor's OverlayWindow (keyboard) or from the
/// toolbar attached to the monitor that currently holds the selection — OverlayController treats
/// both sources identically. Cancel closes the whole overlay unconditionally (toolbar X button).
/// CancelStage is Esc's two-stage semantics: if any monitor has a snip in progress (selection /
/// annotations / drag), clear it and stay open; only with nothing active does it close the
/// overlay. (Esc's innermost stage — an active inline text edit — is consumed locally by
/// OverlayWindow.ProcessKeyCommand before CancelStage is ever raised.) ConfirmPlain is
/// Enter/double-click; Copy/Save/SaveHdr are the explicit Ctrl+C/Ctrl+S/toolbar-button requests.
/// Undo (Ctrl+Z) is handled locally inside OverlayWindow against its own AnnotationLayer and
/// never reaches the controller, since only the OS-focused window can receive it in the first
/// place — there's no cross-monitor ambiguity to resolve for it.</summary>
public enum OverlayCommand
{
    Cancel,
    CancelStage,
    ConfirmPlain,
    Copy,
    Save,
    SaveHdr,
    RecordMp4,
    RecordGif,
    // Sharing/* subsystem: the toolbar's plain Share button (default-provider upload). The
    // dropdown's per-provider picker is wired separately, through OverlayWindow's own
    // _onShareToProvider callback rather than through this enum — OverlayCommand is deliberately
    // payload-free (see e.g. onColorPicked's own analogous callback), and a provider id is a
    // payload. Handling this hands the render off to ShareFlowPresenter and then calls Finish()
    // immediately, exactly like every other command above — see OverlaySession.ShareCurrentSelection's
    // own doc comment (OverlaySession.ShareToSpecificProvider, the dropdown's own handler, follows
    // the identical close-immediately contract).
    Share,
}

public static class OverlayController
{
    /// <summary>One OverlayWindow per monitor; runs until the user cancels (Esc) or confirms
    /// (Enter / double-click / toolbar action). Returns null on cancel. On confirm, performs
    /// Copy/Save side effects itself (clipboard + PNG dialog) per DESIGN.md, then returns a
    /// populated OverlayResult. Matches the AppComposition.RunOverlay hook signature exactly.
    /// <paramref name="notifier"/> (Sharing/* subsystem addition) is the same ITrayNotifier
    /// RunCaptureFlowAsync already threads into StartRecording — the toolbar's Share button needs
    /// it too (clipboard-copy-the-URL balloon / honest error balloon), and this is the earliest
    /// point that reference is available to an OverlaySession; every OTHER caller of this hook
    /// (there is only one, Program.cs's RunCaptureFlowAsync) already has a notifier in hand for
    /// exactly this reason.</summary>
    public static Task<OverlayResult?> RunAsync(
        IReadOnlyList<(CapturedFrame Frame, SdrImage Preview)> monitors,
        RoeSnipSettings settings,
        ITrayNotifier? notifier = null)
    {
        if (monitors.Count == 0)
        {
            return Task.FromResult<OverlayResult?>(null);
        }

        EnsureApplication();

        var session = new OverlaySession(monitors, settings, OnColorPicked, pickOnlyMode: false, notifier);
        return session.RunAsync();
    }

    // ---------- Instant-response flash dimmer (r5-latency) ----------
    //
    // The flash API lives here (not in Program.cs's capture flow) because RunCaptureFlowAsync
    // only hands this package already-captured frames — far too late for an instant response.
    // TrayApp.TriggerCapture (the actual hotkey/tray/pipe entry point) calls TryShowFlash BEFORE
    // kicking off AppComposition.RunCaptureFlowAsync; each monitor's flash is then hidden as its
    // real overlay window renders (OverlaySession.OnOverlayContentRendered), and TrayApp's
    // ObserveCaptureTask calls ReleaseFlash in a finally as the backstop that guarantees the
    // flash never outlives the flow on any no-overlay exit path (capture failed on every monitor,
    // CaptureGate busy, RunOverlay unavailable, unexpected exception).

    private static OverlaySession? s_activeSession;   // UI thread only
    private static int s_flashUsers;                  // UI thread only — capture flows that showed the flash
    private static volatile bool s_flashCancelRequested;
    private static long s_responseStartTimestamp;     // Stopwatch timestamp of the last real trigger

    /// <summary>True while a capture session is actually on screen and interactive. Review fix:
    /// exposed so OverlayWindowPool.ScheduleReprovision's deferred (ContextIdle) callback can apply
    /// the exact same "never rebuild while a session is live" guard PrewarmOverlayPool already
    /// enforces at every OTHER pool-mutating entry point — a reprovision scheduled by one session's
    /// Finish() can otherwise still be pending when a rapid re-trigger opens the NEXT session, and
    /// firing a full pool rebuild (CloseAllParked + N re-constructions) mid-session would steal
    /// UI-thread time from live user interaction. UI thread only.</summary>
    internal static bool IsSessionActive => s_activeSession is not null;

    /// <summary>Honest trigger-based latency instrumentation (r5-latency, item S2): stamps the
    /// moment the user's actual trigger happened (hotkey/tray click/pipe signal), independent of
    /// whether the flash dimmer ends up showing anything. Previously TryShowFlash itself stamped
    /// this timestamp, which meant a flash failure (or the flash being disabled) silently measured
    /// "hotkey-to-overlay" latency from whenever the flash WOULD have started rather than from the
    /// real trigger — call this as the very first statement of TrayApp.TriggerCapture instead, so
    /// every latency log downstream (first-overlay-visible / all-overlays-visible) is honest about
    /// what the user actually experienced.</summary>
    internal static void MarkTriggerTimestamp() =>
        s_responseStartTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();

    /// <summary>Discards a pending trigger timestamp without attributing it to any session (review
    /// fix, r5-latency S2): MarkTriggerTimestamp is called unconditionally as TriggerCapture's first
    /// statement, before it's known whether the resulting RunCaptureFlowAsync call will ever reach
    /// an OverlaySession. Several of its exit paths (RunOverlay unavailable, CaptureGate exhausted
    /// after both wait loops, capture failed on every monitor, an exception before the overlay is
    /// reached) never construct one, so s_responseStartTimestamp would otherwise sit there stale
    /// until whatever OverlaySession runs NEXT silently inherits it — including an unrelated
    /// pick-mode session, which must never attribute its latency to a hotkey trigger it didn't own.
    /// Wired to AppComposition.ClearPendingOverlayTrigger (see the ModuleInitializer below) so
    /// Program.cs (WP-A) can call it from those dead-end paths without taking a direct WP-B
    /// dependency. Idempotent — safe to call even when nothing is pending.</summary>
    internal static void ClearPendingTrigger() => s_responseStartTimestamp = 0;

    /// <summary>Pre-creates the per-monitor flash windows (TrayApp warmup / display-change hook;
    /// must run on the UI thread). AllowsTransparency windows are expensive to create but cheap to
    /// re-Show, so paying creation here keeps the hotkey's hotkey-to-dim time to a Show().</summary>
    public static void PrewarmFlash(IReadOnlyList<MonitorInfo> monitors)
    {
        if (monitors.Count == 0)
        {
            return;
        }
        if (s_activeSession is not null || FlashDimmer.AnyVisible)
        {
            return; // never recreate windows out from under a live flash/session
        }
        try
        {
            EnsureApplication();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            FlashDimmer.EnsureCreated(monitors);
            FileLog.Write(
                $"RoeSnip: flash dimmer windows ready in {watch.ElapsedMilliseconds} ms ({monitors.Count} monitors)");
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: flash dimmer pre-creation failed (non-fatal): {ex.Message}");
        }
    }

    /// <summary>Pre-builds the per-monitor overlay window POOL (r5-latency round 2, D1/D4 — see
    /// OverlayWindowPool's own doc comment) — the single-use, already-first-rendered windows
    /// OverlaySession.RunAsync claims instead of paying construction/Show/first-render on the hot
    /// path. Called right after PrewarmFlash from the same TrayApp warmup step (and again from the
    /// DisplaySettingsChanged path), matching that method's own ordering/thread-marshalling
    /// contract. Guarded the same way: never rebuilds while a session is actually live, since a
    /// rebuild would otherwise spend UI-thread time that session needs, even though it could never
    /// touch that session's own windows (those were already removed from the pool when it started —
    /// see OverlayWindowPool.TryTake).</summary>
    public static void PrewarmOverlayPool(IReadOnlyList<MonitorInfo> monitors)
    {
        if (monitors.Count == 0 || s_activeSession is not null)
        {
            return;
        }
        try
        {
            EnsureApplication();
            OverlayWindowPool.EnsureBuilt(monitors);
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: overlay pool pre-creation failed (non-fatal): {ex.Message}");
        }
    }

    /// <summary>Shows the flash dim on every monitor. Returns false (and shows nothing) when an
    /// overlay session is already on screen — dimming over a live overlay would be wrong — or when
    /// showing failed; the caller must call <see cref="ReleaseFlash"/> exactly once iff this
    /// returned true. UI thread only.</summary>
    public static bool TryShowFlash(IReadOnlyList<MonitorInfo> monitors)
    {
        if (monitors.Count == 0 || s_activeSession is not null)
        {
            return false;
        }
        // Flash dimmer ENABLED by default (r5-latency). It was previously disabled-by-default
        // (opt-in via ROESNIP_USE_FLASH=1) because a luma-sampler trace of the flash-to-overlay
        // handoff showed bright -> dim -> BLACK(0) -> bright — a visible black frame plus a broken
        // final state. That black frame was NOT caused by the two-window handoff itself: it was
        // WPF's own cold-start behavior of clearing a new window's surface to black and letting DWM
        // present that before the render thread produces the first real composited frame. d6b5ec7
        // root-caused and fixed exactly this, independently of the flash (see OverlayWindow's
        // OnSourceInitialized: the window is shown off-screen at its real size first, and the frozen
        // preview is painted into Background as well as PreviewImage, so even a background-only
        // first compositor frame already shows correct pixels — only once ContentRendered fires
        // does OnFirstContentRendered snap it onto the real monitor). With that fix in place the
        // flash-to-overlay swap is a clean monotonic dim -> dim (the overlay's first real frame is
        // opaque and pixel-identical to the flash underneath it), so the flash is re-enabled here.
        // S3 additionally prewarms each flash window's own first composite at warmup (paying the
        // ~150 ms first-Show cost before any hotkey). A SECOND, independent luma-sampler pass (past
        // d6b5ec7's black-frame fix) then caught a real bright-REBOUND bug at this exact handoff:
        // OverlaySession subscribes to window.ContentRendered before window.Show(), while
        // OverlayWindow's own on-screen-move handler subscribes second (from OnSourceInitialized,
        // which Show() runs), so hiding the flash from the FIRST multicast handler fired while the
        // real window was still parked off-screen — a luma-measured ~18-30 ms bright flash of the
        // real (still-dim, but uncovered) desktop. Fixed by deferring the flash-hide to
        // DispatcherPriority.Background from OnOverlayContentRendered (see that method's doc
        // comment) so it always runs after the window's own on-screen move. Luma-verified clean
        // (3 consecutive runs, zero rebound samples between first-dim and Esc) as of that fix.
        // ROESNIP_NO_FLASH=1 remains as a diagnostic escape hatch back to the single-window
        // (capture -> show overlay) path if a visible artifact ever turns up on other hardware.
        if (Environment.GetEnvironmentVariable("ROESNIP_NO_FLASH") == "1")
        {
            return false;
        }
        try
        {
            s_flashCancelRequested = false;
            EnsureApplication();
            FlashDimmer.ShowAll(monitors);
            s_flashUsers++;
            // Flash-phase Esc coverage, focus-independent (see FlashEscapeHook's doc): alive until
            // ReleaseFlash or the real session's SessionKeyboardHook takes over. Install failure is
            // non-fatal inside the ctor itself.
            s_flashEscapeHook ??= new FlashEscapeHook(OnFlashEscape);
            return true;
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: flash dimmer show failed (non-fatal): {ex.Message}");
            try { FlashDimmer.HideAll(); } catch { /* best-effort */ }
            return false;
        }
    }

    /// <summary>Flash-phase Esc hook lifetime (UI thread only): installed by TryShowFlash, removed
    /// on whichever comes first of flow end (ReleaseFlash), flash-phase cancel (OnFlashEscape), or
    /// the OverlaySession opening (hand-off to its full SessionKeyboardHook — the ctor calls this
    /// so there is never a double-hook window).</summary>
    private static FlashEscapeHook? s_flashEscapeHook;

    internal static void DisposeFlashEscapeHook()
    {
        s_flashEscapeHook?.Dispose();
        s_flashEscapeHook = null;
    }

    /// <summary>Backstop pair to a successful <see cref="TryShowFlash"/>: called from
    /// TrayApp.ObserveCaptureTask's finally once the whole capture flow has ended. Counted rather
    /// than boolean so a second trigger that showed the (already-visible) flash and then bounced
    /// off the CaptureGate can't hide it out from under the first flow's still-pending capture.</summary>
    public static void ReleaseFlash()
    {
        if (s_flashUsers == 0)
        {
            return;
        }
        if (--s_flashUsers == 0)
        {
            DisposeFlashEscapeHook();
            try { FlashDimmer.HideAll(); }
            catch (Exception ex) { FileLog.Write($"RoeSnip: flash dimmer hide failed: {ex.Message}"); }
            // No-op unless focus is genuinely stranded on a parked flash window (a flow that never
            // opened a session — capture failed/timed out — with a WON foreground claim): hand it
            // back so the keyboard doesn't look dead until the user clicks another app.
            FlashDimmer.TryRestoreForegroundFromFlash();
        }
    }

    /// <summary>Esc pressed while a flash window had focus. If the real session is already up
    /// (the keydown was queued behind the blocking capture stretch and only dispatched now),
    /// route it as a normal stage-cancel; otherwise flag the pending flow so its session cancels
    /// the moment it starts, and drop the dim immediately for responsiveness.</summary>
    internal static void OnFlashEscape()
    {
        if (s_activeSession is not null)
        {
            s_activeSession.CancelStageFromFlash();
            return;
        }
        s_flashCancelRequested = true;
        DisposeFlashEscapeHook();
        try { FlashDimmer.HideAll(); }
        catch (Exception ex) { FileLog.Write($"RoeSnip: flash dimmer hide failed: {ex.Message}"); }
        // The flash's foreground claim may have WON — without this, focus stays on a parked,
        // key-swallowing flash window after a flash-phase cancel and the keyboard looks dead.
        FlashDimmer.TryRestoreForegroundFromFlash();
    }

    private static bool ConsumeFlashCancelRequest()
    {
        bool requested = s_flashCancelRequested;
        s_flashCancelRequested = false;
        return requested;
    }

    /// <summary>Show-as-ready ordering (r5-latency): the monitor under the cursor is where the
    /// user is looking and about to interact, so its window is constructed, shown and activated
    /// first; the rest follow. Falls back to the given order when the cursor position is
    /// unavailable or on no captured monitor.</summary>
    private static IReadOnlyList<(CapturedFrame Frame, SdrImage Preview)> OrderCursorMonitorFirst(
        IReadOnlyList<(CapturedFrame Frame, SdrImage Preview)> monitors)
    {
        if (monitors.Count < 2)
        {
            return monitors;
        }
        try
        {
            if (!FlashDimmer.TryGetCursorPos(out int cx, out int cy))
            {
                return monitors;
            }
            int cursorIndex = -1;
            for (int i = 0; i < monitors.Count; i++)
            {
                var b = monitors[i].Frame.Monitor.BoundsPx;
                if (cx >= b.Left && cx < b.Right && cy >= b.Top && cy < b.Bottom)
                {
                    cursorIndex = i;
                    break;
                }
            }
            if (cursorIndex <= 0)
            {
                return monitors;
            }
            var reordered = new List<(CapturedFrame Frame, SdrImage Preview)>(monitors);
            var cursorMonitor = reordered[cursorIndex];
            reordered.RemoveAt(cursorIndex);
            reordered.Insert(0, cursorMonitor);
            return reordered;
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: cursor-monitor ordering failed (non-fatal): {ex.Message}");
            return monitors;
        }
    }

    // ---------- Automation hooks (App/AutomationServer.cs) ----------
    //
    // The dev-gated automation channel drives the SAME session the mouse/keyboard already drive -
    // these are thin wrappers only because OverlaySession is a private nested class and its own
    // OnCommand/_windows fields are private; every one of them calls straight into that existing
    // logic (SetSelection, OnCommand), never duplicates it. All take/return virtual-desktop
    // physical pixels (monitor origin folded in) to match AutomationServer's wire protocol.

    /// <summary>The active session's current selection, or null if nothing is selected (or no
    /// session is active).</summary>
    internal static RectPhysical? GetSelectionForAutomation() => s_activeSession?.GetSelectionForAutomation();

    /// <summary>Sets the selection on whichever monitor's window contains
    /// <paramref name="virtualDesktopPx"/>'s top-left corner, through the exact SetSelection path
    /// Ctrl+A (select-all) uses, so the dim mask and toolbar placement update exactly as a
    /// completed drag would leave them. Null on success, else an error string.
    ///
    /// Reads s_activeSession into a local once and branches on IT rather than using
    /// "s_activeSession?.Method(...) ?? "no active overlay session"" — see the identical fix and
    /// its doc comment on RecordingController's own automation wrappers (commit d56ad19). Every
    /// method here returns "null on success, else an error string", so "?." simply FORWARDS
    /// whatever the underlying call returns, including a legitimate null success; "??" then can't
    /// tell "the receiver was null" apart from "the receiver was NOT null and its method returned
    /// null to mean success" — both look like a null left-hand side, so the fallback string fired
    /// on every successful call too, reporting ok:false while the mutation landed anyway.</summary>
    internal static string? SetSelectionForAutomation(RectPhysical virtualDesktopPx)
    {
        var session = s_activeSession;
        return session is null ? "no active overlay session" : session.SetSelectionForAutomation(virtualDesktopPx);
    }

    /// <summary>Invokes the same OverlayCommand.RecordMp4/RecordGif the toolbar's Record menu
    /// choices raise. See SetSelectionForAutomation's doc comment for why this reads
    /// s_activeSession into a local and branches on it instead of "?." + "??".</summary>
    internal static string? RecordForAutomation(RecordingFormat format)
    {
        var session = s_activeSession;
        return session is null ? "no active overlay session" : session.RecordForAutomation(format);
    }

    /// <summary>Invokes the same OverlayCommand.Cancel the toolbar's X button raises. See
    /// SetSelectionForAutomation's doc comment for why this reads s_activeSession into a local and
    /// branches on it instead of "?." + "??" — CancelForAutomation always returns null on success,
    /// so the old pattern reported ok:false on literally every successful cancel.</summary>
    internal static string? CancelForAutomation()
    {
        var session = s_activeSession;
        return session is null ? "no active overlay session" : session.CancelForAutomation();
    }

    /// <summary>Cross-monitor selection (multimon-selection): Copy/Save had no automation entry
    /// point at all before this — the toolbar's Copy button raises OverlayCommand.Copy exactly like
    /// this does, but Save's real path (TryShowSaveDialog) pops a modal SaveFileDialog, which must
    /// never happen from a headless automation call (it would hang the pipe waiting for a human).
    /// "save" therefore REQUIRES an explicit path and writes directly via PngWriter, skipping the
    /// dialog entirely — same production render/write calls Confirm/ConfirmSpanning already use,
    /// just without the interactive picker. Added specifically so a spanning selection's Copy/Save
    /// path — the one place this feature has no other test lever — can be driven and verified
    /// end-to-end; see docs/DESIGN-MULTIMON-SELECTION.md. See SetSelectionForAutomation's doc
    /// comment for why this reads s_activeSession into a local and branches on it instead of "?."
    /// + "??".</summary>
    internal static string? ConfirmForAutomation(string action, string? path)
    {
        var session = s_activeSession;
        return session is null ? "no active overlay session" : session.ConfirmForAutomation(action, path);
    }

    // ---------- Standalone ColorPickerWindow (UX round 2) ----------

    private static ColorPickerWindow? s_colorPickerWindow;

    /// <summary>A plain click on the overlay (not a drag, not on the toolbar/handles/selection)
    /// cancels the whole snip and opens/updates this singleton window with the picked color,
    /// per the round-2 spec. Recreated on demand — its Closed handler nulls this out — rather than
    /// Hide()-and-reuse, since its own persisted state (recents, format visibility) already lives in
    /// settings, so a fresh instance picks up exactly where the old one left off.</summary>
    private static void OnColorPicked(PickedColorInfo info)
    {
        EnsureApplication();

        if (s_colorPickerWindow is null)
        {
            var window = new ColorPickerWindow(TriggerPickModeCapture);
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(s_colorPickerWindow, window))
                {
                    s_colorPickerWindow = null;
                }
            };
            s_colorPickerWindow = window;
        }

        bool wasVisible = s_colorPickerWindow.IsVisible;
        s_colorPickerWindow.ApplyPick(info);
        if (!wasVisible)
        {
            s_colorPickerWindow.Show();
        }
        s_colorPickerWindow.Activate();
    }

    // Reentrancy: pick-mode shares the single process-wide CaptureGate with the hotkey/tray/pipe
    // capture flow, so a hotkey press can't stack an overlay set over a pick-mode session (or
    // vice versa) — two overlay stacks would screenshot each other's UI.

    /// <summary>Re-launches the capture overlay in pick-only mode so the user's next click picks a
    /// new color into the same ColorPickerWindow. Deliberately implemented here rather than reused
    /// via AppComposition.RunCaptureFlowAsync: that hook's signature
    /// (Func&lt;..., RoeSnipSettings, Task&lt;OverlayResult?&gt;&gt;) is fixed (Program.cs is
    /// additive-only for the settings record per the round-2 brief) and has no notion of pick-only
    /// mode, and pick mode never produces a Copy/Save/HDR-export OverlayResult to route back through
    /// it anyway — it only ever ends via OnColorPicked (a pick) or a plain cancel (Esc).</summary>
    private static void TriggerPickModeCapture()
    {
        if (!CaptureGate.TryEnter())
        {
            FileLog.Write("RoeSnip: a capture is already in progress; ignoring pick request.");
            return;
        }

        _ = RunPickModeCaptureAsync();
    }

    private static async Task RunPickModeCaptureAsync()
    {
        try
        {
            var settings = AppComposition.LoadSettings?.Invoke() ?? RoeSnipSettings.Default;

            // Same off-the-UI-thread hop + deadline as RunCaptureFlowAsync's capture (post-sleep
            // stall fix — see the doc there): pick mode shares the identical CaptureAll stretch and
            // previously froze the pump the identical way when a post-resume capture stalled.
            var captureService = new CaptureService();
            var captureTask = Task.Run(() => captureService.CaptureAll());
            if (await Task.WhenAny(captureTask, Task.Delay(AppComposition.CaptureDeadline)) != captureTask)
            {
                FileLog.Write(
                    $"RoeSnip: pick-mode capture did not complete within " +
                    $"{AppComposition.CaptureDeadline.TotalSeconds:0} s; abandoning this pick request.");
                _ = captureTask.ContinueWith(static t =>
                {
                    if (t.Status == TaskStatus.RanToCompletion)
                    {
                        foreach (var frame in t.Result)
                        {
                            frame.Dispose();
                        }
                    }
                    else
                    {
                        _ = t.Exception; // observe so the late fault can't crash the finalizer thread
                    }
                }, TaskScheduler.Default);
                return;
            }

            var frames = await captureTask;
            if (frames.Count == 0)
            {
                FileLog.Write("RoeSnip: pick-mode capture failed on every monitor.");
                return;
            }

            try
            {
                // Fully qualified (rather than a "using RoeSnip.Color;") deliberately — several
                // sibling Overlay/* files alias the unrelated System.Windows.Media.Color as "Color"
                // and a bare namespace import here would create exactly that ambiguity risk.
                var toneMapOpts = new RoeSnip.Color.ToneMapOptions(
                    Knee: settings.ToneMapKneeOverride ?? 0.90,
                    PeakOverride: settings.ToneMapPeakOverride);

                var monitorsWithPreview = new List<(CapturedFrame Frame, SdrImage Preview)>(frames.Count);
                // Off the UI thread like the capture above — full-res tonemaps are pure compute.
                await Task.Run(() =>
                {
                    foreach (var frame in frames)
                    {
                        monitorsWithPreview.Add((frame, SdrImage.FromCapturedFrame(frame, toneMapOpts)));
                    }
                });

                EnsureApplication();
                var session = new OverlaySession(monitorsWithPreview, settings, OnColorPicked, pickOnlyMode: true);
                await session.RunAsync();
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
            FileLog.Write($"RoeSnip: pick-mode capture failed: {ex.Message}");
        }
        finally
        {
            CaptureGate.Exit();
            // Pick-mode never goes through TrayApp.ObserveCaptureTask, so it schedules its own
            // post-flow memory trim (same ApplicationIdle deferral — see IdleMemoryTrimmer).
            IdleMemoryTrimmer.Schedule(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        }
    }

    /// <summary>RunAsync is invoked from the tray app's own STA thread, which pumps Win32 messages
    /// via its WinForms Application.Run() message loop. That pump is sufficient to dispatch input
    /// to our HWNDs and to the WPF Dispatcher's own message-only window even without a running
    /// System.Windows.Application — but several WPF facilities (pack URI / component resource
    /// resolution used by InitializeComponent, Application.Current-based resource lookups) expect
    /// one to exist. If the host has already created one (e.g. a future all-WPF host, or a test
    /// harness), we must not create a second one or touch its ShutdownMode. We deliberately never
    /// call Application.Run() here — there is no need to; the ambient message pump already
    /// dispatches to our windows — and OnExplicitShutdown ensures closing all overlay windows
    /// doesn't implicitly tear down a process-wide Application we created just for this session.</summary>
    private static void EnsureApplication()
    {
        if (Application.Current is null)
        {
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        }
    }

    /// <summary>Owns the windows and state for a single overlay session (one hotkey press / tray
    /// "Capture" click).</summary>
    private sealed class OverlaySession
    {
        private readonly RoeSnipSettings _settings;
        private readonly IReadOnlyList<(CapturedFrame Frame, SdrImage Preview)> _monitors;
        private readonly Action<PickedColorInfo> _onColorPicked;
        private readonly bool _pickOnlyMode;
        // Sharing/* subsystem: null for pick-mode (TriggerPickModeCapture never passes one — that
        // flow has no toolbar/Share button at all) and for any other future direct OverlaySession
        // caller that doesn't pass one; ShareCurrentSelection treats a null notifier the same way
        // every other ITrayNotifier consumer in this codebase does ("?." — the upload still runs,
        // it just can't show a balloon for the result).
        private readonly ITrayNotifier? _notifier;
        private readonly List<OverlayWindow> _windows = new();
        private readonly TaskCompletionSource<OverlayResult?> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Session-scoped reliability layer (a) — see OverlayInputInterop.cs's SessionKeyboardHook
        // doc comment for the full root-cause writeup. Installed here ("when the session opens"),
        // disposed exactly once from Finish() ("removed unconditionally... all paths").
        private readonly SessionKeyboardHook _keyboardHook;

        private OverlayWindow? _activeWindow;
        private bool _finished;
        private int _renderedCount;
        private long _responseBaseTimestamp;

        // ---------- Cross-monitor selection (multimon-selection) ----------
        //
        // Both null for every ordinary, single-monitor selection — the pre-existing per-window
        // _selectionPx-based code paths (Confirm/Record/GetSelectionForAutomation's own
        // _windows.FirstOrDefault(w => w.SelectionPx is not null)) are what run in that case,
        // completely unchanged. These two fields are only ever non-null while a NewSelection,
        // SpanningResize, or SpanningMove drag's candidate rect has been distributed across ≥2
        // monitors — see OnSpanningCandidate. See docs/DESIGN-MULTIMON-SELECTION.md for the full
        // design.
        private RectPhysical? _spanningVirtual;
        private OverlayWindow? _spanningPrimaryWindow;
        private readonly RectPhysical _virtualDesktopBounds;

        public OverlaySession(
            IReadOnlyList<(CapturedFrame Frame, SdrImage Preview)> monitors,
            RoeSnipSettings settings,
            Action<PickedColorInfo> onColorPicked,
            bool pickOnlyMode,
            ITrayNotifier? notifier = null)
        {
            _settings = settings;
            _onColorPicked = onColorPicked;
            _pickOnlyMode = pickOnlyMode;
            _notifier = notifier;
            _monitors = OrderCursorMonitorFirst(monitors);
            _virtualDesktopBounds = ComputeVirtualDesktopBounds(_monitors);

            // Hand-off from the flash phase: the Esc-only flash hook (if any) is removed before the
            // session hook installs, so there is never a moment with two LL hooks (or an Esc routed
            // to the stale flash path once a real session exists).
            DisposeFlashEscapeHook();

            // Installed before any window is constructed/shown/receives input, so session keys are
            // covered for the session's entire visible lifetime. Every path out of RunAsync —
            // including an exception constructing or showing any window — funnels through Finish(),
            // which disposes this hook unconditionally.
            _keyboardHook = new SessionKeyboardHook(() => _activeWindow);
        }

        public Task<OverlayResult?> RunAsync()
        {
            s_activeSession = this;
            _responseBaseTimestamp = TakeResponseBaseTimestamp(_pickOnlyMode);

            // Esc pressed while only the flash dimmer was up: the user already cancelled this
            // capture before the overlay could appear — honor it instead of flashing a full
            // overlay set for a frame.
            if (ConsumeFlashCancelRequest())
            {
                Finish(null);
                return _completion.Task;
            }

            // Show-as-ready (r5-latency): construct+show the cursor monitor's window FIRST and
            // flush its layout/render before even constructing the remaining windows, so the
            // monitor the user is looking at swaps from flash dim to the real overlay as early as
            // possible (previously: construct all, then show all — the first visible overlay paid
            // for every monitor's InitializeComponent/BAML load). Each monitor's flash dimmer is
            // hidden per-monitor from ContentRendered, i.e. only after that monitor's real window
            // has genuinely rendered — zero-gap replacement.
            //
            // The Dispatcher flush below runs a nested message pump, which CAN dispatch input —
            // an Esc there routes through OnFlashEscape/the session keys into Finish(). Hence the
            // _finished checks: the loop must stop constructing/showing windows the moment the
            // session dies under it (windows shown after Finish would be stranded topmost forever).
            var watch = System.Diagnostics.Stopwatch.StartNew();
            int pooledAtStart = OverlayWindowPool.Count;
            int poolHits = 0;
            try
            {
                foreach (var (frame, preview) in _monitors)
                {
                    if (_finished)
                    {
                        break;
                    }

                    // r5-latency round 2 (D3): a pool hit skips construction/Show/first-render
                    // entirely — that window was already built, Show()n and first-rendered (against
                    // a placeholder) back when the pool was provisioned. Initialize() swaps in this
                    // session's real content, a Loaded-priority flush pushes it through one more
                    // render pass, and MoveOnScreen does the actual on-screen handoff directly —
                    // there is no ContentRendered to wait on here (it already fired once, at
                    // pool-build time, and never fires again for this window; see OverlayWindow.
                    // OnFirstContentRendered). OnOverlayContentRendered is called directly instead,
                    // reusing its exact latency-log and deferred-flash-hide logic (see that method).
                    var pooled = OverlayWindowPool.TryTake(frame.Monitor);
                    OverlayWindow window;
                    if (pooled is not null)
                    {
                        poolHits++;
                        window = pooled;
                        // Review fix: register with Finish()'s cleanup (Closed handler + _windows)
                        // BEFORE calling Initialize() — Initialize() is not trivial (ToBitmapSource
                        // plus two Win32 interop calls to clear WS_EX_TOOLWINDOW) and TryTake already
                        // permanently removed this window from the pool above. If Initialize() were
                        // to throw with the window not yet in _windows, it would be neither in the
                        // pool nor in _windows for Finish()'s cleanup loop to find — a permanently
                        // orphaned, still-parked, topmost/capture-excluded HWND. Adding first means
                        // any such exception still reaches Finish() -> CloseOverlay() on this window
                        // like every other exit path.
                        window.Closed += (_, _) => Finish(null);
                        _windows.Add(window);
                        window.Initialize(
                            frame, preview, _settings, OnActivatedByMouse, OnSelectionStarted,
                            OnSpanningCandidate, FinalizeNewSelectionDrag, OnCommand,
                            _onColorPicked, ShareToSpecificProvider, _pickOnlyMode);
                        window.Dispatcher.Invoke(static () => { }, System.Windows.Threading.DispatcherPriority.Loaded);
                        window.MoveOnScreen();
                        OnOverlayContentRendered(window);
                    }
                    else
                    {
                        window = new OverlayWindow(
                            frame, preview, _settings, OnActivatedByMouse, OnSelectionStarted,
                            OnSpanningCandidate, FinalizeNewSelectionDrag, OnCommand,
                            _onColorPicked, ShareToSpecificProvider, _pickOnlyMode);
                        window.Closed += (_, _) => Finish(null);
                        window.ContentRendered += (_, _) => OnOverlayContentRendered(window);
                        // NOTE (anti-flicker): the overlay window is OPAQUE (Background is the frozen
                        // preview, PreviewImage on top, DimPath dim on top of that) — so once it paints
                        // AND is on-screen it fully OCCLUDES the flash dimmer beneath it. There is no
                        // dim-stacking to manage: the overlay's first frame already equals the flash
                        // (preview == live screen + matching dim), so it lands seamlessly and the flash
                        // is hidden invisibly from OnOverlayContentRendered. (Earlier crossfade/dim-fade
                        // attempts were wrong: fading the opaque overlay's own dim in made it paint
                        // BRIGHT first, causing the dim→bright→dim double flicker the user reported.)
                        // Bright-rebound fix (flicker gate — see OnOverlayContentRendered's own doc
                        // comment): "paints" and "is on-screen" are NOT the same moment — ContentRendered
                        // fires while the window is still parked off-screen (OffScreenX), and only
                        // OverlayWindow's own OnFirstContentRendered handler (subscribed second, so it
                        // runs after this session's ContentRendered handler) actually moves it on-screen.
                        // Hiding the flash must therefore be deferred past that on-screen move, or the
                        // flash disappears a beat before the overlay is there to occlude it — a bright
                        // rebound, luma-measured at ~18-30 ms.
                        _windows.Add(window);
                        window.Show();
                    }

                    if (_activeWindow is null)
                    {
                        _activeWindow = window;
                        // Push the first (cursor) window's pending layout/render through before
                        // constructing the rest. Loaded sits just below Render, so this drains
                        // exactly the layout+render queue. (A harmless redundant no-op flush for a
                        // pool hit, which already flushed once above — kept for uniformity with the
                        // fallback path rather than special-cased away.)
                        window.Dispatcher.Invoke(static () => { }, System.Windows.Threading.DispatcherPriority.Loaded);
                    }
                }

                FileLog.Write(
                    $"RoeSnip: overlay pool: {poolHits} hit, {_monitors.Count - poolHits} miss " +
                    $"({pooledAtStart} pooled at session start)");

                if (!_finished && _activeWindow is not null)
                {
                    // Session-start activation ladder (item 3a) — the same three-tier escalation
                    // used when a text edit opens, so a hotkey/pipe-triggered session (which never
                    // had a real OS foreground grant to begin with — see OverlayInputInterop.cs)
                    // starts out with the best foreground/focus this process can obtain. The
                    // cursor monitor's window (shown first) is the one activated: it's where the
                    // user is about to interact; mouse-enter re-activates others as always.
                    //
                    // Review fix: invalidate the flash's own best-effort foreground claim (see
                    // FlashDimmer.ShowAll) right before staking this one — without it, a flash
                    // SetForegroundWindow call delayed by thread-pool/AV interference could
                    // complete AFTER this Activate() and silently steal keyboard focus back onto
                    // the (still on-screen until its own deferred hide) flash window.
                    FlashDimmer.InvalidateForegroundClaim();
                    ForegroundActivator.Activate(_activeWindow, "session-start");
                }
            }
            catch
            {
                // Whatever partially came up must be torn down (windows closed, hook removed,
                // flash dimmers hidden) rather than left stranded topmost on screen with no way
                // for the user to dismiss it (audit finding C.3) — Finish is the single terminal
                // point that does all of that, idempotently.
                Finish(null);
                throw;
            }

            FileLog.Write(
                $"RoeSnip: overlay windows constructed+shown in {watch.ElapsedMilliseconds} ms " +
                $"({_windows.Count} monitors, cursor monitor first)");

            return _completion.Task;
        }

        /// <summary>Latency logs measure from the real trigger (TrayApp.TriggerCapture's
        /// MarkTriggerTimestamp call) when this session was hotkey/tray/pipe-initiated, else from
        /// session start (pick mode, which never calls MarkTriggerTimestamp, and any other direct
        /// caller). Consume-on-read (zeroes the field): a stale timestamp left over from a PREVIOUS
        /// trigger must never be attributed to this session, and reading it unconditionally
        /// (formerly gated on FlashDimmer.AnyVisible) means the flash being disabled/failed/absent
        /// no longer silently falls back to session-start timing for a real hotkey press — S1
        /// re-enabled the flash by default, but this fallback still matters for ROESNIP_NO_FLASH=1
        /// and any flash-show failure.
        ///
        /// Review fix: pickOnlyMode sessions never read the shared field at all, even if one happens
        /// to be pending — TriggerPickModeCapture never calls MarkTriggerTimestamp by design (see its
        /// own doc comment), but the shared s_responseStartTimestamp field has no notion of WHICH
        /// trigger it belongs to, so without this guard a pick-mode session could silently inherit an
        /// unrelated, possibly stale, hotkey-press timestamp (e.g. one left behind by an earlier
        /// dropped/failed trigger) and log a bogus latency figure for a feature that was never
        /// hotkey-initiated in the first place.</summary>
        private static long TakeResponseBaseTimestamp(bool pickOnlyMode)
        {
            if (pickOnlyMode)
            {
                return System.Diagnostics.Stopwatch.GetTimestamp();
            }

            long triggerTimestamp = s_responseStartTimestamp;
            s_responseStartTimestamp = 0;
            return triggerTimestamp != 0
                ? triggerTimestamp
                : System.Diagnostics.Stopwatch.GetTimestamp();
        }

        /// <summary>Per-monitor flash-to-overlay handoff. Also the source of the
        /// "first-overlay-visible"/"all-overlays-visible" latency logs, which are stamped HERE
        /// (ContentRendered — the window's first real render pass) since that's the honest
        /// "content is ready" moment, even though the actual flash-hide is deferred a beat further
        /// — see below.
        ///
        /// Flicker-gate fix (bright rebound at the flash-to-overlay handoff): this session
        /// subscribes to window.ContentRendered at construction time, BEFORE window.Show() — while
        /// OverlayWindow's own OnFirstContentRendered subscribes SECOND, from inside
        /// OnSourceInitialized (which Show() runs synchronously). WPF multicast events invoke
        /// subscribers in subscription order, so at the moment THIS method runs, the window is
        /// still parked off-screen (OverlayWindow.OffScreenX) — its own on-screen SetWindowPos
        /// hasn't happened yet. Hiding the flash right here would uncover the (still-dim, but very
        /// real) desktop underneath for the beat until OnFirstContentRendered moves the window
        /// on-screen — a luma sampler measured this as a reproducible ~18-30 ms bright rebound
        /// exactly at first-overlay-visible. Deferring FlashDimmer.HideForMonitor to
        /// DispatcherPriority.Background pushes it past the REST of this synchronous ContentRendered
        /// dispatch (which runs OnFirstContentRendered's on-screen SetWindowPos right after this
        /// handler returns, still within the same call stack) and past the dispatcher's next
        /// Render-priority pass, so by the time the flash actually disappears the real, on-screen,
        /// opaque overlay is already there to occlude it. Hiding late is always invisible (the
        /// overlay is opaque and the flash is itself capture-excluded, so a brief overlap can never
        /// leak into a screenshot); hiding early is exactly the bug this fixes.
        ///
        /// Torn-down-session NRE guard (found via rapid automated trigger/escape cycling): for a
        /// non-pooled window this method is reached via `window.ContentRendered += (_, _) =>
        /// OnOverlayContentRendered(window)`, subscribed in RunAsync BEFORE window.Show(). WPF does
        /// not raise ContentRendered synchronously — it's queued and fires on a later dispatcher
        /// pass once the render thread has actually composited a frame. A fast enough Cancel/
        /// CancelStage-to-empty (Esc arriving before that render pass has run) reaches Finish()
        /// first: Finish sets `_finished = true` and then calls window.CloseOverlay() -> Close(),
        /// whose OnClosed handler (OverlayWindow.xaml.cs) deliberately nulls `_frame` to release the
        /// session's pixel buffers early — see that method's own doc comment, which assumed (wrongly
        /// under this race) that "nothing reads Frame/Monitor after OnClosed". The already-queued
        /// ContentRendered then fires anyway on the closed window, invoking this method, which reads
        /// `window.Monitor` (= `_frame.Monitor`) below — a NullReferenceException on `_frame`, which
        /// crashed the whole process since this runs on the dispatcher thread with nothing above it
        /// to catch. `_finished` is exactly the flag Finish() sets before doing any of that teardown,
        /// so bailing out here the moment it's set is both necessary and sufficient: every side
        /// effect this method would otherwise perform (the latency logs, and hiding the flash for
        /// this monitor / all monitors) is already subsumed by Finish()'s own unconditional
        /// FlashDimmer.HideAll() in its finally block, so skipping them here loses nothing. Left as
        /// a guard rather than unsubscribing ContentRendered in Finish(), since Finish() closes
        /// windows in a plain foreach with no reference back to each window's own lambda to
        /// unsubscribe, and the flash/overlay dim-show handoff sequencing above is deliberately not
        /// being restructured to fix this.</summary>
        private void OnOverlayContentRendered(OverlayWindow window)
        {
            if (_finished)
            {
                return;
            }

            _renderedCount++;
            double elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(_responseBaseTimestamp).TotalMilliseconds;
            if (_renderedCount == 1)
            {
                FileLog.Write(
                    $"RoeSnip: first-overlay-visible {elapsedMs:0} ms (monitor {window.Monitor.Index})");
            }
            bool allRendered = _renderedCount == _monitors.Count;
            if (allRendered)
            {
                FileLog.Write($"RoeSnip: all-overlays-visible {elapsedMs:0} ms");
            }

            var dispatcher = window.Dispatcher;
            string deviceName = window.Monitor.DeviceName;
            dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => FlashDimmer.HideForMonitor(deviceName)));

            if (allRendered)
            {
                // Same deferred-hide reasoning as above, applied to the all-monitors backstop: a
                // monitor that failed capture has no overlay window and nothing will ever replace
                // its flash, but this must still wait behind the LAST real window's own on-screen
                // handoff rather than racing it.
                dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    new Action(() => FlashDimmer.HideAll()));
            }
        }

        /// <summary>Flash-phase Esc that arrived (via its queued keydown) after this session
        /// already opened — treat it exactly like a normal Esc press.</summary>
        internal void CancelStageFromFlash() => OnCommand(OverlayCommand.CancelStage);

        /// <summary>Mouse-enter (or a click) on another overlay activates it, per DESIGN.md — only
        /// the OS-focused window receives keyboard input, so this is what lets Esc/Enter/Ctrl+C/
        /// Ctrl+S/Ctrl+Z "work from any monitor."</summary>
        private void OnActivatedByMouse(OverlayWindow window)
        {
            if (!ReferenceEquals(_activeWindow, window))
            {
                _activeWindow = window;
                window.Activate();
            }
        }

        /// <summary>Selection lives on exactly one monitor at a time: starting a drag on monitor A
        /// clears any selection on B (and every other monitor).</summary>
        private void OnSelectionStarted(OverlayWindow window)
        {
            foreach (var other in _windows)
            {
                if (!ReferenceEquals(other, window))
                {
                    other.ClearSelection();
                }
            }
        }

        // ---------- Cross-monitor selection (multimon-selection) ----------

        private static RectPhysical ComputeVirtualDesktopBounds(
            IReadOnlyList<(CapturedFrame Frame, SdrImage Preview)> monitors)
        {
            var bounds = new List<RectPhysical>(monitors.Count);
            foreach (var (frame, _) in monitors)
            {
                bounds.Add(frame.Monitor.BoundsPx);
            }
            return SpanningSelectionMath.ComputeVirtualDesktopBounds(bounds);
        }

        /// <summary>The one primitive (resize-after-place made this genuinely shared, not just a
        /// NewSelection-drag concern): called by whichever window's NewSelection/SpanningResize/
        /// SpanningMove drag is currently live, on every mouse-move, with its candidate rect already
        /// translated to virtual-desktop physical pixels (see OverlayWindow's own doc comments at
        /// each call site). Delegates the actual clamp/intersect/real-edge math to
        /// SpanningSelectionMath.Distribute (pure, unit-tested) and only does the WINDOW-touching
        /// side effect here: pushing each monitor's own intersection (or null) into that window via
        /// SetSpanningLocalSelection — which is the SAME dim-mask/adorner/toolbar-placement pipeline
        /// every ordinary single-monitor selection already used, just fed a different rect. Degrades
        /// to exactly the old per-window behavior whenever the candidate only ever touches one
        /// monitor (the common case): <see cref="_spanningVirtual"/> stays null, and that one
        /// window's own SetSpanningLocalSelection call is functionally identical to the old direct
        /// SetSelection call it replaced. <paramref name="preserveSize"/> is true only for a
        /// SpanningMove candidate (see OverlayWindow's own call sites) — a move drag shifts every
        /// edge by the same delta, so a candidate that needs pulling back inside the virtual desktop
        /// must SLIDE (keep its width/height), never get clamped edge-by-edge the way a resize
        /// correctly does (see SpanningSelectionMath.SlideToBounds's own doc comment for why the two
        /// gestures need different clamp strategies).</summary>
        private void OnSpanningCandidate(OverlayWindow owner, RectPhysical candidateVirtual, bool preserveSize)
        {
            var monitorBounds = new List<RectPhysical>(_windows.Count);
            foreach (var w in _windows)
            {
                monitorBounds.Add(w.Monitor.BoundsPx);
            }

            var clampInput = preserveSize
                ? SpanningSelectionMath.SlideToBounds(candidateVirtual.Normalized(), _virtualDesktopBounds)
                : candidateVirtual;
            var distribution = SpanningSelectionMath.Distribute(clampInput, _virtualDesktopBounds, monitorBounds);

            // A Move drag must never delete an already-placed spanning selection out from under the
            // user with no way to undo it via the very gesture that caused it. SlideToBounds only
            // guarantees the candidate stays inside the virtual desktop's own bounding BOX — with
            // non-adjacent monitors (a gap between them), a same-size rect can still land entirely in
            // that gap and intersect nothing, which would otherwise zero out every window's slice mid
            // -drag. When that happens during a preserveSize (Move) candidate and a spanning selection
            // was already in place, ignore this candidate outright and keep the drag's last valid
            // state instead of collapsing it.
            if (preserveSize && distribution.Hits.Count == 0 && _spanningVirtual is not null)
            {
                return;
            }

            // The owner's own monitor can stop intersecting the candidate entirely while the
            // selection still spans ≥2 OTHER monitors (drag the left edge of a 3-monitor span
            // rightward past the leftmost monitor's own boundary, for instance) — the owner is who
            // most recently drove the drag, but it is no longer guaranteed to hold a slice. Falling
            // back to "no window is primary" in that case would hide the toolbar and the true-size
            // badge on EVERY window (see SetSpanningLocalSelection's SuppressBadge/isPrimary doc
            // comments), even though the selection is perfectly valid and just as reachable as any
            // other spanning selection — so fall back to the lowest-indexed monitor that still holds
            // a slice, a deterministic choice that's stable across repeated calls with the same
            // distribution (Hits is built in monitor-index order, so Hits[0] is always that monitor).
            int ownerIndex = _windows.IndexOf(owner);
            int primaryIndex = -1;
            if (distribution.IsSpanning)
            {
                primaryIndex = distribution.Hits.Any(h => h.MonitorIndex == ownerIndex)
                    ? ownerIndex
                    : distribution.Hits[0].MonitorIndex;
            }

            for (int i = 0; i < _windows.Count; i++)
            {
                var w = _windows[i];
                RectPhysical? localRect = null;
                var realEdges = SelectionEdges.All;
                foreach (var hit in distribution.Hits)
                {
                    if (hit.MonitorIndex == i)
                    {
                        localRect = hit.LocalRect;
                        realEdges = hit.RealEdges;
                        break;
                    }
                }
                bool isPrimary = i == primaryIndex;
                // dragInProgress: true — this call only ever runs while a NewSelection/SpanningResize/
                // SpanningMove drag's mouse-move is live (see OnSpanningCandidate's own doc comment),
                // including for every non-owner window; see OverlayWindow's
                // _suppressToolbarForSpanningDrag doc comment for why that matters.
                w.SetSpanningLocalSelection(
                    localRect, distribution.IsSpanning, isPrimary,
                    distribution.IsSpanning ? distribution.ClampedVirtual : null, realEdges,
                    dragInProgress: true);
                if (isPrimary)
                {
                    _spanningPrimaryWindow = w;
                }
            }

            _spanningVirtual = distribution.IsSpanning ? distribution.ClampedVirtual : null;
            if (!distribution.IsSpanning)
            {
                _spanningPrimaryWindow = null;
            }
        }

        /// <summary>Mouse-up (or the `select` automation command) finalizing a NewSelection drag —
        /// or, as of resize-after-place, a SpanningResize/SpanningMove drag too; all three feed
        /// OnSpanningCandidate on every move and land here on release, so this method never needed to
        /// change to support the other two. Applies the existing "<2px on either axis = cancel, not a
        /// real selection" rule against the TRUE selection size: the shared virtual rect while
        /// spanning, or else the single owning window's own local rect exactly as the pre-existing
        /// code did. (Judging the rule against just the calling window's own local slice would be
        /// wrong while spanning — that slice can legitimately be a couple of pixels wide right at a
        /// monitor seam even though the overall selection is large.) Every window's own
        /// SetSpanningLocalSelection call from the drag's last OnSpanningCandidate already left
        /// correct state in place; this only needs to act when the result must be discarded as too
        /// small.</summary>
        internal void FinalizeNewSelectionDrag(OverlayWindow owner)
        {
            if (_spanningVirtual is { } v)
            {
                if (v.Width < 2 || v.Height < 2)
                {
                    ClearSpanningSelection();
                }
            }
            else if (owner.SelectionPx is { } sel && (sel.Width < 2 || sel.Height < 2))
            {
                owner.SetSpanningLocalSelection(null, isSpanning: false, isPrimary: false, virtualRectForLabel: null);
            }

            // Every window took part in the drag's distribute step (see OnSpanningCandidate), not
            // just the owner or, while spanning, just the primary — each one picked up
            // dragInProgress:true on every mouse-move and needs it cleared now that the drag has
            // genuinely ended, or a non-owner window's toolbar would stay suppressed forever (see
            // OverlayWindow._suppressToolbarForSpanningDrag's own doc comment). Harmless/no-op for
            // any window whose flag ClearSpanningSelection's own SetSpanningLocalSelection calls
            // (dragInProgress defaults false) already cleared above.
            foreach (var w in _windows)
            {
                w.NotifySpanningDragEnded();
            }
        }

        private void ClearSpanningSelection()
        {
            foreach (var w in _windows)
            {
                w.SetSpanningLocalSelection(null, isSpanning: false, isPrimary: false, virtualRectForLabel: null);
            }
            _spanningVirtual = null;
            _spanningPrimaryWindow = null;
        }

        /// <summary>One window's contribution to a spanning composite: which window, its own local
        /// (monitor-relative) selection rect, and where that slice lands in the composite canvas
        /// (top-left-relative to the virtual selection rect's own origin). Computed once by
        /// <see cref="ComputeSpanningCropGeometry"/> and consumed by BOTH the SDR (BGRA8,
        /// RenderSpanningSelection) and HDR (raw FP16, BuildSpanningFrameCropsForHdr) composites —
        /// same geometry, two different pixel sources, so the offset math lives in exactly one
        /// place.</summary>
        private readonly record struct SpanningCropGeometry(OverlayWindow Window, RectPhysical CropLocal, int DestX, int DestY);

        private List<SpanningCropGeometry> ComputeSpanningCropGeometry(RectPhysical virtualRect)
        {
            var n = virtualRect.Normalized();
            var list = new List<SpanningCropGeometry>(_windows.Count);
            foreach (var w in _windows)
            {
                if (w.SelectionPx is not { } localSel)
                {
                    continue;
                }
                var cropLocal = localSel.Normalized();
                if (cropLocal.Width <= 0 || cropLocal.Height <= 0)
                {
                    continue;
                }
                var monitorOrigin = w.Monitor.BoundsPx;
                int destX = monitorOrigin.Left + cropLocal.Left - n.Left;
                int destY = monitorOrigin.Top + cropLocal.Top - n.Top;
                list.Add(new SpanningCropGeometry(w, cropLocal, destX, destY));
            }
            return list;
        }

        /// <summary>Byte-composites the final spanning selection (multimon-selection): crops each
        /// intersecting window's own ALREADY tone-mapped preview (SdrImage.Crop — the same crop every
        /// single-monitor render already does) and copies it into a canvas sized to the virtual
        /// selection rect, at that window's own virtual-desktop-relative offset. Never touches
        /// FP16/tone-mapping — this only ever combines bytes each window's own OverlayWindow.Preview
        /// already tone-mapped with ITS OWN monitor's photometrics, per the hard constraint. Gaps (no
        /// captured monitor covers part of the rect) are left OPAQUE BLACK — a deliberate, documented
        /// choice (see docs/DESIGN-MULTIMON-SELECTION.md), not an accidental default. Annotations are
        /// never burned in here: a spanning selection can never have any (see
        /// OverlayWindow.SetSpanningLocalSelection), so this is a plain array composite, not a
        /// DrawingVisual/RenderTargetBitmap pass.</summary>
        private SdrImage RenderSpanningSelection(RectPhysical virtualRect)
        {
            var n = virtualRect.Normalized();
            int width = Math.Max(1, n.Width);
            int height = Math.Max(1, n.Height);
            var pixels = new byte[width * 4 * height];
            for (int i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 255; // opaque black canvas — see the doc comment above for why
            }

            foreach (var geo in ComputeSpanningCropGeometry(virtualRect))
            {
                SdrImage crop;
                try
                {
                    crop = geo.Window.Preview.Crop(geo.CropLocal);
                }
                catch (Exception ex)
                {
                    FileLog.Write(
                        $"RoeSnip: spanning-selection crop failed for monitor {geo.Window.Monitor.DeviceName} (non-fatal, that slice stays black): {ex.Message}");
                    continue;
                }

                CompositeInto(pixels, width, height, crop, geo.DestX, geo.DestY);
            }

            return new SdrImage(width, height, pixels);
        }

        /// <summary>HDR save for a spanning selection: packages each contributing window's own RAW
        /// CapturedFrame (untouched FP16 scRGB, or Bgra8Srgb for a degenerate SDR-only monitor —
        /// JxrWriter.WriteSpanning decodes either uniformly via CapturedFrame.ReadPixelScRgb, exactly
        /// like the single-monitor JxrWriter.Write already does) with the SAME crop-local-rect/dest-
        /// offset geometry ComputeSpanningCropGeometry hands the SDR composite above — the two
        /// composites are geometrically identical, only the pixel source and the sink (BGRA8 canvas
        /// vs. a WIC 128bppRGBAFloat encode) differ. This is what makes the stitch well-defined
        /// despite different monitors' own SDR-white/peak photometrics (see
        /// docs/DESIGN-MULTIMON-SELECTION.md's "HDR save for a spanning selection"): raw scRGB is ONE
        /// absolute linear space (1.0 = 80 nits) for every monitor, so per-monitor photometrics never
        /// enter into it — they only matter for TONE-MAPPING (SdrImage.FromCapturedFrame), a
        /// completely different code path this never touches. Program.cs (WP-A) calls this via
        /// OverlayResult.SpanningFrameCrops + the AppComposition.WriteJxrSpanning hook, mirroring how
        /// the non-spanning path already threads SourceFrame/SelectionPx through for
        /// AppComposition.WriteJxr.</summary>
        private List<RoeSnip.SpanningFrameCrop> BuildSpanningFrameCropsForHdr(RectPhysical virtualRect)
        {
            var list = new List<RoeSnip.SpanningFrameCrop>();
            foreach (var geo in ComputeSpanningCropGeometry(virtualRect))
            {
                list.Add(new RoeSnip.SpanningFrameCrop(geo.Window.Frame, geo.CropLocal, geo.DestX, geo.DestY));
            }
            return list;
        }

        private static void CompositeInto(byte[] dest, int destWidth, int destHeight, SdrImage src, int destX, int destY)
        {
            for (int y = 0; y < src.Height; y++)
            {
                int dy = destY + y;
                if (dy < 0 || dy >= destHeight)
                {
                    continue;
                }
                int srcRowOffset = y * src.Stride;
                int destRowOffset = dy * destWidth * 4;
                for (int x = 0; x < src.Width; x++)
                {
                    int dx = destX + x;
                    if (dx < 0 || dx >= destWidth)
                    {
                        continue;
                    }
                    int so = srcRowOffset + x * 4;
                    int doff = destRowOffset + dx * 4;
                    dest[doff + 0] = src.Pixels[so + 0];
                    dest[doff + 1] = src.Pixels[so + 1];
                    dest[doff + 2] = src.Pixels[so + 2];
                    dest[doff + 3] = 255;
                }
            }
        }

        private void OnCommand(OverlayCommand command)
        {
            if (_finished)
            {
                return;
            }

            switch (command)
            {
                case OverlayCommand.Cancel:
                    Finish(null);
                    break;

                case OverlayCommand.CancelStage:
                    CancelStage();
                    break;

                case OverlayCommand.ConfirmPlain:
                    Confirm(copy: _settings.CopyOnSelect, save: false, saveHdr: false);
                    break;

                case OverlayCommand.Copy:
                    Confirm(copy: true, save: false, saveHdr: false);
                    break;

                case OverlayCommand.Save:
                    Confirm(copy: false, save: true, saveHdr: false);
                    break;

                case OverlayCommand.SaveHdr:
                    // Sets SaveHdrRequested=true on the eventual result only — the actual HDR
                    // write happens back in AppComposition.RunCaptureFlowAsync using SourceFrame +
                    // SelectionPx, per PLAN.md §3.2 ("does not call any HDR-export API itself").
                    Confirm(copy: _settings.CopyOnSelect, save: false, saveHdr: true);
                    break;

                case OverlayCommand.RecordMp4:
                    Record(RecordingFormat.Mp4);
                    break;

                case OverlayCommand.RecordGif:
                    Record(RecordingFormat.Gif);
                    break;

                case OverlayCommand.Share:
                    ShareCurrentSelection();
                    break;
            }
        }

        /// <summary>Record MP4/GIF: unlike Copy/Save/SaveHdr this performs no clipboard/PNG/HDR I/O
        /// itself — it packages (Monitor, SelectionPx, Format) onto the OverlayResult and closes the
        /// overlay normally. The actual WGC capture session starts back in
        /// AppComposition.RunCaptureFlowAsync via the StartRecording hook, exactly like SaveHdr's
        /// write happens back there via WriteJxr.</summary>
        private void Record(RecordingFormat format)
        {
            if (_spanningVirtual is { } spanningRect && _spanningPrimaryWindow is { } primary)
            {
                RecordSpanning(spanningRect, primary, format);
                return;
            }

            var window = _windows.FirstOrDefault(w => w.SelectionPx is not null);
            if (window is null)
            {
                return; // no-op until something is selected, same guard as Confirm
            }

            SdrImage rendered;
            try
            {
                // Still needed: OverlayResult.RenderedImage is non-nullable, and recording ignores
                // annotations on the video itself — this is a cheap one-time crop+composite of a
                // still frame, not a per-frame cost. Keeps every call site (including this one) able
                // to rely on RenderedImage always being populated, avoiding a nullability change to
                // a record every other path already depends on.
                rendered = window.RenderSelectionWithAnnotations();
            }
            catch (InvalidOperationException)
            {
                return;
            }

            var result = new OverlayResult(
                window.Monitor,
                window.SelectionPx!.Value,
                rendered,
                window.Frame,
                CopyPerformed: false,
                SavedPngPath: null,
                SaveHdrRequested: false,
                RecordingRequested: new RecordingRequest(format));

            Finish(result);
        }

        /// <summary>Cross-monitor selection's own Record path (spanning-recording integration): the
        /// GATE that used to sit in the non-spanning Record() above is gone now that a spanning-aware
        /// recorder actually exists (RecordingSession.BeginCapture re-derives the intersected monitor
        /// set itself and takes the spanning path — SpanningCanvasCompositor / EncoderLoopSpanning —
        /// whenever that set has 2+ monitors; see that method's own doc comment). This method's ONLY
        /// job is producing an OverlayResult whose (Monitor, SelectionPx) round-trips back to the
        /// correct ABSOLUTE selection rect once it reaches RecordingSession — it does not, and must
        /// not, know anything about spanning capture itself.
        ///
        /// Mirrors ConfirmSpanning's own anchor-monitor convention: <paramref name="primary"/> (the
        /// window that most recently drove the drag, or the lowest-indexed monitor still holding a
        /// slice — see OnSpanningCandidate) stands in for OverlayResult.Monitor/SourceFrame, which
        /// only need to satisfy that record's non-nullable shape here (recording never reads them for
        /// anything beyond RecordingSession.Start()'s own coordinate conversion below).
        ///
        /// RecordingSession.Start() converts its ctor's SelectionPx (documented as MONITOR-RELATIVE,
        /// same convention every non-spanning OverlayResult.SelectionPx already uses) back to an
        /// ABSOLUTE rect by adding Monitor.BoundsPx's own origin. So packaging the anchor monitor as
        /// Monitor and the virtual (already-absolute) spanning rect converted to be relative to THAT
        /// monitor's own origin as SelectionPx reproduces the exact same absolute rect on the other
        /// side — plain RectPhysical arithmetic done inline here (not
        /// Recording.MultiMonitorRecording.ToMonitorRelative, which is the WP-D-internal helper
        /// RecordingController.cs itself uses for the identical conversion) because Overlay must not
        /// reference Recording package types directly, per OverlayResult's own doc comment ("Only the
        /// cross-cutting bits ... are threaded back through AppComposition, because they need WP-C/
        /// WP-D types that Overlay must not reference directly").</summary>
        private void RecordSpanning(RectPhysical virtualRect, OverlayWindow primary, RecordingFormat format)
        {
            SdrImage rendered;
            try
            {
                // Same "cheap one-time composite, RenderedImage is non-nullable" reasoning as the
                // non-spanning Record() above — recording never reads this still image once capture
                // starts, it only exists to keep OverlayResult's shape uniform across every producer.
                rendered = RenderSpanningSelection(virtualRect);
            }
            catch (Exception ex)
            {
                FileLog.Write($"RoeSnip: spanning-selection record render failed: {ex.Message}");
                return;
            }

            var n = virtualRect.Normalized();
            var anchorBounds = primary.Monitor.BoundsPx;
            var anchorRelativeSelection = new RectPhysical(
                n.Left - anchorBounds.Left, n.Top - anchorBounds.Top,
                n.Right - anchorBounds.Left, n.Bottom - anchorBounds.Top);

            var result = new OverlayResult(
                primary.Monitor,
                anchorRelativeSelection,
                rendered,
                primary.Frame,
                CopyPerformed: false,
                SavedPngPath: null,
                SaveHdrRequested: false,
                RecordingRequested: new RecordingRequest(format));

            Finish(result);
        }

        /// <summary>Toolbar Share button (Sharing/* subsystem, default-provider path only — see
        /// OverlayCommand.Share's own doc comment for the per-provider-dropdown scope note). Renders
        /// the CURRENT selection through the exact same path Copy uses — RenderSelectionWithAnnotations
        /// for a single-monitor selection, RenderSpanningSelection for a spanning one (same
        /// _spanningVirtual/_spanningPrimaryWindow check every other spanning-aware command here
        /// uses) — so whatever gets shared is pixel-identical to what Ctrl+C would have put on the
        /// clipboard.
        ///
        /// Unlike the old stay-open design, this now closes the overlay IMMEDIATELY (Finish(), same
        /// as Copy/Save/Record) once the upload has been handed off to
        /// <see cref="RoeSnip.Sharing.ShareFlowPresenter"/> — progress/success/failure now live in
        /// that presenter's own ShareResultWindow, a small always-on-top toast that outlives the
        /// overlay, so there is no longer any reason to keep single-use overlay windows around for
        /// the whole upload. The render (a one-time crop/composite) still happens synchronously here,
        /// same cost Record()/Confirm() already pay.</summary>
        private void ShareCurrentSelection()
        {
            if (!TryPrepareShareUpload(out var toolbarWindow, out var pngBytes, out var finishResult))
            {
                return;
            }

            // Sharing/* subsystem (doc-honesty/unify-to-one-source fix): resolved from the SAME
            // settings snapshot ShowToolbar's own SetShareProviders call populates the dropdown
            // from (toolbarWindow.LiveSettings), not this session's own separately-snapshotted
            // _settings field — see OverlayWindow.LiveSettings' own doc comment for why the two used
            // to be able to disagree.
            var config = RoeSnip.Core.Sharing.ShareManager.ResolveDefault(
                toolbarWindow!.LiveSettings.ShareProviders, toolbarWindow!.LiveSettings.DefaultShareProviderId);
            if (config is null)
            {
                // ShareButton is only ever enabled once ToolbarControl.SetShareProviders was given at
                // least one enabled provider (see that method's own doc comment), so this should be
                // unreachable in practice — kept as a defensive, honestly-surfaced failure rather than
                // a silent no-op in case settings changed out from under an already-open overlay.
                _notifier?.ShowError("Share failed: no share provider is configured.");
                return;
            }

            StartShareUpload(toolbarWindow, config, pngBytes!);
            Finish(finishResult);
        }

        /// <summary>The dropdown's per-provider Share (Sharing/* subsystem, senior-review wiring
        /// fix — this used to be raised by ToolbarControl and simply discarded, a clickable-but-dead
        /// menu item). Renders the current selection exactly like <see cref="ShareCurrentSelection"/>
        /// does; the only difference is which provider config the render gets uploaded to — resolved
        /// by id against the SAME <see cref="OverlayWindow.LiveSettings"/> source the dropdown itself
        /// was populated from, so a choice visible in the menu can never fail to resolve here.</summary>
        private void ShareToSpecificProvider(string providerId)
        {
            if (!TryPrepareShareUpload(out var toolbarWindow, out var pngBytes, out var finishResult))
            {
                return;
            }

            var config = RoeSnip.Core.Sharing.ShareManager.EffectiveConfigs(toolbarWindow!.LiveSettings.ShareProviders)
                .FirstOrDefault(c => c.Enabled && string.Equals(c.Id, providerId, StringComparison.Ordinal));
            if (config is null)
            {
                // The dropdown (ToolbarControl.SetShareProviders) is only ever populated with
                // currently-enabled providers, so this should be unreachable in practice — same
                // defensive-honesty stance ShareCurrentSelection's own no-provider branch takes,
                // kept in case a provider was disabled/removed out from under an already-open menu.
                _notifier?.ShowError("Share failed: the selected provider is no longer configured.");
                return;
            }

            StartShareUpload(toolbarWindow, config, pngBytes!);
            Finish(finishResult);
        }

        /// <summary>Shared render step for both <see cref="ShareCurrentSelection"/> (default
        /// provider) and <see cref="ShareToSpecificProvider"/> (dropdown pick) — everything the two
        /// used to duplicate verbatim except which provider config the render gets uploaded to.
        /// Returns false (all out params default) on any no-op/failure condition, matching
        /// Confirm/Record's own "nothing selected yet" and render-failure guards.
        /// <paramref name="finishResult"/> is the OverlayResult the caller passes straight to
        /// Finish() right after starting the upload — built the same way Record()/RecordSpanning()
        /// build theirs (RecordingRequested left null here, since sharing has nothing further for
        /// AppComposition to do beyond the settings.AutoSaveHdrCopy check every other Confirm/Record
        /// result already goes through).</summary>
        private bool TryPrepareShareUpload(out OverlayWindow? toolbarWindow, out byte[]? pngBytes, out OverlayResult? finishResult)
        {
            toolbarWindow = null;
            pngBytes = null;
            finishResult = null;

            SdrImage rendered;
            RectPhysical? spanningRect = null;
            if (_spanningVirtual is { } virtualRect && _spanningPrimaryWindow is { } primary)
            {
                toolbarWindow = primary;
                spanningRect = virtualRect;
                try
                {
                    rendered = RenderSpanningSelection(virtualRect);
                }
                catch (Exception ex)
                {
                    FileLog.Write($"RoeSnip: spanning-selection share render failed: {ex.Message}");
                    toolbarWindow = null;
                    return false;
                }
            }
            else
            {
                var window = _windows.FirstOrDefault(w => w.SelectionPx is not null);
                if (window is null)
                {
                    return false; // nothing selected yet — same no-op guard as Confirm/Record
                }
                toolbarWindow = window;
                try
                {
                    rendered = window.RenderSelectionWithAnnotations();
                }
                catch (InvalidOperationException)
                {
                    toolbarWindow = null;
                    return false;
                }
            }

            try
            {
                pngBytes = PngWriter.Encode(rendered);
            }
            catch (Exception ex)
            {
                FileLog.Write($"RoeSnip: share PNG encode failed: {ex.Message}");
                toolbarWindow = null;
                pngBytes = null;
                return false;
            }

            finishResult = spanningRect is { } sr
                ? new OverlayResult(
                    toolbarWindow.Monitor, toolbarWindow.SelectionPx ?? default, rendered, toolbarWindow.Frame,
                    CopyPerformed: false, SavedPngPath: null, SaveHdrRequested: false, RecordingRequested: null,
                    SpanningVirtualSelectionPx: sr, SpanningFrameCrops: BuildSpanningFrameCropsForHdr(sr))
                : new OverlayResult(
                    toolbarWindow.Monitor, toolbarWindow.SelectionPx!.Value, rendered, toolbarWindow.Frame,
                    CopyPerformed: false, SavedPngPath: null, SaveHdrRequested: false, RecordingRequested: null);

            return true;
        }

        /// <summary>Hands the prepared render off to the shared presenter and returns immediately —
        /// the overlay's own Finish() runs right after this, per ShareCurrentSelection/
        /// ShareToSpecificProvider above. SetShareBusy(true) here is now just a momentary double-click
        /// guard (the window is closing anyway), matching that method's own updated doc comment.</summary>
        private static void StartShareUpload(OverlayWindow toolbarWindow, RoeSnip.Core.Sharing.ShareProviderConfig config, byte[] pngBytes)
        {
            toolbarWindow.SetShareBusy(true);
            string fileName = $"roesnip_{DateTime.Now:yyyyMMdd_HHmmss}.png";
            var request = new RoeSnip.Core.Sharing.ShareUploadRequest(
                new System.IO.MemoryStream(pngBytes, writable: false), fileName, "image/png");
            RoeSnip.Sharing.ShareFlowPresenter.StartUpload(config, request, toolbarWindow.Monitor, keptFilePathOnFailure: null, onSuccess: null, onFailure: null);
        }

        /// <summary>Esc's two-stage behavior, decided here because only the session can see every
        /// monitor: the selection may live on a different window than the one that has keyboard
        /// focus. Stage 1 clears the in-progress snip (selection + annotations, back to the
        /// crosshair state); stage 2 — nothing active at all — closes the whole overlay. Each Esc
        /// press performs exactly one stage. (An active inline text edit is a third, innermost
        /// stage, but it's consumed locally by OverlayWindow.ProcessKeyCommand before CancelStage is
        /// ever raised — see OverlayCommand's doc comment.)</summary>
        private void CancelStage()
        {
            bool clearedSnip = false;
            foreach (var window in _windows)
            {
                if (window.HasSnipInProgress)
                {
                    window.ClearSelection();
                    clearedSnip = true;
                }
            }
            // Cross-monitor selection: the shared spanning state is session-level, not any one
            // window's — clear it unconditionally alongside every window's own ClearSelection above
            // (a harmless no-op when nothing was spanning).
            _spanningVirtual = null;
            _spanningPrimaryWindow = null;
            if (!clearedSnip)
            {
                Finish(null);
            }
        }

        private void Confirm(bool copy, bool save, bool saveHdr)
        {
            if (_spanningVirtual is { } spanningRect && _spanningPrimaryWindow is { } primary)
            {
                ConfirmSpanning(spanningRect, primary, copy, save, saveHdr);
                return;
            }

            var window = _windows.FirstOrDefault(w => w.SelectionPx is not null);
            if (window is null)
            {
                return; // nothing selected yet — Enter/Ctrl+C/Ctrl+S/Save-HDR are no-ops until then
            }

            SdrImage rendered;
            try
            {
                rendered = window.RenderSelectionWithAnnotations();
            }
            catch (InvalidOperationException)
            {
                return;
            }

            bool copyPerformed = false;
            if (copy)
            {
                try
                {
                    ClipboardService.CopyToClipboard(rendered);
                    copyPerformed = true;
                    window.ShowShutterFlash();
                }
                catch (Exception)
                {
                    // A failed clipboard write (e.g. locked by another process) shouldn't prevent
                    // the rest of confirm (an independently-requested Save should still happen);
                    // surfaced only via CopyPerformed staying false on the returned OverlayResult.
                }
            }

            string? savedPath = null;
            if (save)
            {
                savedPath = TryShowSaveDialog(window);
                if (savedPath is null)
                {
                    // User cancelled the Save dialog: stay open rather than silently discarding
                    // the selection/annotations they just made.
                    return;
                }

                try
                {
                    PngWriter.WriteFile(savedPath, rendered);
                }
                catch (Exception)
                {
                    savedPath = null;
                }
            }

            var result = new OverlayResult(
                window.Monitor,
                window.SelectionPx!.Value,
                rendered,
                window.Frame,
                copyPerformed,
                savedPath,
                saveHdr);

            Finish(result);
        }

        /// <summary>Cross-monitor selection's own Confirm path: Copy/Save/Save-HDR. Mirrors the
        /// non-spanning Confirm above (same clipboard/dialog/write calls, same
        /// stay-open-on-cancelled-dialog behavior) but renders via RenderSpanningSelection's byte
        /// composite instead of a single window's annotated crop, and packages the result with
        /// <see cref="OverlayResult.SpanningVirtualSelectionPx"/>/<see
        /// cref="OverlayResult.SpanningFrameCrops"/> set. Save HDR (<paramref name="saveHdr"/>) is
        /// genuinely supported now — see BuildSpanningFrameCropsForHdr's own doc comment for why
        /// stitching raw scRGB crops is well-defined where stitching already-tone-mapped ones would
        /// not be — SpanningFrameCrops is populated unconditionally (not just when saveHdr is true),
        /// same as the non-spanning path always carries SourceFrame/SelectionPx regardless of what
        /// the user clicked, so Program.cs's settings.AutoSaveHdrCopy branch works identically for a
        /// spanning result too.</summary>
        private void ConfirmSpanning(RectPhysical virtualRect, OverlayWindow primary, bool copy, bool save, bool saveHdr)
        {
            SdrImage rendered;
            try
            {
                rendered = RenderSpanningSelection(virtualRect);
            }
            catch (Exception ex)
            {
                FileLog.Write($"RoeSnip: spanning-selection render failed: {ex.Message}");
                return;
            }

            bool copyPerformed = false;
            if (copy)
            {
                try
                {
                    ClipboardService.CopyToClipboard(rendered);
                    copyPerformed = true;
                    primary.ShowShutterFlash();
                }
                catch (Exception)
                {
                    // Same non-fatal reasoning as the single-monitor Confirm above.
                }
            }

            string? savedPath = null;
            if (save)
            {
                savedPath = TryShowSaveDialog(primary);
                if (savedPath is null)
                {
                    return; // user cancelled the Save dialog — stay open, same as the single-monitor path
                }

                try
                {
                    PngWriter.WriteFile(savedPath, rendered);
                }
                catch (Exception)
                {
                    savedPath = null;
                }
            }

            var result = new OverlayResult(
                primary.Monitor,
                primary.SelectionPx ?? default,
                rendered,
                primary.Frame,
                copyPerformed,
                savedPath,
                SaveHdrRequested: saveHdr,
                RecordingRequested: null,
                SpanningVirtualSelectionPx: virtualRect,
                SpanningFrameCrops: BuildSpanningFrameCropsForHdr(virtualRect));

            Finish(result);
        }

        private string? TryShowSaveDialog(OverlayWindow window)
        {
            try
            {
                System.IO.Directory.CreateDirectory(_settings.SaveDirectory);
            }
            catch (Exception)
            {
                // Fall through and let SaveFileDialog itself surface a directory problem, if any.
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                InitialDirectory = _settings.SaveDirectory,
                FileName = $"roesnip_{DateTime.Now:yyyyMMdd_HHmmss}.png",
                DefaultExt = ".png",
                Filter = "PNG image (*.png)|*.png",
                AddExtension = true,
            };

            bool? result = dialog.ShowDialog(window);
            return result == true ? dialog.FileName : null;
        }

        private void Finish(OverlayResult? result)
        {
            if (_finished)
            {
                return;
            }
            _finished = true;

            // This is the single terminal point every session exit path funnels through — normal
            // Cancel/CancelStage-to-empty/Confirm-Copy-Save-SaveHdr commands call it directly, and
            // an exception partway through RunAsync's construct/show loop reaches it via that
            // loop's catch block (as does an externally-closed window's Closed event, e.g.
            // Alt+F4). The keyboard hook removal is guaranteed here via try/finally regardless of
            // which of those paths got us here, or whether closing the windows themselves throws;
            // the same finally clears the active-session marker and hides any flash dimmer still
            // up (e.g. a cancel that arrived before every monitor's window had rendered).
            try
            {
                foreach (var window in _windows)
                {
                    try
                    {
                        window.CloseOverlay();
                    }
                    catch (InvalidOperationException)
                    {
                        // Already closing (e.g. this Finish was triggered by that window's own
                        // Closed event, such as an external Alt+F4) — nothing further to do for it.
                    }
                }
            }
            finally
            {
                _keyboardHook.Dispose();
                if (ReferenceEquals(s_activeSession, this))
                {
                    s_activeSession = null;
                }
                // Same race-closing reason as the session-start Activate() call above: a session
                // that finishes before ever activating (e.g. ConsumeFlashCancelRequest's early
                // return) must still invalidate the flash's pending foreground claim, or a
                // delayed SetForegroundWindow could steal focus onto a now-parked flash window
                // after everything has already torn down.
                FlashDimmer.InvalidateForegroundClaim();
                try { FlashDimmer.HideAll(); }
                catch (Exception ex) { FileLog.Write($"RoeSnip: flash dimmer hide failed: {ex.Message}"); }

                // D4: this session's windows (whichever came from the pool, if any — see
                // OverlayWindowPool.TryTake) are gone now, so re-provision a fresh pool for the NEXT
                // trigger — deferred to ContextIdle so it never contends with THIS session's own
                // teardown or, more importantly, a rapid re-trigger's fallback path (G3).
                try
                {
                    OverlayWindowPool.ScheduleReprovision(
                        System.Windows.Threading.Dispatcher.CurrentDispatcher, SessionMonitorInfos());
                }
                catch (Exception ex)
                {
                    FileLog.Write($"RoeSnip: overlay pool reprovision scheduling failed (non-fatal): {ex.Message}");
                }
            }

            _completion.TrySetResult(result);
        }

        // ---------- Automation hooks (App/AutomationServer.cs) — see OverlayController's own
        // wrapper methods for why these exist on this private nested class instead of being called
        // directly. ----------

        internal RectPhysical? GetSelectionForAutomation()
        {
            // Cross-monitor selection: the shared virtual rect IS already virtual-desktop physical
            // pixels (the wire protocol's own convention), so it's returned as-is — no per-window
            // origin math needed, unlike the non-spanning fallback below.
            if (_spanningVirtual is { } v)
            {
                return v;
            }

            var window = _windows.FirstOrDefault(w => w.SelectionPx is not null);
            if (window is null)
            {
                return null;
            }
            var sel = window.SelectionPx!.Value;
            var b = window.Monitor.BoundsPx;
            return new RectPhysical(b.Left + sel.Left, b.Top + sel.Top, b.Left + sel.Right, b.Top + sel.Bottom);
        }

        /// <summary>Cross-monitor selection: the `select` automation command's own lever for
        /// spanning rects (see docs/DESIGN-MULTIMON-SELECTION.md's "Automation" section) — routes
        /// through the EXACT same OnSpanningCandidate/FinalizeNewSelectionDrag functions a real drag
        /// uses, never a separate implementation. The owning/"primary" window is chosen the same way
        /// the pre-existing single-monitor automation path already did: whichever monitor contains
        /// the rect's top-left corner.</summary>
        internal string? SetSelectionForAutomation(RectPhysical virtualDesktopPx)
        {
            var window = _windows.FirstOrDefault(w =>
            {
                var b = w.Monitor.BoundsPx;
                return virtualDesktopPx.Left >= b.Left && virtualDesktopPx.Left < b.Right
                    && virtualDesktopPx.Top >= b.Top && virtualDesktopPx.Top < b.Bottom;
            });
            if (window is null)
            {
                return "selection rect's top-left corner is not on any monitor";
            }

            // Same bookkeeping a real drag-start on this monitor triggers: clear any selection on
            // every OTHER monitor (selection lives on exactly one at a time) and make this the
            // active/focused window.
            OnSelectionStarted(window);
            if (!ReferenceEquals(_activeWindow, window))
            {
                _activeWindow = window;
                window.Activate();
            }
            OnSpanningCandidate(window, virtualDesktopPx.Normalized(), preserveSize: false);
            FinalizeNewSelectionDrag(window); // applies the same too-small-cancel rule a real mouse-up would
            return null;
        }

        internal string? RecordForAutomation(RecordingFormat format)
        {
            // Spanning-recording integration: the old unconditional refusal here ("recording is not
            // supported for a selection spanning multiple monitors") predates RecordingSession's own
            // spanning-aware capture path — see Record()/RecordSpanning's own doc comments. A spanning
            // selection now only needs the same "something is actually selected" guard every other
            // selection shape already gets below (_spanningVirtual is non-null only once a real
            // spanning selection exists — see that field's own doc comment — so there is no separate
            // spanning check left to make here).
            if (_spanningVirtual is null && _windows.FirstOrDefault(w => w.SelectionPx is not null) is null)
            {
                return "no selection to record";
            }
            OnCommand(format == RecordingFormat.Gif ? OverlayCommand.RecordGif : OverlayCommand.RecordMp4);
            return null;
        }

        internal string? CancelForAutomation()
        {
            OnCommand(OverlayCommand.Cancel);
            return null;
        }

        /// <summary>See OverlayController.ConfirmForAutomation's own doc comment for why "save"
        /// requires an explicit path (never the interactive dialog) and exists at all.</summary>
        internal string? ConfirmForAutomation(string action, string? path)
        {
            switch (action)
            {
                case "copy":
                    if (_windows.All(w => w.SelectionPx is null))
                    {
                        return "no selection to copy";
                    }
                    Confirm(copy: true, save: false, saveHdr: false);
                    return null;

                case "save":
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        return "confirm \"save\" requires a non-empty \"path\" (automation never opens the Save dialog)";
                    }
                    if (_windows.All(w => w.SelectionPx is null))
                    {
                        return "no selection to save";
                    }
                    ConfirmSaveForAutomation(path);
                    return null;

                case "share":
                    // Sharing/* subsystem: raises the exact OverlayCommand.Share the toolbar's Share
                    // button raises — hands the render to ShareFlowPresenter and closes the overlay
                    // immediately, same as copy/save (this response's trailing state snapshot no
                    // longer shows the overlay); the upload's own progress/result now arrives via the
                    // presenter's own ShareResultWindow, entirely outside this response.
                    // ShareCurrentSelection has its own no-provider guard; the no-selection case is
                    // pre-checked here so automation gets an explicit error instead of that method's
                    // silent no-op.
                    if (_spanningVirtual is null && _windows.All(w => w.SelectionPx is null))
                    {
                        return "no selection to share";
                    }
                    OnCommand(OverlayCommand.Share);
                    return null;

                default:
                    return "confirm requires \"action\": one of copy|save|share";
            }
        }

        /// <summary>Save's automation path: the same render + PngWriter.WriteFile calls Confirm/
        /// ConfirmSpanning already make, just writing straight to <paramref name="path"/> instead of
        /// asking TryShowSaveDialog for one.</summary>
        private void ConfirmSaveForAutomation(string path)
        {
            if (_spanningVirtual is { } spanningRect && _spanningPrimaryWindow is { } primary)
            {
                SdrImage rendered;
                try
                {
                    rendered = RenderSpanningSelection(spanningRect);
                }
                catch (Exception ex)
                {
                    FileLog.Write($"RoeSnip: spanning-selection render failed: {ex.Message}");
                    return;
                }

                try
                {
                    PngWriter.WriteFile(path, rendered);
                }
                catch (Exception ex)
                {
                    FileLog.Write($"RoeSnip: automation save failed: {ex.Message}");
                    return;
                }

                var result = new OverlayResult(
                    primary.Monitor, primary.SelectionPx ?? default, rendered, primary.Frame,
                    CopyPerformed: false, SavedPngPath: path, SaveHdrRequested: false,
                    RecordingRequested: null, SpanningVirtualSelectionPx: spanningRect,
                    // Automation's "save" action is PNG-only (see this method's own doc comment /
                    // ConfirmForAutomation's), same as SaveHdrRequested staying false above — but
                    // SpanningFrameCrops is still populated so settings.AutoSaveHdrCopy (which is
                    // independent of what this particular call asked for) keeps working for an
                    // automation-driven spanning save exactly like it does for an interactive one.
                    SpanningFrameCrops: BuildSpanningFrameCropsForHdr(spanningRect));
                Finish(result);
                return;
            }

            var window = _windows.FirstOrDefault(w => w.SelectionPx is not null);
            if (window is null)
            {
                return;
            }

            SdrImage renderedLocal;
            try
            {
                renderedLocal = window.RenderSelectionWithAnnotations();
            }
            catch (InvalidOperationException)
            {
                return;
            }

            try
            {
                PngWriter.WriteFile(path, renderedLocal);
            }
            catch (Exception ex)
            {
                FileLog.Write($"RoeSnip: automation save failed: {ex.Message}");
                return;
            }

            var localResult = new OverlayResult(
                window.Monitor, window.SelectionPx!.Value, renderedLocal, window.Frame,
                CopyPerformed: false, SavedPngPath: path, SaveHdrRequested: false);
            Finish(localResult);
        }

        /// <summary>The MonitorInfo for every monitor this session captured — used only to know
        /// which monitors OverlayWindowPool.ScheduleReprovision should rebuild the pool for.
        /// CapturedFrame.Monitor stays valid even after the frame itself is Dispose()'d (Dispose
        /// only nulls the pixel buffer — see CapturedFrame), so this is safe to call from Finish()
        /// regardless of whether the caller has already disposed these frames.</summary>
        private List<MonitorInfo> SessionMonitorInfos()
        {
            var list = new List<MonitorInfo>(_monitors.Count);
            foreach (var (frame, _) in _monitors)
            {
                list.Add(frame.Monitor);
            }
            return list;
        }
    }
}

file static class ModuleInit
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Init()
    {
        AppComposition.RunOverlay = OverlayController.RunAsync;
        // Review fix (r5-latency S2): lets Program.cs (WP-A) discard a pending trigger timestamp
        // from RunCaptureFlowAsync's dead-end paths without taking a direct WP-B dependency — see
        // OverlayController.ClearPendingTrigger's doc comment.
        AppComposition.ClearPendingOverlayTrigger = OverlayController.ClearPendingTrigger;
    }
}
