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
using Brush = System.Windows.Media.Brush;
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
    /// plays no part in either glyph) and rounded to the nearest whole pixel for the stroke tools
    /// (Rectangle/Ellipse/Arrow/Line/Freehand, plus Highlight's marker width and Pixelate's block
    /// size), so repeated scroll-wheel deltas that land on the same integer width reuse one cached
    /// cursor instead of spawning a new one per float.</summary>
    public static CursorKey For(AnnotationTool tool, Color color, double strokeWidthPx)
    {
        bool widthMatters = tool is AnnotationTool.Rectangle or AnnotationTool.Ellipse
            or AnnotationTool.Arrow or AnnotationTool.Line or AnnotationTool.Freehand
            or AnnotationTool.Highlight or AnnotationTool.Pixelate;
        double normalizedWidth = widthMatters ? Math.Round(strokeWidthPx, MidpointRounding.AwayFromZero) : 0.0;
        return new CursorKey(tool, color, normalizedWidth);
    }
}

/// <summary>Computes the on-screen circle diameter and bitmap canvas size for a given (already
/// rounded-to-nearest-pixel) stroke width — UX round 4's tool-cursor redesign: every drawing tool's
/// cursor is a plain circle outline whose diameter equals the stroke width, so scrolling the width
/// visibly grows/shrinks the cursor itself, rather than a per-tool shape glyph. The diameter always
/// equals the stroke width 1:1 — never scaled down to fit — except once it would need a canvas bigger
/// than <see cref="MaxCanvasSize"/>: past that point the bitmap is capped at 64x64, the circle is
/// drawn at the largest diameter that still fits, and <see cref="ShowLabel"/> switches on so the
/// actual numeric width is drawn inside it instead of silently lying about the true size. Public
/// (rather than internal/private) purely so this sizing rule is unit-testable without an
/// InternalsVisibleTo edit, matching <see cref="CursorKey"/> and the rest of Overlay/*'s pure-helper
/// convention.</summary>
public readonly record struct CircleSpec(int CanvasSize, double Diameter, bool ShowLabel, double LabelWidthPx)
{
    public const int MaxCanvasSize = 64;

    // The circle is ALWAYS exactly the stroke width — no legibility clamp. At 1-2px the ring is
    // tiny, but the dark halo keeps it findable, and a cursor bigger than the brush lies about
    // what will be drawn ("it should always be the same size as whatever we'll be drawing" —
    // user feedback). Kept as a floor of 1 purely to guard against zero/negative widths.
    public const double MinDiameter = 1.0;

    // Padding around the circle for the halo stroke's outer half-width plus a little antialiasing
    // headroom, so the ring is never clipped at the bitmap edge.
    public const double Margin = 6.0;

    public static CircleSpec For(double strokeWidthPx)
    {
        double desiredDiameter = Math.Max(strokeWidthPx, MinDiameter);
        double neededCanvas = desiredDiameter + Margin * 2;

        if (neededCanvas <= MaxCanvasSize)
        {
            return new CircleSpec((int)Math.Ceiling(neededCanvas), desiredDiameter, ShowLabel: false, strokeWidthPx);
        }

        double cappedDiameter = MaxCanvasSize - Margin * 2;
        return new CircleSpec(MaxCanvasSize, cappedDiameter, ShowLabel: true, strokeWidthPx);
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
/// combination — item 1 of UX round 3, redesigned in UX round 4 after the original per-tool glyphs
/// (a literal drawn rect/ellipse/arrow/line/pencil icon) read as visual artifacts rather than a
/// cursor — e.g. the arrow/line glyph looked like "a red line through the crosshair". The new design
/// is deliberately minimal: every drawing tool (Rectangle/Ellipse/Arrow/Line/Freehand) gets a plain
/// circle outline whose diameter *is* the current stroke width, so scrolling the width visibly
/// grows/shrinks the cursor itself; the Text tool gets a small I-beam-with-T icon. AnnotationTool.None
/// (the Select tool) is never looked up here — OverlayWindow keeps the plain system crosshair for it.
///
/// Built via DrawingVisual -> RenderTargetBitmap (Pbgra32, i.e. already premultiplied — exactly the
/// pixel layout a 32bpp alpha-icon DIB needs) -> a GDI DIB section (CreateDIBSection) copied in as
/// the icon's color bitmap -> CreateIconIndirect with fIcon=false (a cursor, not an icon) and an
/// explicit hotspot -> CursorInteropHelper.Create wraps the resulting HICON/HCURSOR as a WPF Cursor.
/// Entries are cached per <see cref="CursorKey"/> (scroll-wheel resizing revisits the same widths
/// constantly) in a small LRU-bounded cache; whatever gets evicted is disposed (DestroyIcon)
/// immediately, and <see cref="Dispose"/> tears down every entry still cached — call it once when the
/// owning OverlayWindow closes.</summary>
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
        int canvasWidth, canvasHeight, hotspotX, hotspotY;
        Action<DrawingContext> render;

        if (tool == AnnotationTool.Text)
        {
            canvasWidth = canvasHeight = TextCanvasSize;
            hotspotX = TextHotspotX;
            hotspotY = TextHotspotY;
            render = dc => DrawTextGlyph(dc, color);
        }
        else
        {
            // Every stroke tool (Rectangle/Ellipse/Arrow/Line/Freehand) shares one design: a plain
            // circle outline the same diameter as the stroke width — see CircleSpec.For.
            var spec = CircleSpec.For(normalizedStrokeWidthPx);
            canvasWidth = canvasHeight = spec.CanvasSize;
            hotspotX = hotspotY = spec.CanvasSize / 2;
            render = dc => DrawCircleGlyph(dc, color, spec);
        }

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            render(dc);
        }

        var rtb = new RenderTargetBitmap(canvasWidth, canvasHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        int stride = canvasWidth * 4;
        var pixels = new byte[stride * canvasHeight];
        rtb.CopyPixels(pixels, stride, 0);

        IntPtr hIcon = CreateCursorIcon(pixels, canvasWidth, canvasHeight, hotspotX, hotspotY);
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

    /// <summary>All five drawing tools (Rectangle/Ellipse/Arrow/Line/Freehand) share this one design:
    /// a circle outline sized to the current stroke width, tinted with the draw color, with a dark
    /// halo so it reads on any background — no per-tool shape glyph, which is what previously read as
    /// stray artifacts (e.g. a line tool's glyph looking like "a red line through the crosshair").</summary>
    private static void DrawCircleGlyph(DrawingContext dc, Color color, CircleSpec spec)
    {
        var center = new Point(spec.CanvasSize / 2.0, spec.CanvasSize / 2.0);
        double radius = spec.Diameter / 2.0;

        var haloPen = new Pen(new SolidColorBrush(Color.FromArgb(215, 0, 0, 0)), 3.0);
        var fgPen = new Pen(new SolidColorBrush(color), 1.6);
        dc.DrawEllipse(null, haloPen, center, radius, radius);
        dc.DrawEllipse(null, fgPen, center, radius, radius);

        if (spec.ShowLabel)
        {
            string label = spec.LabelWidthPx.ToString("0", CultureInfo.InvariantCulture);
            var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            DrawHaloedText(dc, label, typeface, 13.0, center, color: Brushes.White);
        }
    }

    /// <summary>Text tool: an I-beam with a small bold "T" beside it (unambiguously "text tool", not
    /// a generic text-select caret), tinted with the draw color and haloed like the circle cursor.
    /// The hotspot (<see cref="TextHotspotX"/>/<see cref="TextHotspotY"/>) sits at the top of the
    /// I-beam's vertical bar — the point BeginTextEditor treats as the new text's origin (top-left of
    /// the editor), not the bar's vertical center a normal caret would use.</summary>
    private static void DrawTextGlyph(DrawingContext dc, Color color)
    {
        var haloPen = new Pen(new SolidColorBrush(Color.FromArgb(215, 0, 0, 0)), 3.0)
        {
            StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round,
        };
        var fgPen = new Pen(new SolidColorBrush(color), 1.5)
        {
            StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round,
        };

        var top = new Point(TextHotspotX, TextHotspotY);
        var bottom = new Point(TextHotspotX, TextHotspotY + TextBarHeight);

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

        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        var tCenter = new Point(TextHotspotX + 8.0, TextHotspotY + TextBarHeight / 2.0);
        DrawHaloedText(dc, "T", typeface, 12.0, tCenter, new SolidColorBrush(color));
    }

    /// <summary>Draws <paramref name="text"/> centered on <paramref name="center"/> with a cheap
    /// (four-offset-copy) dark halo behind it in <paramref name="color"/>, matching the halo-then-
    /// tint convention used throughout the rest of this cache's rendering.</summary>
    private static void DrawHaloedText(DrawingContext dc, string text, Typeface typeface, double fontSize, Point center, Brush color)
    {
        var halo = new FormattedText(
            text, CultureInfo.InvariantCulture, FlowDir.LeftToRight, typeface, fontSize,
            new SolidColorBrush(Color.FromArgb(215, 0, 0, 0)), 1.0);
        var fg = new FormattedText(
            text, CultureInfo.InvariantCulture, FlowDir.LeftToRight, typeface, fontSize, color, 1.0);

        var origin = new Point(center.X - fg.Width / 2.0, center.Y - fg.Height / 2.0);
        foreach (var offset in HaloOffsets)
        {
            dc.DrawText(halo, origin + offset);
        }
        dc.DrawText(fg, origin);
    }

    private static readonly Vector[] HaloOffsets =
    {
        new(-1, 0), new(1, 0), new(0, -1), new(0, 1),
    };

    // Text-tool cursor geometry: a fixed-size canvas (stroke width plays no part in this glyph — see
    // CursorKey.For's normalization), with the hotspot at the top of the I-beam bar.
    private const int TextCanvasSize = 28;
    private const double TextBarHeight = 16.0;
    private const int TextHotspotX = 9;
    private const int TextHotspotY = 5;
}
