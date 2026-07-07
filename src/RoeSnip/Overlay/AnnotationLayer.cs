using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace RoeSnip.Overlay;

// RoeSnip.csproj enables both UseWPF and UseWindowsForms (WPF overlay UI + WinForms NotifyIcon),
// which puts System.Windows.Forms and System.Drawing in scope everywhere alongside System.Windows
// / System.Windows.Media — several common type names collide. Alias to the WPF ones explicitly.
// (These must be declared *after* the namespace line: RoeSnip.Color — WP-A's own sibling
// namespace — would otherwise win simple-name lookup for "Color" before an outer-scope alias is
// even considered, since C# resolves inner-namespace members before outer-scope usings.)
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using FontFamily = System.Windows.Media.FontFamily;
// Not a WinForms/System.Drawing collision — FrameworkElement itself declares an instance
// FlowDirection property, which shadows the enum type name "FlowDirection" inside any method of a
// class deriving from it (AnnotationLayer here). Alias to a distinct name to avoid CS0176.
using FlowDir = System.Windows.FlowDirection;

/// <summary>Annotation tool kinds. Text is implemented but is the lowest-priority / most cuttable
/// tool per PLAN.md §3.2 — the other five (Rectangle/Ellipse/Arrow/Line/Freehand) must work
/// regardless of whether Text makes it in.</summary>
public enum AnnotationTool
{
    None,
    Rectangle,
    Ellipse,
    Arrow,
    Line,
    Freehand,
    Text,
}

/// <summary>One committed (or in-progress) annotation shape. All points are physical pixels,
/// monitor-local — the same coordinate space as the CapturedFrame/SdrImage for that monitor.
/// Never DIPs; DIP conversion happens only where AnnotationLayer renders to screen.</summary>
public sealed class AnnotationShape
{
    public AnnotationTool Tool { get; init; }
    public List<Point> PointsPx { get; init; } = new();
    public Color StrokeColor { get; init; } = Colors.Red;
    public double StrokeWidthPx { get; init; } = 3.0;
    public string Text { get; set; } = string.Empty;

    // Text-only styling (item 4) — unused by every other tool. StrokeWidthPx doubles as the text's
    // font size (matching the pre-existing convention CommitText already used).
    public string TextFontFamily { get; init; } = "Segoe UI";
    public bool TextBold { get; init; }
    public bool TextItalic { get; init; }
}

/// <summary>Vector annotation shapes drawn over an overlay's frozen preview. Renders itself scaled
/// down to DIPs for on-screen display (via <see cref="DeviceScaleX"/>/<see cref="DeviceScaleY"/>,
/// kept in sync by the owning OverlayWindow from CompositionTarget.TransformToDevice), and
/// separately offers <see cref="RenderForExport"/> for 1:1 physical-pixel rasterization at export
/// time. Maintains a simple linear undo stack (Ctrl+Z pops the most recently committed shape).</summary>
public sealed class AnnotationLayer : FrameworkElement
{
    private readonly List<AnnotationShape> _shapes = new();
    private AnnotationShape? _inProgress;

    /// <summary>1 physical pixel == 1/DeviceScaleX (or Y) DIPs on this window's monitor.</summary>
    public double DeviceScaleX { get; set; } = 1.0;
    public double DeviceScaleY { get; set; } = 1.0;

    public bool HasAnnotations => _shapes.Count > 0;

    public AnnotationLayer()
    {
        // OverlayWindow handles all raw mouse input itself and drives this layer programmatically;
        // it must not intercept hit-testing that's meant for the toolbar/magnifier/window.
        IsHitTestVisible = false;
    }

    public void BeginShape(AnnotationTool tool, Point physicalPt, Color color, double strokeWidthPx)
    {
        _inProgress = new AnnotationShape
        {
            Tool = tool,
            StrokeColor = color,
            StrokeWidthPx = strokeWidthPx,
        };
        _inProgress.PointsPx.Add(physicalPt);
        if (tool != AnnotationTool.Freehand)
        {
            _inProgress.PointsPx.Add(physicalPt); // 2nd point tracks the live drag endpoint
        }
        InvalidateVisual();
    }

    public void UpdateShape(Point physicalPt)
    {
        if (_inProgress is null)
        {
            return;
        }

        if (_inProgress.Tool == AnnotationTool.Freehand)
        {
            _inProgress.PointsPx.Add(physicalPt);
        }
        else if (_inProgress.PointsPx.Count >= 2)
        {
            _inProgress.PointsPx[1] = physicalPt;
        }
        InvalidateVisual();
    }

    public void EndShape()
    {
        if (_inProgress is null)
        {
            return;
        }

        // Discard degenerate shapes (a click with no meaningful drag).
        bool isDegenerate = _inProgress.Tool != AnnotationTool.Freehand
            && _inProgress.PointsPx.Count >= 2
            && (_inProgress.PointsPx[0] - _inProgress.PointsPx[1]).LengthSquared < 4.0;

        if (!isDegenerate)
        {
            _shapes.Add(_inProgress);
        }
        _inProgress = null;
        InvalidateVisual();
    }

    public void CancelShape()
    {
        _inProgress = null;
        InvalidateVisual();
    }

