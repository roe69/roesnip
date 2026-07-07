using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RoeSnip.Overlay;

// RoeSnip.csproj enables both UseWPF and UseWindowsForms, so System.Windows.Forms/System.Drawing
// are in scope alongside System.Windows/System.Windows.Media — alias the colliding names to WPF's,
// matching the convention used throughout Overlay/* (see AnnotationLayer.cs). Cursor is a rarer
// collision (System.Windows.Forms.Cursor exists too) but the same rule applies.
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;
using Cursor = System.Windows.Input.Cursor;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;
using FontFamily = System.Windows.Media.FontFamily;
using FlowDir = System.Windows.FlowDirection;

/// <summary>Identifies one cached cursor bitmap: which tool is selected, what color it's drawn in,
/// and (for the stroke-width-sensitive tools) the current stroke width — the plain Select tool
/// never reaches <see cref="ToolCursorCache"/> at all (OverlayWindow keeps the system crosshair for
/// it), and the Text tool ignores stroke width entirely, so its key always normalizes width to 0 to
/// avoid needlessly growing the cache every time a scroll-wheel changes a width that tool doesn't
/// even render. Public (rather than internal) purely so the normalization rule itself is
/// unit-testable without an InternalsVisibleTo edit, matching the rest of Overlay/*'s pure-helper
/// convention (see BoundedColorList).</summary>
public readonly record struct CursorKey(AnnotationTool Tool, Color Color, double StrokeWidthPx)
{
    /// <summary>Builds a normalized key: stroke width is zeroed for AnnotationTool.None/Text (it
    /// plays no part in either glyph) and rounded to the nearest whole pixel for the five stroke
    /// tools (Rectangle/Ellipse/Arrow/Line/Freehand), so repeated scroll-wheel deltas that land on
    /// the same integer width reuse one cached cursor instead of spawning a new one per float.</summary>
    public static CursorKey For(AnnotationTool tool, Color color, double strokeWidthPx)
    {
        bool widthMatters = tool is AnnotationTool.Rectangle or AnnotationTool.Ellipse
            or AnnotationTool.Arrow or AnnotationTool.Line or AnnotationTool.Freehand;
        double normalizedWidth = widthMatters ? Math.Round(strokeWidthPx, MidpointRounding.AwayFromZero) : 0.0;
        return new CursorKey(tool, color, normalizedWidth);
    }
}

file static class CursorInterop
{
    [StructLayout(LayoutKind.Sequential)]
    public struct ICONINFO
    {
        public bool fIcon;
        public uint xHotspot;
        public uint yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors; // unused for 32bpp BI_RGB (no color table) — present only for layout parity
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CreateIconIndirect(ref ICONINFO icon);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr hSection, uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, byte[]? lpvBits);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(IntPtr hObject);

    public const uint DIB_RGB_COLORS = 0;
    public const uint BI_RGB = 0;
}

