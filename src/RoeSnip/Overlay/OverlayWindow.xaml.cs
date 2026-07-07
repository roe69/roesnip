using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RoeSnip.App;
using RoeSnip.Capture;
using RoeSnip.Imaging;
using RoeSnip.Interop;

namespace RoeSnip.Overlay;

// RoeSnip.csproj enables both UseWPF and UseWindowsForms, so System.Windows.Forms/System.Drawing
// are in scope alongside System.Windows/System.Windows.Media/.Input/.Controls — alias the
// colliding names to WPF's. (Declared after the namespace line — see AnnotationLayer.cs for why:
// RoeSnip.Color, a sibling WP-A namespace, would otherwise shadow an outer-scope alias for "Color".)
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;
using TextBox = System.Windows.Controls.TextBox;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Brushes = System.Windows.Media.Brushes;
using Orientation = System.Windows.Controls.Orientation;
using Size = System.Windows.Size;
using FontFamily = System.Windows.Media.FontFamily;

/// <summary>One per monitor. Shows the frozen tone-mapped preview, dims everything outside the
/// current selection, handles drag-to-select / resize / move, hosts the magnifier and (once a
/// selection exists) the annotation toolbar, and renders the final annotated crop on confirm.
/// All selection/annotation/crop math is physical pixels; DIPs from WPF mouse events are converted
/// via <see cref="_scaleX"/>/<see cref="_scaleY"/> (from CompositionTarget.TransformToDevice) at
/// the point of use, per the mixed-DPI contract in DESIGN.md / PLAN.md §3.2.</summary>
public partial class OverlayWindow : Window
{
    private enum DragMode { None, NewSelection, Resize, Move, Annotation }

    private readonly CapturedFrame _frame;
    private readonly SdrImage _preview;
    private readonly RoeSnipSettings _settings;
    private readonly Action<OverlayWindow> _onActivatedByMouse;
    private readonly Action<OverlayWindow> _onSelectionStarted;
    private readonly Action<OverlayCommand> _onCommand;
    private readonly Action<PickedColorInfo> _onColorPicked;
    private readonly bool _pickOnlyMode;

    // Mutable working copy of settings for changes made live during the session (custom colors from
    // the toolbar's "+" swatch) — persisted immediately via App.SettingsStore so they survive even
    // though the immutable _settings snapshot handed in at session start does not change in place.
    private RoeSnipSettings _liveSettings;

    private double _scaleX = 1.0, _scaleY = 1.0;
    private RectPhysical? _selectionPx;

    /// <summary>A mouse-down that would start a new selection is treated as a *click* (color pick)
    /// until the cursor has travelled at least this many physical pixels — only then does it become
    /// a selection drag.</summary>
    private const double ClickDragThresholdPx = 4.0;

    private DragMode _dragMode = DragMode.None;
    private SelectionHandle _activeHandle = SelectionHandle.None;
    private Point _dragAnchorPx;
    private Point _dragAnchorDip;
    private bool _newSelectionPending;
    private RectPhysical _dragStartRect;

    private AnnotationTool _currentTool = AnnotationTool.None;
    private Color _currentColor = Colors.Red;
    private double _currentStrokeWidth = 4.0;

    // Last-used text-annotation style (item 4 / DESIGN addendum), seeded from settings and updated
    // live from the toolbar's text-style group; applies to new text annotations and to an
    // in-progress edit.
    private string _textFontFamily;
    private double _textFontSize;
    private bool _textBold;
    private bool _textItalic;

    // Transient scroll-wheel size indicator (item 3): a single reusable element, re-triggered on
    // each wheel tick rather than stacking overlapping fade animations.
    private System.Windows.Controls.Border? _sizeIndicator;
    private DispatcherTimer? _sizeIndicatorTimer;

    private ToolbarControl? _toolbar;
    private TextBox? _activeTextEditor;
    private Point _activeTextEditorOriginPx;

    internal MonitorInfo Monitor => _frame.Monitor;
    internal CapturedFrame Frame => _frame;
    internal RectPhysical? SelectionPx => _selectionPx;

    /// <summary>True while this window has anything worth cancelling short of closing the whole
    /// overlay — used by OverlayController's two-stage Esc (stage: clear snip, stay open).</summary>
    internal bool HasSnipInProgress =>
        _selectionPx is not null || Annotations.HasAnnotations || _dragMode != DragMode.None;

