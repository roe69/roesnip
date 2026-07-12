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
/// region. A thin topmost window whose gray dotted line hugs the region boundary EXACTLY from the
/// OUTSIDE (the stroke is offset outward by half its own thickness so it touches, but never crosses,
/// the recorded rect's edge) and whose grab band lets the user reshape or reposition it:
///   - Setup (before Start): drag an EDGE or CORNER of the band to RESIZE; hold Shift or Ctrl and
///     drag anywhere inside to MOVE. A plain click inside does nothing (passes through).
///   - Capturing / Reviewing (a take exists, so the size is locked to the encoder): dragging any
///     edge/corner MOVES the region, no modifier needed; a drag slides the recorder's crop live.
///
/// Layered-window input note (this is what a first version got wrong): a WPF AllowsTransparency
/// window is WS_EX_LAYERED, and the OS routes the mouse by the per-pixel ALPHA - fully transparent
/// (alpha 0) pixels are click-through and never even raise WM_NCHITTEST. So the interactive areas
/// must be painted with alpha >= 1: the band always carries a faint fill. The INNER is the
/// opposite case: it sits exactly over the app being recorded, and returning HTTRANSPARENT from
/// WM_NCHITTEST only forwards the hit to windows of THIS THREAD - over another process's window it
/// silently swallows the click (that shipped as "clicks don't reach the recorded app" twice). The
/// only cross-process click-through is real alpha 0, so the inner is normally NOT painted at all,
/// in every state; a 20 ms poll (backstopped by a WM_NCHITTEST re-check) paints it (the Setup
/// tint, or a near-invisible alpha-0x01 fill while capturing) only while Shift/Ctrl is held, which
/// is exactly when it must hit-test for the modifier-move - an active drag needs no fill because
/// SetCapture delivers its mouse messages regardless of alpha.
/// The band sits outside the captured rect so it never lands in a
/// recorded frame; the modifier-held ghost fill sits over the captured rect and is kept
/// imperceptible rather than capture-excluded (this window is not WDA_EXCLUDEFROMCAPTURE - only
/// RecordingChrome is). The band's own fill is dropped to alpha 0x01 (not 0) so it stays
/// hit-testable while reading as visually blank; the only thing the user actually sees is the gray
/// dotted line drawn at the recorded rect's true edge.</summary>
internal sealed class RegionOutline : Window
{
    /// <summary>Physical pixels of grab band around the region (also the resize/move hit zone).</summary>
    private const int BandPx = 8;
    private const int MinSizePx = 32;

    private enum DragKind { None, Move, Left, Right, Top, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }

    private readonly MonitorInfo _monitor;
    private RectPhysical _selectionPx; // slides/reshapes on drag
    private readonly OutlineElement _element;

    private bool _allowResize = true; // true in Setup; false once capturing (size locked)
    private DragKind _drag = DragKind.None;
    private Native.POINT _dragStartCursor;
    private RectPhysical _dragStartSel;
    private System.Windows.Threading.DispatcherTimer? _modifierPoll;

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

    /// <summary>Setup vs capturing. In Setup the band resizes and a Shift/Ctrl-held inner shows the
    /// visible move tint; once capturing (size locked) the band moves and a held inner paints only
    /// the imperceptible ghost fill. Either way an un-held inner is unpainted = click-through.</summary>
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

