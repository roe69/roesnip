using System;
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

/// <summary>The visible "this is being recorded" frame: a thin, click-through, topmost window
/// whose dashed two-tone border (the SelectionAdorner marching-ants recipe) hugs the recorded
/// region from the OUTSIDE. Three invariants make it safe (review-hardened - the first draft got
/// two of these wrong):
/// (1) WDA_EXCLUDEFROMCAPTURE applies to the window's GEOMETRY, not its painted pixels - an
///     excluded rect overlapping the recorded region composites as a black hole in the recording
///     (the hard rule RecordingChrome.PositionNearSelection documents). So this window's shape is
///     cut down to the frame band itself via SetWindowRgn (outer rect minus the recorded rect):
///     the HWND's effective region never overlaps a recorded pixel.
/// (2) SelectionPx is monitor-relative (Program.cs's OverlayResult contract); SetWindowPos wants
///     virtual-desktop coordinates, so the monitor's BoundsPx origin is added, exactly like
///     RecordingChrome.PositionNearSelection.
/// (3) The stroke is drawn in physical pixels (thickness divided by the DPI scale like the
///     offsets), so high display scaling can never widen it across the region boundary.
/// WS_EX_TRANSPARENT keeps it invisible to hit testing. Created and closed by RecordingSession on
/// the UI thread, for both MP4 and GIF recordings.</summary>
internal sealed class RegionOutline : Window
{
    /// <summary>Physical pixels between the recorded rect and the window's outer edge; the stroke
    /// is centered in this band and the window region excludes everything inside it.</summary>
    private const int BandPx = 4;

    private readonly MonitorInfo _monitor;
    private readonly RectPhysical _selectionPx;

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
        IsHitTestVisible = false;
        Focusable = false;

        Content = new OutlineElement(selectionPx);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;

        // Click-through + no Alt+Tab entry + never activated: this window is pure indication.
        long exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(
            exStyle | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_NOACTIVATE));

        // Cut the window's shape down to the frame band BEFORE excluding it from capture: the
        // exclusion black-holes wherever the window's REGION overlaps the recording, so the region
        // must never include the recorded rect. SetWindowRgn takes ownership of the HRGN on
        // success; on failure the recording still works, we just skip the exclusion below (a
        // visible-in-recording outline beats a fully black recording).
        int outerW = _selectionPx.Width + BandPx * 2;
        int outerH = _selectionPx.Height + BandPx * 2;
        IntPtr outer = NativeMethods.CreateRectRgn(0, 0, outerW, outerH);
        IntPtr inner = NativeMethods.CreateRectRgn(BandPx, BandPx, BandPx + _selectionPx.Width, BandPx + _selectionPx.Height);
        NativeMethods.CombineRgn(outer, outer, inner, NativeMethods.RGN_DIFF);
        NativeMethods.DeleteObject(inner);
        bool regionApplied = NativeMethods.SetWindowRgn(hwnd, outer, redraw: false) != 0;
        if (!regionApplied)
        {
            NativeMethods.DeleteObject(outer);
            Console.Error.WriteLine("RoeSnip: SetWindowRgn failed on the recording outline; skipping capture exclusion so the recording is not blacked out.");
        }

        if (regionApplied
            && Environment.GetEnvironmentVariable("ROESNIP_DIAG_NOEXCLUDE") != "1"
            && !NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE))
        {
            Console.Error.WriteLine(
                "RoeSnip: SetWindowDisplayAffinity failed on the recording outline; it may appear in captures.");
        }

        // Virtual-desktop placement: SelectionPx is monitor-relative, SetWindowPos is absolute.
        var bounds = _monitor.BoundsPx;
        NativeMethods.SetWindowPos(
            hwnd, NativeMethods.HWND_TOPMOST,
            bounds.Left + _selectionPx.Left - BandPx, bounds.Top + _selectionPx.Top - BandPx,
            outerW, outerH,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }

    /// <summary>Close, from RecordingSession.Stop (UI thread). No close-guard needed: with no
    /// taskbar entry, no Alt+Tab presence, and hit-test transparency there is no external path to
    /// a stray close.</summary>
    public void CloseOutline() => Close();

    /// <summary>Draws the dashed frame: SelectionAdorner's two-tone recipe (dark under-stroke with
    /// light 3-3 dashes riding on it) so the recording border reads as the same visual language as
    /// the selection border it replaces. Pens are built per DPI scale so the stroke's PHYSICAL
    /// width stays fixed and can never widen across the region boundary at high display scaling.</summary>
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
            IsHitTestVisible = false;
        }

        protected override void OnRender(DrawingContext dc)
        {
            // Device scale so both the stroke position AND its width land in physical pixels.
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

            // Stroke centered in the outer band: BandPx/2 physical pixels in from the window edge,
            // which keeps the full stroke width outside the recorded rect.
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
}