/// <summary>Builds and caches a small custom Windows cursor per (tool, color, strokeWidth)
/// combination — item 1 of UX round 3: the crosshair changes to visualize what the selected drawing
/// tool will actually draw (a rect/ellipse/arrow/line/pencil glyph, or an I-beam-with-T for text),
/// tinted with the current draw color, with a faint ring around stroke tools' glyph that grows/
/// shrinks with the current stroke width so a scroll-wheel resize is visible in the cursor itself
/// without needing to look at the toolbar. AnnotationTool.None (the Select tool) is never looked up
/// here — OverlayWindow keeps the plain system crosshair for it.
///
/// Built via DrawingVisual -> RenderTargetBitmap (Pbgra32, i.e. already premultiplied — exactly the
/// pixel layout a 32bpp alpha-icon DIB needs) -> a GDI DIB section (CreateDIBSection) copied in as
/// the icon's color bitmap -> CreateIconIndirect with fIcon=false (a cursor, not an icon) and an
/// explicit centered hotspot -> CursorInteropHelper.Create wraps the resulting HICON/HCURSOR as a
/// WPF Cursor. Entries are cached per <see cref="CursorKey"/> (scroll-wheel resizing revisits the
/// same widths constantly) in a small LRU-bounded cache; whatever gets evicted is disposed
/// (DestroyIcon) immediately, and <see cref="Dispose"/> tears down every entry still cached — call
/// it once when the owning OverlayWindow closes.</summary>
public sealed class ToolCursorCache : IDisposable
{
    /// <summary>Owns one HICON/HCURSOR returned by CreateIconIndirect. CreateIconIndirect's own
    /// docs say to destroy the result via DestroyIcon regardless of whether fIcon was true or false
    /// (HICON and HCURSOR are the same underlying handle kind in Win32) — SafeHandle guarantees that
    /// happens exactly once even if the owning cache entry is evicted while some other code still
    /// holds a reference to the wrapping WPF Cursor.</summary>
    private sealed class SafeIconHandle : SafeHandle
    {
        public SafeIconHandle(IntPtr handle) : base(IntPtr.Zero, ownsHandle: true) => SetHandle(handle);
        public override bool IsInvalid => handle == IntPtr.Zero;
        protected override bool ReleaseHandle() => CursorInterop.DestroyIcon(handle);
    }

    private const int MaxCachedCursors = 48;
    private const int CanvasSize = 32;

    private readonly Dictionary<CursorKey, (Cursor Cursor, SafeIconHandle Handle)> _cache = new();
    private readonly List<CursorKey> _lruOrder = new(); // oldest first

    public Cursor GetOrCreate(AnnotationTool tool, Color color, double strokeWidthPx)
    {
        var key = CursorKey.For(tool, color, strokeWidthPx);
        if (_cache.TryGetValue(key, out var existing))
        {
            Touch(key);
            return existing.Cursor;
        }

        var (cursor, handle) = Build(tool, color, key.StrokeWidthPx);
        _cache[key] = (cursor, handle);
        _lruOrder.Add(key);
        EvictIfNeeded();
        return cursor;
    }

    private void Touch(CursorKey key)
    {
        _lruOrder.Remove(key);
        _lruOrder.Add(key);
    }

    private void EvictIfNeeded()
    {
        while (_lruOrder.Count > MaxCachedCursors)
        {
            var oldest = _lruOrder[0];
            _lruOrder.RemoveAt(0);
            if (_cache.Remove(oldest, out var evicted))
            {
                evicted.Handle.Dispose(); // DestroyIcon via SafeIconHandle.ReleaseHandle
            }
        }
    }

    public void Dispose()
    {
        foreach (var entry in _cache.Values)
        {
            entry.Handle.Dispose();
        }
        _cache.Clear();
        _lruOrder.Clear();
    }