        // Paints/unpaints the inner as Shift/Ctrl goes down/up (see the class doc). Polled because
        // this WS_EX_NOACTIVATE window never receives keyboard messages of its own; GetAsyncKeyState
        // is the physical key state, no focus needed. The poll is the only path that can paint the
        // fill ON (an unpainted inner raises no messages at all), so it runs every 20 ms at Normal
        // priority - Input priority can starve under encoder load (the f7aa9a3 lesson) and a stale
        // fill swallows clicks. State-change-only invalidation keeps the tick free of renders.
        _modifierPoll = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(20),
        };
        _modifierPoll.Tick += (_, _) => UpdateInnerHitFill();
        _modifierPoll.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _modifierPoll?.Stop();
        _modifierPoll = null;
        base.OnClosed(e);
    }

    /// <summary>The inner is painted exactly while Shift/Ctrl is held - never for a drag: once a
    /// drag owns the mouse via SetCapture, its messages arrive regardless of pixel alpha, so a
    /// modifier released mid-drag can unpaint without dropping the drag, and a plain band resize
    /// never tints the region.</summary>
    private void UpdateInnerHitFill()
    {
        bool wanted =
            (Native.GetAsyncKeyState(Native.VK_SHIFT) & 0x8000) != 0
            || (Native.GetAsyncKeyState(Native.VK_CONTROL) & 0x8000) != 0;
        if (_element.InnerHitTestable != wanted)
        {
            _element.InnerHitTestable = wanted;
            _element.InvalidateVisual();
        }
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
                // A hit against a stale still-painted inner (modifier just released, poll hasn't
                // ticked) would return HTTRANSPARENT and swallow the click cross-process; re-checking
                // here makes the very first mouse move over the region queue the alpha-0 repaint, so
                // the swallow window shrinks from a poll tick to about one render frame.
                UpdateInnerHitFill();
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

    /// <summary>AutomationServer hook (dev-gated automation channel — App/AutomationServer.cs):
    /// applies a new selection exactly like a finished band drag would - the same field updates
    /// (_selectionPx / _element.Selection / MoveWindowToSelection) and the same RegionChanged
    /// event as WM_MOUSEMOVE's drag-apply branch above - without needing a live mouse-move stream.
    /// rect is MONITOR-RELATIVE physical pixels; clamped/evened the same way ApplyDrag does.</summary>
    internal void SetSelectionForAutomation(RectPhysical monitorRelativeRect)
    {
        var r = monitorRelativeRect.Normalized();
        int monW = _monitor.BoundsPx.Width, monH = _monitor.BoundsPx.Height;
        int left = Math.Clamp(r.Left, 0, Math.Max(0, monW - MinSizePx));
        int top = Math.Clamp(r.Top, 0, Math.Max(0, monH - MinSizePx));
        int right = Math.Clamp(r.Right, left + MinSizePx, monW);
        int bottom = Math.Clamp(r.Bottom, top + MinSizePx, monH);
        int w = (right - left) & ~1;
        int h = (bottom - top) & ~1;
        var next = RectPhysical.FromSize(left, top, Math.Max(MinSizePx, w), Math.Max(MinSizePx, h));

        _selectionPx = next;
        _element.Selection = next;
        _element.InvalidateVisual();
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            MoveWindowToSelection(hwnd);
        }
        RegionChanged?.Invoke(next);
    }

    /// <summary>Close, from RecordingSession (UI thread).</summary>
    public void CloseOutline() => Close();

    /// <summary>Draws the gray dotted region-boundary line plus the near-invisible fills that make
    /// the band - and, in Setup, the inner - hit-testable on this layered window.</summary>
    private sealed class OutlineElement : FrameworkElement
    {
        // "1-2px" dotted line, in physical pixels. A darker shadow dot is drawn 1 physical px
        // further outward underneath the main gray dot so the line stays legible against both light
        // and dark backgrounds: the light-gray main dot pops on dark backgrounds, the dark shadow
        // pops on light ones, and together something is always visible regardless of what is being
        // recorded. Both are read as ONE line, not a two-tone rope, because the shadow is a faint
        // low-alpha halo rather than an equal-weight alternating color.
        private const double DotThicknessPhysicalPx = 1.5;
        private const double DotShadowOffsetPhysicalPx = 1.0;

        private RectPhysical _selection;
        private bool _showInnerTint;
        private double _pensScale = -1;
        private Pen? _dotShadowPen;
        private Pen? _dotPen;

        // Faint fills (alpha kept low so they read as a subtle highlight, but > 0 so the layered
        // window hit-tests these pixels - see the class doc).
        // Alpha 0x01: the fill must stay hit-testable (the layered window's per-pixel-alpha hit
        // test needs >= 1) but visually imperceptible - the visible boundary marker is the dotted
        // line below, not this fill (see class doc).
        private static readonly Brush s_bandFill = Frozen(Color.FromArgb(0x01, 0xFF, 0xFF, 0xFF));
        private static readonly Brush s_innerTint = Frozen(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
        // Near-zero alpha (~0.4%) inner fill used while capturing/reviewing: imperceptible in the
        // recording, but still > 0 so the layered window hit-tests the inner and Shift/Ctrl can
        // grab it to move the region while recording (see class doc).
        private static readonly Brush s_innerGhostFill = Frozen(Color.FromArgb(0x01, 0xFF, 0xFF, 0xFF));

        private bool _innerHitTestable;

        public RectPhysical Selection { get => _selection; set => _selection = value; }
        public bool ShowInnerTint { get => _showInnerTint; set => _showInnerTint = value; }
        /// <summary>Paint the inner at all (Shift/Ctrl held or a drag live). Off = alpha 0 = the OS
        /// routes inner clicks straight through to the app being recorded (see class doc).</summary>
        public bool InnerHitTestable { get => _innerHitTestable; set => _innerHitTestable = value; }

        protected override void OnRender(DrawingContext dc)
        {
            double scaleX = 1.0, scaleY = 1.0;
            if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
            {
                scaleX = target.TransformToDevice.M11;
                scaleY = target.TransformToDevice.M22;
            }
            double scale = Math.Max(scaleX, scaleY);
            double dotThickness = DotThicknessPhysicalPx / scale;
            if (Math.Abs(scale - _pensScale) > 0.001 || _dotShadowPen is null || _dotPen is null)
            {
                _pensScale = scale;
                // #888C90 - the same neutral gray used by the blur-region affordances elsewhere in
                // the app, kept as a single tone so the line reads as ONE dotted outline.
                _dotShadowPen = CreateDottedPen(Color.FromArgb(0xA0, 0x00, 0x00, 0x00), dotThickness);
                _dotPen = CreateDottedPen(Color.FromArgb(0xFF, 0x88, 0x8C, 0x90), dotThickness);
            }

            double band = BandPx / scaleX;
            double innerX = band, innerY = BandPx / scaleY;
            double innerW = _selection.Width / scaleX, innerH = _selection.Height / scaleY;
            double outerW = (_selection.Width + BandPx * 2) / scaleX, outerH = (_selection.Height + BandPx * 2) / scaleY;

            // Band ring fill (outer rect minus inner rect), always present so the band hit-tests.
            var full = new RectangleGeometry(new Rect(0, 0, outerW, outerH));
            var hole = new RectangleGeometry(new Rect(innerX, innerY, innerW, innerH));
            dc.DrawGeometry(s_bandFill, null, new CombinedGeometry(GeometryCombineMode.Exclude, full, hole));

            // Inner fill only while Shift/Ctrl is held (or a drag is live): painted, it hit-tests so
            // the modifier-move works and doubles as "move mode" feedback (setup tint) or stays
            // imperceptible (capturing ghost fill); unpainted, its alpha-0 pixels are OS-level
            // click-through so the recorded app stays fully interactive - see the class doc.
            if (_innerHitTestable)
            {
                dc.DrawRectangle(_showInnerTint ? s_innerTint : s_innerGhostFill, null, new Rect(innerX, innerY, innerW, innerH));
            }

            // Region boundary line: a gray dotted stroke that hugs the recorded rect EXACTLY from the
            // OUTSIDE. WPF centers a stroke on its path, so the path is pushed outward by half the
            // pen thickness first - the stroke then runs from (boundary - thickness) to (boundary),
            // i.e. it touches the true edge and extends only outward, at inset 0, never crossing into
            // recorded pixels. The shadow dot is drawn first, on a path pushed out by one more
            // physical pixel, so it forms a faint offset halo just behind the main line rather than
            // sitting under it (see field doc for why both tones are needed).
            double half = dotThickness / 2;
            var lineRect = new Rect(innerX - half, innerY - half, innerW + dotThickness, innerH + dotThickness);
            double shadowInset = half + (DotShadowOffsetPhysicalPx / scale);
            var shadowRect = new Rect(
                innerX - shadowInset, innerY - shadowInset,
                innerW + shadowInset * 2, innerH + shadowInset * 2);
            dc.DrawRectangle(null, _dotShadowPen, shadowRect);
            dc.DrawRectangle(null, _dotPen, lineRect);
        }

        private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        // Round caps + a zero-length "dash" draw a filled circle at each dash start, i.e. a row of
        // round dots rather than a solid or dashed line - the standard WPF dotted-line recipe.
        private static Pen CreateDottedPen(Color color, double thickness)
        {
            var pen = new Pen(new SolidColorBrush(color), thickness)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                DashCap = PenLineCap.Round,
                DashStyle = new DashStyle(new double[] { 0, 2.2 }, 0),
            };
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
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
    }
}
