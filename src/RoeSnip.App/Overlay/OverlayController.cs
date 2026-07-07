using Avalonia.Platform.Storage;
using RoeSnip.Core.Capture;
using RoeSnip.Core.Imaging;
using RoeSnip.Core.Settings;

namespace RoeSnip.App.Overlay;

/// <summary>Commands that can be raised from any monitor's OverlayWindow (keyboard) or from the
/// toolbar attached to the monitor that currently holds the selection — OverlayController treats
/// both sources identically. Cancel closes the whole overlay unconditionally (toolbar X button).
/// CancelStage is Esc's two-stage semantics: if any monitor has a snip in progress (selection /
/// annotations / drag), clear it and stay open; only with nothing active does it close the
/// overlay. (Esc's innermost stages — an active inline text edit or an open color-info panel —
/// are consumed locally by OverlayWindow before CancelStage is ever raised.) ConfirmPlain is
/// Enter/double-click; Copy/Save/SaveHdr are the explicit Ctrl+C/Ctrl+S/toolbar-button requests.
/// Undo (Ctrl+Z) is handled locally inside OverlayWindow against its own AnnotationLayer and
/// never reaches the controller, since only the OS-focused window can receive it in the first
/// place — there's no cross-monitor ambiguity to resolve for it. Ported from the frozen WPF
/// app's src/RoeSnip/Overlay/OverlayController.cs.</summary>
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
    /// populated OverlayResult. Matches the AppComposition.RunOverlay hook signature exactly.
    /// Must be invoked on the Avalonia UI thread (Dispatcher.UIThread) — the WPF version's
    /// EnsureApplication dance has no Avalonia equivalent because App.axaml.cs (WP-X2) has
    /// already established Application.Current before any capture flow runs.</summary>
    public static Task<OverlayResult?> RunAsync(
        IReadOnlyList<(CapturedFrame Frame, SdrImage Preview)> monitors,
        RoeSnipSettings settings)
    {
        if (monitors.Count == 0)
        {
            return Task.FromResult<OverlayResult?>(null);
        }

        var session = new OverlaySession(monitors, settings);
        return session.RunAsync();
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
        private bool _confirmInProgress;

        public OverlaySession(IReadOnlyList<(CapturedFrame Frame, SdrImage Preview)> monitors, RoeSnipSettings settings)
        {
            _settings = settings;

            foreach (var (frame, preview) in monitors)
            {
                var window = new OverlayWindow(frame, preview, settings, OnActivatedByMouse, OnSelectionStarted, OnCommand);
                _windows.Add(window);
            }
        }

        public Task<OverlayResult?> RunAsync()
        {
            // Correlate every frame to its Avalonia screen and set position/size BEFORE Show()
            // (mixed-DPI discipline, PLAN-XPLAT.md §3.3). A frame with no exactly-matching screen
            // is skipped (logged inside TryPlaceOnScreen), never guessed.
            var placed = new List<OverlayWindow>();
            foreach (var window in _windows)
            {
                if (window.TryPlaceOnScreen())
                {
                    placed.Add(window);
                }
                else
                {
                    try
                    {
                        window.CloseOverlay(); // Closed isn't subscribed yet — no Finish side effect
                    }
                    catch (Exception)
                    {
                        // Best-effort disposal of a never-shown window.
                    }
                }
            }
            _windows.Clear();
            _windows.AddRange(placed);

            if (_windows.Count == 0)
            {
                return Task.FromResult<OverlayResult?>(null);
            }

            // If showing (or activating) one of these windows throws partway through, every window
            // already shown must be force-closed rather than left stranded, topmost, on screen with
            // no way for the user to dismiss it (same audit finding C.3 as the WPF version).
            var shown = new List<OverlayWindow>();
            try
            {
                foreach (var window in _windows)
                {
                    window.Closed += (_, _) => Finish(null);
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

                // A color-info panel belongs to where the user is working: when attention moves
                // to another monitor, dismiss any panel left open elsewhere (also guarantees at
                // most one panel exists across the whole session).
                foreach (var other in _windows)
                {
                    if (!ReferenceEquals(other, window))
                    {
                        other.DismissColorInfo();
                    }
                }
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
                    _ = ConfirmAsync(copy: _settings.CopyOnSelect, save: false, saveHdr: false);
                    break;

                case OverlayCommand.Copy:
                    _ = ConfirmAsync(copy: true, save: false, saveHdr: false);
                    break;

                case OverlayCommand.Save:
                    _ = ConfirmAsync(copy: false, save: true, saveHdr: false);
                    break;

                case OverlayCommand.SaveHdr:
                    // Sets SaveHdrRequested=true on the eventual result only — the actual HDR
                    // write happens back in AppComposition.RunCaptureFlowAsync using SourceFrame +
                    // SelectionPx (PLAN-XPLAT.md §3.3: the overlay never calls an HDR-export API).
                    _ = ConfirmAsync(copy: _settings.CopyOnSelect, save: false, saveHdr: true);
                    break;
            }
        }

        /// <summary>Esc's two-stage behavior, decided here because only the session can see every
        /// monitor: the selection may live on a different window than the one that has keyboard
        /// focus. Stage 1 dismisses any open color-info panel; stage 2 clears the in-progress snip
        /// (selection + annotations, back to the crosshair state); stage 3 — nothing active at all
        /// — closes the whole overlay. Each Esc press performs exactly one stage.</summary>
        private void CancelStage()
        {
            bool dismissedInfo = false;
            foreach (var window in _windows)
            {
                dismissedInfo |= window.DismissColorInfo();
            }
            if (dismissedInfo)
            {
                return;
            }

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

        /// <summary>The Avalonia analog of the WPF version's synchronous Confirm: clipboard and
        /// the save dialog are async here, so re-entrancy (e.g. mashing Ctrl+S while the picker
        /// is open) is guarded by <see cref="_confirmInProgress"/>. Semantics are otherwise
        /// identical: a cancelled Save dialog stays open; a failed copy doesn't block a save;
        /// a failed PNG write surfaces as SavedPngPath == null on the result.</summary>
        private async Task ConfirmAsync(bool copy, bool save, bool saveHdr)
        {
            if (_finished || _confirmInProgress)
            {
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

            _confirmInProgress = true;
            try
            {
                bool copyPerformed = false;
                if (copy)
                {
                    try
                    {
                        await ClipboardService.CopyImageAsync(window, rendered);
                        copyPerformed = true;
                        window.ShowShutterFlash();
                    }
                    catch (Exception)
                    {
                        // A failed clipboard write (e.g. locked by another process) shouldn't
                        // prevent the rest of confirm (an independently-requested Save should
                        // still happen); surfaced only via CopyPerformed staying false.
                    }
                }

                string? savedPath = null;
                if (save)
                {
                    savedPath = await TryPickSavePathAsync(window);
                    if (savedPath is null)
                    {
                        // User cancelled the Save dialog: stay open rather than silently
                        // discarding the selection/annotations they just made.
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

                if (_finished || window.SelectionPx is not { } selection)
                {
                    return; // overlay was closed/cleared externally while an await was pending
                }

                var result = new OverlayResult(
                    window.Monitor,
                    selection,
                    rendered,
                    window.Frame,
                    copyPerformed,
                    savedPath,
                    saveHdr);

                Finish(result);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"RoeSnip: overlay confirm failed: {ex.Message}");
            }
            finally
            {
                _confirmInProgress = false;
            }
        }

        private async Task<string?> TryPickSavePathAsync(OverlayWindow window)
        {
            try
            {
                System.IO.Directory.CreateDirectory(_settings.SaveDirectory);
            }
            catch (Exception)
            {
                // Fall through and let the picker itself surface a directory problem, if any.
            }

            IStorageFolder? startLocation = null;
            try
            {
                startLocation = await window.StorageProvider.TryGetFolderFromPathAsync(_settings.SaveDirectory);
            }
            catch (Exception)
            {
                // A missing/inaccessible start folder just means the picker opens at its default.
            }

            var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                SuggestedStartLocation = startLocation,
                SuggestedFileName = $"roesnip_{DateTime.Now:yyyyMMdd_HHmmss}.png",
                DefaultExtension = "png",
                FileTypeChoices = new[] { FilePickerFileTypes.ImagePng },
                ShowOverwritePrompt = true,
            });

            return file?.TryGetLocalPath();
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
