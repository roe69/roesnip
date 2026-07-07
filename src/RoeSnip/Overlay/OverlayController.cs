using System;
using System.Collections.Generic;
using System.Linq;
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
/// both sources identically. ConfirmPlain is Enter/double-click; Copy/Save/SaveHdr are the
/// explicit Ctrl+C/Ctrl+S/toolbar-button requests. Undo (Ctrl+Z) is handled locally inside
/// OverlayWindow against its own AnnotationLayer and never reaches the controller, since only the
/// OS-focused window can receive it in the first place — there's no cross-monitor ambiguity to
/// resolve for it.</summary>
public enum OverlayCommand
{
    Cancel,
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

        var session = new OverlaySession(monitors, settings);
        return session.RunAsync();
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

        private OverlayWindow? _activeWindow;
        private bool _finished;

        public OverlaySession(IReadOnlyList<(CapturedFrame Frame, SdrImage Preview)> monitors, RoeSnipSettings settings)
        {
            _settings = settings;

            foreach (var (frame, preview) in monitors)
            {
                var window = new OverlayWindow(frame, preview, settings, OnActivatedByMouse, OnSelectionStarted, OnCommand);
                window.Closed += (_, _) => Finish(null);
                _windows.Add(window);
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

            foreach (var window in _windows)
            {
                try
                {
                    window.CloseOverlay();
                }
                catch (InvalidOperationException)
                {
                    // Already closing (e.g. this Finish was triggered by that window's own Closed
                    // event, such as an external Alt+F4) — nothing further to do for it.
                }
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
