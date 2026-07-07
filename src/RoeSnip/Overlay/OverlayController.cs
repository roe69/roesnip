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
        private readonly List<OverlayWindow> _windows = new();
        private readonly TaskCompletionSource<OverlayResult?> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Session-scoped reliability layer (a) — see OverlayInputInterop.cs's SessionKeyboardHook
        // doc comment for the full root-cause writeup. Installed here ("when the session opens"),
        // disposed exactly once from Finish() ("removed unconditionally... all paths").
        private readonly SessionKeyboardHook _keyboardHook;

        private OverlayWindow? _activeWindow;
        private bool _finished;

        public OverlaySession(
            IReadOnlyList<(CapturedFrame Frame, SdrImage Preview)> monitors,
            RoeSnipSettings settings,
            Action<PickedColorInfo> onColorPicked,
            bool pickOnlyMode)
        {
            _settings = settings;

            // Installed before any window is shown/receives input, so session keys are covered for
            // the session's entire visible lifetime — including the (rare) window it races with
            // Show() below throwing partway through, since that path still routes through Finish()
            // via each shown window's Closed event (see RunAsync's catch block).
            _keyboardHook = new SessionKeyboardHook(() => _activeWindow);

            // If constructing any OverlayWindow throws partway through this loop, the session
            // object itself never finishes constructing — Finish() (the normal disposal point)
            // would never run and the hook would leak permanently. Guard this loop specifically so
            // the hook is provably removed on every path, including this one.
            try
            {
                foreach (var (frame, preview) in monitors)
                {
                    var window = new OverlayWindow(
                        frame, preview, settings, OnActivatedByMouse, OnSelectionStarted, OnCommand,
                        onColorPicked, pickOnlyMode);
                    window.Closed += (_, _) => Finish(null);
                    _windows.Add(window);
                }
            }
            catch
            {
                _keyboardHook.Dispose();
                throw;
            }
        }

        public Task<OverlayResult?> RunAsync()
        {
            // If showing (or activating) one of these windows throws partway through, every window
            // already shown must be force-closed rather than left stranded, topmost, on screen with
            // no way for the user to dismiss it (audit finding C.3).
            var shown = new List<OverlayWindow>();
            try
            {
                foreach (var window in _windows)
                {
                    window.Show();
                    shown.Add(window);
                }

                _activeWindow = _windows.FirstOrDefault(w => w.Monitor.IsPrimary) ?? _windows[0];
                _activeWindow.Activate();
            }
            catch
            {
                foreach (var window in shown)
                {
                    try
                    {
                        window.CloseOverlay();
                    }
                    catch
                    {
                        // Best-effort: nothing further to do if closing a partially-shown window
                        // itself fails.
                    }
                }
                throw;
            }

            return _completion.Task;
        }

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
            }
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
            // an exception partway through RunAsync's Show() loop reaches it indirectly (that catch
            // block force-closes every already-shown window, whose Closed event calls Finish(null)).
            // The keyboard hook removal is guaranteed here via try/finally regardless of which of
            // those paths got us here, or whether closing the windows themselves throws.
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
            }

            _completion.TrySetResult(result);
        }
    }
}

file static class ModuleInit
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Init() => AppComposition.RunOverlay = OverlayController.RunAsync;
}
