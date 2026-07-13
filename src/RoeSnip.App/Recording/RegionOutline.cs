using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using RoeSnip.Core.Capture;

namespace RoeSnip.App.Recording;

/// <summary>The "this is being recorded" frame AND the handle for resizing/moving the recorded
/// region (item 21) - ported from the WPF reference's src/RoeSnip/Recording/RegionOutline.cs (that
/// file's own doc comment is the full design rationale for the band/inner click-through contract;
/// read it before changing behavior here):
///   - Setup (allowResize=true): drag an EDGE or CORNER of the band to RESIZE; hold Shift or Ctrl and
///     drag anywhere inside to MOVE. A plain click inside does nothing (passes through to the app
///     being recorded).
///   - Capturing/Reviewing (allowResize=false, size locked to the encoder): dragging any edge/corner
///     MOVES the region, no modifier needed.
///
/// MECHANISM DEVIATION from the WPF reference (documented, not a silent reinterpretation): WPF
/// achieves cross-process click-through via a genuinely alpha-0 WS_EX_LAYERED pixel plus a subclassed
/// WM_NCHITTEST returning HTTRANSPARENT for the band's own zone classification - a Win32 window-
/// message-pump technique this port's OverlayWindow never needed (it owns the whole screen, so it
/// never has to let a click fall through to another process) and Avalonia's own Win32 backend gives
/// no supported hook to safely replicate (subclassing Avalonia's own window procedure risks fighting
/// its internal message handling). Instead, this class toggles the WS_EX_TRANSPARENT extended style
/// bit on/off via a polling timer (~16ms, the same cadence class the WPF reference's own 20ms
/// modifier-poll already established as safe under encoder load): WS_EX_TRANSPARENT ON makes the
/// WHOLE window invisible to the OS's hit-testing (a real, well-established Win32 mechanism - clicks
/// fall through to whatever is behind, including a different process) so it is set whenever the
/// cursor is over the click-through inner (no modifier held, not mid-drag), and cleared whenever the
/// cursor is over the band (or the modifier-held inner, or an active drag) so THIS window receives
/// ordinary Avalonia pointer input there. The band's own resize/move zone classification and drag
/// math are ported verbatim from the WPF reference's ZoneAt/ApplyDrag; button-down/up is read via
/// GetAsyncKeyState(VK_LBUTTON) on the same poll rather than a WM_LBUTTONDOWN/UP subclass hook, for
/// the same "no window-procedure subclassing" reason above.
///
/// Windows-only: X11/Wayland/macOS have no WS_EX_TRANSPARENT equivalent portable through Avalonia,
/// and Wayland forbids a client positioning its own window at all (same accepted-limitation class as
/// FlashDimmer's own window parking). RecordingOrchestrator only constructs this class when
/// OperatingSystem.IsWindows() is true; on every other OS a recording proceeds with NO region
/// boundary marker and no drag-to-move/resize UI - the take itself (RegionRecorder/RecordingChrome)
/// is unaffected, this is purely the missing visual/interactive outline (docs/PARITY.md item 21's own
/// note).</summary>
public sealed class RegionOutline : Window
{
    private const int BandPx = 8;
    private const int MinSizePx = 32;

    private enum DragKind { None, Move, Left, Right, Top, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }

    private readonly MonitorInfo _monitor;
    private RectPhysical _selectionPx; // monitor-relative physical pixels - same convention as RecordingSession's own field
    private readonly OutlineElement _element;

    private bool _allowResize = true;
    private DragKind _drag = DragKind.None;
    private int _dragStartCursorX, _dragStartCursorY;
    private RectPhysical _dragStartSel;
    private bool _wasTransparent = true; // last WS_EX_TRANSPARENT state actually applied - avoids a redundant SetWindowLongPtr every tick
    private bool _wasLeftDown;
    private DispatcherTimer? _poll;

    /// <summary>Raised on the UI thread whenever a drag changes the region (move or resize), with the
    /// new MONITOR-RELATIVE selection (RecordingSession's own convention - see its field doc).</summary>
    public event Action<RectPhysical>? RegionChanged;

    public RegionOutline(MonitorInfo monitor, RectPhysical selectionPx)
    {
        _monitor = monitor;
        _selectionPx = selectionPx;

        WindowDecorations = WindowDecorations.None;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        Focusable = false;
        Cursor = new Cursor(StandardCursorType.Arrow);

        _element = new OutlineElement { Selection = selectionPx, ShowInnerTint = true };
        Content = _element;

        Opened += OnOpened;
        Closed += (_, _) => { _poll?.Stop(); _poll = null; };
    }

