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
    private enum DragMode { None, NewSelection, Resize, Move, Annotation }

    private readonly CapturedFrame _frame;
    private readonly SdrImage _preview;
    private readonly RoeSnipSettings _settings;
    private readonly Action<OverlayWindow> _onActivatedByMouse;
    private readonly Action<OverlayWindow> _onSelectionStarted;
    private readonly Action<OverlayCommand> _onCommand;

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
    private RectPhysical _dragStartRect;
    private Border? _colorInfoPanel;
    private IPointer? _capturedPointer;

    private AnnotationTool _currentTool = AnnotationTool.None;
    private Color _currentColor = Colors.Red;
    private double _currentStrokeWidth = 4.0;

    private ToolbarControl? _toolbar;
    private TextBox? _activeTextEditor;
    private Point _activeTextEditorOriginPx;

    internal MonitorInfo Monitor => _frame.Monitor;
    internal CapturedFrame Frame => _frame;
    internal RectPhysical? SelectionPx => _selectionPx;

    /// <summary>O1 audit fix: set by OverlayController while the (only owner-modal) save picker
    /// is open, on EVERY window in the session — not just the one that opened the picker — so a
    /// key/pointer event on a DIFFERENT monitor can't cancel/close/mutate the session while a
    /// write is in flight. Checked at the top of every tunnel-routed input handler below.</summary>
    internal bool SessionInputSuspended { get; set; }

    /// <summary>True while this window has anything worth cancelling short of closing the whole
    /// overlay — used by OverlayController's two-stage Esc (stage: clear snip, stay open).</summary>
    internal bool HasSnipInProgress =>
        _selectionPx is not null || _annotations.HasAnnotations || _dragMode != DragMode.None;

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
        _settings = null!;
        _onActivatedByMouse = null!;
        _onSelectionStarted = null!;
        _onCommand = null!;
    }

    internal OverlayWindow(
        CapturedFrame frame,
        SdrImage preview,
        RoeSnipSettings settings,
        Action<OverlayWindow> onActivatedByMouse,
        Action<OverlayWindow> onSelectionStarted,
        Action<OverlayCommand> onCommand)
        : this()
    {
        _frame = frame;
        _preview = preview;
        _settings = settings;
        _onActivatedByMouse = onActivatedByMouse;
        _onSelectionStarted = onSelectionStarted;
        _onCommand = onCommand;

        // Nearest-neighbor scaling for the frozen preview, same as the WPF version's
        // BitmapScalingMode.NearestNeighbor — on an all-96-DPI machine this is a 1:1 blit anyway.
        RenderOptions.SetBitmapInterpolationMode(_previewImage, BitmapInterpolationMode.None);
        _previewBitmap = preview.ToAvaloniaBitmap();
        _previewImage.Source = _previewBitmap;

        // O4 audit fix: free the preview bitmap on every path this window stops being used —
        // the normal Closed path, AND CloseOverlay() below covers windows that were placed but
        // never actually got to fire Closed (e.g. TryPlaceOnScreen succeeded but Show() threw
        // partway through the session, or the window was closed before ever being shown).
        Closed += (_, _) => DisposePreviewBitmap();

        // Tunnel-routed handlers are the Avalonia analog of WPF's Preview* events — they run
        // before any child control (toolbar buttons, text editor) sees the event.
        AddHandler(PointerPressedEvent, OnPreviewPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnPreviewPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnPreviewPointerReleased, RoutingStrategies.Tunnel);
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
            return; // let the toolbar's own controls handle their own click
        }

        var dip = e.GetPosition(this);
        var px = ToPhysical(dip);

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

        if (e.ClickCount >= 2 && _selectionPx is { } existing && RectContains(existing, px))
        {
            _onCommand(OverlayCommand.ConfirmPlain);
            e.Handled = true;
            return;
        }

        if (_currentTool != AnnotationTool.None)
        {
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

        var handle = _adorner.HitTestHandle(px);
        if (handle == SelectionHandle.Body)
        {
            _dragMode = DragMode.Move;
            _activeHandle = handle;
            _dragAnchorPx = px;
            _dragStartRect = _selectionPx!.Value;
        }
        else if (handle != SelectionHandle.None)
        {
            _dragMode = DragMode.Resize;
            _activeHandle = handle;
            _dragAnchorPx = px;
            _dragStartRect = _selectionPx!.Value;
        }
        else
        {
            // Don't start (or clear) any selection yet: below ClickDragThresholdPx of travel this
            // is a color-inspection click, not a drag. The selection work happens in
            // OnPreviewPointerMoved once the threshold is crossed.
            _dragMode = DragMode.NewSelection;
            _newSelectionPending = true;
            _dragAnchorPx = px;
            _dragAnchorDip = dip;
        }

        CapturePointer(e.Pointer);
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

        _magnifier.Update(_preview, _frame, dip, px);

        switch (_dragMode)
        {
            case DragMode.NewSelection:
                if (_newSelectionPending)
                {
                    double dx = px.X - _dragAnchorPx.X, dy = px.Y - _dragAnchorPx.Y;
                    if (dx * dx + dy * dy < ClickDragThresholdPx * ClickDragThresholdPx)
                    {
                        break; // still a click candidate, not a drag — don't disturb any selection
                    }
                    _newSelectionPending = false;
                    _onSelectionStarted(this); // clears other monitors' selections, per DESIGN.md
                }
                SetSelection(RectPhysical.FromSize(
                    (int)Math.Min(_dragAnchorPx.X, px.X),
                    (int)Math.Min(_dragAnchorPx.Y, px.Y),
                    (int)Math.Abs(px.X - _dragAnchorPx.X),
                    (int)Math.Abs(px.Y - _dragAnchorPx.Y)));
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
                SetSelection(ApplyResize(_dragStartRect, _activeHandle, px));
                break;

            case DragMode.Annotation:
                _annotations.UpdateShape(px);
                break;
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
                else if (_selectionPx is { } sel)
                {
                    SetSelection(sel.Width < 2 || sel.Height < 2 ? null : ClampToFrame(sel));
                }
                break;

            case DragMode.Resize:
            case DragMode.Move:
                if (_selectionPx is { } sel2)
                {
                    SetSelection(ClampToFrame(sel2.Normalized()));
                }
                break;

            case DragMode.Annotation:
                _annotations.EndShape();
                break;
        }

        bool wasAreaDrag = _dragMode is DragMode.NewSelection or DragMode.Move or DragMode.Resize;
        _dragMode = DragMode.None;
        _activeHandle = SelectionHandle.None;
        ReleasePointer();

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

    private static RectPhysical ApplyResize(RectPhysical start, SelectionHandle handle, Point px)
    {
        int left = start.Left, top = start.Top, right = start.Right, bottom = start.Bottom;
        int x = (int)px.X, y = (int)px.Y;

        switch (handle)
        {
            case SelectionHandle.TopLeft: left = x; top = y; break;
            case SelectionHandle.Top: top = y; break;
            case SelectionHandle.TopRight: right = x; top = y; break;
            case SelectionHandle.Right: right = x; break;
            case SelectionHandle.BottomRight: right = x; bottom = y; break;
            case SelectionHandle.Bottom: bottom = y; break;
            case SelectionHandle.BottomLeft: left = x; bottom = y; break;
            case SelectionHandle.Left: left = x; break;
        }

        return new RectPhysical(left, top, right, bottom).Normalized();
    }

    private static bool IsWithin(Visual? container, object? source)
    {
        if (container is null || source is not Visual visual)
        {
            return false;
        }
        Visual? current = visual;
        while (current is not null)
        {
            if (ReferenceEquals(current, container))
            {
                return true;
            }
            current = current.GetVisualParent();
        }
        return false;
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
            _annotations.Undo();
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
        SetSelection(null);
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

        // A drag that's creating/moving/resizing the selection shouldn't show the toolbar
        // mid-drag — it belongs at the FINAL rect, not flickering along for the ride. Annotation
        // drags are exempt: they draw inside the already-settled selection and never change its
        // bounds. Just collapse the existing element (if any) rather than HideToolbar(), which
        // also resets _currentTool and the toolbar's checked-tool state as if the selection had
        // been cleared — neither of which should happen mid-drag.
        if (_dragMode is DragMode.NewSelection or DragMode.Move or DragMode.Resize)
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
            _toolbar.ToolSelected += tool => { _currentTool = tool; UpdateCursor(); };
            _toolbar.ColorSelected += color => _currentColor = color;
            _toolbar.StrokeWidthSelected += width => _currentStrokeWidth = width;
            _toolbar.UndoClicked += () => _annotations.Undo();
            _toolbar.CopyClicked += () => _onCommand(OverlayCommand.Copy);
            _toolbar.SaveClicked += () => _onCommand(OverlayCommand.Save);
            _toolbar.SaveHdrClicked += () => _onCommand(OverlayCommand.SaveHdr);
            // The toolbar's X button always closes the whole overlay outright — deliberately NOT
            // the staged CancelStage semantics Esc has.
            _toolbar.CancelClicked += () => _onCommand(OverlayCommand.Cancel);
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
        _toolbar.IsVisible = true;
    }

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

    /// <summary>Keeps the pointer cursor honest about what a click would do right now. A drawing
    /// tool being active always wins (crosshair, same as this window's default) — otherwise, with
    /// the Select tool active, hovering the selection body shows a grab cursor and hovering a
    /// resize handle shows the matching directional resize cursor, so an already-placed selection
    /// doesn't keep looking like a fresh crosshair-drag target. Re-run on every pointer move (using
    /// the live drag/hit-test state) and whenever _currentTool changes.</summary>
    private void UpdateCursor()
    {
        if (_currentTool != AnnotationTool.None)
        {
            Cursor = new Cursor(StandardCursorType.Cross);
            return;
        }

        // While a drag IS active, the cursor tracks the drag kind rather than re-hit-testing —
        // a Move/Resize drag can carry the pointer off the body/handle it started on (fast mouse
        // movement outruns the rect), and it must still read as the drag it actually is.
        SelectionHandle handle = _dragMode switch
        {
            DragMode.Move => SelectionHandle.Body,
            DragMode.Resize => _activeHandle,
            DragMode.NewSelection => SelectionHandle.None, // dragging out a new rect: stay crosshair
            _ => _adorner.HitTestHandle(_lastHoverPx),
        };

        Cursor = handle switch
        {
            SelectionHandle.Body => new Cursor(StandardCursorType.Hand),
            SelectionHandle.TopLeft => new Cursor(StandardCursorType.TopLeftCorner),
            SelectionHandle.TopRight => new Cursor(StandardCursorType.TopRightCorner),
            SelectionHandle.BottomLeft => new Cursor(StandardCursorType.BottomLeftCorner),
            SelectionHandle.BottomRight => new Cursor(StandardCursorType.BottomRightCorner),
            SelectionHandle.Top => new Cursor(StandardCursorType.TopSide),
            SelectionHandle.Bottom => new Cursor(StandardCursorType.BottomSide),
            SelectionHandle.Left => new Cursor(StandardCursorType.LeftSide),
            SelectionHandle.Right => new Cursor(StandardCursorType.RightSide),
            _ => new Cursor(StandardCursorType.Cross),
        };
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

    private void BeginTextEditor(Point originPx, Point originDip)
    {
        CommitActiveTextEditor();

        var editor = new TextBox
        {
            MinWidth = 140,
            FontSize = Math.Max(14.0, _currentStrokeWidth * 4.0),
            Foreground = new SolidColorBrush(_currentColor),
            Background = new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0)),
            BorderBrush = new SolidColorBrush(_currentColor),
            BorderThickness = new Thickness(1),
            AcceptsReturn = false,
            AcceptsTab = false,
        };

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
        _overlayCanvas.Children.Remove(editor);
        _activeTextEditor = null;

        if (!string.IsNullOrWhiteSpace(text))
        {
            _annotations.CommitText(_activeTextEditorOriginPx, text, _currentColor, fontSize);
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
