using System.Globalization;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RoeSnip.Core.Capture;
using RoeSnip.Core.Imaging;
using RoeSnip.Core.Overlay;
using RoeSnip.Core.Settings;
using Path = Avalonia.Controls.Shapes.Path;

namespace RoeSnip.App.Overlay;

/// <summary>One per monitor. Shows the frozen tone-mapped preview, dims everything outside the
/// current selection, handles drag-to-select / resize / move, hosts the magnifier and (once a
/// selection exists) the annotation toolbar, and renders the final annotated crop on confirm.
/// All selection/annotation/crop math is physical pixels; DIPs from Avalonia pointer events are
/// converted via <see cref="_scaleX"/>/<see cref="_scaleY"/> (from the correlated Screen's
/// Scaling — Avalonia's direct analog of WPF's CompositionTarget.TransformToDevice M11/M22) at
/// the point of use, per the mixed-DPI contract (PLAN-XPLAT.md §3.3). Ported from the frozen WPF
/// app's src/RoeSnip/Overlay/OverlayWindow.xaml(.cs); interaction behavior is intended to be
/// identical, including the 4px click-vs-drag threshold and the Esc staging.</summary>
public partial class OverlayWindow : Window
{
    // SelectedMove/SelectedResize/SelectedEndpoint (Feature B, additive) are the Select-tool drags
    // on a picked annotation itself — distinct from NewSelection/Resize/Move (the CROP selection)
    // and from Annotation (drawing a brand-new shape with a tool active). SelectedEndpoint is
    // Line/Arrow's analogue of SelectedResize: there's no rect to grow/shrink, so dragging
    // repositions one endpoint (see _dragEndpointIndex) instead. SpanningResize/SpanningMove
    // (cross-monitor selection, item 09) are the spanning-aware twins of Resize/Move — used only
    // while IsSpanningSelection is true, and unlike Resize/Move they never touch _dragStartRect/
    // SetSelection directly: every candidate they compute is a VIRTUAL-desktop rect fed through
    // _onSpanningCandidate, the exact same distribute primitive a NewSelection drag uses. Mirrors
    // WPF's DragMode (src/RoeSnip/Overlay/OverlayWindow.xaml.cs:54-58).
    private enum DragMode
    {
        None, NewSelection, Resize, Move, Annotation, SelectedMove, SelectedResize, SelectedEndpoint,
        SpanningResize, SpanningMove,
    }

    private readonly CapturedFrame _frame;
    private readonly SdrImage _preview;

    // Not readonly (unlike _frame/_preview above): the magnifier wheel-zoom handler below persists
    // a dialed MagnifierSampleRadius back into this live copy (same "_liveSettings with { ... };
    // TrySaveLiveSettings()" pattern as the frozen WPF app's OverlayWindow), so later sessions in
    // this same tray-app process pick up the new default without a restart.
    private RoeSnipSettings _liveSettings;
    private readonly Action<OverlayWindow> _onActivatedByMouse;
    private readonly Action<OverlayWindow> _onSelectionStarted;
    // Cross-monitor selection (item 09): the two callbacks every SPANNING drag uses instead of
    // calling SetSelection directly — see OverlayController.OverlaySession.OnSpanningCandidate/
    // FinalizeNewSelectionDrag for what they do. NewSelection always routed through these (even for
    // a same-monitor drag — it degrades to the old per-window behavior when Distribute reports a
    // single hit); SpanningResize/SpanningMove do too, for the same reason. The plain single-monitor
    // Move/Resize/annotation drag modes are untouched and still call SetSelection locally; they never
    // run while IsSpanningSelection is true (see OnPreviewPointerPressed's spanning branch). The
    // trailing bool is preserveSize: true only for a SpanningMove candidate, so OnSpanningCandidate
    // can slide the rect back into the virtual desktop's bounds instead of clamping each edge
    // independently (which would shrink it) — see SpanningSelectionMath.SlideToBounds's own doc
    // comment.
    private readonly Action<OverlayWindow, RectPhysical, bool> _onSpanningCandidate;
    private readonly Action<OverlayWindow> _onFinalizeNewSelection;
    private readonly Action<OverlayCommand> _onCommand;

    /// <summary>Sharing/* subsystem (item 12): the dropdown's per-provider pick carries a payload
    /// OverlayCommand can't, so it goes through its own dedicated callback instead of _onCommand —
    /// same reason onColorPicked (if this port had one) would bypass the enum too. Mirrors the WPF
    /// app's own OverlayWindow._onShareToProvider.</summary>
    private readonly Action<string> _onShareToProvider;

    private readonly Image _previewImage;
    private readonly Path _dimPath;
    private readonly Canvas _overlayCanvas;
    private readonly AnnotationLayer _annotations;
    private readonly SelectionAdorner _adorner;
    private readonly Magnifier _magnifier;

    private double _scaleX = 1.0, _scaleY = 1.0;
    private RectPhysical? _selectionPx;

    /// <summary>The frozen preview bitmap backing <see cref="_previewImage"/>. Kept in its own
    /// field (O4 audit fix) so it can be explicitly disposed — WriteableBitmap owns native/GPU
    /// backing memory that isn't reliably freed just by dropping the Image control's Source
    /// reference on a window that may never even finish showing.</summary>
    private WriteableBitmap? _previewBitmap;

    /// <summary>A mouse-down that would start a new selection is treated as a *click* (color
    /// inspection) until the cursor has travelled at least this many physical pixels — only then
    /// does it become a selection drag. Identical constant and role as the WPF app.</summary>
    private const double ClickDragThresholdPx = 4.0;

    private DragMode _dragMode = DragMode.None;
    private SelectionHandle _activeHandle = SelectionHandle.None;
    private Point _dragAnchorPx;
    private Point _dragAnchorDip;
    private bool _newSelectionPending;

    /// <summary>Last physical-pixel pointer position seen by <see cref="OnPreviewPointerMoved"/> —
    /// kept around so <see cref="UpdateCursor"/> can re-hit-test the same spot when it's invoked
    /// from a non-pointer-move trigger (a tool change), where there's no fresh position to hand it.</summary>
    private Point _lastHoverPx;

    /// <summary>Last KeyModifiers seen on a pointer event — kept around so <see cref="UpdateCursor"/>
    /// and the modifier-grab hit test can re-evaluate "is Shift/Ctrl held" when invoked from a
    /// non-pointer trigger (a tool change), mirroring how <see cref="_lastHoverPx"/> is reused for
    /// the same reason. WPF reads a live global (Keyboard.Modifiers) instead; Avalonia has no direct
    /// analog wired into this app, so this is the nearest equivalent.</summary>
    private KeyModifiers _lastKeyModifiers;

    private RectPhysical _dragStartRect;

    // Cross-monitor selection (item 09): the shared virtual rect at the moment a SpanningResize/
    // SpanningMove drag started, in virtual-desktop physical pixels — the frame of reference EVERY
    // subsequent pointer-move of that drag computes its candidate against (ApplyResize is a pure
    // function of (start rect, handle, pointer position) — "pointer position" just means something
    // different, window-local vs. virtual-desktop, depending on the caller).
    private RectPhysical _dragStartVirtualRect;

    private Border? _colorInfoPanel;
    private IPointer? _capturedPointer;

    /// <summary>Feature B: the selected shape's own rect at the start of a SelectedResize drag — the
    /// ApplyResize/ClampToFrame math below is reused verbatim from the crop-selection resize, just
    /// fed this rect instead of _dragStartRect.</summary>
    private RectPhysical _dragStartShapeRect;

    /// <summary>Which endpoint (0 = P0, 1 = P1) a DragMode.SelectedEndpoint drag is repositioning —
    /// set once at pointer-down from AnnotationLayer.HitTestSelectedEndpoint, read on every
    /// subsequent pointer-move until pointer-up ends the gesture.</summary>
    private int _dragEndpointIndex;

    /// <summary>The ORIGINAL shape being re-edited via double-click, so CommitActiveTextEditor knows
    /// to Replace it instead of adding a new shape, and CancelActiveTextEditor knows to leave it
    /// untouched. Null for a brand-new text placement.</summary>
    private AnnotationShape? _textEditReplacing;

    private AnnotationTool _currentTool = AnnotationTool.None;
    private Color _currentColor = Colors.Red;
    private double _currentStrokeWidth = 4.0;

    /// <summary>Item 16: per-tool custom cursor bitmaps (tool/color/strokeWidth-keyed), the
    /// Avalonia port of WPF's own ToolCursorCache. One instance per window/session — disposed
    /// alongside the preview bitmap on Closed below.</summary>
    private readonly ToolCursorCache _toolCursorCache = new();

    // Last-used text-annotation style (toolbar's text-style group, item 08) — applied to new text
    // annotations and carried across overlay sessions via _liveSettings, mirroring WPF's own
    // _textFontFamily/_textFontSize/_textBold/_textItalic fields (OverlayWindow.xaml.cs:174-177).
    private string _textFontFamily;
    private double _textFontSize;
    private bool _textBold;
    private bool _textItalic;

    private ToolbarControl? _toolbar;
    private TextBox? _activeTextEditor;
    private Point _activeTextEditorOriginPx;

    internal MonitorInfo Monitor => _frame.Monitor;
    internal CapturedFrame Frame => _frame;
    internal RectPhysical? SelectionPx => _selectionPx;

    /// <summary>Cross-monitor selection (item 09): the already-tone-mapped preview, exposed so
    /// OverlaySession.RenderSpanningSelection can crop it directly — the SAME per-monitor bytes the
    /// local (non-spanning) render path (RenderSelectionWithAnnotations) already crops from, never a
    /// re-tone-map.</summary>
    internal SdrImage Preview => _preview;

    /// <summary>Sharing/* subsystem (item 12): the settings snapshot ShowToolbar's own
    /// SetShareProviders call populates the provider picker from — exposed so
    /// OverlaySession.ShareCurrentSelection/ShareToSpecificProvider resolve the actual upload target
    /// from this SAME snapshot rather than the session's own separately-snapshotted _settings field,
    /// so the dropdown's own choices can never disagree with what a click actually uploads to. Never
    /// re-read from disk while the overlay stays open (see ShowToolbar's own doc comment on
    /// SetShareProviders) — this is a live copy of THIS window's _liveSettings, same field the
    /// palette/audio-toggle persistence already mutates in place. Mirrors the WPF app's own
    /// OverlayWindow.LiveSettings.</summary>
    internal RoeSnipSettings LiveSettings => _liveSettings;

    /// <summary>Cross-monitor selection: true while the CURRENT selection (this window's own local
    /// slice included) touches more than one monitor — set only by SetSpanningLocalSelection, never
    /// by the plain SetSelection path Move/Resize/annotation drags still use. Read by
    /// OnPreviewPointerPressed to hit-test through the Adorner (RealEdges-gated) instead of always
    /// starting a fresh NewSelection, and by UpdateToolbarPlacement to hide the toolbar on every
    /// non-primary window.</summary>
    internal bool IsSpanningSelection => _isSpanningSelection;

    private bool _isSpanningSelection;
    private bool _isSpanningPrimary;

    // A SpanningResize/SpanningMove (or NewSelection) drag redistributes its candidate to EVERY
    // window on EVERY pointer-move (see OverlaySession.OnSpanningCandidate) — including windows that
    // are not the drag owner and so have _dragMode == None throughout. UpdateToolbarPlacement's
    // mid-drag suppression is keyed on _dragMode, which correctly hides the toolbar on the owner but
    // does nothing for a non-owner window whose candidate slice just appeared/changed via
    // SetSpanningLocalSelection — that window would call ShowToolbar() on every pointer-move and the
    // toolbar would visibly slide around under the live drag. This flag is set whenever
    // SetSpanningLocalSelection runs as part of an in-progress drag (regardless of which window owns
    // it) and cleared only by NotifySpanningDragEnded, which the session calls on every window once
    // FinalizeNewSelectionDrag has settled the drag for good.
    private bool _suppressToolbarForSpanningDrag;