    private static (Cursor Cursor, SafeIconHandle Handle) Build(AnnotationTool tool, Color color, double normalizedStrokeWidthPx)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            DrawGlyph(dc, tool, color, normalizedStrokeWidthPx);
        }

        var rtb = new RenderTargetBitmap(CanvasSize, CanvasSize, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        int stride = CanvasSize * 4;
        var pixels = new byte[stride * CanvasSize];
        rtb.CopyPixels(pixels, stride, 0);

        IntPtr hIcon = CreateCursorIcon(pixels, CanvasSize, CanvasSize, CanvasSize / 2, CanvasSize / 2);
        var handle = new SafeIconHandle(hIcon);
        Cursor cursor = CursorInteropHelper.Create(handle);
        return (cursor, handle);
    }

    private static IntPtr CreateCursorIcon(byte[] premultipliedBgraPixels, int width, int height, int hotspotX, int hotspotY)
    {
        var bmi = new CursorInterop.BITMAPINFO
        {
            bmiHeader = new CursorInterop.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<CursorInterop.BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // negative => top-down DIB, matching RenderTargetBitmap.CopyPixels' row order
                biPlanes = 1,
                biBitCount = 32,
                biCompression = CursorInterop.BI_RGB,
            },
        };

        IntPtr hbmColor = CursorInterop.CreateDIBSection(IntPtr.Zero, ref bmi, CursorInterop.DIB_RGB_COLORS, out IntPtr bits, IntPtr.Zero, 0);
        if (hbmColor == IntPtr.Zero || bits == IntPtr.Zero)
        {
            throw new InvalidOperationException("CreateDIBSection failed while building a tool cursor.");
        }

        try
        {
            Marshal.Copy(premultipliedBgraPixels, 0, bits, premultipliedBgraPixels.Length);

            // The AND mask is irrelevant for a 32bpp color bitmap with a real alpha channel (Windows
            // XP+ composites using the alpha channel instead) but CreateIconIndirect still requires
            // one of matching dimensions — an all-zero (fully "unmasked") 1bpp mask is the documented
            // convention for alpha-icons/cursors, so build it explicitly rather than pass NULL bits
            // (whose contents CreateBitmap leaves undefined).
            int maskStride = ((width + 15) / 16) * 2;
            var maskBits = new byte[maskStride * height];
            IntPtr hbmMask = CursorInterop.CreateBitmap(width, height, 1, 1, maskBits);
            try
            {
                var iconInfo = new CursorInterop.ICONINFO
                {
                    fIcon = false, // a cursor, not an icon
                    xHotspot = (uint)hotspotX,
                    yHotspot = (uint)hotspotY,
                    hbmMask = hbmMask,
                    hbmColor = hbmColor,
                };

                IntPtr hIcon = CursorInterop.CreateIconIndirect(ref iconInfo);
                if (hIcon == IntPtr.Zero)
                {
                    throw new InvalidOperationException("CreateIconIndirect failed while building a tool cursor.");
                }
                return hIcon;
            }
            finally
            {
                CursorInterop.DeleteObject(hbmMask);
            }
        }
        finally
        {
            CursorInterop.DeleteObject(hbmColor);
        }
    }

    // ---------- Glyph rendering ----------

    private static void DrawGlyph(DrawingContext dc, AnnotationTool tool, Color color, double strokeWidthPx)
    {
        var center = new Point(CanvasSize / 2.0, CanvasSize / 2.0);

        bool isStrokeTool = tool is AnnotationTool.Rectangle or AnnotationTool.Ellipse
            or AnnotationTool.Arrow or AnnotationTool.Line or AnnotationTool.Freehand;
        if (isStrokeTool)
        {
            double ringDiameter = Math.Clamp(strokeWidthPx * 1.4 + 6.0, 10.0, 27.0);
            var ringPen = new Pen(new SolidColorBrush(Color.FromArgb(130, color.R, color.G, color.B)), 1.3);
            dc.DrawEllipse(null, ringPen, center, ringDiameter / 2, ringDiameter / 2);
        }

        // A dark halo drawn first (then the actual color on top) keeps the glyph legible over both
        // bright and dark screen content — a plain colored line can vanish over similarly-colored
        // background pixels otherwise.
        var haloPen = new Pen(new SolidColorBrush(Color.FromArgb(215, 0, 0, 0)), 3.2)
        {
            StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round,
        };
        var fgPen = new Pen(new SolidColorBrush(color), 1.5)
        {
            StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round,
        };

        switch (tool)
        {
            case AnnotationTool.Rectangle:
            {
                var rect = new Rect(center.X - 6, center.Y - 6, 12, 12);
                dc.DrawRectangle(null, haloPen, rect);
                dc.DrawRectangle(null, fgPen, rect);
                break;
            }
            case AnnotationTool.Ellipse:
                dc.DrawEllipse(null, haloPen, center, 6.5, 6.5);
                dc.DrawEllipse(null, fgPen, center, 6.5, 6.5);
                break;
            case AnnotationTool.Line:
            {
                var a = new Point(center.X - 7, center.Y + 7);
                var b = new Point(center.X + 7, center.Y - 7);
                dc.DrawLine(haloPen, a, b);
                dc.DrawLine(fgPen, a, b);
                break;
            }
            case AnnotationTool.Arrow:
            {
                var a = new Point(center.X - 7, center.Y + 7);
                var b = new Point(center.X + 7, center.Y - 7);
                DrawArrowGlyph(dc, haloPen, fgPen, color, a, b);
                break;
            }
            case AnnotationTool.Freehand:
                DrawPencilGlyph(dc, haloPen, fgPen, color, center);
                break;
            case AnnotationTool.Text:
                DrawTextGlyph(dc, haloPen, fgPen, color, center);
                break;
        }
    }

    private static void DrawArrowGlyph(DrawingContext dc, Pen haloPen, Pen fgPen, Color color, Point from, Point to)
    {
        dc.DrawLine(haloPen, from, to);
        dc.DrawLine(fgPen, from, to);

        var direction = to - from;
        if (direction.LengthSquared < 1e-6)
        {
            return;
        }
        direction.Normalize();

        const double headLength = 6.0;
        const double headAngle = Math.PI / 6.5;

        static Vector Rotate(Vector v, double radians)
        {
            double cos = Math.Cos(radians), sin = Math.Sin(radians);
            return new Vector(v.X * cos - v.Y * sin, v.X * sin + v.Y * cos);
        }

        var back = -direction * headLength;
        var left = to + Rotate(back, headAngle);
        var right = to + Rotate(back, -headAngle);

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(to, true, true);
            ctx.LineTo(left, true, true);
            ctx.LineTo(right, true, true);
        }
        dc.DrawGeometry(new SolidColorBrush(color), new Pen(Brushes.Black, 1.0), geometry);
    }

    private static void DrawPencilGlyph(DrawingContext dc, Pen haloPen, Pen fgPen, Color color, Point center)
    {
        var tail = new Point(center.X + 8, center.Y - 7);
        var tip = new Point(center.X - 7, center.Y + 8);
        dc.DrawLine(haloPen, tail, tip);
        dc.DrawLine(fgPen, tail, tip);

        var dir = tip - tail;
        dir.Normalize();
        var perp = new Vector(-dir.Y, dir.X);
        const double tipLength = 4.5;
        var p2 = tip - dir * tipLength + perp * 2.4;
        var p3 = tip - dir * tipLength - perp * 2.4;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(tip, true, true);
            ctx.LineTo(p2, true, true);
            ctx.LineTo(p3, true, true);
        }
        dc.DrawGeometry(new SolidColorBrush(color), new Pen(Brushes.Black, 1.0), geometry);
    }

    private static void DrawTextGlyph(DrawingContext dc, Pen haloPen, Pen fgPen, Color color, Point center)
    {
        const double halfHeight = 8.0;
        var top = new Point(center.X - 2, center.Y - halfHeight);
        var bottom = new Point(center.X - 2, center.Y + halfHeight);

        void Serif(Point p)
        {
            var a = new Point(p.X - 3, p.Y);
            var b = new Point(p.X + 3, p.Y);
            dc.DrawLine(haloPen, a, b);
            dc.DrawLine(fgPen, a, b);
        }

        dc.DrawLine(haloPen, top, bottom);
        dc.DrawLine(fgPen, top, bottom);
        Serif(top);
        Serif(bottom);

        // A small bold "T" beside the I-beam so the glyph reads unambiguously as "text tool", not a
        // generic text-select caret.
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        var formatted = new FormattedText(
            "T", CultureInfo.InvariantCulture, FlowDir.LeftToRight,
            typeface, 12.0, new SolidColorBrush(color), 1.0);
        dc.DrawText(formatted, new Point(center.X + 5, center.Y - halfHeight - 1));
    }
}
