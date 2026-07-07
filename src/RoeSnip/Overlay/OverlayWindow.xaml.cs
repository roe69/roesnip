using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
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

    private double _scaleX = 1.0, _scaleY = 1.0;
    private RectPhysical? _selectionPx;

    private DragMode _dragMode = DragMode.None;
    private SelectionHandle _activeHandle = SelectionHandle.None;
    private Point _dragAnchorPx;
    private RectPhysical _dragStartRect;

    private AnnotationTool _currentTool = AnnotationTool.None;
    private Color _currentColor = Colors.Red;
    private double _currentStrokeWidth = 4.0;

    private ToolbarControl? _toolbar;
    private TextBox? _activeTextEditor;
    private Point _activeTextEditorOriginPx;

    internal MonitorInfo Monitor => _frame.Monitor;
    internal CapturedFrame Frame => _frame;
    internal RectPhysical? SelectionPx => _selectionPx;

    internal OverlayWindow(
        CapturedFrame frame,
        SdrImage preview,
        RoeSnipSettings settings,
        Action<OverlayWindow> onActivatedByMouse,
        Action<OverlayWindow> onSelectionStarted,
        Action<OverlayCommand> onCommand)
    {
        InitializeComponent();

        _frame = frame;
        _preview = preview;
        _settings = settings;
        _onActivatedByMouse = onActivatedByMouse;
        _onSelectionStarted = onSelectionStarted;
        _onCommand = onCommand;

        PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        PreviewMouseMove += OnPreviewMouseMove;
        PreviewMouseLeftButtonUp += OnPreviewMouseLeftButtonUp;
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
            _onSelectionStarted(this);
            _dragMode = DragMode.NewSelection;
            _dragAnchorPx = px;
            SetSelection(RectPhysical.FromSize((int)px.X, (int)px.Y, 0, 0));
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
                if (_selectionPx is { } sel)
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

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        if (e.Key == Key.Escape)
        {
            _onCommand(OverlayCommand.Cancel);
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
            Annotations.Undo();
            e.Handled = true;
        }
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

        double toolbarWidth = toolbar.ActualWidth > 0 ? toolbar.ActualWidth : 480.0;
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
            OverlayCanvas.Children.Add(_toolbar);
        }
        _toolbar.Visibility = Visibility.Visible;
    }

    private void HideToolbar()
    {
        if (_toolbar is not null)
        {
            _toolbar.Visibility = Visibility.Collapsed;
        }
        _currentTool = AnnotationTool.None;
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
        OverlayCanvas.Children.Add(editor);

        editor.Focus();
        Keyboard.Focus(editor);
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
            Annotations.CommitText(_activeTextEditorOriginPx, text, _currentColor, fontSize);
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