    /// <summary>Cross-monitor selection: the shared virtual-desktop rect from the most recent
    /// SetSpanningLocalSelection call, or null when not spanning. Every window gets the SAME value
    /// (OnSpanningCandidate passes it to every window, not just the primary), so a SpanningResize/
    /// SpanningMove drag can start from ANY window whose own local slice offers a real handle/body,
    /// not just the primary one; whichever window the drag starts on becomes primary for its
    /// duration (and after — same rule a NewSelection drag already follows).</summary>
    private RectPhysical? _spanningVirtualRectPx;

    /// <summary>O1 audit fix: set by OverlayController while the (only owner-modal) save picker
    /// is open, on EVERY window in the session — not just the one that opened the picker — so a
    /// key/pointer event on a DIFFERENT monitor can't cancel/close/mutate the session while a
    /// write is in flight. Checked at the top of every tunnel-routed input handler below.</summary>
    internal bool SessionInputSuspended { get; set; }

    /// <summary>True while this window has anything worth cancelling short of closing the whole
    /// overlay — used by OverlayController's two-stage Esc (stage: clear snip, stay open).</summary>
    internal bool HasSnipInProgress =>
        _selectionPx is not null || _annotations.HasAnnotations || _dragMode != DragMode.None
        || _annotations.SelectedShape is not null;

    /// <summary>XAML-infrastructure-only constructor (satisfies the XAML compiler's public-ctor
    /// expectation, AVLN3001) — RoeSnip's own code always uses the internal data-taking overload
    /// below; a window created this way is inert and must not be shown.</summary>
    public OverlayWindow()
    {
        AvaloniaXamlLoader.Load(this);

        T Find<T>(string name) where T : Control =>
            this.FindControl<T>(name) ?? throw new InvalidOperationException($"OverlayWindow.axaml is missing '{name}'.");

        _previewImage = Find<Image>("PreviewImage");
        _dimPath = Find<Path>("DimPath");
        _overlayCanvas = Find<Canvas>("OverlayCanvas");
        _annotations = Find<AnnotationLayer>("Annotations");
        _adorner = Find<SelectionAdorner>("Adorner");
        _magnifier = Find<Magnifier>("MagnifierControl");

        _frame = null!;
        _preview = null!;
        _liveSettings = null!;
        _onActivatedByMouse = null!;
        _onSelectionStarted = null!;
        _onSpanningCandidate = null!;
        _onFinalizeNewSelection = null!;
        _onCommand = null!;
        _onShareToProvider = null!;
        _textFontFamily = "Segoe UI";
    }

