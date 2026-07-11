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
using Brushes = System.Windows.Media.Brushes;

/// <summary>The "this is being recorded" frame AND the handle for repositioning the recorded region.
/// A thin, topmost window whose dashed two-tone border (the SelectionAdorner marching-ants recipe)
/// hugs the recorded region from the OUTSIDE, and which the user can drag to move the region during
/// any phase (Setup / Capturing / Reviewing).
///
/// Interaction (per user request): the outer BAND is always a move handle - hovering it shows the
/// 4-way move cursor and dragging moves the whole region. INSIDE the recorded rect, a plain click
/// falls THROUGH to whatever app is being recorded (so recorded interactions still work); holding
/// Shift or Ctrl turns an inside click into a move-grab too. Every move raises <see cref="RegionMoved"/>
/// so RecordingSession can re-anchor the chrome and (while capturing) slide the recorder's crop.
///
/// Recording-safety: unlike the old click-through version this window is NOT WDA_EXCLUDEFROMCAPTURE
/// and has no SetWindowRgn hole (it must cover the inner rect to hit-test clicks there). It stays
/// invisible in the recording anyway because it paints nothing over the recorded rect - the dashed
/// stroke lives entirely in the BAND, which is outside the captured region, and the inner area is
/// fully transparent (alpha 0), which contributes nothing to the DWM composition WGC captures.
/// SelectionPx is monitor-relative (Program.cs's OverlayResult contract); SetWindowPos wants
/// virtual-desktop coordinates, so the monitor's BoundsPx origin is added.</summary>
internal sealed class RegionOutline : Window
{
    /// <summary>Physical pixels between the recorded rect and the window's outer edge; the stroke is
    /// centered in this band and it doubles as the always-grabbable move handle.</summary>
    private const int BandPx = 4;

    private readonly MonitorInfo _monitor;
    private RectPhysical _selectionPx; // position mutable (drag); size fixed

    private bool _dragging;
    private Native.POINT _dragStartCursor;
    private RectPhysical _dragStartSel;

    /// <summary>Raised on the UI thread each time a drag moves the region, with the new
    /// monitor-relative selection. RecordingSession re-anchors the chrome and updates the live crop.</summary>
    public event Action<RectPhysical>? RegionMoved;

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

        Content = new OutlineElement(selectionPx);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;

