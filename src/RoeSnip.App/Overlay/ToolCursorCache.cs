using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using RoeSnip.Core.Diagnostics;

namespace RoeSnip.App.Overlay;

/// <summary>Identifies one cached cursor bitmap: which tool is selected, what color it's drawn in,
/// and (for the stroke-width-sensitive tools) the current stroke width — the plain Select tool
/// never reaches <see cref="ToolCursorCache"/> at all (OverlayWindow keeps the system crosshair for
/// it), and the Text tool ignores stroke width entirely, so its key always normalizes width to 0 to
/// avoid needlessly growing the cache every time a scroll-wheel changes a width that tool doesn't
/// even render. Ported verbatim from the WPF reference's Overlay/ToolCursorCache.cs (same
/// normalization rule, just Avalonia's Color type). Public (rather than internal) purely so the
/// normalization rule itself is unit-testable without an InternalsVisibleTo edit, matching this
/// port's SwatchPalette/SizeInput convention (item 08).</summary>
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
/// rounded-to-nearest-pixel) stroke width. Ported verbatim from the WPF reference's
/// Overlay/ToolCursorCache.cs: every drawing tool's cursor is a plain circle outline whose diameter
/// equals the stroke width, so scrolling the width visibly grows/shrinks the cursor itself. The
/// diameter always equals the stroke width 1:1 — never scaled down to fit — except once it would
/// need a canvas bigger than <see cref="MaxCanvasSize"/>: past that point the bitmap is capped at
/// 64x64, the circle is drawn at the largest diameter that still fits, and <see cref="ShowLabel"/>
/// switches on so the actual numeric width is drawn inside it instead of silently lying about the
/// true size.</summary>
public readonly record struct CircleSpec(int CanvasSize, double Diameter, bool ShowLabel, double LabelWidthPx)
{
    public const int MaxCanvasSize = 64;

    // The circle is ALWAYS exactly the stroke width — no legibility clamp. At 1-2px the ring is
    // tiny, but the dark halo keeps it findable, and a cursor bigger than the brush lies about
    // what will be drawn. Kept as a floor of 1 purely to guard against zero/negative widths.
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

/// <summary>A tiny detached <see cref="Control"/> whose sole job is running a caller-supplied
/// <see cref="DrawingContext"/> callback — Avalonia's equivalent of WPF's
/// <c>DrawingVisual</c>/<c>RenderOpen()</c> pair, needed because <see cref="RenderTargetBitmap"/>
/// can only rasterize an actual <see cref="Visual"/>, and Avalonia (unlike WPF) has no
/// visual-tree-free DrawingVisual type to hand it directly.</summary>
file sealed class CursorGlyphControl : Control
{
    private readonly Action<DrawingContext> _render;

    public CursorGlyphControl(int size, Action<DrawingContext> render)
    {
        _render = render;
        Width = size;
        Height = size;
    }

    public override void Render(DrawingContext context) => _render(context);
}

/// <summary>Builds and caches a small custom cursor bitmap per (tool, color, strokeWidth)
/// combination — the Avalonia port of the WPF reference's ToolCursorCache. Every drawing tool
/// (Rectangle/Ellipse/Arrow/Line/Freehand/Highlight) gets a plain circle outline whose diameter
/// *is* the current stroke width, so scrolling the width visibly grows/shrinks the cursor itself;
/// the Text tool gets a small I-beam-with-T icon. AnnotationTool.None (the Select tool) and
/// AnnotationTool.Pixelate (a fixed crosshair, per user feedback — the block-size ring would grow
/// far past the actual pixelated region) are never looked up here; OverlayWindow keeps the plain
/// system cursor for both.
///
/// Built via a detached <see cref="CursorGlyphControl"/> measured/arranged off-screen and
/// rasterized through <see cref="RenderTargetBitmap"/>, then wrapped as an Avalonia
/// <see cref="Cursor"/> with an explicit hotspot — Avalonia's <c>Cursor(IBitmap, PixelPoint)</c>
/// constructor does the platform cursor-handle plumbing WPF's own P/Invoke
/// CreateIconIndirect/DestroyIcon dance had to do by hand, so this port needs none of that
/// interop. Entries are cached per <see cref="CursorKey"/> (scroll-wheel resizing revisits the
/// same widths constantly) in a small LRU-bounded cache; whatever gets evicted is disposed
/// immediately, and <see cref="Dispose"/> tears down every entry still cached.
///
/// Bitmap cursors are a platform capability, not guaranteed on every backend Avalonia might run
/// on — <see cref="Build"/> is wrapped in try/catch and, on first failure, this cache permanently
/// falls back to the plain system cursor for the rest of the process's life (logged once, never
/// spammed per scroll-wheel tick).</summary>
public sealed class ToolCursorCache : IDisposable
{
    private const int MaxCachedCursors = 48;

    private readonly Dictionary<CursorKey, Cursor> _cache = new();
    private readonly List<CursorKey> _lruOrder = new(); // oldest first

    private static readonly Cursor FallbackCrossCursor = new(StandardCursorType.Cross);
    private static readonly Cursor FallbackIBeamCursor = new(StandardCursorType.Ibeam);

    private bool _bitmapCursorsUnsupported;

    public Cursor GetOrCreate(AnnotationTool tool, Color color, double strokeWidthPx)
    {
        if (_bitmapCursorsUnsupported)
        {
            return FallbackCursor(tool);
        }

        var key = CursorKey.For(tool, color, strokeWidthPx);
        if (_cache.TryGetValue(key, out var existing))
        {
            Touch(key);
            return existing;
        }

        Cursor cursor;
        try
        {
            cursor = Build(tool, color, key.StrokeWidthPx);
        }
        catch (Exception ex)
        {
            // Custom bitmap cursors aren't guaranteed on every desktop backend — degrade to the
            // system cursor for the rest of this session rather than re-attempting (and re-logging)
            // on every subsequent tool/color/width change.
            _bitmapCursorsUnsupported = true;
            FileLog.Write($"RoeSnip: custom tool cursor rendering failed ({ex.Message}); falling back to the system cursor.");
            return FallbackCursor(tool);
        }

        _cache[key] = cursor;
        _lruOrder.Add(key);
        EvictIfNeeded();
        return cursor;
    }

    private static Cursor FallbackCursor(AnnotationTool tool) =>
        tool == AnnotationTool.Text ? FallbackIBeamCursor : FallbackCrossCursor;

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
                evicted.Dispose();
            }
        }
    }

    public void Dispose()
    {
        foreach (var cursor in _cache.Values)
        {
            cursor.Dispose();
        }
        _cache.Clear();
        _lruOrder.Clear();
    }

    private static Cursor Build(AnnotationTool tool, Color color, double normalizedStrokeWidthPx)
    {
        int canvasSize;
        int hotspotX, hotspotY;
        Action<DrawingContext> render;

        if (tool == AnnotationTool.Text)
        {
            canvasSize = TextCanvasSize;
            hotspotX = TextHotspotX;
            hotspotY = TextHotspotY;
            render = dc => DrawTextGlyph(dc, color);
        }
        else
        {
            // Every stroke tool (Rectangle/Ellipse/Arrow/Line/Freehand/Highlight) shares one
            // design: a plain circle outline the same diameter as the stroke width — see
            // CircleSpec.For.
            var spec = CircleSpec.For(normalizedStrokeWidthPx);
            canvasSize = spec.CanvasSize;
            hotspotX = hotspotY = spec.CanvasSize / 2;
            render = dc => DrawCircleGlyph(dc, color, spec);
        }

        var control = new CursorGlyphControl(canvasSize, render);
        control.Measure(new Size(canvasSize, canvasSize));
        control.Arrange(new Rect(0, 0, canvasSize, canvasSize));

        var rtb = new RenderTargetBitmap(new PixelSize(canvasSize, canvasSize), new Vector(96, 96));
        rtb.Render(control);

        return new Cursor(rtb, new PixelPoint(hotspotX, hotspotY));
    }

    // ---------- Glyph rendering ----------

    private static readonly Color HaloColor = Color.FromArgb(215, 0, 0, 0);
    private static readonly IBrush HaloBrush = new SolidColorBrush(HaloColor);

    /// <summary>All stroke tools share this one design: a circle outline sized to the current
    /// stroke width, tinted with the draw color, with a dark halo so it reads on any background —
    /// no per-tool shape glyph (an earlier per-tool arrow/line/etc. glyph read as a visual artifact
    /// rather than a cursor, per the WPF reference's own history).</summary>
    private static void DrawCircleGlyph(DrawingContext dc, Color color, CircleSpec spec)
    {
        var center = new Point(spec.CanvasSize / 2.0, spec.CanvasSize / 2.0);
        double radius = spec.Diameter / 2.0;

        var haloPen = new Pen(HaloBrush, 3.0);
        var fgPen = new Pen(new SolidColorBrush(color), 1.6);
        dc.DrawEllipse(null, haloPen, center, radius, radius);
        dc.DrawEllipse(null, fgPen, center, radius, radius);

        if (spec.ShowLabel)
        {
            string label = spec.LabelWidthPx.ToString("0", CultureInfo.InvariantCulture);
            var typeface = new Typeface("Segoe UI", FontStyle.Normal, FontWeight.Bold);
            DrawHaloedText(dc, label, typeface, 13.0, center, Brushes.White);
        }
    }

    /// <summary>Text tool: an I-beam with a small bold "T" beside it (unambiguously "text tool",
    /// not a generic text-select caret), tinted with the draw color and haloed like the circle
    /// cursor. The hotspot (<see cref="TextHotspotX"/>/<see cref="TextHotspotY"/>) sits at the top
    /// of the I-beam's vertical bar — the point BeginTextEditor treats as the new text's origin
    /// (top-left of the editor), not the bar's vertical center a normal caret would use.</summary>
    private static void DrawTextGlyph(DrawingContext dc, Color color)
    {
        var haloPen = new Pen(HaloBrush, 3.0) { LineCap = PenLineCap.Round };
        var fgPen = new Pen(new SolidColorBrush(color), 1.5) { LineCap = PenLineCap.Round };

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

        var typeface = new Typeface("Segoe UI", FontStyle.Normal, FontWeight.Bold);
        var tCenter = new Point(TextHotspotX + 8.0, TextHotspotY + TextBarHeight / 2.0);
        DrawHaloedText(dc, "T", typeface, 12.0, tCenter, new SolidColorBrush(color));
    }

    /// <summary>Draws <paramref name="text"/> centered on <paramref name="center"/> with a cheap
    /// (four-offset-copy) dark halo behind it, matching the halo-then-tint convention used
    /// throughout the rest of this cache's rendering.</summary>
    private static void DrawHaloedText(DrawingContext dc, string text, Typeface typeface, double fontSize, Point center, IBrush color)
    {
        var halo = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, fontSize, HaloBrush);
        var fg = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, fontSize, color);

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

    // Text-tool cursor geometry: a fixed-size canvas (stroke width plays no part in this glyph —
    // see CursorKey.For's normalization), with the hotspot at the top of the I-beam bar.
    private const int TextCanvasSize = 28;
    private const double TextBarHeight = 16.0;
    private const int TextHotspotX = 9;
    private const int TextHotspotY = 5;
}