    /// <summary>True while an inline text annotation TextBox is being edited. Read by the
    /// session-scoped low-level keyboard hook (SessionKeyboardHook) to decide whether to swallow
    /// session keys globally (normal case) or let everything except Esc reach the real focused
    /// control through the normal WPF pipeline (text-editing case) — see OverlayInputInterop.cs.</summary>
    internal bool IsTextEditingActive => _activeTextEditor is not null;

    internal OverlayWindow(
        CapturedFrame frame,
        SdrImage preview,
        RoeSnipSettings settings,
        Action<OverlayWindow> onActivatedByMouse,
        Action<OverlayWindow> onSelectionStarted,
        Action<OverlayCommand> onCommand,
        Action<PickedColorInfo> onColorPicked,
        bool pickOnlyMode)
    {
        InitializeComponent();

        _frame = frame;
        _preview = preview;
        _settings = settings;
        _liveSettings = settings;
        _onActivatedByMouse = onActivatedByMouse;
        _onSelectionStarted = onSelectionStarted;
        _onCommand = onCommand;
        _onColorPicked = onColorPicked;
        _pickOnlyMode = pickOnlyMode;

        _textFontFamily = settings.TextFontFamily;
        _textFontSize = settings.TextFontSize;
        _textBold = settings.TextBold;
        _textItalic = settings.TextItalic;

        PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        PreviewMouseMove += OnPreviewMouseMove;
        PreviewMouseLeftButtonUp += OnPreviewMouseLeftButtonUp;
        PreviewMouseWheel += OnPreviewMouseWheel;
        PreviewKeyDown += OnPreviewKeyDown;
        MouseEnter += OnMouseEnter;
        SizeChanged += (_, _) =>
        {
            SyncChromeSizes();
            UpdateDimGeometry();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var bounds = _frame.Monitor.BoundsPx;
        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowPos(
            hwnd, NativeMethods.HWND_TOPMOST,
            bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is { } target)
        {
            _scaleX = target.TransformToDevice.M11;
            _scaleY = target.TransformToDevice.M22;
        }

        Annotations.DeviceScaleX = _scaleX;
        Annotations.DeviceScaleY = _scaleY;
        Adorner.DeviceScaleX = _scaleX;
        Adorner.DeviceScaleY = _scaleY;
        MagnifierControl.DeviceScaleX = _scaleX;
        MagnifierControl.DeviceScaleY = _scaleY;

        PreviewImage.Source = _preview.ToBitmapSource();
        RenderOptions.SetBitmapScalingMode(PreviewImage, BitmapScalingMode.NearestNeighbor);

        UpdateDimGeometry();
    }

    private void SyncChromeSizes()
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0)
        {
            return;
        }
        Annotations.Width = w;
        Annotations.Height = h;
        Adorner.Width = w;
        Adorner.Height = h;
        MagnifierControl.Width = w;
        MagnifierControl.Height = h;
    }

    // ---------- Mouse interaction ----------

    private Point ToPhysical(Point dip) => new(dip.X * _scaleX, dip.Y * _scaleY);

    private void OnMouseEnter(object sender, MouseEventArgs e) => _onActivatedByMouse(this);

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsWithinToolbar(e.OriginalSource as DependencyObject))
        {
            return; // let the toolbar's own controls handle their own click
        }

        var dip = e.GetPosition(this);
        var px = ToPhysical(dip);

        if (_pickOnlyMode)
        {
            // Pick-only mode (ColorPickerWindow's "Pick" button): no selection/toolbar/annotation
            // concept at all — every click immediately picks the color under the cursor and closes
            // this ad-hoc overlay session. No drag-threshold gating (unlike normal mode's plain
            // click), since there's nothing else a click could mean here.
            _onActivatedByMouse(this);
            TriggerColorPick(px, dip);
            e.Handled = true;
            return;
        }

        if (_activeTextEditor is not null && !ReferenceEquals(e.OriginalSource, _activeTextEditor))
        {
            CommitActiveTextEditor();
        }

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
                Annotations.BeginShape(_currentTool, px, _currentColor, _currentStrokeWidth);
                CaptureMouse();
            }
            e.Handled = true;
            return;
        }

        var handle = Adorner.HitTestHandle(px);
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
            // OnPreviewMouseMove once the threshold is crossed.
            _dragMode = DragMode.NewSelection;
            _newSelectionPending = true;
            _dragAnchorPx = px;
            _dragAnchorDip = dip;
        }

        CaptureMouse();
        e.Handled = true;
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        var dip = e.GetPosition(this);
        var px = ToPhysical(dip);

        MagnifierControl.Update(_preview, _frame, dip, px);

        switch (_dragMode)
        {
            case DragMode.NewSelection:
                if (_newSelectionPending)
                {
                    if ((px - _dragAnchorPx).LengthSquared < ClickDragThresholdPx * ClickDragThresholdPx)
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
                var delta = px - _dragAnchorPx;
                var moved = new RectPhysical(
                    (int)(_dragStartRect.Left + delta.X), (int)(_dragStartRect.Top + delta.Y),
                    (int)(_dragStartRect.Right + delta.X), (int)(_dragStartRect.Bottom + delta.Y));
                SetSelection(ClampToFrame(moved));
                break;
            }

            case DragMode.Resize:
                SetSelection(ApplyResize(_dragStartRect, _activeHandle, px));
                break;

            case DragMode.Annotation:
                Annotations.UpdateShape(px);
                break;
        }
    }

    private void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        switch (_dragMode)
        {
            case DragMode.NewSelection:
                if (_newSelectionPending)
                {
                    // Mouse never travelled far enough to become a drag: this is a plain click —
                    // per the round-2 spec this now cancels the whole snip (like Esc) and opens the
                    // standalone ColorPickerWindow with the clicked pixel's color, replacing the old
                    // inline click panel.
                    _newSelectionPending = false;
                    TriggerColorPick(_dragAnchorPx, _dragAnchorDip);
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
                Annotations.EndShape();
                break;
        }

        _dragMode = DragMode.None;
        _activeHandle = SelectionHandle.None;
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
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

    private bool IsWithinToolbar(DependencyObject? source)
    {
        if (_toolbar is null)
        {
            return false;
        }
        var current = source;
        while (current is not null)
        {
            if (ReferenceEquals(current, _toolbar))
            {
                return true;
            }
            current = current is Visual ? VisualTreeHelper.GetParent(current) : null;
        }
        return false;
    }

    // ---------- Keyboard ----------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ProcessKeyCommand(e.Key, Keyboard.Modifiers))
        {
            e.Handled = true;
        }
    }

    /// <summary>The single implementation of what each session key means, shared by two callers:
    /// this window's own WPF PreviewKeyDown (used whenever the overlay genuinely has OS focus) and
    /// SessionKeyboardHook's low-level keyboard hook (used unconditionally, since a background tray
    /// process cannot reliably steal foreground focus — see OverlayInputInterop.cs for the full
    /// root-cause writeup). Keeping one implementation guarantees the two paths can never disagree.
    /// Returns whether the key was consumed.</summary>
    internal bool ProcessKeyCommand(Key key, ModifierKeys modifiers)
    {
        if (_activeTextEditor is not null)
        {
            if (key == Key.Escape)
            {
                CancelActiveTextEditor();
                return true;
            }
            if (key == Key.Enter)
            {
                CommitActiveTextEditor();
                return true;
            }
            // Everything else (typing, Ctrl+C/paste within the box, etc.) must reach the real
            // focused TextBox through the normal WPF pipeline — not handled here, and the
            // low-level hook deliberately doesn't swallow these while text-editing either.
            return false;
        }

        bool ctrl = (modifiers & ModifierKeys.Control) != 0;

        if (key == Key.Escape)
        {
            // The session's cross-monitor two-stage Esc (clear an in-progress snip on whichever
            // monitor holds it, else close the whole overlay) needs the session's view, so it
            // always escalates to CancelStage — an active text edit (the only other local Esc
            // stage) was already consumed above.
            _onCommand(OverlayCommand.CancelStage);
            return true;
        }
        if (key == Key.Enter)
        {
            _onCommand(OverlayCommand.ConfirmPlain);
            return true;
        }
        if (ctrl && key == Key.C)
        {
            _onCommand(OverlayCommand.Copy);
            return true;
        }
        if (ctrl && key == Key.S)
        {
            _onCommand(OverlayCommand.Save);
            return true;
        }
        if (ctrl && key == Key.Z)
        {
            Annotations.Undo();
            return true;
        }
        return false;
    }

    // ---------- Selection / toolbar / dim mask ----------

    private void SetSelection(RectPhysical? rect)
    {
        _selectionPx = rect;
        Adorner.SelectionPx = rect;
        Adorner.InvalidateVisual();
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
            if (IsMouseCaptured)
            {
                ReleaseMouseCapture();
            }
        }
        CancelActiveTextEditor();
        Annotations.Clear();
        SetSelection(null);
    }

    private void UpdateDimGeometry()
    {
        double w = ActualWidth > 0 ? ActualWidth : _frame.Width / Math.Max(_scaleX, 1e-6);
        double h = ActualHeight > 0 ? ActualHeight : _frame.Height / Math.Max(_scaleY, 1e-6);
        var outer = new RectangleGeometry(new Rect(0, 0, w, h));

        if (_selectionPx is { } sel)
        {
            var n = sel.Normalized();
            var holeDip = new Rect(n.Left / _scaleX, n.Top / _scaleY, n.Width / _scaleX, n.Height / _scaleY);
            DimPath.Data = new CombinedGeometry(GeometryCombineMode.Exclude, outer, new RectangleGeometry(holeDip));
        }
        else
        {
            DimPath.Data = outer;
        }
    }

    private void UpdateToolbarPlacement()
    {
        if (_selectionPx is not { } sel)
        {
            HideToolbar();
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

        double toolbarWidth = toolbar.ActualWidth > 0 ? toolbar.ActualWidth : 700.0;
        double toolbarHeight = toolbar.ActualHeight > 0 ? toolbar.ActualHeight : 44.0;

        if (ActualHeight > 0 && y + toolbarHeight > ActualHeight)
        {
            y = n.Top / _scaleY - toolbarHeight - 8.0; // flip above if it would go off the bottom
            if (y < 0)
            {
                y = 0;
            }
        }
        if (ActualWidth > 0 && x + toolbarWidth > ActualWidth)
        {
            x = ActualWidth - toolbarWidth;
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
            _toolbar.ToolSelected += tool => _currentTool = tool;
            _toolbar.ColorSelected += color => _currentColor = color;
            _toolbar.StrokeWidthSelected += width => _currentStrokeWidth = width;
            _toolbar.UndoClicked += () => Annotations.Undo();
            _toolbar.CopyClicked += () => _onCommand(OverlayCommand.Copy);
            _toolbar.SaveClicked += () => _onCommand(OverlayCommand.Save);
            _toolbar.SaveHdrClicked += () => _onCommand(OverlayCommand.SaveHdr);
            // The toolbar's X button always closes the whole overlay outright — deliberately NOT
            // the staged CancelStage semantics Esc has.
            _toolbar.CancelClicked += () => _onCommand(OverlayCommand.Cancel);

            // Text-style group (item 4): live-applies to new annotations and to an in-progress edit.
            _toolbar.FontSizeSelected += size => SetTextFontSize(size, showIndicator: false);
            _toolbar.BoldToggled += bold =>
            {
                _textBold = bold;
                if (_activeTextEditor is not null)
                {
                    _activeTextEditor.FontWeight = bold ? FontWeights.Bold : FontWeights.Normal;
                }
            };
            _toolbar.ItalicToggled += italic =>
            {
                _textItalic = italic;
                if (_activeTextEditor is not null)
                {
                    _activeTextEditor.FontStyle = italic ? FontStyles.Italic : FontStyles.Normal;
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

            // Custom colors (item 5): "+" swatch opens System.Windows.Forms.ColorDialog.
            _toolbar.CustomColorRequested += OnCustomColorRequested;

            OverlayCanvas.Children.Add(_toolbar);
        }

        _toolbar.InitializeTextStyle(_textFontFamily, _textFontSize, _textBold, _textItalic);
        _toolbar.SetCustomColors(_liveSettings.CustomColors);
        _toolbar.Visibility = Visibility.Visible;
    }

    private void HideToolbar()
    {
        if (_toolbar is not null)
        {
            _toolbar.Visibility = Visibility.Collapsed;
            // Keep the visible checked state in sync with _currentTool being reset below — the
            // toolbar instance is reused the next time a selection is made.
            _toolbar.ResetToolSelection();
        }
        _currentTool = AnnotationTool.None;
    }

    // ---------- Click-to-pick: standalone ColorPickerWindow (replaces the old inline click panel) ----------

    /// <summary>A plain click (normal mode) or any click (pick-only mode) samples the clicked
    /// pixel's color from the tone-mapped preview (what the user sees) and its HDR nits value from
    /// the raw FP16 CapturedFrame, auto-copies the hex to the clipboard (existing behavior, kept),
    /// hands the snapshot up to OverlayController's standalone ColorPickerWindow singleton, and
    /// cancels this session — the whole overlay closes, exactly like Esc. The Cancel is dispatched
    /// via BeginInvoke rather than called inline: Cancel synchronously closes every window in the
    /// session (including this one, from inside its own mouse-event handler), and deferring it to
    /// the next dispatcher cycle avoids touching a window that's already mid-teardown.</summary>
    private void TriggerColorPick(Point clickPx, Point clickDip)
    {
        int sx = Math.Clamp((int)clickPx.X, 0, _preview.Width - 1);
        int sy = Math.Clamp((int)clickPx.Y, 0, _preview.Height - 1);
        int o = sy * _preview.Stride + sx * 4;
        byte b = _preview.Pixels[o];
        byte g = _preview.Pixels[o + 1];
        byte r = _preview.Pixels[o + 2];
        double nits = _frame.ReadPixelNits(
            Math.Clamp(sx, 0, _frame.Width - 1),
            Math.Clamp(sy, 0, _frame.Height - 1));

        TryCopyTextToClipboard(string.Create(CultureInfo.InvariantCulture, $"#{r:X2}{g:X2}{b:X2}"));

        var screenPx = new Point(_frame.Monitor.BoundsPx.Left + clickPx.X, _frame.Monitor.BoundsPx.Top + clickPx.Y);
        var info = new PickedColorInfo(r, g, b, nits, screenPx, _frame.Monitor.DpiX, _frame.Monitor.DpiY);
        _onColorPicked(info);

        Dispatcher.BeginInvoke(new Action(() => _onCommand(OverlayCommand.Cancel)));
    }

    private static bool TryCopyTextToClipboard(string text)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
            return true;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Clipboard transiently locked by another process — the panel reports the failure;
            // color inspection is a convenience, never worth crashing the overlay over.
            return false;
        }
    }

    // ---------- Inline text annotation (cuttable per PLAN.md §3.2 if it endangers the milestone) ----------

    private void BeginTextEditor(Point originPx, Point originDip)
    {
        CommitActiveTextEditor();

        var editor = new TextBox
        {
            MinWidth = 140,
            FontSize = _textFontSize,
            FontFamily = new FontFamily(_textFontFamily),
            FontWeight = _textBold ? FontWeights.Bold : FontWeights.Normal,
            FontStyle = _textItalic ? FontStyles.Italic : FontStyles.Normal,
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
        OverlayCanvas.Children.Add(editor);

        editor.Focus();
        Keyboard.Focus(editor);

        // Reliability layer (b): best-effort — a background tray process may not hold the actual OS
        // foreground grant (see OverlayInputInterop.cs), which normally doesn't matter because the
        // session-scoped WH_KEYBOARD_LL hook (layer a) handles Esc/Enter/Ctrl+C/Ctrl+S/Ctrl+Z
        // regardless of focus. Typing into this TextBox (and IME composition) is different: it needs
        // *real* keyboard/text-services focus, which the hook deliberately does not fake. So try
        // hard here, but never let a failure be fatal — the hook still guarantees Esc cancels the
        // edit even if this doesn't manage to steal focus.
        TryActivateForTextInput();
    }

    /// <summary>AttachThreadInput trick (reliability layer b, per the round-2 spec): Activate() a
    /// background-process window is not guaranteed to actually move OS foreground focus onto it
    /// (SetForegroundWindow has restrictions on which process may steal foreground). If a plain
    /// Activate() didn't work, temporarily attach this thread's input queue to whichever thread
    /// currently owns the foreground window, so SetForegroundWindow/SetFocus are allowed to
    /// succeed, then detach again immediately. Best-effort only — failures are logged, never
    /// thrown, and the low-level keyboard hook (layer a) is what actually guarantees Esc still
    /// cancels the edit even if this doesn't manage to grab real focus.</summary>
    private void TryActivateForTextInput()
    {
        try
        {
            Activate();

            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero || OverlayInputInterop.GetForegroundWindow() == hwnd)
            {
                return;
            }

            IntPtr foregroundHwnd = OverlayInputInterop.GetForegroundWindow();
            uint foregroundThreadId = OverlayInputInterop.GetWindowThreadProcessId(foregroundHwnd, IntPtr.Zero);
            uint thisThreadId = OverlayInputInterop.GetCurrentThreadId();

            if (foregroundThreadId == 0 || foregroundThreadId == thisThreadId)
            {
                return;
            }

            bool attached = OverlayInputInterop.AttachThreadInput(foregroundThreadId, thisThreadId, true);
            try
            {
                OverlayInputInterop.SetForegroundWindow(hwnd);
                OverlayInputInterop.SetFocus(hwnd);
            }
            finally
            {
                if (attached)
                {
                    OverlayInputInterop.AttachThreadInput(foregroundThreadId, thisThreadId, false);
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: best-effort text-input activation failed: {ex.Message}");
        }
    }

    private void CommitActiveTextEditor()
    {
        if (_activeTextEditor is not { } editor)
        {
            return;
        }

        string text = editor.Text;
        double fontSize = editor.FontSize;
        OverlayCanvas.Children.Remove(editor);
        _activeTextEditor = null;

        if (!string.IsNullOrWhiteSpace(text))
        {
            Annotations.CommitText(_activeTextEditorOriginPx, text, _currentColor, fontSize, _textFontFamily, _textBold, _textItalic);
        }
    }

    private void CancelActiveTextEditor()
    {
        if (_activeTextEditor is not { } editor)
        {
            return;
        }
        OverlayCanvas.Children.Remove(editor);
        _activeTextEditor = null;
    }

    // ---------- Scroll-wheel sizing (item 3) ----------

    /// <summary>Sel/no tool: wheel does nothing (no zoom). Text tool: wheel resizes the font,
    /// including live while an edit is open. Any other (drawing) tool: wheel resizes the stroke
    /// width. Either way, a transient cursor-follower indicator shows the new size for ~600ms and
    /// the toolbar's own numeric display updates to match.</summary>
    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_currentTool == AnnotationTool.None)
        {
            return;
        }

        int notches = e.Delta / 120;
        if (notches == 0)
        {
            return;
        }

        var cursorDip = e.GetPosition(this);

        if (_currentTool == AnnotationTool.Text)
        {
            double newSize = Math.Clamp(_textFontSize + notches * 2.0, 6.0, 96.0);
            SetTextFontSize(newSize, showIndicator: true, cursorDip);
        }
        else
        {
            double newWidth = Math.Clamp(_currentStrokeWidth + notches, 1.0, 32.0);
            SetStrokeWidth(newWidth, cursorDip);
        }

        e.Handled = true;
    }

    private void SetTextFontSize(double size, bool showIndicator, Point cursorDip = default)
    {
        _textFontSize = size;
        _toolbar?.SetFontSize(size);
        if (_activeTextEditor is not null)
        {
            _activeTextEditor.FontSize = size;
        }
        if (showIndicator)
        {
            ShowSizeIndicator(cursorDip, string.Create(CultureInfo.InvariantCulture, $"{size:0}pt"), circleDiameterDip: null);
        }
    }

    private void SetStrokeWidth(double width, Point cursorDip)
    {
        _currentStrokeWidth = width;
        _toolbar?.SetStrokeWidth(width);
        ShowSizeIndicator(cursorDip, string.Create(CultureInfo.InvariantCulture, $"{width:0}px"), circleDiameterDip: width);
    }

    /// <summary>A single reusable indicator element, re-triggered (not stacked) on each wheel tick:
    /// updates content/position and restarts the same ~600ms fade-out timer rather than layering a
    /// new fading element per tick, which would otherwise flicker under a fast wheel.</summary>
    private void ShowSizeIndicator(Point cursorDip, string label, double? circleDiameterDip)
    {
        if (_sizeIndicator is null)
        {
            _sizeIndicator = new System.Windows.Controls.Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x14, 0x14, 0x16)),
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                IsHitTestVisible = false,
            };
            OverlayCanvas.Children.Add(_sizeIndicator);
        }
        else
        {
            _sizeIndicator.BeginAnimation(OpacityProperty, null); // cancel any in-flight fade
        }

        var content = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 5, 8, 5) };
        if (circleDiameterDip is { } d)
        {
            double clamped = Math.Clamp(d, 2.0, 40.0);
            content.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = clamped,
                Height = clamped,
                Fill = Brushes.White,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        content.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });

        _sizeIndicator.Child = content;
        _sizeIndicator.Opacity = 1.0;
        _sizeIndicator.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        double x = cursorDip.X + 20.0;
        double y = cursorDip.Y + 20.0;
        Canvas.SetLeft(_sizeIndicator, x);
        Canvas.SetTop(_sizeIndicator, y);

        _sizeIndicatorTimer?.Stop();
        _sizeIndicatorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _sizeIndicatorTimer.Tick += (_, _) =>
        {
            _sizeIndicatorTimer?.Stop();
            var fade = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(250))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            };
            _sizeIndicator?.BeginAnimation(OpacityProperty, fade);
        };
        _sizeIndicatorTimer.Start();
    }

    // ---------- Custom colors (item 5) ----------

    /// <summary>The toolbar's "+" swatch: opens System.Windows.Forms.ColorDialog (FullOpen so the
    /// full custom-color editor is visible immediately) owned by this window's HWND so it stacks
    /// correctly above the topmost overlay. The chosen color becomes the active draw color and is
    /// pushed onto the persisted custom palette (settings, additive, capped at 8, LRU).</summary>
    private void OnCustomColorRequested()
    {
        using var dialog = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            AnyColor = true,
        };

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        var owner = new Win32WindowHandle(hwnd);

        if (dialog.ShowDialog(owner) != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }

        var picked = dialog.Color;
        _currentColor = Color.FromRgb(picked.R, picked.G, picked.B);
        string hex = ColorFormatting.HexWithHash(picked.R, picked.G, picked.B);

        _liveSettings = _liveSettings with
        {
            CustomColors = BoundedColorList.Push(_liveSettings.CustomColors, hex, maxCount: 8),
        };
        TrySaveLiveSettings();
        _toolbar?.SetCustomColors(_liveSettings.CustomColors);
    }

    private void TrySaveLiveSettings()
    {
        try
        {
            SettingsStore.Save(_liveSettings);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: failed to save custom color settings: {ex.Message}");
        }
    }

    /// <summary>Minimal System.Windows.Forms.IWin32Window adapter so ColorDialog.ShowDialog can be
    /// owned by this WPF window's HWND (WindowInteropHelper) instead of popping up unowned behind
    /// the topmost overlay.</summary>
    private sealed class Win32WindowHandle : System.Windows.Forms.IWin32Window
    {
        public Win32WindowHandle(IntPtr handle) => Handle = handle;
        public IntPtr Handle { get; }
    }

    // ---------- Export / feedback ----------

    /// <summary>Renders the current selection's crop with all committed annotations burned in, at
    /// 1:1 physical-pixel scale (96 DPI both axes — see SdrImage.ToBitmapSource's contract).</summary>
    internal SdrImage RenderSelectionWithAnnotations()
    {
        if (_selectionPx is not { } sel)
        {
            throw new InvalidOperationException("OverlayWindow has no active selection to render.");
        }

        var n = sel.Normalized();
        var cropped = _preview.Crop(n);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(cropped.ToBitmapSource(), new Rect(0, 0, n.Width, n.Height));
            Annotations.RenderForExport(dc, new Point(n.Left, n.Top));
        }

        var rtb = new RenderTargetBitmap(Math.Max(1, n.Width), Math.Max(1, n.Height), 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        var converted = new FormatConvertedBitmap(rtb, PixelFormats.Bgra32, null, 0);
        int stride = n.Width * 4;
        var pixels = new byte[stride * n.Height];
        converted.CopyPixels(pixels, stride, 0);

        return new SdrImage(n.Width, n.Height, pixels);
    }

    internal void ShowShutterFlash()
    {
        var flash = new System.Windows.Shapes.Rectangle
        {
            Fill = Brushes.White,
            IsHitTestVisible = false,
            Width = Math.Max(1.0, ActualWidth),
            Height = Math.Max(1.0, ActualHeight),
            Opacity = 0.85,
        };
        OverlayCanvas.Children.Add(flash);

        var animation = new DoubleAnimation(0.85, 0.0, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        animation.Completed += (_, _) => OverlayCanvas.Children.Remove(flash);
        flash.BeginAnimation(OpacityProperty, animation);
    }

    internal void CloseOverlay() => Close();
}