        // No Alt+Tab entry, never activated (dragging must not steal focus from the app being
        // recorded). NOT WS_EX_TRANSPARENT: this window needs the mouse. Per-point pass-through is
        // decided in WM_NCHITTEST instead, so an un-modified click inside the region still reaches
        // the app underneath.
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
        var bounds = _monitor.BoundsPx;
        NativeMethods.SetWindowPos(
            hwnd, NativeMethods.HWND_TOPMOST,
            bounds.Left + _selectionPx.Left - BandPx, bounds.Top + _selectionPx.Top - BandPx,
            outerW, outerH,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case Native.WM_NCHITTEST:
            {
                int sx = (short)(lParam.ToInt64() & 0xFFFF);
                int sy = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
                handled = true;
                return new IntPtr(HitTest(sx, sy));
            }

            case Native.WM_SETCURSOR:
            {
                // LOWORD(lParam) is the hit-test result we returned above. Only OUR movable hits are
                // HTCLIENT here (pass-through inner returned HTTRANSPARENT and never gets this), so a
                // HTCLIENT means "show the move cursor".
                int hit = (int)(lParam.ToInt64() & 0xFFFF);
                if (hit == Native.HTCLIENT)
                {
                    Native.SetCursor(Native.LoadCursor(IntPtr.Zero, Native.IDC_SIZEALL));
                    handled = true;
                    return new IntPtr(1);
                }
                break;
            }

            case Native.WM_LBUTTONDOWN:
            {
                // Only delivered when the hit test above allowed it (band, or inner + Shift/Ctrl).
                Native.GetCursorPos(out _dragStartCursor);
                _dragStartSel = _selectionPx;
                _dragging = true;
                Native.SetCapture(hwnd);
                handled = true;
                break;
            }

            case Native.WM_MOUSEMOVE:
            {
                if (_dragging)
                {
                    Native.GetCursorPos(out var cur);
                    int dx = cur.X - _dragStartCursor.X;
                    int dy = cur.Y - _dragStartCursor.Y;
                    int w = _selectionPx.Width, h = _selectionPx.Height;
                    int maxL = Math.Max(0, _monitor.BoundsPx.Width - w);
                    int maxT = Math.Max(0, _monitor.BoundsPx.Height - h);
                    int newL = Math.Clamp(_dragStartSel.Left + dx, 0, maxL);
                    int newT = Math.Clamp(_dragStartSel.Top + dy, 0, maxT);
                    if (newL != _selectionPx.Left || newT != _selectionPx.Top)
                    {
                        _selectionPx = RectPhysical.FromSize(newL, newT, w, h);
                        MoveWindowToSelection(hwnd);
                        RegionMoved?.Invoke(_selectionPx);
                    }
                    handled = true;
                }
                break;
            }

            case Native.WM_LBUTTONUP:
            {
                if (_dragging)
                {
                    _dragging = false;
                    Native.ReleaseCapture();
                    handled = true;
                }
                break;
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>Returns HTCLIENT for a point that should be a move-grab (the band, or the inner rect
    /// while Shift/Ctrl is held) and HTTRANSPARENT for an inner point with no modifier (so the click
    /// falls through to the app being recorded).</summary>
    private int HitTest(int screenX, int screenY)
    {
        var bounds = _monitor.BoundsPx;
        int originX = bounds.Left + _selectionPx.Left - BandPx;
        int originY = bounds.Top + _selectionPx.Top - BandPx;
        int rx = screenX - originX;
        int ry = screenY - originY;

        bool inInner =
            rx >= BandPx && rx < BandPx + _selectionPx.Width &&
            ry >= BandPx && ry < BandPx + _selectionPx.Height;

        if (inInner)
        {
            bool modifier =
                (Native.GetKeyState(Native.VK_SHIFT) & 0x8000) != 0 ||
                (Native.GetKeyState(Native.VK_CONTROL) & 0x8000) != 0;
            return modifier ? Native.HTCLIENT : Native.HTTRANSPARENT;
        }

        // The band ring (the window covers only region + band, so any non-inner point is the band).
        return Native.HTCLIENT;
    }

    /// <summary>Close, from RecordingSession (UI thread).</summary>
    public void CloseOutline() => Close();

    /// <summary>Draws the dashed frame: SelectionAdorner's two-tone recipe (dark under-stroke with
    /// light 3-3 dashes riding on it), sized in physical pixels so the stroke width stays fixed at
    /// high display scaling and never widens across the region boundary.</summary>
    private sealed class OutlineElement : FrameworkElement
    {
        private const double StrokePhysicalPx = 1.5;

        private readonly RectPhysical _selectionPx;
        private double _pensScale = -1;
        private Pen? _underPen;
        private Pen? _dashPen;

        public OutlineElement(RectPhysical selectionPx)
        {
            _selectionPx = selectionPx;
            IsHitTestVisible = false; // hit-testing is decided in the window's WM_NCHITTEST hook
        }

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

            double insetX = BandPx / 2.0 / scaleX;
            double insetY = BandPx / 2.0 / scaleY;
            double width = (_selectionPx.Width + BandPx * 2) / scaleX;
            double height = (_selectionPx.Height + BandPx * 2) / scaleY;
            var frame = new Rect(insetX, insetY, Math.Max(0, width - insetX * 2), Math.Max(0, height - insetY * 2));

            dc.DrawRectangle(null, _underPen, frame);
            dc.DrawRectangle(null, _dashPen, frame);
        }

        private static Pen CreateFrozenPen(Color color, double thickness, DashStyle? dash)
        {
            var pen = new Pen(new SolidColorBrush(color), thickness)
            {
                StartLineCap = PenLineCap.Flat,
                EndLineCap = PenLineCap.Flat,
            };
            if (dash is not null)
            {
                pen.DashStyle = dash;
                pen.DashCap = PenLineCap.Flat;
            }
            pen.Freeze();
            return pen;
        }
    }

    /// <summary>Win32 bits used only for the drag/hit-test interaction, kept local to this window.</summary>
    private static class Native
    {
        public const int WM_NCHITTEST = 0x0084;
        public const int WM_SETCURSOR = 0x0020;
        public const int WM_LBUTTONDOWN = 0x0201;
        public const int WM_MOUSEMOVE = 0x0200;
        public const int WM_LBUTTONUP = 0x0202;

        public const int HTTRANSPARENT = -1;
        public const int HTCLIENT = 1;

        public const int WS_EX_NOACTIVATE = 0x08000000;

        public const int VK_SHIFT = 0x10;
        public const int VK_CONTROL = 0x11;

        public static readonly IntPtr IDC_SIZEALL = new(32646);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern IntPtr SetCapture(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern short GetKeyState(int vKey);
        [DllImport("user32.dll")] public static extern IntPtr SetCursor(IntPtr hCursor);
        [DllImport("user32.dll")] public static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);
    }
}
