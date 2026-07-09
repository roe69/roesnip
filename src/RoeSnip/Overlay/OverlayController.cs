using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using RoeSnip.Capture;
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
}

public static class OverlayController
{
    /// <summary>One OverlayWindow per monitor; runs until the user cancels (Esc) or confirms
    /// (Enter / double-click / toolbar action). Returns null on cancel. On confirm, performs
    /// Copy/Save side effects itself (clipboard + PNG dialog) per DESIGN.md, then returns a
    /// populated OverlayResult. Matches the AppComposition.RunOverlay hook signature exactly.</summary>
    public static Task<OverlayResult?> RunAsync(
        IReadOnlyList<(CapturedFrame Frame, SdrImage Preview)> monitors,
        RoeSnipSettings settings)
    {
        if (monitors.Count == 0)
        {
            return Task.FromResult<OverlayResult?>(null);
        }

        EnsureApplication();

        var session = new OverlaySession(monitors, settings, OnColorPicked, pickOnlyMode: false);
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
            Console.Error.WriteLine(
                $"RoeSnip: flash dimmer windows ready in {watch.ElapsedMilliseconds} ms ({monitors.Count} monitors)");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: flash dimmer pre-creation failed (non-fatal): {ex.Message}");
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
            Console.Error.WriteLine($"RoeSnip: overlay pool pre-creation failed (non-fatal): {ex.Message}");
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
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: flash dimmer show failed (non-fatal): {ex.Message}");
            try { FlashDimmer.HideAll(); } catch { /* best-effort */ }
            return false;
        }
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
            try { FlashDimmer.HideAll(); }
            catch (Exception ex) { Console.Error.WriteLine($"RoeSnip: flash dimmer hide failed: {ex.Message}"); }
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
        try { FlashDimmer.HideAll(); }
        catch (Exception ex) { Console.Error.WriteLine($"RoeSnip: flash dimmer hide failed: {ex.Message}"); }
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
            Console.Error.WriteLine($"RoeSnip: cursor-monitor ordering failed (non-fatal): {ex.Message}");
            return monitors;
        }
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
            Console.Error.WriteLine("RoeSnip: a capture is already in progress; ignoring pick request.");
            return;
        }

        _ = RunPickModeCaptureAsync();
    }

    private static async Task RunPickModeCaptureAsync()
    {
        try
        {
            var settings = AppComposition.LoadSettings?.Invoke() ?? RoeSnipSettings.Default;

            var captureService = new CaptureService();
            var frames = captureService.CaptureAll();
            if (frames.Count == 0)
            {
                Console.Error.WriteLine("RoeSnip: pick-mode capture failed on every monitor.");
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
                foreach (var frame in frames)
                {
                    monitorsWithPreview.Add((frame, SdrImage.FromCapturedFrame(frame, toneMapOpts)));
                }

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
            Console.Error.WriteLine($"RoeSnip: pick-mode capture failed: {ex.Message}");
        }
        finally
        {
            CaptureGate.Exit();
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

        public OverlaySession(
            IReadOnlyList<(CapturedFrame Frame, SdrImage Preview)> monitors,
            RoeSnipSettings settings,
            Action<PickedColorInfo> onColorPicked,
            bool pickOnlyMode)
        {
            _settings = settings;
            _onColorPicked = onColorPicked;
            _pickOnlyMode = pickOnlyMode;
            _monitors = OrderCursorMonitorFirst(monitors);

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
                            frame, preview, _settings, OnActivatedByMouse, OnSelectionStarted, OnCommand,
                            _onColorPicked, _pickOnlyMode);
                        window.Dispatcher.Invoke(static () => { }, System.Windows.Threading.DispatcherPriority.Loaded);
                        window.MoveOnScreen();
                        OnOverlayContentRendered(window);
                    }
                    else
                    {
                        window = new OverlayWindow(
                            frame, preview, _settings, OnActivatedByMouse, OnSelectionStarted, OnCommand,
                            _onColorPicked, _pickOnlyMode);
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

                Console.Error.WriteLine(
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

            Console.Error.WriteLine(
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
        /// leak into a screenshot); hiding early is exactly the bug this fixes.</summary>
        private void OnOverlayContentRendered(OverlayWindow window)
        {
            _renderedCount++;
            double elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(_responseBaseTimestamp).TotalMilliseconds;
            if (_renderedCount == 1)
            {
                Console.Error.WriteLine(
                    $"RoeSnip: first-overlay-visible {elapsedMs:0} ms (monitor {window.Monitor.Index})");
            }
            bool allRendered = _renderedCount == _monitors.Count;
            if (allRendered)
            {
                Console.Error.WriteLine($"RoeSnip: all-overlays-visible {elapsedMs:0} ms");
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
            }
        }

        /// <summary>Record MP4/GIF: unlike Copy/Save/SaveHdr this performs no clipboard/PNG/HDR I/O
        /// itself — it packages (Monitor, SelectionPx, Format) onto the OverlayResult and closes the
        /// overlay normally. The actual WGC capture session starts back in
        /// AppComposition.RunCaptureFlowAsync via the StartRecording hook, exactly like SaveHdr's
        /// write happens back there via WriteJxr.</summary>
        private void Record(RecordingFormat format)
        {
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
            if (!clearedSnip)
            {
                Finish(null);
            }
        }

        private void Confirm(bool copy, bool save, bool saveHdr)
        {
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
                catch (Exception ex) { Console.Error.WriteLine($"RoeSnip: flash dimmer hide failed: {ex.Message}"); }

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
                    Console.Error.WriteLine($"RoeSnip: overlay pool reprovision scheduling failed (non-fatal): {ex.Message}");
                }
            }

            _completion.TrySetResult(result);
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
