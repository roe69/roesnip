using Avalonia.Platform.Storage;
using RoeSnip.Core.Capture;
using RoeSnip.Core.Imaging;
using RoeSnip.Core.Overlay;
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

    /// <summary>Toolbar Share button's own click: upload to the caller's configured DEFAULT
    /// provider (Sharing/* subsystem, item 12). The dropdown's per-provider pick carries a payload
    /// this enum can't, so it bypasses OverlayCommand entirely - see OverlayWindow's own
    /// onShareToProvider callback.</summary>
    Share,
}

public static class OverlayController
{
    // ---------- Automation hooks (AppShell/AutomationServer.cs) — UI thread only, mirroring the
    // WPF app's OverlayController.s_activeSession/IsSessionActive/*ForAutomation wrappers. Only
    // ONE overlay session can ever be active at a time (RunAsync below is the sole entry point),
    // so a single static reference is enough; no stack/collection needed. ----------

    private static OverlaySession? s_activeSession; // UI thread only

    internal static bool IsSessionActive => s_activeSession is not null;

    internal static RectPhysical? GetSelectionForAutomation() => s_activeSession?.GetSelectionForAutomation();

    /// <summary>Reads s_activeSession into a local once and branches on IT rather than
    /// "s_activeSession?.Method(...) ?? "no active overlay session"" — a null-coalescing collapse
    /// here can't tell "no session" apart from "the session's own call legitimately returned null
    /// for success" (the exact bug the WPF app's automation wrappers hit and fixed, per that
    /// class's own doc comment).</summary>
    internal static string? SetSelectionForAutomation(RectPhysical virtualDesktopPx)
    {
        var session = s_activeSession;
        return session is null ? "no active overlay session" : session.SetSelectionForAutomation(virtualDesktopPx);
    }

    internal static string? CancelForAutomation()
    {
        var session = s_activeSession;
        return session is null ? "no active overlay session" : session.CancelForAutomation();
    }

    internal static string? ConfirmForAutomation(string action, string? path)
    {
        var session = s_activeSession;
        return session is null ? "no active overlay session" : session.ConfirmForAutomation(action, path);
    }

