using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using RoeSnip.Capture;
using RoeSnip.Interop;

namespace RoeSnip.Recording;

// Same WPF/WinForms name-collision aliases as the sibling Overlay/* files.
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using WCursors = System.Windows.Input.Cursors;
using WCursor = System.Windows.Input.Cursor;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

/// <summary>The "this is being recorded" frame AND the handle for resizing / moving the recorded
/// region. A thin topmost window whose dashed two-tone border hugs the region from the OUTSIDE and
/// whose grab band lets the user reshape or reposition it:
///   - Setup (before Start): drag an EDGE or CORNER of the band to RESIZE; hold Shift or Ctrl and
///     drag anywhere inside to MOVE. A plain click inside does nothing (passes through).
///   - Capturing / Reviewing (a take exists, so the size is locked to the encoder): dragging any
///     edge/corner MOVES the region, no modifier needed; a drag slides the recorder's crop live.
///
/// Layered-window input note (this is what a first version got wrong): a WPF AllowsTransparency
/// window is WS_EX_LAYERED, and the OS routes the mouse by the per-pixel ALPHA - fully transparent
/// (alpha 0) pixels are click-through and never even raise WM_NCHITTEST. So the interactive areas
/// must be painted with alpha >= 1: the band always carries a faint fill, and in Setup the inner
/// gets a faint tint too (which doubles as a "this is your region" highlight); once capturing, the
/// inner keeps a near-invisible (alpha 0x01, ~0.4%) fill instead of going fully transparent, so it
/// stays hit-testable and Shift/Ctrl can still grab it to move the region mid-recording - a fully
/// transparent inner would be click-through and WM_NCHITTEST would never even fire there.
/// WM_NCHITTEST then refines it - an un-modified click inside returns HTTRANSPARENT so it still
/// falls through to the app being recorded. The band sits outside the captured rect so it never
/// lands in a recorded frame; the alpha-0x01 inner fill sits exactly over the captured rect and is
/// deliberately kept imperceptible rather than capture-excluded (this window is not
/// WDA_EXCLUDEFROMCAPTURE - only RecordingChrome is).</summary>
internal sealed class RegionOutline : Window
{
    /// <summary>Physical pixels of grab band around the region (also the resize/move hit zone).</summary>
    private const int BandPx = 8;
    private const int FrameInsetPx = 4; // dashed frame sits this far outside the region
    private const int MinSizePx = 32;

    private enum DragKind { None, Move, Left, Right, Top, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }

    private readonly MonitorInfo _monitor;
    private RectPhysical _selectionPx; // slides/reshapes on drag
    private readonly OutlineElement _element;

    private bool _allowResize = true; // true in Setup; false once capturing (size locked)
    private DragKind _drag = DragKind.None;
    private Native.POINT _dragStartCursor;
    private RectPhysical _dragStartSel;

    /// <summary>Raised on the UI thread whenever a drag changes the region (move or resize), with the
    /// new monitor-relative selection.</summary>
    public event Action<RectPhysical>? RegionChanged;

    public RegionOutline(MonitorInfo monitor, RectPhysical selectionPx)
    {
        _monitor = monitor;
        _selectionPx = selectionPx;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;

        _element = new OutlineElement { Selection = selectionPx, ShowInnerTint = true };
        Content = _element;
    }

