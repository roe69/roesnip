using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace RoeSnip.App.Overlay;

/// <summary>Annotation tool kinds. Text is implemented but is the lowest-priority / most cuttable
/// tool per PLAN.md §3.2 (allowance restated by PLAN-XPLAT.md §3.3) — the other five
/// (Rectangle/Ellipse/Arrow/Line/Freehand) must work regardless of whether Text makes it in.</summary>
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
}

/// <summary>Vector annotation shapes drawn over an overlay's frozen preview. Renders itself scaled
/// down to DIPs for on-screen display (via <see cref="DeviceScaleX"/>/<see cref="DeviceScaleY"/>,
/// kept in sync by the owning OverlayWindow from the correlated Screen's Scaling — the Avalonia
/// analog of WPF's CompositionTarget.TransformToDevice), and separately offers
/// <see cref="RenderForExport"/> for 1:1 physical-pixel rasterization at export time. Maintains a
/// simple linear undo stack (Ctrl+Z pops the most recently committed shape). Ported from the
/// frozen WPF app's src/RoeSnip/Overlay/AnnotationLayer.cs.</summary>
public sealed class AnnotationLayer : Control
{
    private readonly List<AnnotationShape> _shapes = new();
    private AnnotationShape? _inProgress;

    /// <summary>1 physical pixel == 1/DeviceScaleX (or Y) DIPs on this window's monitor.</summary>
    public double DeviceScaleX { get; set; } = 1.0;
    public double DeviceScaleY { get; set; } = 1.0;

    public bool HasAnnotations => _shapes.Count > 0;

    public AnnotationLayer()
    {
        // OverlayWindow handles all raw pointer input itself and drives this layer
        // programmatically; it must not intercept hit-testing meant for the toolbar/magnifier.
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
            && LengthSquared(_inProgress.PointsPx[0], _inProgress.PointsPx[1]) < 4.0;

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
    /// (a real Avalonia TextBox, for native IME support — the same reason the WPF version used a
    /// real WPF TextBox) rather than the Begin/Update/EndShape drag pipeline, since text entry
    /// isn't a drag gesture.</summary>
    public void CommitText(Point physicalPt, string text, Color color, double fontSizePx)
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

    public override void Render(DrawingContext dc)
    {
        double sx = DeviceScaleX <= 0 ? 1.0 : DeviceScaleX;
        double sy = DeviceScaleY <= 0 ? 1.0 : DeviceScaleY;
        using (dc.PushTransform(Matrix.CreateScale(1.0 / sx, 1.0 / sy)))
        {
            foreach (var shape in _shapes)
            {
                Draw(dc, shape, translateX: 0, translateY: 0);
            }
            if (_inProgress is not null)
            {
                Draw(dc, _inProgress, translateX: 0, translateY: 0);
            }
        }
    }

    /// <summary>Rasterizes all committed shapes at 1:1 physical-pixel scale (no DIP scaling),
    /// translated so that <paramref name="originPx"/> maps to (0,0) — used by OverlayWindow when
    /// burning annotations into the exported crop. The in-progress (uncommitted) shape, if any,
    /// is intentionally excluded — export only ever runs after EndShape/CommitText.</summary>
    public void RenderForExport(DrawingContext dc, Point originPx)
    {
        foreach (var shape in _shapes)
        {
            Draw(dc, shape, -originPx.X, -originPx.Y);
        }
    }

    private static double LengthSquared(Point a, Point b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    private static void Draw(DrawingContext dc, AnnotationShape shape, double translateX, double translateY)
    {
        var pen = new Pen(
            new SolidColorBrush(shape.StrokeColor),
            Math.Max(1.0, shape.StrokeWidthPx),
            lineCap: PenLineCap.Round,
            lineJoin: PenLineJoin.Round);

        Point P(int i) => new(shape.PointsPx[i].X + translateX, shape.PointsPx[i].Y + translateY);

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
                        new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2),
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
                        ctx.BeginFigure(P(0), false);
                        for (int i = 1; i < shape.PointsPx.Count; i++)
                        {
                            ctx.LineTo(P(i));
                        }
                        ctx.EndFigure(false);
                    }
                    dc.DrawGeometry(null, pen, geometry);
                }
                break;

            case AnnotationTool.Text:
                if (shape.PointsPx.Count >= 1 && !string.IsNullOrEmpty(shape.Text))
                {
                    var typeface = new Typeface(OverlayFonts.Ui, FontStyle.Normal, FontWeight.SemiBold);
                    var formatted = new FormattedText(
                        shape.Text,
                        CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        typeface,
                        Math.Max(8.0, shape.StrokeWidthPx),
                        new SolidColorBrush(shape.StrokeColor));
                    dc.DrawText(formatted, P(0));
                }
                break;
        }
    }

    private static void DrawArrow(DrawingContext dc, Pen pen, Color color, Point from, Point to)
    {
        dc.DrawLine(pen, from, to);

        double dx = to.X - from.X, dy = to.Y - from.Y;
        double lengthSquared = dx * dx + dy * dy;
        if (lengthSquared < 1e-6)
        {
            return;
        }
        double length = Math.Sqrt(lengthSquared);
        double dirX = dx / length, dirY = dy / length;

        double headLength = Math.Max(10.0, pen.Thickness * 4.0);
        double headAngle = Math.PI / 7.0; // ~25.7 degrees

        (double X, double Y) Rotate(double vx, double vy, double radians)
        {
            double cos = Math.Cos(radians), sin = Math.Sin(radians);
            return (vx * cos - vy * sin, vx * sin + vy * cos);
        }

        double backX = -dirX * headLength, backY = -dirY * headLength;
        var (lx, ly) = Rotate(backX, backY, headAngle);
        var (rx, ry) = Rotate(backX, backY, -headAngle);
        var left = new Point(to.X + lx, to.Y + ly);
        var right = new Point(to.X + rx, to.Y + ry);

        var headGeometry = new StreamGeometry();
        using (var ctx = headGeometry.Open())
        {
            ctx.BeginFigure(to, true);
            ctx.LineTo(left);
            ctx.LineTo(right);
            ctx.EndFigure(true);
        }
        dc.DrawGeometry(new SolidColorBrush(color), null, headGeometry);
    }
}

/// <summary>Shared font choices for the overlay chrome. The WPF app hardcoded "Segoe UI" and
/// "Consolas" (Windows-only fonts); Avalonia FontFamily supports a fallback list, so non-Windows
/// hosts fall back to a present equivalent (Inter ships with the app via Avalonia.Fonts.Inter).</summary>
internal static class OverlayFonts
{
    public static readonly FontFamily Ui = new("Segoe UI,Inter,Liberation Sans,DejaVu Sans");
    public static readonly FontFamily Mono = new("Consolas,Menlo,DejaVu Sans Mono,Liberation Mono");
}