    /// <summary>Commits a completed text annotation. Used by OverlayWindow's inline text editor
    /// (a real WPF TextBox, for native IME support) rather than the Begin/Update/EndShape drag
    /// pipeline, since text entry isn't a drag gesture. fontFamily/bold/italic (item 4) default to
    /// the pre-existing plain style so any other caller keeps working unchanged.</summary>
    public void CommitText(
        Point physicalPt, string text, Color color, double fontSizePx,
        string fontFamily = "Segoe UI", bool bold = false, bool italic = false)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var shape = new AnnotationShape
        {
            Tool = AnnotationTool.Text,
            StrokeColor = color,
            StrokeWidthPx = fontSizePx,
            Text = text,
            TextFontFamily = fontFamily,
            TextBold = bold,
            TextItalic = italic,
        };
        shape.PointsPx.Add(physicalPt);
        _shapes.Add(shape);
        InvalidateVisual();
    }

    public void Undo()
    {
        if (_shapes.Count == 0)
        {
            return;
        }
        _shapes.RemoveAt(_shapes.Count - 1);
        InvalidateVisual();
    }

    public void Clear()
    {
        _shapes.Clear();
        _inProgress = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.PushTransform(new ScaleTransform(1.0 / DeviceScaleX, 1.0 / DeviceScaleY));
        try
        {
            foreach (var shape in _shapes)
            {
                Draw(dc, shape, translate: default);
            }
            if (_inProgress is not null)
            {
                Draw(dc, _inProgress, translate: default);
            }
        }
        finally
        {
            dc.Pop();
        }
    }

    /// <summary>Rasterizes all committed shapes at 1:1 physical-pixel scale (no DIP scaling),
    /// translated so that <paramref name="originPx"/> maps to (0,0) — used by OverlayController
    /// when burning annotations into the exported crop. The in-progress (uncommitted) shape, if
    /// any, is intentionally excluded — export only ever runs after EndShape/CommitText.</summary>
    public void RenderForExport(DrawingContext dc, Point originPx)
    {
        var translate = new Vector(-originPx.X, -originPx.Y);
        foreach (var shape in _shapes)
        {
            Draw(dc, shape, translate);
        }
    }

    private static void Draw(DrawingContext dc, AnnotationShape shape, Vector translate)
    {
        var pen = new Pen(new SolidColorBrush(shape.StrokeColor), Math.Max(1.0, shape.StrokeWidthPx))
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };

        Point P(int i) => shape.PointsPx[i] + translate;

        switch (shape.Tool)
        {
            case AnnotationTool.Rectangle:
                if (shape.PointsPx.Count >= 2)
                {
                    dc.DrawRectangle(null, pen, new Rect(P(0), P(1)));
                }
                break;

            case AnnotationTool.Ellipse:
                if (shape.PointsPx.Count >= 2)
                {
                    var rect = new Rect(P(0), P(1));
                    dc.DrawEllipse(null, pen,
                        new Point(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2),
                        rect.Width / 2, rect.Height / 2);
                }
                break;

            case AnnotationTool.Line:
                if (shape.PointsPx.Count >= 2)
                {
                    dc.DrawLine(pen, P(0), P(1));
                }
                break;

            case AnnotationTool.Arrow:
                if (shape.PointsPx.Count >= 2)
                {
                    DrawArrow(dc, pen, shape.StrokeColor, P(0), P(1));
                }
                break;

            case AnnotationTool.Freehand:
                if (shape.PointsPx.Count >= 2)
                {
                    var geometry = new StreamGeometry();
                    using (var ctx = geometry.Open())
                    {
                        ctx.BeginFigure(shape.PointsPx[0] + translate, false, false);
                        var rest = new List<Point>(shape.PointsPx.Count - 1);
                        for (int i = 1; i < shape.PointsPx.Count; i++)
                        {
                            rest.Add(shape.PointsPx[i] + translate);
                        }
                        ctx.PolyLineTo(rest, true, true);
                    }
                    dc.DrawGeometry(null, pen, geometry);
                }
                break;

            case AnnotationTool.Text:
                if (shape.PointsPx.Count >= 1 && !string.IsNullOrEmpty(shape.Text))
                {
                    var typeface = new Typeface(
                        new FontFamily(shape.TextFontFamily),
                        shape.TextItalic ? FontStyles.Italic : FontStyles.Normal,
                        shape.TextBold ? FontWeights.Bold : FontWeights.SemiBold,
                        FontStretches.Normal);
                    var formatted = new FormattedText(
                        shape.Text,
                        CultureInfo.InvariantCulture,
                        FlowDir.LeftToRight,
                        typeface,
                        Math.Max(8.0, shape.StrokeWidthPx),
                        new SolidColorBrush(shape.StrokeColor),
                        1.0);
                    dc.DrawText(formatted, P(0));
                }
                break;
        }
    }

    private static void DrawArrow(DrawingContext dc, Pen pen, Color color, Point from, Point to)
    {
        dc.DrawLine(pen, from, to);

        var direction = to - from;
        if (direction.LengthSquared < 1e-6)
        {
            return;
        }
        direction.Normalize();

        double headLength = Math.Max(10.0, pen.Thickness * 4.0);
        double headAngle = Math.PI / 7.0; // ~25.7 degrees

        Vector Rotate(Vector v, double radians)
        {
            double cos = Math.Cos(radians), sin = Math.Sin(radians);
            return new Vector(v.X * cos - v.Y * sin, v.X * sin + v.Y * cos);
        }

        var back = -direction * headLength;
        var left = to + Rotate(back, headAngle);
        var right = to + Rotate(back, -headAngle);

        var headGeometry = new StreamGeometry();
        using (var ctx = headGeometry.Open())
        {
            ctx.BeginFigure(to, true, true);
            ctx.LineTo(left, true, true);
            ctx.LineTo(right, true, true);
        }
        dc.DrawGeometry(new SolidColorBrush(color), null, headGeometry);
    }
}