    /// <summary>One OverlayWindow per monitor; runs until the user cancels (Esc) or confirms
    /// (Enter / double-click / toolbar action). Returns null on cancel. On confirm, performs
    /// Copy/Save side effects itself (clipboard + PNG dialog) per DESIGN.md, then returns a
    /// populated OverlayResult. Matches the AppComposition.RunOverlay hook signature exactly.
    /// Must be invoked on the Avalonia UI thread (Dispatcher.UIThread) — the WPF version's
    /// EnsureApplication dance has no Avalonia equivalent because App.axaml.cs (WP-X2) has
    /// already established Application.Current before any capture flow runs.</summary>
    public static Task<OverlayResult?> RunAsync(
        IReadOnlyList<(CapturedFrame Frame, SdrImage Preview)> monitors,
        RoeSnipSettings settings,
        ITrayNotifier? notifier = null)
    {
        if (monitors.Count == 0)
        {
            return Task.FromResult<OverlayResult?>(null);
        }

        var session = new OverlaySession(monitors, settings, notifier);
        s_activeSession = session;
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

        // Sharing/* subsystem (item 12): used only by ShareCurrentSelection/ShareToSpecificProvider
        // to surface an upload's result (URL-copied balloon / honest error balloon) - every other
        // notifier-worthy event in this class (capture failures, HDR export failures) is handled by
        // AppComposition.RunCaptureFlowAsync instead, which owns the notifier for the WHOLE capture
        // flow. Nullable/optional (defaults null) so callers that construct a session without a tray
        // app in play (none exist today, but nothing here requires one) degrade to a silent no-op
        // via the same "?." every other ITrayNotifier consumer in this codebase uses.
        private readonly ITrayNotifier? _notifier;

        private OverlayWindow? _activeWindow;
        private bool _finished;
        private bool _confirmInProgress;
        private int _modalDepth; // O1 audit fix — see BeginModal/EndModal

        // ---------- Cross-monitor selection (item 09) ----------
        //
        // Both null for every ordinary, single-monitor selection — the pre-existing per-window
        // _selectionPx-based code paths (Confirm/GetSelectionForAutomation's own
        // _windows.FirstOrDefault(w => w.SelectionPx is not null)) are what run in that case,
        // completely unchanged. These two fields are only ever non-null while a NewSelection,
        // SpanningResize, or SpanningMove drag's candidate rect has been distributed across ≥2
        // monitors — see OnSpanningCandidate. See docs/DESIGN-MULTIMON-SELECTION.md for the full
        // design.
        private RectPhysical? _spanningVirtual;
        private OverlayWindow? _spanningPrimaryWindow;
        private readonly RectPhysical _virtualDesktopBounds;

        public OverlaySession(
            IReadOnlyList<(CapturedFrame Frame, SdrImage Preview)> monitors, RoeSnipSettings settings, ITrayNotifier? notifier = null)
        {
            _settings = settings;
            _notifier = notifier;

            var monitorBoundsForVdb = new List<RectPhysical>(monitors.Count);
            foreach (var (frame, preview) in monitors)
            {
                var window = new OverlayWindow(
                    frame, preview, settings, OnActivatedByMouse, OnSelectionStarted,
                    OnSpanningCandidate, FinalizeNewSelectionDrag, OnCommand, ShareToSpecificProvider);
                _windows.Add(window);
                monitorBoundsForVdb.Add(frame.Monitor.BoundsPx);
            }
            _virtualDesktopBounds = SpanningSelectionMath.ComputeVirtualDesktopBounds(monitorBoundsForVdb);
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
                // Automation hooks: this session never gets to Finish() (nothing was ever shown),
                // so it must clear the active-session marker itself here — otherwise
                // OverlayController.IsSessionActive would report true forever after a "no matching
                // screens" edge case, wedging every future `trigger`/state-dependent automation call.
                if (ReferenceEquals(s_activeSession, this))
                {
                    s_activeSession = null;
                }
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
                // Same leak this method's other early-exit branch documents: nothing calls Finish()
                // on this path either, so the active-session marker must be cleared here too.
                if (ReferenceEquals(s_activeSession, this))
                {
                    s_activeSession = null;
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
        /// clears any selection on B (and every other monitor). This still holds even for a
        /// spanning selection: it's a momentary reset at the START of a fresh NewSelection drag
        /// (before the click-vs-drag threshold is crossed) — the drag's very next OnSpanningCandidate
        /// call immediately redistributes the new candidate back out to every monitor it actually
        /// touches, so this never leaves a spanning selection's other windows stuck cleared.</summary>
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

        // ---------- Cross-monitor selection (item 09) ----------

        /// <summary>The one primitive that makes both a fresh drag AND a resize/move of an
        /// already-placed spanning selection correct (see OverlayWindow's NewSelection/
        /// SpanningResize/SpanningMove handlers, which all funnel their candidate rect through here
        /// rather than any window's own local frame): delegates the actual clamp/intersect/real-edge
        /// math to SpanningSelectionMath.Distribute (pure, unit-tested) and only does the
        /// WINDOW-touching side effect here — pushing each monitor's own intersection (or null) into
        /// that window via SetSpanningLocalSelection, the SAME dim-mask/adorner/toolbar-placement
        /// pipeline every ordinary single-monitor selection already used, just fed a different rect.
        /// Degrades to exactly the old per-window behavior whenever the candidate only ever touches
        /// one monitor (the common case): <see cref="_spanningVirtual"/> stays null, and that one
        /// window's own SetSpanningLocalSelection call is functionally identical to the old direct
        /// SetSelection call it replaced. <paramref name="preserveSize"/> is true only for a
        /// SpanningMove candidate — a move drag shifts every edge by the same delta, so a candidate
        /// that needs pulling back inside the virtual desktop must SLIDE (keep its width/height),
        /// never get clamped edge-by-edge the way a resize correctly does (see
        /// SpanningSelectionMath.SlideToBounds's own doc comment for why the two gestures need
        /// different clamp strategies).</summary>
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
            // that gap and intersect nothing, which would otherwise zero out every window's slice
            // mid-drag. When that happens during a preserveSize (Move) candidate and a spanning
            // selection was already in place, ignore this candidate outright and keep the drag's last
            // valid state instead of collapsing it.
            if (preserveSize && distribution.Hits.Count == 0 && _spanningVirtual is not null)
            {
                return;
            }

            // The owner's own monitor can stop intersecting the candidate entirely while the
            // selection still spans ≥2 OTHER monitors (drag the left edge of a 3-monitor span
            // rightward past the leftmost monitor's own boundary, for instance) — the owner is who
            // most recently drove the drag, but it is no longer guaranteed to hold a slice. Falling
            // back to "no window is primary" in that case would hide the toolbar and the true-size
            // badge on EVERY window, even though the selection is perfectly valid and just as
            // reachable as any other spanning selection — so fall back to the lowest-indexed monitor
            // that still holds a slice, a deterministic choice that's stable across repeated calls
            // with the same distribution (Hits is built in monitor-index order, so Hits[0] is always
            // that monitor).
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
                // SpanningMove drag's pointer-move is live, including for every non-owner window; see
                // OverlayWindow._suppressToolbarForSpanningDrag's own doc comment for why that matters.
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

        /// <summary>Pointer-up (or the `select` automation command) finalizing a NewSelection drag —
        /// or a SpanningResize/SpanningMove drag too; all three feed OnSpanningCandidate on every
        /// move and land here on release, so this method never needs to change to support the other
        /// two. Applies the existing "&lt;2px on either axis = cancel, not a real selection" rule
        /// against the TRUE selection size: the shared virtual rect while spanning, or else the
        /// single owning window's own local rect exactly as the pre-existing code did. (Judging the
        /// rule against just the calling window's own local slice would be wrong while spanning —
        /// that slice can legitimately be a couple of pixels wide right at a monitor seam even though
        /// the overall selection is large.) Every window's own SetSpanningLocalSelection call from
        /// the drag's last OnSpanningCandidate already left correct state in place; this only needs
        /// to act when the result must be discarded as too small.</summary>
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
            // dragInProgress:true on every pointer-move and needs it cleared now that the drag has
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
        /// (top-left-relative to the virtual selection rect's own origin). Computed once and consumed
        /// by BOTH the SDR (BGRA8, RenderSpanningSelection) and HDR (raw FP16,
        /// BuildSpanningFrameCropsForHdr) composites — same geometry, two different pixel sources, so
        /// the offset math lives in exactly one place.</summary>
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

        /// <summary>Byte-composites the final spanning selection: crops each intersecting window's
        /// own ALREADY tone-mapped preview (SdrImage.Crop — the same crop every single-monitor render
        /// already does) and copies it into a canvas sized to the virtual selection rect, at that
        /// window's own virtual-desktop-relative offset. Never touches FP16/tone-mapping — this only
        /// ever combines bytes each window's own OverlayWindow.Preview already tone-mapped with ITS
        /// OWN monitor's photometrics, per the hard constraint. Gaps (no captured monitor covers part
        /// of the rect) are left OPAQUE BLACK — a deliberate, documented choice (see
        /// docs/DESIGN-MULTIMON-SELECTION.md), not an accidental default. Annotations are never
        /// burned in here: a spanning selection can never have any (see OverlayWindow.
        /// SetSpanningLocalSelection), so this is a plain array composite, not a render-target pass.</summary>
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
                    Console.Error.WriteLine(
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
        /// vs. a WIC 128bppRGBAFloat encode) differ. Program.cs calls this via
        /// OverlayResult.SpanningFrameCrops + the AppComposition.WriteHdrExportSpanning hook,
        /// mirroring how the non-spanning path already threads SourceFrame/SelectionPx through for
        /// AppComposition.WriteHdrExport.</summary>
        private List<SpanningFrameCrop> BuildSpanningFrameCropsForHdr(RectPhysical virtualRect)
        {
            var list = new List<SpanningFrameCrop>();
            foreach (var geo in ComputeSpanningCropGeometry(virtualRect))
            {
                list.Add(new SpanningFrameCrop(geo.Window.Frame, geo.CropLocal, geo.DestX, geo.DestY));
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
                    FireAndForget(ConfirmAsync(copy: _settings.CopyOnSelect, save: false, saveHdr: false));
                    break;

                case OverlayCommand.Copy:
                    FireAndForget(ConfirmAsync(copy: true, save: false, saveHdr: false));
                    break;

                case OverlayCommand.Save:
                    FireAndForget(ConfirmAsync(copy: false, save: true, saveHdr: false));
                    break;

                case OverlayCommand.SaveHdr:
                    // Sets SaveHdrRequested=true on the eventual result only — the actual HDR
                    // write happens back in AppComposition.RunCaptureFlowAsync using SourceFrame +
                    // SelectionPx (PLAN-XPLAT.md §3.3: the overlay never calls an HDR-export API).
                    FireAndForget(ConfirmAsync(copy: _settings.CopyOnSelect, save: false, saveHdr: true));
                    break;

                case OverlayCommand.Share:
                    // Deliberately does NOT fall through to Finish() the way every command above
                    // does — see ShareCurrentSelection's own doc comment for why the overlay has to
                    // stay open for the whole upload.
                    ShareCurrentSelection();
                    break;
            }
        }

        /// <summary>Toolbar Share button (Sharing/* subsystem, item 12 — default-provider path only;
        /// see OverlayCommand.Share's own doc comment for the per-provider-dropdown scope note).
        /// Renders the CURRENT selection through the exact same path Copy uses —
        /// RenderSelectionWithAnnotations for a single-monitor selection, RenderSpanningSelection for
        /// a spanning one (same _spanningVirtual/_spanningPrimaryWindow check every other spanning-
        /// aware command here uses) — so whatever gets shared is pixel-identical to what Ctrl+C would
        /// have put on the clipboard.
        ///
        /// Unlike Copy/Save/SaveHdr, this never calls Finish(): those are all synchronous, one-shot
        /// side effects with nothing left to do once they return, but an upload is a network call
        /// that can legitimately take up to ShareManager's own 10-minute timeout. Closing the overlay
        /// immediately (the way Copy does) would leave the toolbar's SetShareBusy(true) feedback
        /// showing on a window that's already gone — so the overlay, and the toolbar-hosting window in
        /// particular, stays fully open and interactive (the user can still Cancel/Copy/Save
        /// independently) for the whole upload; only the Share button itself goes busy/disabled
        /// (ToolbarControl.SetShareBusy) until the result comes back. Mirrors the WPF app's
        /// OverlaySession.ShareCurrentSelection.</summary>
        private void ShareCurrentSelection()
        {
            if (!TryPrepareShareUpload(out var toolbarWindow, out var pngBytes))
            {
                return;
            }

            // Sharing/* subsystem (doc-honesty/unify-to-one-source fix, matching the WPF app):
            // resolved from the SAME settings snapshot ShowToolbar's own SetShareProviders call
            // populates the dropdown from (toolbarWindow.LiveSettings), not this session's own
            // separately-snapshotted _settings field — see OverlayWindow.LiveSettings' own doc
            // comment for why the two can disagree.
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
        }

        /// <summary>The dropdown's per-provider Share (Sharing/* subsystem, item 12 — this used to be
        /// raised by ToolbarControl and simply discarded in the WPF app before its own senior-review
        /// wiring fix; ported here already wired). Renders the current selection exactly like
        /// <see cref="ShareCurrentSelection"/> does; the only difference is which provider config the
        /// render gets uploaded to — resolved by id against the SAME <see cref="OverlayWindow.LiveSettings"/>
        /// source the dropdown itself was populated from, so a choice visible in the menu can never
        /// fail to resolve here.</summary>
        private void ShareToSpecificProvider(string providerId)
        {
            if (!TryPrepareShareUpload(out var toolbarWindow, out var pngBytes))
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
        }

        /// <summary>Shared render step for both <see cref="ShareCurrentSelection"/> (default
        /// provider) and <see cref="ShareToSpecificProvider"/> (dropdown pick) — everything the two
        /// would otherwise duplicate verbatim except which provider config the result gets uploaded
        /// to. Returns false (both out params default) on any no-op/failure condition, matching
        /// Confirm/Record's own "nothing selected yet" and render-failure guards.</summary>
        private bool TryPrepareShareUpload(out OverlayWindow? toolbarWindow, out byte[]? pngBytes)
        {
            toolbarWindow = null;
            pngBytes = null;

            SdrImage rendered;
            if (_spanningVirtual is { } spanningRect && _spanningPrimaryWindow is { } primary)
            {
                toolbarWindow = primary;
                try
                {
                    rendered = RenderSpanningSelection(spanningRect);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"RoeSnip: spanning-selection share render failed: {ex.Message}");
                    toolbarWindow = null;
                    return false;
                }
            }
            else
            {
                var window = _windows.FirstOrDefault(w => w.SelectionPx is not null);
                if (window is null)
                {
                    return false; // nothing selected yet — same no-op guard as Confirm
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
                Console.Error.WriteLine($"RoeSnip: share PNG encode failed: {ex.Message}");
                toolbarWindow = null;
                pngBytes = null;
                return false;
            }

            return true;
        }

        private void StartShareUpload(OverlayWindow toolbarWindow, RoeSnip.Core.Sharing.ShareProviderConfig config, byte[] pngBytes)
        {
            toolbarWindow.SetShareBusy(true);
            string fileName = $"roesnip_{DateTime.Now:yyyyMMdd_HHmmss}.png";
            _ = UploadShareAsync(config, pngBytes, fileName, toolbarWindow, _notifier);
        }

        /// <summary>The async half of <see cref="ShareCurrentSelection"/>/<see cref="ShareToSpecificProvider"/>,
        /// factored out as a static method (takes everything it needs as parameters) so it is
        /// obviously safe to run detached — no accidental capture of session/window mutable state
        /// beyond the explicit params. Never throws: ShareManager.UploadAsync already contracts to
        /// return a Success=false result rather than throw for an ordinary failure, and the try/catch
        /// here is belt-and-braces for the PNG stream/HTTP plumbing around that call, matching every
        /// other "fire and forget from a UI click" handler's own contract in this codebase.</summary>
        private static async Task UploadShareAsync(
            RoeSnip.Core.Sharing.ShareProviderConfig config, byte[] pngBytes, string fileName,
            OverlayWindow toolbarWindow, ITrayNotifier? notifier)
        {
            RoeSnip.Core.Sharing.ShareUploadResult result;
            try
            {
                using var stream = new System.IO.MemoryStream(pngBytes, writable: false);
                result = await RoeSnip.Core.Sharing.ShareManager
                    .UploadAsync(config, stream, fileName, "image/png", CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                result = new RoeSnip.Core.Sharing.ShareUploadResult(false, null, $"Upload failed: {ex.Message}");
            }

            // this whole session runs on the Avalonia UI thread already (RunAsync's own doc comment),
            // but the HTTP call above just resumed on a thread-pool thread (ConfigureAwait(false)) —
            // hop back via Dispatcher.UIThread.Post rather than assuming a bare `await` would resume
            // here, mirroring the WPF app's own Dispatcher.BeginInvoke marshal for the identical
            // reason. Discarded via `_ =`: nothing further to do once the UI-thread callback is
            // QUEUED, same "fire-and-forget async continuation on the UI thread" idiom TrayApp.cs
            // already uses (e.g. CompleteStartupAsync).
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = FinishShareUploadAsync(result, toolbarWindow, notifier));
        }

        /// <summary>The UI-thread tail of <see cref="UploadShareAsync"/>: restores the toolbar's
        /// Share buttons, copies a successful URL to the clipboard, and surfaces the result via the
        /// tray notifier. Best-effort throughout — the overlay may have been cancelled/confirmed out
        /// from under a still-in-flight upload (the user is free to keep interacting — see
        /// ShareCurrentSelection's own doc comment), so SetShareBusy on an already-closed window is
        /// wrapped defensively rather than allowed to turn a teardown race into an unhandled
        /// exception.</summary>
        private static async Task FinishShareUploadAsync(
            RoeSnip.Core.Sharing.ShareUploadResult result, OverlayWindow toolbarWindow, ITrayNotifier? notifier)
        {
            try { toolbarWindow.SetShareBusy(false); }
            catch (Exception ex) { Console.Error.WriteLine($"RoeSnip: post-share SetShareBusy failed (non-fatal): {ex.Message}"); }

            if (result.Success && result.Url is not null)
            {
                // ClipboardService.TryCopyTextAsync never throws (see its own doc comment) — its
                // bool return IS the honest "did the clipboard write actually happen" signal
                // ShowShareUploadedBalloon's second parameter needs, matching the WPF app's own
                // "vary the balloon wording on a failed copy, don't just claim success" fix.
                bool clipboardCopied = await ClipboardService.TryCopyTextAsync(toolbarWindow, result.Url);
                notifier?.ShowShareUploadedBalloon(result.Url, clipboardCopied);
            }
            else
            {
                notifier?.ShowError(result.ErrorMessage ?? "Share upload failed.");
            }
        }

        /// <summary>O8 audit fix: OnCommand's callers (key handlers, toolbar button clicks) cannot
        /// await, so ConfirmAsync is necessarily fire-and-forget from here — but a discarded Task
        /// whose exception nobody ever observes just vanishes on .NET Core (no crash, no log;
        /// unlike .NET Framework's process-terminating unobserved-exception behavior). That would
        /// silently turn Enter/Ctrl+C/Ctrl+S/Save-HDR into a no-op with zero diagnostic anywhere
        /// if anything above ConfirmAsync's own top-level catch ever throws. Always attach a
        /// continuation that logs a fault to stderr.</summary>
        private static void FireAndForget(Task task)
        {
            _ = task.ContinueWith(
                t => Console.Error.WriteLine(
                    $"RoeSnip: unhandled overlay command failure: {t.Exception?.GetBaseException().Message}"),
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
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

            if (_spanningVirtual is { } spanningRect && _spanningPrimaryWindow is { } primary)
            {
                await ConfirmSpanningAsync(spanningRect, primary, copy, save, saveHdr);
                return;
            }

            var window = _windows.FirstOrDefault(w => w.SelectionPx is not null);
            if (window is null)
            {
                return; // nothing selected yet — Enter/Ctrl+C/Ctrl+S/Save-HDR are no-ops until then
            }

            // Snapshot the selection this render was produced from — used below (O1 audit fix) to
            // detect whether the session changed underneath an in-flight Save picker await.
            var selectionAtRenderTime = window.SelectionPx;

            SdrImage rendered;
            try
            {
                rendered = window.RenderSelectionWithAnnotations();
            }
            catch (InvalidOperationException)
            {
                return;
            }
            catch (ArgumentOutOfRangeException)
            {
                // O8 audit fix: RenderSelectionWithAnnotations clamps defensively, but treat any
                // remaining out-of-bounds crop the same as "nothing to confirm yet" rather than
                // letting it fault this Task silently (FireAndForget still logs it either way).
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
                    // O1 audit fix: the save picker is only OWNER-modal — while it's open the
                    // session would otherwise stay fully interactive, so e.g. Esc on a DIFFERENT
                    // monitor could cancel/close the whole overlay while this await is pending.
                    // Suspend every window's own input handling for the duration.
                    BeginModal();
                    try
                    {
                        savedPath = await TryPickSavePathAsync(window);
                    }
                    finally
                    {
                        EndModal();
                    }
                    if (savedPath is null)
                    {
                        // User cancelled the Save dialog: stay open rather than silently
                        // discarding the selection/annotations they just made.
                        return;
                    }

                    // Re-check session state BEFORE writing (O1 audit fix): even with input
                    // suspended above, the session can still end from outside this method (e.g.
                    // the resident process shutting down, or another completion racing in) while
                    // the picker's await was pending — and the rendered crop was captured from
                    // `selectionAtRenderTime`, which must still be what's selected.
                    if (_finished || window.SelectionPx != selectionAtRenderTime)
                    {
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

        /// <summary>Cross-monitor selection's own Confirm path (item 09): Copy/Save/Save-HDR.
        /// Mirrors the non-spanning ConfirmAsync above (same clipboard/save-picker/write calls, same
        /// stay-open-on-cancelled-dialog behavior) but renders via RenderSpanningSelection's byte
        /// composite instead of a single window's annotated crop, and packages the result with
        /// OverlayResult.SpanningVirtualSelectionPx/SpanningFrameCrops set — SpanningFrameCrops is
        /// populated unconditionally (not just when saveHdr is true), same as the non-spanning path
        /// always carries SourceFrame/SelectionPx regardless of what the user clicked, so
        /// settings.AutoSaveHdrCopy works identically for a spanning result too.</summary>
        private async Task ConfirmSpanningAsync(RectPhysical virtualRect, OverlayWindow primary, bool copy, bool save, bool saveHdr)
        {
            SdrImage rendered;
            try
            {
                rendered = RenderSpanningSelection(virtualRect);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"RoeSnip: spanning-selection render failed: {ex.Message}");
                return;
            }

            var selectionAtRenderTime = virtualRect;

            _confirmInProgress = true;
            try
            {
                bool copyPerformed = false;
                if (copy)
                {
                    try
                    {
                        await ClipboardService.CopyImageAsync(primary, rendered);
                        copyPerformed = true;
                        primary.ShowShutterFlash();
                    }
                    catch (Exception)
                    {
                        // Same non-fatal reasoning as the single-monitor path above.
                    }
                }

                string? savedPath = null;
                if (save)
                {
                    BeginModal();
                    try
                    {
                        savedPath = await TryPickSavePathAsync(primary);
                    }
                    finally
                    {
                        EndModal();
                    }
                    if (savedPath is null)
                    {
                        return; // user cancelled the Save dialog — stay open, same as the single-monitor path
                    }

                    if (_finished || _spanningVirtual != selectionAtRenderTime)
                    {
                        return; // the session/spanning selection changed while the picker's await was pending
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

                if (_finished || _spanningVirtual is not { } finalVirtual)
                {
                    return; // overlay was closed/cleared externally while an await was pending
                }

                var result = new OverlayResult(
                    primary.Monitor,
                    primary.SelectionPx ?? default,
                    rendered,
                    primary.Frame,
                    copyPerformed,
                    savedPath,
                    SaveHdrRequested: saveHdr,
                    SpanningVirtualSelectionPx: finalVirtual,
                    SpanningFrameCrops: BuildSpanningFrameCropsForHdr(finalVirtual));

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

        /// <summary>Suspends every window's own key/pointer input handling while the (only
        /// owner-modal) save picker is open (O1 audit fix). Reentrant via a depth counter since
        /// nothing here strictly prevents nested modal sections in principle.</summary>
        private void BeginModal()
        {
            _modalDepth++;
            if (_modalDepth == 1)
            {
                foreach (var w in _windows)
                {
                    w.SessionInputSuspended = true;
                }
            }
        }

        private void EndModal()
        {
            _modalDepth = Math.Max(0, _modalDepth - 1);
            if (_modalDepth == 0)
            {
                foreach (var w in _windows)
                {
                    w.SessionInputSuspended = false;
                }
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

            if (ReferenceEquals(s_activeSession, this))
            {
                s_activeSession = null;
            }

            _completion.TrySetResult(result);
        }

        // ---------- Automation hooks (AppShell/AutomationServer.cs) — see OverlayController's own
        // wrapper methods for why these exist on this private nested class instead of being called
        // directly. Ported from the WPF app's OverlayController.OverlaySession. ----------

        internal RectPhysical? GetSelectionForAutomation()
        {
            // Cross-monitor selection (item 09): the shared virtual rect IS already virtual-desktop
            // physical pixels (the wire protocol's own convention), so it's returned as-is — no
            // per-window origin math needed, unlike the non-spanning fallback below.
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

        /// <summary>Cross-monitor selection (item 09): the `select` automation command's own lever
        /// for spanning rects — routes through the EXACT same OnSpanningCandidate/
        /// FinalizeNewSelectionDrag functions a real drag uses, never a separate implementation. The
        /// owning/"primary" window is chosen the same way the pre-existing single-monitor automation
        /// path already did: whichever monitor contains the rect's top-left corner.</summary>
        internal string? SetSelectionForAutomation(RectPhysical virtualDesktopPx)
        {
            var normalized = virtualDesktopPx.Normalized();
            var window = _windows.FirstOrDefault(w =>
            {
                var b = w.Monitor.BoundsPx;
                return normalized.Left >= b.Left && normalized.Left < b.Right
                    && normalized.Top >= b.Top && normalized.Top < b.Bottom;
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
            OnSpanningCandidate(window, normalized, preserveSize: false);
            FinalizeNewSelectionDrag(window); // applies the same too-small-cancel rule a real pointer-up would
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
                    FireAndForget(ConfirmAsync(copy: true, save: false, saveHdr: false));
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
                    // Sharing/* subsystem (item 12): raises the exact OverlayCommand.Share the
                    // toolbar's Share button raises — same detached-upload/URL-to-clipboard/balloon
                    // flow, same stay-open semantics (this response's trailing state snapshot still
                    // shows the overlay, unlike copy/save which close it; the upload's own result
                    // arrives later via the tray balloon). ShareCurrentSelection has its own
                    // no-provider guard; the no-selection case is pre-checked here so automation gets
                    // an explicit error instead of that method's silent no-op. Mirrors the WPF app's
                    // own OverlaySession.ConfirmForAutomation.
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

        /// <summary>Save's automation path: the same render + PngWriter.WriteFile calls ConfirmAsync/
        /// ConfirmSpanningAsync already make, just writing straight to <paramref name="path"/>
        /// instead of awaiting the interactive save picker. Fully synchronous (no dialog, no
        /// clipboard) so — unlike ConfirmAsync — it needs no fire-and-forget wrapper; any failure is
        /// caught and logged here directly.</summary>
        private void ConfirmSaveForAutomation(string path)
        {
            if (_finished || _confirmInProgress)
            {
                return;
            }

            if (_spanningVirtual is { } spanningRect && _spanningPrimaryWindow is { } primary)
            {
                SdrImage renderedSpanning;
                try
                {
                    renderedSpanning = RenderSpanningSelection(spanningRect);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"RoeSnip: spanning-selection render failed: {ex.Message}");
                    return;
                }

                try
                {
                    PngWriter.WriteFile(path, renderedSpanning);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"RoeSnip: automation save failed: {ex.Message}");
                    return;
                }

                var spanningResult = new OverlayResult(
                    primary.Monitor, primary.SelectionPx ?? default, renderedSpanning, primary.Frame,
                    CopyPerformed: false, SavedPngPath: path, SaveHdrRequested: false,
                    SpanningVirtualSelectionPx: spanningRect,
                    // Automation's "save" action is PNG-only (see this method's own doc comment /
                    // ConfirmForAutomation's), same as SaveHdrRequested staying false above — but
                    // SpanningFrameCrops is still populated so settings.AutoSaveHdrCopy (independent
                    // of what this particular call asked for) keeps working for an automation-driven
                    // spanning save exactly like it does for an interactive one.
                    SpanningFrameCrops: BuildSpanningFrameCropsForHdr(spanningRect));
                Finish(spanningResult);
                return;
            }

            var window = _windows.FirstOrDefault(w => w.SelectionPx is not null);
            if (window is null)
            {
                return;
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
            catch (ArgumentOutOfRangeException)
            {
                return;
            }

            try
            {
                PngWriter.WriteFile(path, rendered);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"RoeSnip: automation save failed: {ex.Message}");
                return;
            }

            if (_finished || window.SelectionPx is not { } selection)
            {
                return; // overlay was closed/cleared externally while this ran
            }

            var result = new OverlayResult(window.Monitor, selection, rendered, window.Frame, false, path, false);
            Finish(result);
        }
    }
}

file static class ModuleInit
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Init() => AppComposition.RunOverlay = OverlayController.RunAsync;
}