    internal OverlayWindow(
        CapturedFrame frame,
        SdrImage preview,
        RoeSnipSettings settings,
        Action<OverlayWindow> onActivatedByMouse,
        Action<OverlayWindow> onSelectionStarted,
        Action<OverlayWindow, RectPhysical, bool> onSpanningCandidate,
        Action<OverlayWindow> onFinalizeNewSelection,
        Action<OverlayCommand> onCommand,
        Action<string> onShareToProvider)
        : this()
    {
        _frame = frame;
        _preview = preview;
        _liveSettings = settings;
        _onActivatedByMouse = onActivatedByMouse;
        _onSelectionStarted = onSelectionStarted;
        _onSpanningCandidate = onSpanningCandidate;
        _onFinalizeNewSelection = onFinalizeNewSelection;
        _onCommand = onCommand;
        _onShareToProvider = onShareToProvider;
        _textFontFamily = settings.TextFontFamily;
        _textFontSize = settings.TextFontSize;
        _textBold = settings.TextBold;
        _textItalic = settings.TextItalic;

        // Nearest-neighbor scaling for the frozen preview, same as the WPF version's
        // BitmapScalingMode.NearestNeighbor — on an all-96-DPI machine this is a 1:1 blit anyway.
        RenderOptions.SetBitmapInterpolationMode(_previewImage, BitmapInterpolationMode.None);
        _previewBitmap = preview.ToAvaloniaBitmap();
        _previewImage.Source = _previewBitmap;
        _annotations.PreviewSource = preview; // Pixelate tool mosaics these pixels (raw SdrImage, not the Avalonia bitmap)
        _magnifier.SampleRadius = settings.MagnifierSampleRadius;

        // O4 audit fix: free the preview bitmap on every path this window stops being used —
        // the normal Closed path, AND CloseOverlay() below covers windows that were placed but
        // never actually got to fire Closed (e.g. TryPlaceOnScreen succeeded but Show() threw
        // partway through the session, or the window was closed before ever being shown).
        Closed += (_, _) => DisposePreviewBitmap();
        Closed += (_, _) => _toolCursorCache.Dispose();

        // Tunnel-routed handlers are the Avalonia analog of WPF's Preview* events — they run
        // before any child control (toolbar buttons, text editor) sees the event.
        AddHandler(PointerPressedEvent, OnPreviewPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnPreviewPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnPreviewPointerReleased, RoutingStrategies.Tunnel);
        AddHandler(PointerWheelChangedEvent, OnPreviewPointerWheelChanged, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        PointerEntered += OnPointerEnteredWindow;

        SizeChanged += (_, _) =>
        {
            SyncChromeSizes();
            UpdateDimGeometry();
        };

        // The WPF version read the actual device transform at SourceInitialized; the Avalonia
        // analog is RenderScaling once the window is opened. The window is NEVER repositioned or
        // resized here (Avalonia mixed-DPI bugs #13917/#17834 — position/size are final before
        // Show); only the DIP<->px conversion factors are refreshed if the platform reports a
        // different effective scale than the Screen did.
        Opened += (_, _) =>
        {
            // D6 parity (WPF OverlayWindow.xaml.cs:536-555): exclude this overlay window from
            // screen capture. The platform handle only exists once Opened fires, so this can't
            // move any earlier. See WindowCaptureExclusion's doc comment for why permanent
            // application is safe (this app never re-captures the screen while an overlay window
            // is up) and for the ROESNIP_DIAG_NOEXCLUDE=1 escape hatch.
            AppShell.WindowCaptureExclusion.Apply(this);

            double actual = RenderScaling;
            if (actual > 0 && (Math.Abs(actual - _scaleX) > 1e-9 || Math.Abs(actual - _scaleY) > 1e-9))
            {
                // O2 audit fix: adopting the new scale WITHOUT re-deriving the DIP size breaks
                // both invariants at once. TryPlaceOnScreen set Width/Height = physical bounds /
                // the ASSUMED Screen.Scaling; if RenderScaling turns out different, the window's
                // actual on-screen PHYSICAL size (DIPs * RenderScaling) no longer equals the
                // monitor's physical bounds — the overlay would under/over-cover the monitor, and
                // every DIP<->px conversion done since Show() (selection math, magnifier sampling)
                // would already have used the wrong scale for whatever the user did before Opened
                // fired. Fix coherently: recompute DIP ClientSize from the ORIGINAL physical
                // bounds and the RUNTIME scale (never from the current, already-wrong DIP size),
                // THEN update _scaleX/_scaleY so everything after this point uses one consistent
                // scale. This still resizes the window in place after it's already shown — a
                // documented lesser evil versus the mixed-DPI Avalonia resize bugs (#13917/#17834)
                // the "never reposition/resize post-Show" discipline exists to avoid; it only
                // fires on this rare mismatch path, never on the common one.
                var boundsPx = _frame.Monitor.BoundsPx;
                Console.Error.WriteLine(
                    $"RoeSnip: overlay for monitor {_frame.Monitor.Index} opened with RenderScaling {actual} " +
                    $"but Screen.Scaling was {_scaleX}; resizing to {boundsPx.Width}x{boundsPx.Height}px " +
                    $"({boundsPx.Width / actual:0.##}x{boundsPx.Height / actual:0.##} DIP) to keep monitor " +
                    "coverage and 1:1 preview scaling correct for the runtime scale.");

                _scaleX = _scaleY = actual;
                Width = boundsPx.Width / actual;
                Height = boundsPx.Height / actual;

                PropagateDeviceScale();
                UpdateDimGeometry();
                UpdateToolbarPlacement();
            }
            SyncChromeSizes();
        };
    }

    /// <summary>Correlates this window's CapturedFrame to the Avalonia Screen with the exact same
    /// physical-pixel bounds, then positions (physical PixelPoint) and sizes (DIPs = physical /
    /// Scaling) the window — all BEFORE Show(), per the mixed-DPI discipline. Returns false (and
    /// logs to stderr) if no screen matches exactly — e.g. a monitor unplugged between capture and
    /// overlay show; the caller skips this window rather than guessing a match (PLAN-XPLAT §3.3).</summary>
    internal bool TryPlaceOnScreen()
    {
        var bounds = _frame.Monitor.BoundsPx;
        Screen? match = null;
        foreach (var screen in Screens.All)
        {
            var b = screen.Bounds;
            if (b.X == bounds.Left && b.Y == bounds.Top && b.Width == bounds.Width && b.Height == bounds.Height)
            {
                match = screen;
                break;
            }
        }

        if (match is null)
        {
            Console.Error.WriteLine(
                $"RoeSnip: no Avalonia screen matches monitor {_frame.Monitor.Index} " +
                $"({_frame.Monitor.DeviceName}, bounds {bounds.Left},{bounds.Top} {bounds.Width}x{bounds.Height}); " +
                "skipping its overlay window.");
            return false;
        }

        double scale = match.Scaling > 0 ? match.Scaling : 1.0;
        _scaleX = _scaleY = scale;

        Position = new PixelPoint(bounds.Left, bounds.Top);
        Width = bounds.Width / scale;
        Height = bounds.Height / scale;

        PropagateDeviceScale();
        UpdateDimGeometry();
        return true;
    }

    private void PropagateDeviceScale()
    {
        _annotations.DeviceScaleX = _scaleX;
        _annotations.DeviceScaleY = _scaleY;
        _adorner.DeviceScaleX = _scaleX;
        _adorner.DeviceScaleY = _scaleY;
        _magnifier.DeviceScaleX = _scaleX;
        _magnifier.DeviceScaleY = _scaleY;
    }

    private void SyncChromeSizes()
    {
        double w = ClientSize.Width, h = ClientSize.Height;
        if (w <= 0 || h <= 0)
        {
            return;
        }
        _annotations.Width = w;
        _annotations.Height = h;
        _adorner.Width = w;
        _adorner.Height = h;
        _magnifier.Width = w;
        _magnifier.Height = h;
    }

    // ---------- Pointer interaction ----------

    private Point ToPhysical(Point dip) => new(dip.X * _scaleX, dip.Y * _scaleY);

    /// <summary>Gates HitTestEditable to the active drawing tool: only shapes of the SAME tool are
    /// selectable/editable while a drawing tool is active (blur only grabs blurs, line only grabs
    /// lines, etc.). The Select tool (AnnotationTool.None) passes null through, so it still grabs
    /// any editable shape.</summary>
    private AnnotationShape? HitEditableForTool(Point px, bool interiorGrab = false) =>
        _annotations.HitTestEditable(px, interiorGrab, _currentTool == AnnotationTool.None ? null : _currentTool);

    /// <summary>True while Shift or Ctrl is held — the modifier-grab chord: a click grabs the drawn
    /// object under the cursor, hit-testing outline interiors too, regardless of the active tool.</summary>
    private static bool IsGrabModifierHeld(KeyModifiers modifiers) =>
        (modifiers & (KeyModifiers.Shift | KeyModifiers.Control)) != 0;

    private static RectPhysical RectFromPoints(Point a, Point b) =>
        new RectPhysical((int)a.X, (int)a.Y, (int)b.X, (int)b.Y).Normalized();

    private void OnPointerEnteredWindow(object? sender, PointerEventArgs e) => _onActivatedByMouse(this);

    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (SessionInputSuspended)
        {
            return;
        }

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (IsWithin(_toolbar, e.Source))
        {
            // Clicking any toolbar control OTHER than the size box hands focus back to this window
            // (every other toolbar control is Focusable=false and would never take it) — otherwise
            // a size box that was mid-edit would keep keyboard focus forever and Enter would keep
            // committing the size instead of confirming the snip. Mirrors WPF's OnPreviewMouseLeftButtonDown
            // (OverlayWindow.xaml.cs:706-717).
            if (_toolbar?.IsWithinSizeInput(e.Source) != true)
            {
                Focus();
            }
            return; // let the toolbar's own controls handle their own click
        }

        var dip = e.GetPosition(this);
        var px = ToPhysical(dip);
        _lastKeyModifiers = e.KeyModifiers;

        if (_activeTextEditor is not null && !IsWithin(_activeTextEditor, e.Source))
        {
            CommitActiveTextEditor();
        }
        else if (_activeTextEditor is not null)
        {
            return; // click inside the active editor: let the TextBox place its own caret
        }

        // Any real click (i.e. not on the toolbar, handled above) dismisses an open color-info
        // panel — a background click may then open a fresh one at the new spot on mouse-up.
        DismissColorInfo();

        _onActivatedByMouse(this);

        // Feature B: double-clicking a placed TEXT annotation reopens its inline editor, prefilled —
        // and this must win over the confirm-snip double-click check right below, or a double-click
        // meant to re-edit text would just confirm the whole snip instead. A SINGLE click on the
        // same text is handled further down (selects + starts a move drag); this ClickCount>=2
        // branch only ever fires on the second click of a double-click, so it doesn't fight that
        // gesture.
        if (e.ClickCount >= 2 && HitEditableForTool(px) is { Tool: AnnotationTool.Text } textHit)
        {
            _annotations.Select(textHit);
            BeginTextReEdit(textHit, dip);
            e.Handled = true;
            return;
        }

        if (e.ClickCount >= 2 && _selectionPx is { } existing && RectContains(existing, px))
        {
            _onCommand(OverlayCommand.ConfirmPlain);
            e.Handled = true;
            return;
        }

        // Modifier-grab: Shift/Ctrl + click is an explicit "grab whatever drawn object is under the
        // cursor" that beats every tool behavior. Hit-tests GENEROUSLY (interiorGrab): the middle of
        // a rectangle/ellipse counts, not just the stroke ring plain clicks use — so a box drawn
        // around content can be repositioned from anywhere inside it. The grab is always a move;
        // handles/endpoints still resize via plain clicks once the shape is selected.
        if (IsGrabModifierHeld(e.KeyModifiers) && HitEditableForTool(px, interiorGrab: true) is { } grabbed)
        {
            _annotations.Select(grabbed);
            _dragMode = DragMode.SelectedMove;
            _dragAnchorPx = px;
            _annotations.BeginDragSelected();
            CapturePointer(e.Pointer);
            UpdateCursor();
            e.Handled = true;
            return;
        }

        // Cross-monitor selection (item 09): a spanning selection's handle/body hit-test goes
        // through the SAME Adorner every ordinary selection uses — Adorner.RealEdges (set per window
        // by OverlaySession.OnSpanningCandidate/SpanningSelectionMath.Distribute) already refuses to
        // report a handle on an edge that's merely this monitor's own screen boundary clipping the
        // true rect, so a handle is only ever offered here where dragging it actually means
        // something. Both branches below compute their candidate as a VIRTUAL-desktop rect and hand
        // it to _onSpanningCandidate — the exact same distribute primitive a NewSelection drag uses —
        // rather than ever treating this window's own local (possibly-clipped) rect as ground truth.
        // Annotations stay deselected/disabled either way — see SetSpanningLocalSelection.
        if (_isSpanningSelection)
        {
            var spanningHandle = _adorner.HitTestHandle(px);
            if (spanningHandle != SelectionHandle.None && spanningHandle != SelectionHandle.Body
                && _spanningVirtualRectPx is { } resizeStart)
            {
                _annotations.Deselect();
                _dragMode = DragMode.SpanningResize;
                _activeHandle = spanningHandle;
                _dragAnchorPx = px;
                _dragStartVirtualRect = resizeStart;
                CapturePointer(e.Pointer);
                UpdateCursor();
                e.Handled = true;
                return;
            }
            if (spanningHandle == SelectionHandle.Body && _spanningVirtualRectPx is { } moveStart)
            {
                _annotations.Deselect();
                _dragMode = DragMode.SpanningMove;
                _dragAnchorPx = px;
                _dragStartVirtualRect = moveStart;
                CapturePointer(e.Pointer);
                UpdateCursor();
                e.Handled = true;
                return;
            }

            // Outside the current spanning selection (or nothing usable was hit — e.g.
            // _spanningVirtualRectPx was somehow null, which should never happen while
            // _isSpanningSelection is true, but this keeps the fallback total): replace it wholesale
            // with a brand-new drag. This is still a perfectly good way to reshape a spanning
            // selection, just a different gesture than grabbing a handle.
            _annotations.Deselect();
            _dragMode = DragMode.NewSelection;
            _newSelectionPending = true;
            _dragAnchorPx = px;
            _dragAnchorDip = dip;
            CapturePointer(e.Pointer);
            UpdateCursor();
            e.Handled = true;
            return;
        }

        // With a drawing tool active, a click that lands on an ALREADY-PLACED shape of THAT SAME
        // TOOL (the selected one's resize handles or endpoints, or any same-tool shape's body) edits
        // that shape instead of starting a new one on top of it. Shapes of a DIFFERENT tool are not
        // selectable here — a press anywhere else still draws as before, so overlapping shapes stay
        // reachable.
        bool editsExistingShape =
            _annotations.HitTestSelectedHandle(px) != SelectionHandle.None
            || _annotations.HitTestSelectedEndpoint(px) >= 0
            || HitEditableForTool(px) is not null;

        if (_currentTool != AnnotationTool.None && !editsExistingShape)
        {
            // Starting a drawing/text gesture is one of the "any other gesture" cases that
            // deselects a leftover annotation selection.
            _annotations.Deselect();
            if (_currentTool == AnnotationTool.Text)
            {
                BeginTextEditor(px, dip);
            }
            else
            {
                _dragMode = DragMode.Annotation;
                _annotations.BeginShape(_currentTool, px, _currentColor, _currentStrokeWidth);
                CapturePointer(e.Pointer);
            }
            e.Handled = true;
            return;
        }

        // Select tool (or a drawing tool clicking an already-placed shape of that tool): a selected
        // annotation's own resize handles/endpoints beat clicking an (unselected or already-selected)
        // annotation's body, which beats the crop selection's own handle/body, which beats starting a
        // brand-new crop-selection drag.
        var selectedHandle = _annotations.SelectedShape is not null
            ? _annotations.HitTestSelectedHandle(px)
            : SelectionHandle.None;
        // Line/Arrow's endpoint handles are this predicate's Line/Arrow analogue — mutually
        // exclusive with selectedHandle in practice, but checked as its own branch, ABOVE the plain
        // body-move hit test below, so grabbing an endpoint always resizes rather than moving the
        // whole segment.
        int selectedEndpoint = _annotations.SelectedShape is not null
            ? _annotations.HitTestSelectedEndpoint(px)
            : -1;

        if (selectedHandle != SelectionHandle.None)
        {
            _dragStartShapeRect = RectFromPoints(
                _annotations.SelectedShape!.PointsPx[0], _annotations.SelectedShape!.PointsPx[1]);
            _dragMode = DragMode.SelectedResize;
            _activeHandle = selectedHandle;
            _dragAnchorPx = px;
            _annotations.BeginDragSelected();
        }
        else if (selectedEndpoint >= 0)
        {
            _dragMode = DragMode.SelectedEndpoint;
            _dragEndpointIndex = selectedEndpoint;
            _dragAnchorPx = px;
            _annotations.BeginDragSelected();
        }
        else
        {
            var handle = _adorner.HitTestHandle(px);
            if (handle != SelectionHandle.None && handle != SelectionHandle.Body)
            {
                _annotations.Deselect();
                _dragMode = DragMode.Resize;
                _activeHandle = handle;
                _dragAnchorPx = px;
                _dragStartRect = _selectionPx!.Value;
            }
            else
            {
                var hitShape = HitEditableForTool(px);
                if (hitShape is not null)
                {
                    // Click-selects AND immediately starts a drag in the same gesture. After
                    // selecting, re-run the handle/endpoint hit tests (they only answer for the
                    // CURRENT selection, which just changed): a first click landing exactly on what
                    // is now a corner handle or a segment endpoint starts that resize directly
                    // instead of degrading to a whole-shape move.
                    _annotations.Select(hitShape);
                    var freshHandle = _annotations.HitTestSelectedHandle(px);
                    int freshEndpoint = freshHandle == SelectionHandle.None
                        ? _annotations.HitTestSelectedEndpoint(px)
                        : -1;
                    if (freshHandle != SelectionHandle.None)
                    {
                        _dragStartShapeRect = RectFromPoints(hitShape.PointsPx[0], hitShape.PointsPx[1]);
                        _dragMode = DragMode.SelectedResize;
                        _activeHandle = freshHandle;
                    }
                    else if (freshEndpoint >= 0)
                    {
                        _dragMode = DragMode.SelectedEndpoint;
                        _dragEndpointIndex = freshEndpoint;
                    }
                    else
                    {
                        _dragMode = DragMode.SelectedMove;
                    }
                    _dragAnchorPx = px;
                    _annotations.BeginDragSelected();
                }
                else if (handle == SelectionHandle.Body)
                {
                    _annotations.Deselect();
                    _dragMode = DragMode.Move;
                    _activeHandle = handle;
                    _dragAnchorPx = px;
                    _dragStartRect = _selectionPx!.Value;
                }
                else
                {
                    _annotations.Deselect();
                    // Don't start (or clear) any selection yet: below ClickDragThresholdPx of
                    // travel this is a color-inspection click, not a drag. The selection work
                    // happens in OnPreviewPointerMoved once the threshold is crossed.
                    _dragMode = DragMode.NewSelection;
                    _newSelectionPending = true;
                    _dragAnchorPx = px;
                    _dragAnchorDip = dip;
                }
            }
        }

        CapturePointer(e.Pointer);
        UpdateCursor();
        e.Handled = true;
    }