    /// <summary>Setup vs capturing. In Setup the band resizes and a Shift/Ctrl-held inner shows the
    /// visible move tint; once capturing (size locked) the band moves and a held inner paints only
    /// the imperceptible ghost fill.</summary>
    public void SetInteractionMode(bool allowResize)
    {
        _allowResize = allowResize;
        _element.ShowInnerTint = allowResize;
        _element.InvalidateVisual();
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        MoveWindowToSelection();

        if (!OperatingSystem.IsWindows())
        {
            return; // no click-through mechanism to poll for - see class doc comment
        }

        ApplyTransparent(true); // starts fully click-through; the poll below corrects it the instant the cursor is over the band
        _poll = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(16) };
        _poll.Tick += OnPollTick;
        _poll.Start();
    }

    // Named method-group handler (not an inline lambda) so the platform-compat analyzer can see this
    // is only ever wired up inside OnOpened's own OperatingSystem.IsWindows() guard above - matching
    // item 15's ElevationManager precedent (an inline lambda closure hides the guard from CA1416's
    // flow analysis).
    [SupportedOSPlatform("windows")]
    private void OnPollTick(object? sender, EventArgs e) => PollWindows();

    private void MoveWindowToSelection()
    {
        double scale = _monitor.DpiX / 96.0;
        int outerW = _selectionPx.Width + BandPx * 2;
        int outerH = _selectionPx.Height + BandPx * 2;
        var b = _monitor.BoundsPx;
        Position = new PixelPoint(b.Left + _selectionPx.Left - BandPx, b.Top + _selectionPx.Top - BandPx);
        Width = outerW / scale;
        Height = outerH / scale;
    }

    // ---------- Windows-only poll: zone classification, WS_EX_TRANSPARENT toggle, drag state machine ----------

    [SupportedOSPlatform("windows")]
    private void PollWindows()
    {
        if (!Native.GetCursorPos(out var cur))
        {
            return;
        }
        bool leftDown = (Native.GetAsyncKeyState(Native.VK_LBUTTON) & 0x8000) != 0;
        bool modifier = (Native.GetAsyncKeyState(Native.VK_SHIFT) & 0x8000) != 0
            || (Native.GetAsyncKeyState(Native.VK_CONTROL) & 0x8000) != 0;

        if (_drag != DragKind.None)
        {
            if (!leftDown)
            {
                _drag = DragKind.None; // button released mid-poll - end the drag
            }
            else
            {
                var next = ApplyDrag(_drag, _dragStartSel, cur.X - _dragStartCursorX, cur.Y - _dragStartCursorY);
                if (next != _selectionPx)
                {
                    _selectionPx = next;
                    _element.Selection = next;
                    _element.InvalidateVisual();
                    MoveWindowToSelection();
                    RegionChanged?.Invoke(next);
                }
            }
            _wasLeftDown = leftDown;
            return; // stay non-transparent for the whole drag regardless of zone
        }

        var kind = ZoneAt(cur.X, cur.Y, modifier);
        bool wantTransparent = kind == DragKind.None;
        if (wantTransparent != _wasTransparent)
        {
            ApplyTransparent(wantTransparent);
        }
        _element.Cursor = new Cursor(CursorTypeFor(kind));

        if (!wantTransparent && leftDown && !_wasLeftDown)
        {
            // Fresh button-down over an interactive zone - begin a drag.
            _drag = kind;
            _dragStartCursorX = cur.X;
            _dragStartCursorY = cur.Y;
            _dragStartSel = _selectionPx;
        }
        _wasLeftDown = leftDown;
    }

    [SupportedOSPlatform("windows")]
    private void ApplyTransparent(bool transparent)
    {
        var handle = TryGetPlatformHandle();
        if (handle is null || handle.Handle == IntPtr.Zero)
        {
            return;
        }
        IntPtr hwnd = handle.Handle;
        long exStyle = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
        exStyle |= Native.WS_EX_TOOLWINDOW;
        exStyle = transparent ? (exStyle | Native.WS_EX_TRANSPARENT) : (exStyle & ~Native.WS_EX_TRANSPARENT);
        Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(exStyle));
        _wasTransparent = transparent;
    }

    /// <summary>Classifies a screen point (physical pixels, virtual-desktop-absolute - the same
    /// convention GetCursorPos itself uses) into a drag zone given the current mode. Ported verbatim
    /// from the WPF reference's ZoneAt, minus the modifier read (passed in here since this port reads
    /// it once per poll tick rather than re-querying per call).</summary>
    private DragKind ZoneAt(int screenX, int screenY, bool modifierHeld)
    {
        var b = _monitor.BoundsPx;
        int selLeft = b.Left + _selectionPx.Left, selTop = b.Top + _selectionPx.Top;
        int rx = screenX - (selLeft - BandPx);
        int ry = screenY - (selTop - BandPx);
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
                return DragKind.Move;
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

        return modifierHeld ? DragKind.Move : DragKind.None;
    }

    private static StandardCursorType CursorTypeFor(DragKind kind) => kind switch
    {
        DragKind.Left or DragKind.Right => StandardCursorType.SizeWestEast,
        DragKind.Top or DragKind.Bottom => StandardCursorType.SizeNorthSouth,
        // Avalonia's StandardCursorType has no generic diagonal-resize glyph (same gap item 07's own
        // doc comment already notes for the selection endpoint cursor) - reuse the nearest corner
        // cursor instead of a dedicated SizeNWSE/SizeNESW.
        DragKind.TopLeft or DragKind.BottomRight => StandardCursorType.TopLeftCorner,
        DragKind.TopRight or DragKind.BottomLeft => StandardCursorType.TopRightCorner,
        DragKind.Move => StandardCursorType.SizeAll,
        _ => StandardCursorType.Arrow,
    };

    /// <summary>Computes the new selection for a drag of <paramref name="kind"/> by (dx,dy) from the
    /// selection captured at drag start. Moves translate; resizes move the grabbed edge(s) while the
    /// opposite edge stays put. Clamped to THIS monitor's own bounds (single-monitor recording only in
    /// this port - see RecordingController.cs's own doc comment; the WPF reference clamps to the union
    /// of every monitor, which has no meaning here), kept at/above the minimum, and forced to even
    /// width/height (the H.264/GIF encoders need even dimensions).</summary>
    private RectPhysical ApplyDrag(DragKind kind, RectPhysical s, int dx, int dy)
    {
        var mb = _monitor.BoundsPx;
        var u = new RectPhysical(0, 0, mb.Width, mb.Height); // monitor-relative bounds, matching _selectionPx's own convention

        if (kind == DragKind.Move)
        {
            int nl = Math.Clamp(s.Left + dx, u.Left, Math.Max(u.Left, u.Right - s.Width));
            int nt = Math.Clamp(s.Top + dy, u.Top, Math.Max(u.Top, u.Bottom - s.Height));
            return RectPhysical.FromSize(nl, nt, s.Width, s.Height);
        }

        int left = s.Left, top = s.Top, right = s.Right, bottom = s.Bottom;

        if (kind is DragKind.Left or DragKind.TopLeft or DragKind.BottomLeft)
            left = Math.Clamp(s.Left + dx, u.Left, right - MinSizePx);
        if (kind is DragKind.Right or DragKind.TopRight or DragKind.BottomRight)
            right = Math.Clamp(s.Right + dx, left + MinSizePx, u.Right);
        if (kind is DragKind.Top or DragKind.TopLeft or DragKind.TopRight)
            top = Math.Clamp(s.Top + dy, u.Top, bottom - MinSizePx);
        if (kind is DragKind.Bottom or DragKind.BottomLeft or DragKind.BottomRight)
            bottom = Math.Clamp(s.Bottom + dy, top + MinSizePx, u.Bottom);

        int w = (right - left) & ~1;
        int h = (bottom - top) & ~1;
        if (kind is DragKind.Left or DragKind.TopLeft or DragKind.BottomLeft) left = right - w;
        if (kind is DragKind.Top or DragKind.TopLeft or DragKind.TopRight) top = bottom - h;
        return RectPhysical.FromSize(left, top, Math.Max(MinSizePx, w), Math.Max(MinSizePx, h));
    }

    /// <summary>Automation hook (item 21f, AppShell/AutomationServer.cs `select` command while a
    /// recording is active): applies a new MONITOR-RELATIVE selection exactly like a finished band
    /// drag would, without needing a live mouse-move stream. While size-locked
    /// (<see cref="_allowResize"/> false) this is a MOVE-ONLY call exactly like a real band drag would
    /// be in that state (<see cref="ZoneAt"/> only ever returns <see cref="DragKind.Move"/> then) -
    /// the incoming rect's width/height are ignored and the CURRENT selection's own width/height are
    /// kept.</summary>
    public void SetSelectionForAutomation(RectPhysical monitorRelativeRect)
    {
        var r = monitorRelativeRect.Normalized();
        var mb = _monitor.BoundsPx;
        var u = new RectPhysical(0, 0, mb.Width, mb.Height);
        int w = _allowResize ? Math.Max(MinSizePx, r.Width & ~1) : _selectionPx.Width;
        int h = _allowResize ? Math.Max(MinSizePx, r.Height & ~1) : _selectionPx.Height;
        int left = Math.Clamp(r.Left, u.Left, Math.Max(u.Left, u.Right - w));
        int top = Math.Clamp(r.Top, u.Top, Math.Max(u.Top, u.Bottom - h));
        var next = RectPhysical.FromSize(left, top, w, h);

        _selectionPx = next;
        _element.Selection = next;
        _element.InvalidateVisual();
        MoveWindowToSelection();
        RegionChanged?.Invoke(next);
    }

    public void CloseOutline()
    {
        try { Close(); }
        catch (InvalidOperationException) { /* already closing */ }
    }

    /// <summary>Draws the gray dotted region-boundary line plus the near-invisible fills that make
    /// the band visible - the fills here are purely cosmetic in this port (they carried the
    /// hit-testing contract in the WPF reference's own per-pixel-alpha mechanism; this port's
    /// click-through instead comes from the WS_EX_TRANSPARENT poll above), kept anyway so the panel
    /// reads identically to the WPF reference's own screenshot-verified look.</summary>
    private sealed class OutlineElement : Control
    {
        private const double DotThicknessDip = 1.5;
        private const double DotShadowOffsetDip = 1.0;

        private static readonly IBrush s_bandFill = new SolidColorBrush(Color.FromArgb(0x08, 0xFF, 0xFF, 0xFF));
        private static readonly IBrush s_innerTint = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
        private static readonly Pen s_dotShadowPen = new(new SolidColorBrush(Color.FromArgb(0xA0, 0x00, 0x00, 0x00)), DotThicknessDip)
        {
            LineCap = PenLineCap.Round,
            DashStyle = new DashStyle(new double[] { 0, 2.2 }, 0),
        };
        private static readonly Pen s_dotPen = new(new SolidColorBrush(Color.FromArgb(0xFF, 0x88, 0x8C, 0x90)), DotThicknessDip)
        {
            LineCap = PenLineCap.Round,
            DashStyle = new DashStyle(new double[] { 0, 2.2 }, 0),
        };

        public RectPhysical Selection { get; set; }
        public bool ShowInnerTint { get; set; }

        public override void Render(DrawingContext dc)
        {
            double scale = Math.Max(1.0, TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0);
            double band = BandPx / scale;
            double innerW = Selection.Width / scale, innerH = Selection.Height / scale;
            double outerW = (Selection.Width + BandPx * 2) / scale, outerH = (Selection.Height + BandPx * 2) / scale;

            var full = new RectangleGeometry(new Rect(0, 0, outerW, outerH));
            var hole = new RectangleGeometry(new Rect(band, band, innerW, innerH));
            dc.DrawGeometry(s_bandFill, null, new CombinedGeometry(GeometryCombineMode.Exclude, full, hole));
            if (ShowInnerTint)
            {
                dc.DrawRectangle(s_innerTint, null, new Rect(band, band, innerW, innerH));
            }

            double half = DotThicknessDip / 2;
            var lineRect = new Rect(band - half, band - half, innerW + DotThicknessDip, innerH + DotThicknessDip);
            double shadowInset = half + DotShadowOffsetDip;
            var shadowRect = new Rect(
                band - shadowInset, band - shadowInset, innerW + shadowInset * 2, innerH + shadowInset * 2);
            dc.DrawRectangle(null, s_dotShadowPen, shadowRect);
            dc.DrawRectangle(null, s_dotPen, lineRect);
        }
    }

    [SupportedOSPlatform("windows")]
    private static class Native
    {
        public const int GWL_EXSTYLE = -20;
        public const long WS_EX_TOOLWINDOW = 0x00000080;
        public const long WS_EX_TRANSPARENT = 0x00000020;
        public const int VK_LBUTTON = 0x01, VK_SHIFT = 0x10, VK_CONTROL = 0x11;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")] public static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", EntryPoint = "GetWindowLong")] public static extern int GetWindowLong32(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")] public static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
        [DllImport("user32.dll", EntryPoint = "SetWindowLong")] public static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        public static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex) =>
            IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));

        public static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong) =>
            IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong) : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));
    }
}