    /// <summary>Setup vs capturing. In Setup the band resizes and the inner is a tinted move-target;
    /// once capturing (size locked) the band moves and the inner reverts to click-through so the
    /// recorded app stays interactive.</summary>
    public void SetInteractionMode(bool allowResize)
    {
        _allowResize = allowResize;
        _element.ShowInnerTint = allowResize;
        _element.InvalidateVisual();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;

        long exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(
            exStyle | NativeMethods.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE));

        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        MoveWindowToSelection(hwnd);
    }

    private void MoveWindowToSelection(IntPtr hwnd)
    {
        int outerW = _selectionPx.Width + BandPx * 2;
        int outerH = _selectionPx.Height + BandPx * 2;
        var b = _monitor.BoundsPx;
        NativeMethods.SetWindowPos(
            hwnd, NativeMethods.HWND_TOPMOST,
            b.Left + _selectionPx.Left - BandPx, b.Top + _selectionPx.Top - BandPx,
            outerW, outerH, NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case Native.WM_NCHITTEST:
            {
                int sx = (short)(lParam.ToInt64() & 0xFFFF);
                int sy = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
                var kind = ZoneAt(sx, sy);
                handled = true;
                return new IntPtr(kind == DragKind.None ? Native.HTTRANSPARENT : Native.HTCLIENT);
            }
            case Native.WM_LBUTTONDOWN:
            {
                Native.GetCursorPos(out _dragStartCursor);
                _drag = ZoneAt(_dragStartCursor.X, _dragStartCursor.Y);
                if (_drag != DragKind.None)
                {
                    _dragStartSel = _selectionPx;
                    _element.Cursor = WpfCursorFor(_drag);
                    Native.SetCapture(hwnd);
                    handled = true;
                }
                // _drag == None here means WM_NCHITTEST classified this point as HTTRANSPARENT (a
                // plain click inside the region, meant to fall through to the app being recorded —
                // see the class doc). Unconditionally setting handled = true regardless of _drag (the
                // bug this fixes) swallowed WM_LBUTTONDOWN itself even for a click-through point,
                // same as WM_MOUSEMOVE/WM_LBUTTONUP below already correctly avoid.
                break;
            }
            case Native.WM_MOUSEMOVE:
            {
                if (_drag != DragKind.None)
                {
                    Native.GetCursorPos(out var cur);
                    var next = ApplyDrag(_drag, _dragStartSel, cur.X - _dragStartCursor.X, cur.Y - _dragStartCursor.Y);
                    if (next != _selectionPx)
                    {
                        _selectionPx = next;
                        _element.Selection = next;
                        _element.InvalidateVisual();
                        MoveWindowToSelection(hwnd);
                        RegionChanged?.Invoke(next);
                    }
                    handled = true;
                }
                break;
            }
            case Native.WM_LBUTTONUP:
            {
                if (_drag != DragKind.None)
                {
                    _drag = DragKind.None;
                    Native.ReleaseCapture();
                    handled = true;
                }
                break;
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>Classifies a screen point into a drag zone given the current mode. Band edges/corners
    /// resize (Setup) or move (Capturing); the inner is a modifier-gated move (Setup) or nothing.</summary>
    private DragKind ZoneAt(int screenX, int screenY)
    {
        var b = _monitor.BoundsPx;
        int rx = screenX - (b.Left + _selectionPx.Left - BandPx);
        int ry = screenY - (b.Top + _selectionPx.Top - BandPx);
        int outerW = _selectionPx.Width + BandPx * 2;
        int outerH = _selectionPx.Height + BandPx * 2;
        if (rx < 0 || ry < 0 || rx >= outerW || ry >= outerH)
        {
            return DragKind.None;
        }

        bool nearLeft = rx < BandPx;
        bool nearRight = rx >= BandPx + _selectionPx.Width;
        bool nearTop = ry < BandPx;
        bool nearBottom = ry >= BandPx + _selectionPx.Height;
        bool onBand = nearLeft || nearRight || nearTop || nearBottom;

        if (onBand)
        {
            if (!_allowResize)
            {
                return DragKind.Move; // size locked while capturing - edges move
            }
            if (nearTop && nearLeft) return DragKind.TopLeft;
            if (nearTop && nearRight) return DragKind.TopRight;
            if (nearBottom && nearLeft) return DragKind.BottomLeft;
            if (nearBottom && nearRight) return DragKind.BottomRight;
            if (nearLeft) return DragKind.Left;
            if (nearRight) return DragKind.Right;
            if (nearTop) return DragKind.Top;
            return DragKind.Bottom;
        }

        // Inside the region: a move only while Shift/Ctrl is held, else fall through to the app.
        bool modifier =
            (Native.GetKeyState(Native.VK_SHIFT) & 0x8000) != 0 ||
            (Native.GetKeyState(Native.VK_CONTROL) & 0x8000) != 0;
        return modifier ? DragKind.Move : DragKind.None;
    }

    // Cursor feedback goes through WPF (set on the element), NOT Win32 WM_SETCURSOR: WPF re-applies
    // the element's own cursor while processing each WM_MOUSEMOVE, which would immediately clobber a
    // cursor set from a WM_SETCURSOR hook (that was the "cursor only changes once I start dragging"
    // bug). Setting the element's Cursor makes WPF itself show the right one on hover. During a drag
    // this handler bails, so the grab cursor set at drag start persists.
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag != DragKind.None)
        {
            return;
        }
        Native.GetCursorPos(out var c);
        _element.Cursor = WpfCursorFor(ZoneAt(c.X, c.Y));
    }

    private static WCursor WpfCursorFor(DragKind kind) => kind switch
    {
        DragKind.Left or DragKind.Right => WCursors.SizeWE,
        DragKind.Top or DragKind.Bottom => WCursors.SizeNS,
        DragKind.TopLeft or DragKind.BottomRight => WCursors.SizeNWSE,
        DragKind.TopRight or DragKind.BottomLeft => WCursors.SizeNESW,
        DragKind.Move => WCursors.SizeAll,
        _ => WCursors.Arrow,
    };

    /// <summary>Computes the new selection for a drag of <paramref name="kind"/> by (dx,dy) from the
    /// selection captured at drag start. Moves translate; resizes move the grabbed edge(s) while the
    /// opposite edge stays put. Clamped to the monitor, kept at/above the minimum, and forced to even
    /// width/height (the H.264/GIF encoders need even dimensions).</summary>
    private RectPhysical ApplyDrag(DragKind kind, RectPhysical s, int dx, int dy)
    {
        int monW = _monitor.BoundsPx.Width, monH = _monitor.BoundsPx.Height;

        if (kind == DragKind.Move)
        {
            int nl = Math.Clamp(s.Left + dx, 0, Math.Max(0, monW - s.Width));
            int nt = Math.Clamp(s.Top + dy, 0, Math.Max(0, monH - s.Height));
            return RectPhysical.FromSize(nl, nt, s.Width, s.Height);
        }

        int left = s.Left, top = s.Top, right = s.Right, bottom = s.Bottom;

        if (kind is DragKind.Left or DragKind.TopLeft or DragKind.BottomLeft)
            left = Math.Clamp(s.Left + dx, 0, right - MinSizePx);
        if (kind is DragKind.Right or DragKind.TopRight or DragKind.BottomRight)
            right = Math.Clamp(s.Right + dx, left + MinSizePx, monW);
        if (kind is DragKind.Top or DragKind.TopLeft or DragKind.TopRight)
            top = Math.Clamp(s.Top + dy, 0, bottom - MinSizePx);
        if (kind is DragKind.Bottom or DragKind.BottomLeft or DragKind.BottomRight)
            bottom = Math.Clamp(s.Bottom + dy, top + MinSizePx, monH);

        int w = (right - left) & ~1; // even
        int h = (bottom - top) & ~1;
        // If a left/top edge is the one moving, keep the fixed (right/bottom) edge anchored after the
        // even-rounding by recomputing the moving edge from the fixed one.
        if (kind is DragKind.Left or DragKind.TopLeft or DragKind.BottomLeft) left = right - w;
        if (kind is DragKind.Top or DragKind.TopLeft or DragKind.TopRight) top = bottom - h;
        return RectPhysical.FromSize(left, top, Math.Max(MinSizePx, w), Math.Max(MinSizePx, h));
    }

    /// <summary>Close, from RecordingSession (UI thread).</summary>
    public void CloseOutline() => Close();

    /// <summary>Draws the dashed frame (two-tone marching-ants recipe) plus the faint fills that make
    /// the band - and, in Setup, the inner - hit-testable on this layered window.</summary>
    private sealed class OutlineElement : FrameworkElement
    {
        private const double StrokePhysicalPx = 1.5;

        private RectPhysical _selection;
        private bool _showInnerTint;
        private double _pensScale = -1;
        private Pen? _underPen;
        private Pen? _dashPen;

        // Faint fills (alpha kept low so they read as a subtle highlight, but > 0 so the layered
        // window hit-tests these pixels - see the class doc).
        private static readonly Brush s_bandFill = Frozen(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
        private static readonly Brush s_innerTint = Frozen(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
        // Near-zero alpha (~0.4%) inner fill used while capturing/reviewing: imperceptible in the
        // recording, but still > 0 so the layered window keeps hit-testing the inner and Shift/Ctrl
        // can grab it to move the region while recording (see class doc).
        private static readonly Brush s_innerGhostFill = Frozen(Color.FromArgb(0x01, 0xFF, 0xFF, 0xFF));

        public RectPhysical Selection { get => _selection; set => _selection = value; }
        public bool ShowInnerTint { get => _showInnerTint; set => _showInnerTint = value; }

        protected override void OnRender(DrawingContext dc)
        {
            double scaleX = 1.0, scaleY = 1.0;
            if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
            {
                scaleX = target.TransformToDevice.M11;
                scaleY = target.TransformToDevice.M22;
            }
            double scale = Math.Max(scaleX, scaleY);
            if (Math.Abs(scale - _pensScale) > 0.001 || _underPen is null || _dashPen is null)
            {
                _pensScale = scale;
                _underPen = CreateFrozenPen(Color.FromArgb(0xB0, 0x00, 0x00, 0x00), StrokePhysicalPx / scale, null);
                _dashPen = CreateFrozenPen(
                    Color.FromArgb(0xFF, 0xDC, 0xDC, 0xE0), StrokePhysicalPx / scale, new DashStyle(new double[] { 3, 3 }, 0));
            }

            double band = BandPx / scaleX;
            double innerX = band, innerY = BandPx / scaleY;
            double innerW = _selection.Width / scaleX, innerH = _selection.Height / scaleY;
            double outerW = (_selection.Width + BandPx * 2) / scaleX, outerH = (_selection.Height + BandPx * 2) / scaleY;

            // Band ring fill (outer rect minus inner rect), always present so the band hit-tests.
            var full = new RectangleGeometry(new Rect(0, 0, outerW, outerH));
            var hole = new RectangleGeometry(new Rect(innerX, innerY, innerW, innerH));
            dc.DrawGeometry(s_bandFill, null, new CombinedGeometry(GeometryCombineMode.Exclude, full, hole));

            // Inner fill is always present so the inner stays hit-testable (layered-window alpha-0
            // pixels are click-through, which would swallow the Shift/Ctrl move while capturing):
            // the visible setup tint in Setup, a near-invisible ghost fill in Capturing/Reviewing.
            dc.DrawRectangle(_showInnerTint ? s_innerTint : s_innerGhostFill, null, new Rect(innerX, innerY, innerW, innerH));

            // Dashed frame, FrameInsetPx outside the region.
            double fx = FrameInsetPx / scaleX, fy = FrameInsetPx / scaleY;
            var frame = new Rect(fx, fy, Math.Max(0, outerW - fx * 2), Math.Max(0, outerH - fy * 2));
            dc.DrawRectangle(null, _underPen, frame);
            dc.DrawRectangle(null, _dashPen, frame);
        }

        private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        private static Pen CreateFrozenPen(Color color, double thickness, DashStyle? dash)
        {
            var pen = new Pen(new SolidColorBrush(color), thickness) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
            if (dash is not null) { pen.DashStyle = dash; pen.DashCap = PenLineCap.Flat; }
            pen.Freeze();
            return pen;
        }
    }

    /// <summary>Win32 bits used only for the drag/hit-test interaction, kept local to this window.</summary>
    private static class Native
    {
        public const int WM_NCHITTEST = 0x0084, WM_SETCURSOR = 0x0020,
            WM_LBUTTONDOWN = 0x0201, WM_MOUSEMOVE = 0x0200, WM_LBUTTONUP = 0x0202;
        public const int HTTRANSPARENT = -1, HTCLIENT = 1;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int VK_SHIFT = 0x10, VK_CONTROL = 0x11;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern IntPtr SetCapture(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern short GetKeyState(int vKey);
    }
}