    private void OnPreviewPointerMoved(object? sender, PointerEventArgs e)
    {
        if (SessionInputSuspended)
        {
            return;
        }

        var dip = e.GetPosition(this);
        var px = ToPhysical(dip);
        _lastHoverPx = px;
        _lastKeyModifiers = e.KeyModifiers;

        // Color codes only when the color picker is enabled AND not the Pixelate tool — otherwise
        // the loupe is a pure placement/zoom aid with no color readout (WPF OverlayWindow.xaml.cs:1056).
        _magnifier.ShowColorReadout = _liveSettings.ColorPickerEnabled && _currentTool != AnnotationTool.Pixelate;
        _magnifier.Update(_preview, _frame, dip, px);

        switch (_dragMode)
        {
            case DragMode.NewSelection:
                if (_newSelectionPending)
                {
                    double dx0 = px.X - _dragAnchorPx.X, dy0 = px.Y - _dragAnchorPx.Y;
                    if (dx0 * dx0 + dy0 * dy0 < ClickDragThresholdPx * ClickDragThresholdPx)
                    {
                        break; // still a click candidate, not a drag — don't disturb any selection
                    }
                    _newSelectionPending = false;
                    _onSelectionStarted(this); // clears other monitors' selections, per DESIGN.md
                }
                // Cross-monitor selection (item 09): _dragAnchorPx/px are THIS window's own local
                // physical pixels, deliberately never clamped to this window's frame during the drag
                // (see docs/DESIGN-MULTIMON-SELECTION.md) — pointer capture keeps delivering these
                // even once the cursor has visually left this window. Translate to virtual-desktop
                // coordinates (this window's own monitor origin folded in) before handing it to the
                // session, which distributes each monitor's own intersection back out via
                // SetSpanningLocalSelection — which is also what sets THIS window's own _selectionPx
                // now, replacing the old direct SetSelection call here.
                var localCandidate = RectPhysical.FromSize(
                    (int)Math.Min(_dragAnchorPx.X, px.X),
                    (int)Math.Min(_dragAnchorPx.Y, px.Y),
                    (int)Math.Abs(px.X - _dragAnchorPx.X),
                    (int)Math.Abs(px.Y - _dragAnchorPx.Y));
                var monitorOrigin = Monitor.BoundsPx;
                // false: the anchor is fixed; only the far corner moves, like a resize.
                _onSpanningCandidate(this, new RectPhysical(
                    monitorOrigin.Left + localCandidate.Left, monitorOrigin.Top + localCandidate.Top,
                    monitorOrigin.Left + localCandidate.Right, monitorOrigin.Top + localCandidate.Bottom),
                    false);
                break;

            case DragMode.Move:
            {
                double dx = px.X - _dragAnchorPx.X, dy = px.Y - _dragAnchorPx.Y;
                var moved = new RectPhysical(
                    (int)(_dragStartRect.Left + dx), (int)(_dragStartRect.Top + dy),
                    (int)(_dragStartRect.Right + dx), (int)(_dragStartRect.Bottom + dy));
                SetSelection(ClampToFrame(moved));
                break;
            }

            case DragMode.Resize:
                SetSelection(SpanningSelectionMath.ApplyResize(_dragStartRect, _activeHandle, px.X, px.Y));
                break;

            case DragMode.SpanningResize:
            {
                // Same translation-to-virtual-coordinates the NewSelection case above uses: px is
                // THIS window's own local physical pixels (never clamped mid-drag), so folding in
                // this window's monitor origin gives the cursor's true virtual-desktop position even
                // once it's visually left this monitor. SpanningSelectionMath.ApplyResize then
                // replaces just the dragged handle's edge(s) on the VIRTUAL start rect, and the
                // result goes through the exact same distribute step a NewSelection drag uses — never
                // this window's own local rect.
                var spanningMonitorOrigin = Monitor.BoundsPx;
                double virtualX = spanningMonitorOrigin.Left + px.X, virtualY = spanningMonitorOrigin.Top + px.Y;
                // false: only the dragged handle's edge(s) move; stopping at the boundary is correct.
                _onSpanningCandidate(
                    this,
                    SpanningSelectionMath.ApplyResize(_dragStartVirtualRect, _activeHandle, virtualX, virtualY),
                    false);
                break;
            }

            case DragMode.SpanningMove:
            {
                // A plain pixel delta is monitor-independent (px and _dragAnchorPx are both THIS
                // window's own local pixels throughout the whole drag — pointer capture guarantees
                // that), so it can be applied directly to the virtual start rect with no origin
                // translation needed, unlike the resize case above (which needs px's ABSOLUTE
                // position, not just its delta).
                double moveDx = px.X - _dragAnchorPx.X, moveDy = px.Y - _dragAnchorPx.Y;
                var movedVirtual = new RectPhysical(
                    (int)(_dragStartVirtualRect.Left + moveDx), (int)(_dragStartVirtualRect.Top + moveDy),
                    (int)(_dragStartVirtualRect.Right + moveDx), (int)(_dragStartVirtualRect.Bottom + moveDy));
                // true — both edges moved by the same delta, so if the result needs to be pulled back
                // into the virtual desktop's bounds, it must SLIDE (keep its size), not get clamped
                // edge-by-edge (which would shrink it) — see OnSpanningCandidate /
                // SpanningSelectionMath.SlideToBounds.
                _onSpanningCandidate(this, movedVirtual, true);
                break;
            }

            case DragMode.Annotation:
                _annotations.UpdateShape(px);
                break;

            case DragMode.SelectedMove:
                // Avalonia's Point - Point returns a Point (not a Vector like WPF's) — build the
                // Vector TranslateSelected expects explicitly.
                _annotations.TranslateSelected(
                    new Vector(px.X - _dragAnchorPx.X, px.Y - _dragAnchorPx.Y), new Size(_frame.Width, _frame.Height));
                break;

            case DragMode.SelectedResize:
            {
                // Byte-for-byte the same corner/edge math the crop selection's own resize uses.
                var resized = ClampToFrame(SpanningSelectionMath.ApplyResize(_dragStartShapeRect, _activeHandle, px.X, px.Y));
                _annotations.SetSelectedRect(
                    new Point(resized.Left, resized.Top), new Point(resized.Right, resized.Bottom));
                break;
            }

            case DragMode.SelectedEndpoint:
            {
                // Reposition just the dragged endpoint, clamped into the frame the same way every
                // other selected-shape drag is (SelectedMove/SelectedResize both clamp too).
                var clamped = new Point(
                    Math.Clamp(px.X, 0, _frame.Width), Math.Clamp(px.Y, 0, _frame.Height));
                _annotations.SetSelectedEndpoint(_dragEndpointIndex, clamped);
                break;
            }
        }

        UpdateCursor();
    }

    private void OnPreviewPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (SessionInputSuspended)
        {
            return;
        }

        if (e.InitialPressMouseButton != MouseButton.Left)
        {
            return;
        }

        switch (_dragMode)
        {
            case DragMode.NewSelection:
                if (_newSelectionPending)
                {
                    // Mouse never travelled far enough to become a drag: this was a click —
                    // inspect the clicked pixel's color instead of touching the selection.
                    _newSelectionPending = false;
                    ShowColorInfo(_dragAnchorPx, _dragAnchorDip);
                }
                else
                {
                    // Cross-monitor selection (item 09): the too-small-cancel rule must be judged
                    // against the TRUE selection (the shared virtual rect while spanning), not just
                    // this window's own local slice — a spanning selection whose slice on THIS
                    // monitor happens to be a couple of pixels wide (e.g. the drag ended right at the
                    // seam) is not "too small" overall. See OverlaySession.FinalizeNewSelectionDrag.
                    _onFinalizeNewSelection(this);
                }
                break;

            case DragMode.Resize:
            case DragMode.Move:
                if (_selectionPx is { } sel2)
                {
                    SetSelection(ClampToFrame(sel2.Normalized()));
                }
                break;

            case DragMode.SpanningResize:
            case DragMode.SpanningMove:
                // Cross-monitor selection: every pointer-move of this drag already redistributed its
                // candidate through _onSpanningCandidate (see OnPreviewPointerMoved), which is also
                // what set every window's own _selectionPx as it went — there's no window-local rect
                // left to clamp here. Just apply the same "<2px on either axis = cancel" rule
                // FinalizeNewSelectionDrag already applies to a NewSelection drag's result (judged
                // against the true virtual size, not this window's own local slice).
                _onFinalizeNewSelection(this);
                break;

            case DragMode.Annotation:
                // A just-placed click-editable shape (Pixelate/Rectangle/Ellipse/Line/Arrow) is
                // auto-selected: its chrome (and handles, where applicable) appears right away, so
                // move/resize/wheel-adjust are discoverable without the Select tool, and the wheel
                // immediately adjusts THIS shape rather than the next one's.
                var committed = _annotations.EndShape();
                if (committed is not null && AnnotationLayer.IsClickEditableTool(committed.Tool))
                {
                    _annotations.Select(committed);
                }
                break;

            case DragMode.SelectedMove:
            case DragMode.SelectedResize:
            case DragMode.SelectedEndpoint:
                _annotations.EndDragSelected();
                break;
        }

        bool wasAreaDrag = _dragMode is DragMode.NewSelection or DragMode.Move or DragMode.Resize
            or DragMode.SpanningResize or DragMode.SpanningMove;
        bool wasSelectedDrag = _dragMode is DragMode.SelectedMove or DragMode.SelectedResize or DragMode.SelectedEndpoint;
        _dragMode = DragMode.None;
        _activeHandle = SelectionHandle.None;
        ReleasePointer();

        if (wasSelectedDrag)
        {
            // Mirrors the NewSelection-drag re-evaluation below: the release point may now sit over
            // a different handle (or the plain body) than the one that was being dragged.
            UpdateCursor();
        }

        if (wasAreaDrag)
        {
            // SetSelection() above (for the NewSelection/Resize/Move branches) ran while _dragMode
            // still held the drag value, so UpdateToolbarPlacement() suppressed the toolbar even for
            // this final rect. Re-evaluate now that _dragMode is None: the toolbar (re)appears at the
            // settled selection, or stays hidden if the drag ended with no selection at all (either
            // SetSelection(null) above, or the click-to-pick path where _selectionPx was never
            // touched). Gated to area drags: an Annotation mouse-up never hid the toolbar, so there
            // is nothing to re-show.
            UpdateToolbarPlacement();
            // The release point of a NewSelection drag is, by construction, a corner of the fresh
            // selection — re-evaluate now so the cursor shows that handle's resize arrow immediately
            // instead of keeping the drag's crosshair until the next pointer jitter.
            UpdateCursor();
        }
    }

    // ---------- Scroll-wheel loupe zoom ----------

    /// <summary>Extends item 06's loupe-zoom-only handler with the rest of WPF's wheel logic
    /// (OverlayWindow.xaml.cs:2253-2345): while typing, the wheel resizes the active text editor's
    /// font size directly; mid-drag, it resizes the in-progress shape's stroke width in place; over
    /// a selected shape it routes to SetSelectedPixelateBlock/StrokeWidth/FontSize, each funneled
    /// through BeginDragSelected/CommitPendingDrag so one wheel session (however many notches) ends
    /// up as one Replace history action. Falls through to the loupe-zoom behavior only when none of
    /// those apply, exactly like WPF's own fall-through order.</summary>
    private void OnPreviewPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (SessionInputSuspended)
        {
            return;
        }

        if (IsWithin(_toolbar, e.Source))
        {
            return; // scrolling a toolbar control (future size box / palette) must scroll THAT, not the loupe
        }

        int notches = (int)Math.Round(e.Delta.Y);
        if (notches == 0)
        {
            return;
        }

        if (_activeTextEditor is not null)
        {
            double editorSize = SizeInput.ClampFont(_activeTextEditor.FontSize + notches * 2.0);
            _activeTextEditor.FontSize = editorSize;
            _textFontSize = editorSize;
            _toolbar?.SetFontSize(editorSize);
            e.Handled = true;
            return;
        }

        // A shape is half-drawn (pointer held, not yet released): the wheel resizes THAT shape's
        // stroke width in place rather than only the next one, and adopts the size as the current
        // tool size so it carries to the following shape. Pixelate is excluded — the blur tool's
        // wheel zooms its placement loupe instead (handled below).
        if (_annotations.InProgressTool is { } drawingTool && drawingTool != AnnotationTool.Pixelate)
        {
            double newSize = SizeInput.ClampStroke(_currentStrokeWidth + notches);
            _annotations.SetInProgressStrokeWidth(newSize);
            _currentStrokeWidth = newSize;
            _toolbar?.SetStrokeWidth(newSize);
            e.Handled = true;
            return;
        }

        // A selected rect-resizable/segment/text shape owns the wheel — under the Select tool AND
        // under the shape's own drawing tool (a just-placed shape is auto-selected, so "place, then
        // scroll to tune it" works in one flow): Pixelate scrolls its mosaic block size,
        // Rectangle/Ellipse/Line/Arrow their stroke width, Text its own font size.
        if (_annotations.SelectedShape is { } selectedShape
            && (_currentTool == AnnotationTool.None || _currentTool == selectedShape.Tool))
        {
            if (selectedShape.Tool == AnnotationTool.Pixelate)
            {
                _annotations.BeginDragSelected(); // no-op if a gesture from an earlier notch is still open
                double newBlock = Math.Clamp(selectedShape.StrokeWidthPx + notches * 2.0, 3.0, SizeInput.MaxStrokePx);
                _annotations.SetSelectedPixelateBlock(newBlock);
                // Resize ONLY the selected blur's mosaic block — do NOT sync it to the current tool
                // size. A blur's coarseness is a per-shape edit, not the tool default.
                e.Handled = true;
                return;
            }
            if (selectedShape.Tool is AnnotationTool.Rectangle or AnnotationTool.Ellipse
                                   or AnnotationTool.Line or AnnotationTool.Arrow)
            {
                _annotations.BeginDragSelected();
                double newWidth = SizeInput.ClampStroke(selectedShape.StrokeWidthPx + notches);
                _annotations.SetSelectedStrokeWidth(newWidth);
                _currentStrokeWidth = newWidth;
                _toolbar?.SetStrokeWidth(newWidth);
                e.Handled = true;
                return;
            }
            if (selectedShape.Tool == AnnotationTool.Text)
            {
                _annotations.BeginDragSelected(); // no-op if a gesture from an earlier notch is still open
                double resizedFont = SizeInput.ClampFont(selectedShape.StrokeWidthPx + notches * 2.0);
                _annotations.SetSelectedFontSize(resizedFont);
                _textFontSize = resizedFont;
                _toolbar?.SetFontSize(resizedFont);
                e.Handled = true;
                return;
            }
        }

        if (_currentTool is AnnotationTool.Pixelate or AnnotationTool.None)
        {
            // A SELECTED blur is handled by the selected-shape branch above (still resizes its
            // block); this only runs when nothing is selected.
            int newRadius = Math.Clamp(
                _magnifier.SampleRadius - notches, Magnifier.MinSampleRadius, Magnifier.MaxSampleRadius);
            if (newRadius != _magnifier.SampleRadius)
            {
                _magnifier.SampleRadius = newRadius;
                _liveSettings = _liveSettings with { MagnifierSampleRadius = newRadius };
                TrySaveLiveSettings();
            }
            e.Handled = true;
            return;
        }

        // Every other drawing tool (Rectangle/Ellipse/Arrow/Line/Freehand/Highlight/Text) with
        // nothing selected and nothing mid-drawn: the wheel pre-dials the DEFAULT size the next
        // shape will use, syncing the toolbar's own size box so it stays honest. Mirrors WPF's own
        // fall-through tail (OverlayWindow.xaml.cs:2382-2391).
        if (_currentTool == AnnotationTool.Text)
        {
            double newSize = SizeInput.ClampFont(_textFontSize + notches * 2.0);
            _textFontSize = newSize;
            _toolbar?.SetFontSize(newSize);
        }
        else
        {
            double newWidth = SizeInput.ClampStroke(_currentStrokeWidth + notches);
            _currentStrokeWidth = newWidth;
            _toolbar?.SetStrokeWidth(newWidth);
        }
        e.Handled = true;
    }

    private void TrySaveLiveSettings()
    {
        try
        {
            SettingsStore.Save(_liveSettings);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: failed to save live overlay settings: {ex.Message}");
        }
    }

    private void CapturePointer(IPointer pointer)
    {
        _capturedPointer = pointer;
        pointer.Capture(this);
    }

    private void ReleasePointer()
    {
        if (_capturedPointer is not null)
        {
            _capturedPointer.Capture(null);
            _capturedPointer = null;
        }
    }

    private static bool RectContains(RectPhysical r, Point p)
    {
        var n = r.Normalized();
        return p.X >= n.Left && p.X <= n.Right && p.Y >= n.Top && p.Y <= n.Bottom;
    }

    private RectPhysical ClampToFrame(RectPhysical r)
    {
        var n = r.Normalized();
        int w = Math.Min(n.Width, _frame.Width);
        int h = Math.Min(n.Height, _frame.Height);
        int left = Math.Clamp(n.Left, 0, Math.Max(0, _frame.Width - w));
        int top = Math.Clamp(n.Top, 0, Math.Max(0, _frame.Height - h));
        return RectPhysical.FromSize(left, top, w, h);
    }

    /// <summary>True when <paramref name="source"/> belongs to <paramref name="container"/>'s UI —
    /// including content hosted in an Avalonia Popup (the size/font ComboBox dropdowns, the
    /// palette's right-click context menus, the Replace flyout). Popup content renders in its own
    /// disconnected PopupRoot visual tree, so a pure visual-parent walk never reaches back to
    /// <paramref name="container"/> from a dropdown row and its clicks/scrolls would fall through
    /// to the overlay's own select/draw/resize handlers. Each step prefers the LOGICAL parent (the
    /// link Avalonia's Popup uses to bridge a popup's subtree back to the control that owns it —
    /// Avalonia.Visual itself implements ILogical, so every Control already carries this) and falls
    /// back to the visual parent for cases with no logical bridge. A walk that dead-ends without
    /// reaching this window is popup chrome by definition (nothing inside the overlay's own tree
    /// dead-ends before the window), so it counts as "within" too — that covers popup-rooted
    /// elements with no logical bridge, e.g. context-menu internals. Ported from WPF's
    /// IsWithinToolbar (OverlayWindow.xaml.cs:1308-1335).</summary>
    private bool IsWithin(Visual? container, object? source)
    {
        if (container is null || source is null)
        {
            return false;
        }
        object? current = source;
        while (current is not null)
        {
            if (ReferenceEquals(current, container))
            {
                return true;
            }
            if (ReferenceEquals(current, this))
            {
                return false; // reached the overlay window itself — a genuine overlay-surface event
            }
            current = (current as Avalonia.LogicalTree.ILogical)?.LogicalParent
                ?? (current as Visual)?.GetVisualParent();
        }
        return true; // dead-ended outside the window's tree: popup chrome
    }

    // ---------- Keyboard ----------

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (SessionInputSuspended)
        {
            return;
        }

        if (_activeTextEditor is not null)
        {
            if (e.Key == Key.Escape)
            {
                CancelActiveTextEditor();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                CommitActiveTextEditor();
                e.Handled = true;
            }
            return; // don't let Esc/Enter fall through to session-level handling while typing
        }

        // Feature B: Delete/Back removes a selected annotation; Esc deselects it. Both are a new
        // stage strictly INNER than the color-info-panel/CancelStage escalation below — an active
        // text edit (the only other local stage) was already consumed above, and this must run
        // before it so Esc never eats a whole snip-clear just because a leftover selection happened
        // to still be checked.
        if ((e.Key == Key.Delete || e.Key == Key.Back) && _annotations.SelectedShape is not null)
        {
            _annotations.DeleteSelected();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && _annotations.SelectedShape is not null)
        {
            _annotations.Deselect();
            e.Handled = true;
            return;
        }

        // Meta is accepted alongside Control so Cmd+C/Cmd+S/Cmd+Z work naturally on macOS — but
        // ONLY on macOS (O7 audit fix): on Windows, KeyModifiers.Meta is the Windows key, and
        // treating Win+C/Win+S/Win+Z as Copy/Save/Undo would collide with OS-level shortcuts and
        // surprise Windows/Linux users who never asked for a Meta-modified binding.
        bool ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0
            || (OperatingSystem.IsMacOS() && (e.KeyModifiers & KeyModifiers.Meta) != 0);

        if (e.Key == Key.Escape)
        {
            // Two-stage Esc, innermost thing first. An active text edit was already consumed
            // above; next comes this window's color-info panel; everything beyond that (clear
            // the snip on whichever monitor holds it, else close the whole overlay) needs the
            // session's cross-monitor view, so it escalates as CancelStage.
            if (!DismissColorInfo())
            {
                _onCommand(OverlayCommand.CancelStage);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            _onCommand(OverlayCommand.ConfirmPlain);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.C)
        {
            _onCommand(OverlayCommand.Copy);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.S)
        {
            _onCommand(OverlayCommand.Save);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.Z)
        {
            if ((e.KeyModifiers & KeyModifiers.Shift) != 0)
            {
                _annotations.Redo(); // Ctrl+Shift+Z, the other common redo binding
            }
            else
            {
                _annotations.Undo();
            }
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.Y)
        {
            _annotations.Redo();
            e.Handled = true;
        }
    }

    // ---------- Selection / toolbar / dim mask ----------

    private void SetSelection(RectPhysical? rect)
    {
        _selectionPx = rect;
        _adorner.SelectionPx = rect;
        _adorner.InvalidateVisual();
        UpdateDimGeometry();
        UpdateToolbarPlacement();
    }

    /// <summary>Clears this window's selection/annotations — called by OverlayController when a
    /// new drag starts on a different monitor (selection lives on exactly one monitor at a time).</summary>
    internal void ClearSelection()
    {
        if (_dragMode != DragMode.None)
        {
            _dragMode = DragMode.None;
            _newSelectionPending = false;
            ReleasePointer();
        }
        DismissColorInfo();
        CancelActiveTextEditor();
        _annotations.Clear();
        SetSpanningLocalSelection(null, isSpanning: false, isPrimary: false, virtualRectForLabel: null);
    }

    /// <summary>Cross-monitor selection (item 09): the per-window half of OverlaySession.
    /// OnSpanningCandidate's distribute step — sets THIS window's own local slice of the current
    /// selection (monitor-relative physical pixels, or null if the shared virtual rect doesn't
    /// intersect this monitor at all) and the two flags that change how it renders/behaves:
    /// <paramref name="isSpanning"/> (the OVERALL selection, not just this window's slice, touches
    /// ≥2 monitors) and <paramref name="isPrimary"/> (this is the one window that owns the toolbar/
    /// interaction — the window whose drag produced the current candidate). Reuses the existing
    /// SetSelection pipeline verbatim (dim mask, adorner, toolbar placement) — a spanning selection's
    /// per-window rendering is the SAME code path as an ordinary one, just fed a different rect.
    /// <paramref name="dragInProgress"/> is true only when this call came from OnSpanningCandidate
    /// mid-drag (every other call site is a "drag has ended / selection cleared" moment) — see
    /// _suppressToolbarForSpanningDrag's own doc comment for why a non-owner window needs this to
    /// suppress its toolbar too.</summary>
    internal void SetSpanningLocalSelection(
        RectPhysical? monitorRelativeRect, bool isSpanning, bool isPrimary, RectPhysical? virtualRectForLabel,
        SelectionEdges realEdges = SelectionEdges.All, bool dragInProgress = false)
    {
        _isSpanningSelection = isSpanning;
        _isSpanningPrimary = isPrimary;
        _spanningVirtualRectPx = virtualRectForLabel;
        _suppressToolbarForSpanningDrag = dragInProgress;
        _adorner.RealEdges = isSpanning ? realEdges : SelectionEdges.All;
        _adorner.SuppressBadge = isSpanning && !isPrimary;
        _adorner.OverrideSizeLabel = isSpanning && isPrimary && virtualRectForLabel is { } v
            ? string.Create(CultureInfo.InvariantCulture, $"{v.Width} x {v.Height}")
            : null;
        if (isSpanning)
        {
            // Belt-and-braces (the toolbar already hides every drawing tool while spanning — see
            // ShowToolbar/SetSpanningMode): no annotation shape can exist to select/edit on a
            // spanning selection, so there is nothing for the stitcher (RenderSpanningSelection) to
            // burn in.
            _currentTool = AnnotationTool.None;
            _annotations.Deselect();
        }
        SetSelection(monitorRelativeRect);
    }

    /// <summary>Cross-monitor selection: called by OverlaySession on EVERY window once
    /// FinalizeNewSelectionDrag has settled a NewSelection/SpanningResize/SpanningMove drag for
    /// good (pointer-up, or the `select` automation command) — the counterpart to the
    /// dragInProgress:true SetSpanningLocalSelection calls that ran on every pointer-move of that
    /// drag. Only the drag-owning window gets its own belt-and-braces re-placement from
    /// OnPreviewPointerReleased once _dragMode settles back to None; every OTHER window that took
    /// part in the distribute step (see _suppressToolbarForSpanningDrag) has no such hook of its
    /// own, so this is what lets a non-owner monitor's toolbar re-appear (or correctly stay hidden)
    /// once the drag is truly over instead of staying suppressed forever.</summary>
    internal void NotifySpanningDragEnded()
    {
        if (_suppressToolbarForSpanningDrag)
        {
            _suppressToolbarForSpanningDrag = false;
            UpdateToolbarPlacement();
        }
    }

    private void UpdateDimGeometry()
    {
        double w = ClientSize.Width > 0 ? ClientSize.Width : _frame.Width / Math.Max(_scaleX, 1e-6);
        double h = ClientSize.Height > 0 ? ClientSize.Height : _frame.Height / Math.Max(_scaleY, 1e-6);
        var outer = new RectangleGeometry(new Rect(0, 0, w, h));

        if (_selectionPx is { } sel)
        {
            var n = sel.Normalized();
            var holeDip = new Rect(n.Left / _scaleX, n.Top / _scaleY, n.Width / _scaleX, n.Height / _scaleY);
            _dimPath.Data = new CombinedGeometry(GeometryCombineMode.Exclude, outer, new RectangleGeometry(holeDip));
        }
        else
        {
            _dimPath.Data = outer;
        }
    }

    private void UpdateToolbarPlacement()
    {
        if (_selectionPx is not { } sel)
        {
            HideToolbar();
            return;
        }

        // Cross-monitor selection (item 09): only the PRIMARY window of a spanning selection ever
        // shows a toolbar — a secondary window's own local slice is real selection state (for
        // rendering/hit-testing) but never gets its own toolbar instance.
        if (_isSpanningSelection && !_isSpanningPrimary)
        {
            HideToolbar();
            return;
        }

        // A drag that's creating/moving/resizing the selection shouldn't show the toolbar
        // mid-drag — it belongs at the FINAL rect, not flickering along for the ride. Annotation
        // drags are exempt: they draw inside the already-settled selection and never change its
        // bounds. Just collapse the existing element (if any) rather than HideToolbar(), which
        // also resets _currentTool and the toolbar's checked-tool state as if the selection had
        // been cleared — neither of which should happen mid-drag. _dragMode alone only catches the
        // window that OWNS the drag (has pointer capture) — a SpanningResize/SpanningMove candidate
        // is redistributed to every OTHER window too (see OnSpanningCandidate), and one of those can
        // pick up a non-null local slice while its own _dragMode is still None; that window needs
        // _suppressToolbarForSpanningDrag (see its own doc comment) to stay suppressed too.
        if (_dragMode is DragMode.NewSelection or DragMode.Move or DragMode.Resize
            or DragMode.SpanningResize or DragMode.SpanningMove
            || _suppressToolbarForSpanningDrag)
        {
            if (_toolbar is not null)
            {
                _toolbar.IsVisible = false;
            }
            return;
        }

        ShowToolbar();
        if (_toolbar is not { } toolbar)
        {
            return;
        }

        var n = sel.Normalized();
        double x = n.Left / _scaleX;
        double y = n.Bottom / _scaleY + 8.0;

        double toolbarWidth = toolbar.Bounds.Width;
        double toolbarHeight = toolbar.Bounds.Height;
        if (toolbarWidth <= 0 || toolbarHeight <= 0)
        {
            toolbar.Measure(Size.Infinity);
            toolbarWidth = toolbar.DesiredSize.Width > 0 ? toolbar.DesiredSize.Width : 700.0;
            toolbarHeight = toolbar.DesiredSize.Height > 0 ? toolbar.DesiredSize.Height : 44.0;
        }

        double actualWidth = ClientSize.Width, actualHeight = ClientSize.Height;
        if (actualHeight > 0 && y + toolbarHeight > actualHeight)
        {
            y = n.Top / _scaleY - toolbarHeight - 8.0; // flip above if it would go off the bottom
            if (y < 0)
            {
                y = 0;
            }
        }
        if (actualWidth > 0 && x + toolbarWidth > actualWidth)
        {
            x = actualWidth - toolbarWidth;
        }
        x = Math.Max(0, x);

        Canvas.SetLeft(toolbar, x);
        Canvas.SetTop(toolbar, y);
    }

    private void ShowToolbar()
    {
        if (_toolbar is null)
        {
            _toolbar = new ToolbarControl();
            _toolbar.ToolSelected += tool =>
            {
                // Commit any still-open inline text editor FIRST, before evaluating the tool-scoped
                // deselect check below — a toolbar tool click never routes through the canvas's own
                // commit-on-click, so without this an editor left open across a tool switch has
                // nothing selected yet at switch time.
                CommitActiveTextEditor();
                _currentTool = tool;
                // Feature B: selection is tool-scoped — switching to a DIFFERENT drawing tool than
                // the currently selected shape's own kind drops that selection (chrome/handles
                // disappear). Switching TO the Select tool (None) is exempt — that's the tool
                // selections are FOR, so it always keeps whatever is selected. Deselect() commits
                // any pending wheel gesture first, so a mid-resize wheel tweak is never silently
                // dropped by a tool switch.
                if (tool != AnnotationTool.None
                    && _annotations.SelectedShape is { } selected && selected.Tool != tool)
                {
                    _annotations.Deselect();
                }
                UpdateCursor();
            };
            _toolbar.ColorSelected += color => _currentColor = color;
            _toolbar.StrokeWidthSelected += width => _currentStrokeWidth = width;
            _toolbar.UndoClicked += () => _annotations.Undo();
            _toolbar.RedoClicked += () => _annotations.Redo();
            // Keeps Undo/Redo grayed exactly when they'd be no-ops, whatever mutated the history
            // (toolbar clicks, Ctrl+Z/Y, ClearSelection).
            _annotations.HistoryChanged += () =>
                _toolbar?.SetHistoryState(_annotations.CanUndo, _annotations.CanRedo);
            _toolbar.CopyClicked += () => _onCommand(OverlayCommand.Copy);
            _toolbar.SaveClicked += () => _onCommand(OverlayCommand.Save);
            _toolbar.SaveHdrClicked += () => _onCommand(OverlayCommand.SaveHdr);
            // Sharing/* subsystem (item 12): plain click is the default-provider upload, routed
            // through OverlayCommand.Share (payload-free, like every other command here). The
            // dropdown's per-provider picker needs a payload the enum can't carry, so it goes through
            // its own dedicated _onShareToProvider callback instead. ManageProvidersRequested has no
            // reachable "open Settings" entry point from inside an overlay session, so it is
            // deliberately left unwired: ToolbarControl disables the chevron itself whenever there
            // are zero providers, which is exactly the case that would have raised this event, so it
            // can never fire from a live click — no clickable-but-dead UI results.
            _toolbar.ShareClicked += () => _onCommand(OverlayCommand.Share);
            _toolbar.ShareToProviderRequested += providerId => _onShareToProvider(providerId);
            // Record (item 21): the toolbar's format menu closes the overlay exactly like Copy/Save/
            // SaveHdr - see OverlayController.OverlaySession.Record's own doc comment.
            _toolbar.RecordMp4Clicked += () => _onCommand(OverlayCommand.RecordMp4);
            _toolbar.RecordGifClicked += () => _onCommand(OverlayCommand.RecordGif);
            // The toolbar's X button always closes the whole overlay outright — deliberately NOT
            // the staged CancelStage semantics Esc has.
            _toolbar.CancelClicked += () => _onCommand(OverlayCommand.Cancel);

            // Text-style group (item 08): live-applies to new annotations and to an in-progress edit.
            _toolbar.FontSizeSelected += size =>
            {
                _textFontSize = SizeInput.ClampFont(size);
                if (_activeTextEditor is not null)
                {
                    _activeTextEditor.FontSize = _textFontSize;
                }
            };
            _toolbar.BoldToggled += bold =>
            {
                _textBold = bold;
                if (_activeTextEditor is not null)
                {
                    _activeTextEditor.FontWeight = bold ? FontWeight.Bold : FontWeight.Normal;
                }
            };
            _toolbar.ItalicToggled += italic =>
            {
                _textItalic = italic;
                if (_activeTextEditor is not null)
                {
                    _activeTextEditor.FontStyle = italic ? FontStyle.Italic : FontStyle.Normal;
                }
            };
            _toolbar.FontFamilySelected += family =>
            {
                _textFontFamily = family;
                if (_activeTextEditor is not null)
                {
                    _activeTextEditor.FontFamily = new FontFamily(family);
                }
            };

            // Palette editing (item 08): each swatch's right-click "Replace..." recolors it in
            // place. All mutations persist immediately via SettingsStore, mirroring WPF's own
            // OnPaletteReplaceRequested/UpdatePalette pattern.
            _toolbar.PaletteReplaceRequested += OnPaletteReplaceRequested;

            // "Save HDR is Windows-only v1 (backend capability flag hides the button elsewhere)"
            // — DESIGN-XPLAT.md; the flag reaches the overlay as the composition root's
            // WriteHdrExport hook being non-null (set by WP-X2 from Platform.Windows's JxrWriter).
            // O3 audit fix: gate on the RUNTIME capability too — WriteHdrExport being non-null only
            // means Platform.Windows was compiled in, not that THIS frame can actually be exported
            // as HDR (an SDR monitor on the same Windows machine still captures Bgra8Srgb). Without
            // this the button appeared for every monitor on a Windows build regardless of format.
            _toolbar.SetSaveHdrVisible(
                _frame.Format == FrameFormat.Fp16ScRgb && AppComposition.WriteHdrExport is not null);
            _overlayCanvas.Children.Add(_toolbar);
        }

        _toolbar.InitializeTextStyle(_textFontFamily, _textFontSize, _textBold, _textItalic);
        RefreshToolbarPalette();
        _toolbar.SetStrokeWidth(_currentStrokeWidth);
        _toolbar.SetHistoryState(_annotations.CanUndo, _annotations.CanRedo);
        // Cross-monitor selection (item 09): re-evaluated on every show, not just once, so a reused
        // toolbar instance can never show stale drawing tools after a spanning<->single-monitor
        // transition (a fresh drag replacing a spanning selection with a same-monitor one, or vice
        // versa) on the same session.
        _toolbar.SetSpanningMode(_isSpanningSelection);
        // Sharing/* subsystem (item 12): re-populated on every show (not just once) — but the real
        // reason is POOLED-WINDOW REUSE (a window claimed from a pool, built and first-rendered
        // against an earlier session's settings, or none at all, must pick up THIS session's own
        // settings), not a live mid-session settings refresh. _liveSettings is a snapshot taken once,
        // either at this window's construction or the last in-session edit IT made — it is never
        // re-read from disk while the overlay stays open, so a share provider added/removed via the
        // tray's separate Settings window WHILE this overlay is up will NOT be reflected here until
        // the next capture starts a fresh session. OverlaySession.ShareCurrentSelection/
        // ShareToSpecificProvider resolve the actual upload target from this exact same _liveSettings
        // (via the LiveSettings property above), not the session's own separately-snapshotted
        // _settings field, so the dropdown's own choices can never disagree with what a click
        // actually uploads to — even though neither one is disk-fresh mid-session. Only ENABLED
        // configs are offered — a built-in the user has never filled in a credential for is seeded
        // disabled (ShareProviderCatalog.DefaultConfigFor) and must not appear as a clickable-but-
        // broken picker entry. Mirrors the WPF app's own ShowToolbar wiring.
        _toolbar.SetShareProviders(
            RoeSnip.Core.Sharing.ShareManager.EffectiveConfigs(_liveSettings.ShareProviders)
                .Where(c => c.Enabled)
                .Select(c => (c.Id, c.DisplayName))
                .ToList(),
            _liveSettings.DefaultShareProviderId);
        _toolbar.IsVisible = true;
    }

    /// <summary>Sharing/* subsystem (item 12): the overlay stays open for the whole upload (see
    /// OverlaySession.ShareCurrentSelection's own doc comment), so OverlayController drives the
    /// toolbar's busy state directly through this pass-through rather than through OverlayCommand. A
    /// no-op if this window has no toolbar yet (nothing has been shared before a selection existed —
    /// can't happen in practice, since the Share buttons only enable once a toolbar exists to hold
    /// them, but kept null-safe like every other _toolbar? access in this class).</summary>
    internal void SetShareBusy(bool busy) => _toolbar?.SetShareBusy(busy);

    /// <summary>The current palette as displayed: the persisted list, or (pre-item-08 settings
    /// file) the migration seed — every mutation below starts from this so the first edit
    /// materializes the migrated list into PaletteColors. Mirrors WPF's CurrentEffectivePalette
    /// (OverlayWindow.xaml.cs:2516-2520).</summary>
    private List<string> CurrentEffectivePalette() =>
        SwatchPalette.EffectivePalette(_liveSettings.PaletteColors, _liveSettings.CustomColors);

    /// <summary>Pushes the current effective palette into the toolbar's swatch row — called
    /// whenever the toolbar is (re)shown and after every palette edit. Mirrors WPF's
    /// RefreshToolbarPalette (OverlayWindow.xaml.cs:1819-1831).</summary>
    private void RefreshToolbarPalette() =>
        _toolbar?.SetPaletteColors(CurrentEffectivePalette(), _currentColor);

    /// <summary>Persists a palette mutation immediately (like the old custom-color flow) and
    /// refreshes the toolbar's swatch row from it. Mirrors WPF's UpdatePalette
    /// (OverlayWindow.xaml.cs:2522-2529).</summary>
    private void UpdatePalette(List<string> palette)
    {
        _liveSettings = _liveSettings with { PaletteColors = palette };
        TrySaveLiveSettings();
        RefreshToolbarPalette();
    }

    /// <summary>Right-click "Replace...": the toolbar has already run its own ColorReplaceFlyout
    /// (anchored to the swatch that was clicked — see ToolbarControl.BuildSwatchContextMenu) and
    /// hands back the swatch's index plus the newly picked color; this swaps that palette entry in
    /// place. If the replaced swatch was the active color, the replacement becomes active. Mirrors
    /// WPF's OnPaletteReplaceRequested (OverlayWindow.xaml.cs:2543-2564), adapted because WPF's
    /// System.Windows.Forms.ColorDialog is a blocking modal OverlayWindow itself could invoke,
    /// while Avalonia's Flyout is anchored/async UI that only ToolbarControl (which owns the swatch
    /// Control) can show — see ColorReplaceFlyout's own doc comment.</summary>
    private void OnPaletteReplaceRequested(int index, Color picked)
    {
        var palette = CurrentEffectivePalette();
        if (index < 0 || index >= palette.Count)
        {
            return;
        }

        bool wasActive = string.Equals(palette[index], FormatHex(_currentColor), StringComparison.OrdinalIgnoreCase);
        if (wasActive)
        {
            _currentColor = picked;
        }
        UpdatePalette(SwatchPalette.ReplaceAt(palette, index, FormatHex(picked)));
    }

    private static string FormatHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private void HideToolbar()
    {
        if (_toolbar is not null)
        {
            _toolbar.IsVisible = false;
            // Keep the visible checked state in sync with _currentTool being reset below — the
            // toolbar instance is reused the next time a selection is made.
            _toolbar.ResetToolSelection();
        }
        _currentTool = AnnotationTool.None;
        UpdateCursor();
    }

    /// <summary>Keeps the pointer cursor honest about what a click would do right now. Re-run on
    /// every pointer move (using the live drag/hit-test state) and whenever _currentTool or the
    /// selection changes. For the Select tool (AnnotationTool.None) this mirrors WPF's
    /// UpdateToolCursor exactly: the selected shape's own handles/endpoints beat the crop
    /// selection's, which beat hovering an editable annotation body (Hand), which beat the crop's
    /// own body/handle mapping. A drawing tool being active advertises the same Hand/handle/endpoint
    /// affordance over an already-placed same-tool shape (so a placed shape stays discoverably
    /// grabbable no matter which tool is active), falling back to a plain crosshair.</summary>
    private void UpdateCursor()
    {
        if (_currentTool != AnnotationTool.None)
        {
            if (_dragMode == DragMode.SelectedMove)
            {
                Cursor = new Cursor(StandardCursorType.Hand);
                return;
            }
            if (_dragMode == DragMode.SelectedResize)
            {
                Cursor = CursorForHandle(_activeHandle);
                return;
            }
            if (_dragMode == DragMode.SelectedEndpoint)
            {
                Cursor = CursorForSegmentEndpoint(_annotations.SelectedShape);
                return;
            }
            if (_dragMode == DragMode.None)
            {
                var hoverHandle = _annotations.HitTestSelectedHandle(_lastHoverPx);
                if (hoverHandle != SelectionHandle.None)
                {
                    Cursor = CursorForHandle(hoverHandle);
                    return;
                }
                if (_annotations.HitTestSelectedEndpoint(_lastHoverPx) >= 0)
                {
                    Cursor = CursorForSegmentEndpoint(_annotations.SelectedShape);
                    return;
                }
                if (HitEditableForTool(_lastHoverPx, interiorGrab: IsGrabModifierHeld(_lastKeyModifiers)) is not null)
                {
                    Cursor = new Cursor(StandardCursorType.Hand);
                    return;
                }
            }
            if (_currentTool == AnnotationTool.Pixelate)
            {
                // The blur/pixelate tool drags out a region like the area selector, so its cursor
                // is a fixed crosshair — NOT the brush circle whose diameter tracks the block size.
                // Scrolling the block size must not grow or shrink the crosshair.
                Cursor = new Cursor(StandardCursorType.Cross);
                return;
            }
            Cursor = _toolCursorCache.GetOrCreate(_currentTool, _currentColor, _currentStrokeWidth);
            return;
        }

        // While a drag IS active, the cursor tracks the drag kind rather than re-hit-testing — a
        // Move/Resize drag can carry the pointer off the body/handle it started on (fast pointer
        // movement outruns the rect), and it must still read as the drag it actually is.
        switch (_dragMode)
        {
            case DragMode.Move:
                Cursor = new Cursor(StandardCursorType.Hand);
                return;
            case DragMode.Resize:
                Cursor = CursorForHandle(_activeHandle);
                return;
            case DragMode.NewSelection:
                Cursor = new Cursor(StandardCursorType.Cross); // dragging out a new rect: stay crosshair
                return;
            case DragMode.SpanningMove:
                Cursor = new Cursor(StandardCursorType.Hand);
                return;
            case DragMode.SpanningResize:
                Cursor = CursorForHandle(_activeHandle);
                return;
            case DragMode.SelectedMove:
                Cursor = new Cursor(StandardCursorType.Hand);
                return;
            case DragMode.SelectedResize:
                Cursor = CursorForHandle(_activeHandle);
                return;
            case DragMode.SelectedEndpoint:
                Cursor = CursorForSegmentEndpoint(_annotations.SelectedShape);
                return;
        }

        Cursor = CursorForHover(_lastHoverPx);
    }

    /// <summary>Directional resize cursor for a Line/Arrow endpoint, chosen by the segment's own
    /// angle — dragging an endpoint stretches ALONG the segment, so the pointer should say "resize
    /// this way", not SizeAll's four-way move arrows (the grabbing hand already means "move the
    /// whole thing"). Buckets at ~22.5° off each axis (tan 67.5° ~= 2.414). Avalonia has no generic
    /// diagonal size cursor (only the rect-corner set already used by <see cref="CursorForHandle"/>),
    /// so the two diagonal buckets reuse TopLeftCorner/TopRightCorner for the same NW-SE/NE-SW
    /// visual direction — screen Y grows downward, so same-sign dx/dy is the NW-SE diagonal, exactly
    /// matching WPF's own bucket rule (which has real SizeNWSE/SizeNESW cursors to reach for).</summary>
    private static Cursor CursorForSegmentEndpoint(AnnotationShape? shape)
    {
        if (shape is null || shape.PointsPx.Count < 2)
        {
            return new Cursor(StandardCursorType.TopLeftCorner);
        }
        var d = shape.PointsPx[1] - shape.PointsPx[0];
        double adx = Math.Abs(d.X), ady = Math.Abs(d.Y);
        if (adx >= ady * 2.414)
        {
            return new Cursor(StandardCursorType.SizeWestEast);
        }
        if (ady >= adx * 2.414)
        {
            return new Cursor(StandardCursorType.SizeNorthSouth);
        }
        return (d.X > 0) == (d.Y > 0)
            ? new Cursor(StandardCursorType.TopLeftCorner)
            : new Cursor(StandardCursorType.TopRightCorner);
    }

    /// <summary>Maps a selection hit-test result to the matching Avalonia cursor: Hand over the
    /// body, the directional resize cursor over each handle, and Cross for None.</summary>
    private static Cursor CursorForHandle(SelectionHandle handle) => handle switch
    {
        SelectionHandle.Body => new Cursor(StandardCursorType.Hand),
        SelectionHandle.TopLeft => new Cursor(StandardCursorType.TopLeftCorner),
        SelectionHandle.TopRight => new Cursor(StandardCursorType.TopRightCorner),
        SelectionHandle.BottomLeft => new Cursor(StandardCursorType.BottomLeftCorner),
        SelectionHandle.BottomRight => new Cursor(StandardCursorType.BottomRightCorner),
        SelectionHandle.Top => new Cursor(StandardCursorType.SizeNorthSouth),
        SelectionHandle.Bottom => new Cursor(StandardCursorType.SizeNorthSouth),
        SelectionHandle.Left => new Cursor(StandardCursorType.SizeWestEast),
        SelectionHandle.Right => new Cursor(StandardCursorType.SizeWestEast),
        _ => new Cursor(StandardCursorType.Cross),
    };

    /// <summary>Static (non-drag) Select-tool hover cursor — mirrors the click priority in
    /// OnPreviewPointerPressed: the selected annotation's own handles/endpoints beat the crop
    /// selection's handles, which beat hovering an editable annotation body (Hand), which beat the
    /// crop's own body/handle mapping.</summary>
    private Cursor CursorForHover(Point px)
    {
        if (_annotations.SelectedShape is not null)
        {
            var selectedHandle = _annotations.HitTestSelectedHandle(px);
            if (selectedHandle != SelectionHandle.None)
            {
                return CursorForHandle(selectedHandle);
            }
            if (_annotations.HitTestSelectedEndpoint(px) >= 0)
            {
                return CursorForSegmentEndpoint(_annotations.SelectedShape);
            }
        }

        var cropHandle = _adorner.HitTestHandle(px);
        if (cropHandle != SelectionHandle.None && cropHandle != SelectionHandle.Body)
        {
            return CursorForHandle(cropHandle);
        }

        if (HitEditableForTool(px, interiorGrab: IsGrabModifierHeld(_lastKeyModifiers)) is not null)
        {
            return new Cursor(StandardCursorType.Hand);
        }

        return CursorForHandle(cropHandle); // Body -> Hand, None -> Cross
    }

    // ---------- Click-to-inspect color info panel ----------

    /// <summary>Small floating panel opened by a plain click (no drag): color swatch, hex, R/G/B
    /// bytes sampled from the tone-mapped preview (what the user sees), and the HDR nits value
    /// read from the raw CapturedFrame — the signature feature. The hex string is auto-copied
    /// to the clipboard. Dismissed by the next click, by starting a drag, or by Esc (stage 1 of
    /// the two-stage Esc); it never confirms or closes the overlay.</summary>
    private void ShowColorInfo(Point clickPx, Point clickDip)
    {
        DismissColorInfo();

        int sx = Math.Clamp((int)clickPx.X, 0, _preview.Width - 1);
        int sy = Math.Clamp((int)clickPx.Y, 0, _preview.Height - 1);
        int o = sy * _preview.Stride + sx * 4;
        byte b = _preview.Pixels[o];
        byte g = _preview.Pixels[o + 1];
        byte r = _preview.Pixels[o + 2];
        double nits = _frame.ReadPixelNits(
            Math.Clamp(sx, 0, _frame.Width - 1),
            Math.Clamp(sy, 0, _frame.Height - 1));

        string hex = string.Create(CultureInfo.InvariantCulture, $"#{r:X2}{g:X2}{b:X2}");
        var copyTask = ClipboardService.TryCopyTextAsync(this, hex);

        var swatch = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.FromRgb(r, g, b)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var hexText = new TextBlock
        {
            Text = hex,
            FontFamily = OverlayFonts.Mono,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
        };
        var rgbText = new TextBlock
        {
            Text = string.Create(CultureInfo.InvariantCulture, $"R{r} G{g} B{b}"),
            FontFamily = OverlayFonts.Mono,
            FontSize = 11.5,
            Foreground = Brushes.LightGray,
            Margin = new Thickness(0, 1, 0, 0),
        };
        var textColumn = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        textColumn.Children.Add(hexText);
        textColumn.Children.Add(rgbText);

        var topRow = new StackPanel { Orientation = Orientation.Horizontal };
        topRow.Children.Add(swatch);
        topRow.Children.Add(textColumn);

        // Same >250-nits "this is an HDR highlight" emphasis rule as the magnifier readout.
        bool isHighlight = nits > 250.0;
        var nitsText = new TextBlock
        {
            Text = string.Create(CultureInfo.InvariantCulture, $"{nits:0.#} nits"),
            FontFamily = OverlayFonts.Mono,
            FontSize = 15,
            FontWeight = FontWeight.Bold,
            Foreground = isHighlight ? new SolidColorBrush(Color.FromRgb(0xFF, 0xD5, 0x4F)) : Brushes.White,
            Margin = new Thickness(0, 7, 0, 0),
        };
        var copiedText = new TextBlock
        {
            Text = "copying…",
            FontSize = 10.5,
            Foreground = Brushes.LightGray,
            Margin = new Thickness(0, 4, 0, 0),
        };
        _ = UpdateCopiedLabelAsync(copyTask, copiedText);

        var stack = new StackPanel();
        stack.Children.Add(topRow);
        stack.Children.Add(nitsText);
        stack.Children.Add(copiedText);

        var panel = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xEC, 0x14, 0x14, 0x16)),
            BorderBrush = Brushes.DimGray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(11, 9, 11, 8),
            Child = stack,
            // Clicks must pass through: the next click anywhere dismisses this panel (and may
            // open a new one at that spot) rather than interacting with the panel itself.
            IsHitTestVisible = false,
        };

        panel.Measure(Size.Infinity);
        double w = panel.DesiredSize.Width;
        double h = panel.DesiredSize.Height;

        const double offsetDip = 16.0;
        double x = clickDip.X + offsetDip;
        double y = clickDip.Y + offsetDip;
        double actualWidth = ClientSize.Width, actualHeight = ClientSize.Height;
        if (actualWidth > 0 && x + w > actualWidth)
        {
            x = Math.Max(0, clickDip.X - offsetDip - w);
        }
        if (actualHeight > 0 && y + h > actualHeight)
        {
            y = Math.Max(0, clickDip.Y - offsetDip - h);
        }

        Canvas.SetLeft(panel, x);
        Canvas.SetTop(panel, y);
        _overlayCanvas.Children.Add(panel); // appended last => renders above magnifier/toolbar
        _colorInfoPanel = panel;
    }

    private static async Task UpdateCopiedLabelAsync(Task<bool> copyTask, TextBlock label)
    {
        bool copied;
        try
        {
            copied = await copyTask;
        }
        catch (Exception)
        {
            copied = false;
        }
        // On Windows the P/Invoke copy completes synchronously, so this runs before the panel is
        // even rendered — the user only ever sees the final state, same as the WPF app.
        label.Text = copied ? "copied" : "copy failed (clipboard busy)";
        label.Foreground = copied
            ? new SolidColorBrush(Color.FromRgb(0x7F, 0xD8, 0x8A))
            : new SolidColorBrush(Color.FromRgb(0xE0, 0x9A, 0x9A));
    }

    /// <summary>Removes the color-info panel if one is open. Returns whether anything was
    /// dismissed — Esc uses that to decide whether this press was consumed (stage 1) or should
    /// escalate to CancelStage.</summary>
    internal bool DismissColorInfo()
    {
        if (_colorInfoPanel is not { } panel)
        {
            return false;
        }
        _overlayCanvas.Children.Remove(panel);
        _colorInfoPanel = null;
        return true;
    }

    // ---------- Inline text annotation (cuttable per PLAN.md §3.2 if it endangers the milestone) ----------

    /// <summary>Double-click on a placed Text annotation (Select tool, or any tool — placed shapes
    /// are always clickable) reopens this SAME inline editor at the shape's own position, prefilled
    /// with its text/color/size — reuses BeginTextEditor's machinery wholesale rather than a second
    /// editor implementation. The original shape is suppressed from AnnotationLayer's normal render
    /// for the duration (see SuppressedFromRender) so the floating editor doesn't show doubled
    /// text.</summary>
    private void BeginTextReEdit(AnnotationShape shape, Point clickDip)
    {
        var originPx = shape.PointsPx[0];
        var originDip = new Point(originPx.X / _scaleX, originPx.Y / _scaleY);
        BeginTextEditor(originPx, originDip, shape);
    }

    private void BeginTextEditor(Point originPx, Point originDip, AnnotationShape? editingExisting = null)
    {
        CommitActiveTextEditor();

        _textEditReplacing = editingExisting;
        if (editingExisting is not null)
        {
            _annotations.SuppressedFromRender = editingExisting;
            _annotations.InvalidateVisual();
        }

        var editColor = editingExisting?.StrokeColor ?? _currentColor;
        var editor = new TextBox
        {
            MinWidth = 140,
            FontSize = editingExisting?.StrokeWidthPx ?? _textFontSize,
            FontFamily = new FontFamily(editingExisting?.TextFontFamily ?? _textFontFamily),
            FontWeight = (editingExisting?.TextBold ?? _textBold) ? FontWeight.Bold : FontWeight.Normal,
            FontStyle = (editingExisting?.TextItalic ?? _textItalic) ? FontStyle.Italic : FontStyle.Normal,
            Foreground = new SolidColorBrush(editColor),
            Background = new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0)),
            BorderBrush = new SolidColorBrush(editColor),
            BorderThickness = new Thickness(1),
            AcceptsReturn = false,
            AcceptsTab = false,
            Text = editingExisting?.Text ?? string.Empty,
        };
        if (editingExisting is not null)
        {
            editor.CaretIndex = editor.Text?.Length ?? 0; // reopen with the caret at the end, not a full re-type
        }

        _activeTextEditor = editor;
        _activeTextEditorOriginPx = originPx;
        Canvas.SetLeft(editor, originDip.X);
        Canvas.SetTop(editor, originDip.Y);
        _overlayCanvas.Children.Add(editor);

        editor.Focus();
    }

    private void CommitActiveTextEditor()
    {
        if (_activeTextEditor is not { } editor)
        {
            return;
        }

        string text = editor.Text ?? string.Empty;
        double fontSize = editor.FontSize;
        var replacing = _textEditReplacing;
        _overlayCanvas.Children.Remove(editor);
        _activeTextEditor = null;
        _textEditReplacing = null;
        _annotations.SuppressedFromRender = null;

        if (replacing is not null)
        {
            // A re-edit commit REPLACES the original shape, never adds a new one — an empty result
            // is treated exactly like the new-text path's degenerate-text guard below (silently
            // no-op, leaving the original untouched) rather than deleting it.
            if (!string.IsNullOrWhiteSpace(text))
            {
                var replacement = new AnnotationShape
                {
                    Tool = AnnotationTool.Text,
                    StrokeColor = ((SolidColorBrush)editor.Foreground!).Color,
                    StrokeWidthPx = fontSize,
                    Text = text,
                    TextFontFamily = editor.FontFamily.Name,
                    TextBold = editor.FontWeight == FontWeight.Bold,
                    TextItalic = editor.FontStyle == FontStyle.Italic,
                };
                replacement.PointsPx.Add(_activeTextEditorOriginPx);
                _annotations.ReplaceShape(replacing, replacement);
            }
            else
            {
                _annotations.InvalidateVisual(); // un-suppress the original so it renders again
            }
            return;
        }

        if (!string.IsNullOrWhiteSpace(text))
        {
            // Auto-select the newly committed text exactly like a just-placed Pixelate/Rectangle/
            // etc — its chrome shows up immediately so it reads as grabbable/editable like every
            // other kind, without a trip through the Select tool.
            var committed = _annotations.CommitText(
                _activeTextEditorOriginPx, text, _currentColor, fontSize, _textFontFamily, _textBold, _textItalic);
            if (committed is not null)
            {
                _annotations.Select(committed);
            }
        }
    }

    private void CancelActiveTextEditor()
    {
        if (_activeTextEditor is not { } editor)
        {
            return;
        }
        _overlayCanvas.Children.Remove(editor);
        _activeTextEditor = null;
        _textEditReplacing = null;
        _annotations.SuppressedFromRender = null;
        _annotations.InvalidateVisual();
    }

    // ---------- Export / feedback ----------

    /// <summary>Renders the current selection's crop with all committed annotations burned in, at
    /// 1:1 physical-pixel scale (96 DPI both axes) — the same WYSIWYG path as the WPF version's
    /// DrawingVisual + RenderTargetBitmap, using Avalonia's RenderTargetBitmap so annotations are
    /// rasterized by the exact same drawing code that rendered them on screen.</summary>
    internal SdrImage RenderSelectionWithAnnotations()
    {
        if (_selectionPx is not { } sel)
        {
            throw new InvalidOperationException("OverlayWindow has no active selection to render.");
        }

        // O8 audit fix: defensively clamp to the frame here too, not just in the drag handlers.
        // Confirm can race a resize/move drag or an annotation that nudged the rect a pixel past
        // the frame edge between the last SetSelection and this call; Crop() below throws
        // ArgumentOutOfRangeException on ANY out-of-bounds rect, which — left uncaught in a
        // fire-and-forget ConfirmAsync — would otherwise turn Enter/Ctrl+C/Ctrl+S into a silent
        // no-op with no diagnostic anywhere.
        var n = ClampToFrame(sel).Normalized();
        if (n.Width < 1 || n.Height < 1)
        {
            throw new InvalidOperationException("Selection is empty.");
        }

        var cropped = _preview.Crop(n);
        int w = n.Width, h = n.Height;

        using var croppedBitmap = cropped.ToAvaloniaBitmap();
        using var rtb = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
        using (var dc = rtb.CreateDrawingContext())
        {
            dc.DrawImage(croppedBitmap, new Rect(0, 0, w, h));
            _annotations.RenderForExport(dc, new Point(n.Left, n.Top));
        }

        int stride = w * 4;
        var pixels = new byte[stride * h];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            rtb.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), pixels.Length, stride);
        }
        finally
        {
            handle.Free();
        }

        // The backend's native surface format may be RGBA rather than BGRA (Skia picks per
        // platform); SdrImage's contract is BGRA8 straight-alpha. Everything drawn is opaque
        // (the crop forces A=255 and strokes composite over it), so only channel order needs
        // normalizing — and alpha is forced to 255 exactly like the WPF FormatConvertedBitmap
        // path ended up producing.
        bool isRgba = rtb.Format is { } fmt && fmt == PixelFormat.Rgba8888;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (isRgba)
            {
                (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
            }
            pixels[i + 3] = 255;
        }

        return new SdrImage(w, h, pixels);
    }

    internal void ShowShutterFlash()
    {
        var flash = new Avalonia.Controls.Shapes.Rectangle
        {
            Fill = Brushes.White,
            IsHitTestVisible = false,
            Width = Math.Max(1.0, ClientSize.Width),
            Height = Math.Max(1.0, ClientSize.Height),
            Opacity = 0.85,
            Transitions = new Transitions
            {
                new DoubleTransition
                {
                    Property = OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(220),
                    Easing = new QuadraticEaseOut(),
                },
            },
        };
        _overlayCanvas.Children.Add(flash);

        Dispatcher.UIThread.Post(() => flash.Opacity = 0.0, DispatcherPriority.Background);
        DispatcherTimer.RunOnce(() => _overlayCanvas.Children.Remove(flash), TimeSpan.FromMilliseconds(300));
    }

    internal void CloseOverlay()
    {
        Close();
        // Belt-and-braces alongside the Closed handler (O4 audit fix): a window that was placed
        // via TryPlaceOnScreen but never actually reached a shown state isn't guaranteed to raise
        // Closed on every backend — make sure the preview bitmap is freed either way.
        DisposePreviewBitmap();
    }

    private void DisposePreviewBitmap()
    {
        _previewBitmap?.Dispose();
        _previewBitmap = null;
    }
}
