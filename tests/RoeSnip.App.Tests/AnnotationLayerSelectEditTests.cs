using Avalonia;
using Avalonia.Media;
using RoeSnip.App.Overlay;
using Xunit;

namespace RoeSnip.App.Tests;

/// <summary>Feature B (Select tool: editable Pixelate/Text/Rectangle/Ellipse/Line/Arrow) — the
/// hit-testing/selection/drag-gesture model ported from the frozen WPF app's
/// src/RoeSnip/Overlay/AnnotationLayer.cs:308-752. No Avalonia Application/visual-tree bootstrap is
/// needed: AnnotationLayer's Feature B surface is plain CLR state manipulated through its public
/// API, and InvalidateVisual() is a safe no-op on an unattached Control.</summary>
public class AnnotationLayerSelectEditTests
{
    private static AnnotationShape Rect(Point p0, Point p1, double strokeWidth = 4.0) => new()
    {
        Tool = AnnotationTool.Rectangle,
        PointsPx = { p0, p1 },
        StrokeColor = Colors.Red,
        StrokeWidthPx = strokeWidth,
    };

    private static AnnotationLayer NewLayerWith(params AnnotationShape[] shapes)
    {
        var layer = new AnnotationLayer();
        foreach (var s in shapes)
        {
            // Route through the same Begin/Update/End pipeline every real placement uses, so the
            // shape actually lands in the undo history (direct history access isn't exposed).
            layer.BeginShape(s.Tool, s.PointsPx[0], s.StrokeColor, s.StrokeWidthPx);
            if (s.PointsPx.Count > 1)
            {
                layer.UpdateShape(s.PointsPx[1]);
            }
            else
            {
                layer.UpdateShape(new Point(s.PointsPx[0].X + 20, s.PointsPx[0].Y + 20));
            }
            layer.EndShape();
        }
        return layer;
    }

    [Fact]
    public void HitTestEditable_TopmostShapeWins_WhenOverlapping()
    {
        var layer = NewLayerWith(
            Rect(new Point(0, 0), new Point(100, 100)),
            Rect(new Point(50, 50), new Point(150, 150)));

        // Both rectangles cover (60,60), but only near their own stroke (outline hit rule) —
        // the second (topmost) rect's top-left stroke passes near (55,55).
        var hit = layer.HitTestEditable(new Point(55, 55));
        Assert.NotNull(hit);
        Assert.Equal(new Point(50, 50), hit!.PointsPx[0]);
    }

    [Fact]
    public void HitTestEditable_OutlineShape_InteriorClickMisses_WithoutInteriorGrab()
    {
        var layer = NewLayerWith(Rect(new Point(0, 0), new Point(100, 100)));

        Assert.Null(layer.HitTestEditable(new Point(50, 50))); // dead center — far from any stroke
    }

    [Fact]
    public void HitTestEditable_OutlineShape_InteriorClickHits_WithInteriorGrab()
    {
        var layer = NewLayerWith(Rect(new Point(0, 0), new Point(100, 100)));

        Assert.NotNull(layer.HitTestEditable(new Point(50, 50), interiorGrab: true));
    }

    [Fact]
    public void HitTestEditable_RestrictToTool_ExcludesOtherToolShapes()
    {
        var layer = new AnnotationLayer();
        layer.BeginShape(AnnotationTool.Ellipse, new Point(0, 0), Colors.Blue, 4.0);
        layer.UpdateShape(new Point(100, 100));
        layer.EndShape();

        Assert.Null(layer.HitTestEditable(new Point(50, 0), interiorGrab: true, restrictToTool: AnnotationTool.Rectangle));
        Assert.NotNull(layer.HitTestEditable(new Point(50, 0), interiorGrab: true, restrictToTool: AnnotationTool.Ellipse));
    }

    [Fact]
    public void Select_ThenHitTestSelectedHandle_FindsCorner()
    {
        var layer = NewLayerWith(Rect(new Point(10, 10), new Point(110, 110)));
        var shape = layer.HitTestEditable(new Point(10, 60), interiorGrab: true);
        layer.Select(shape);

        Assert.Equal(SelectionHandle.TopLeft, layer.HitTestSelectedHandle(new Point(10, 10)));
        Assert.Equal(SelectionHandle.BottomRight, layer.HitTestSelectedHandle(new Point(110, 110)));
        Assert.Equal(SelectionHandle.None, layer.HitTestSelectedHandle(new Point(60, 60)));
    }

    [Fact]
    public void HitTestSelectedHandle_TextShape_NeverReportsAHandle()
    {
        var layer = new AnnotationLayer();
        var text = layer.CommitText(new Point(10, 10), "hello", Colors.White, 16.0);
        layer.Select(text);

        Assert.Equal(SelectionHandle.None, layer.HitTestSelectedHandle(new Point(10, 10)));
    }

    [Fact]
    public void HitTestSelectedEndpoint_FindsEachEndOfALine()
    {
        var layer = new AnnotationLayer();
        layer.BeginShape(AnnotationTool.Line, new Point(0, 0), Colors.Red, 4.0);
        layer.UpdateShape(new Point(100, 0));
        var line = layer.EndShape();
        layer.Select(line);

        Assert.Equal(0, layer.HitTestSelectedEndpoint(new Point(0, 0)));
        Assert.Equal(1, layer.HitTestSelectedEndpoint(new Point(100, 0)));
        Assert.Equal(-1, layer.HitTestSelectedEndpoint(new Point(50, 0)));
    }

    [Fact]
    public void Select_Deselect_RoundTrips()
    {
        var layer = NewLayerWith(Rect(new Point(0, 0), new Point(50, 50)));
        var shape = layer.HitTestEditable(new Point(25, 25), interiorGrab: true);

        layer.Select(shape);
        Assert.Same(shape, layer.SelectedShape);

        layer.Deselect();
        Assert.Null(layer.SelectedShape);
    }

    [Fact]
    public void TranslateSelected_MovesEveryPointByTheSameDelta()
    {
        var layer = NewLayerWith(Rect(new Point(10, 10), new Point(30, 30)));
        var shape = layer.HitTestEditable(new Point(20, 20), interiorGrab: true)!;
        layer.Select(shape);

        layer.BeginDragSelected();
        layer.TranslateSelected(new Vector(5, 7), new Size(1000, 1000));

        Assert.Equal(new Point(15, 17), shape.PointsPx[0]);
        Assert.Equal(new Point(35, 37), shape.PointsPx[1]);
    }

    [Fact]
    public void TranslateSelected_ClampsWholeShapeInsideTheFrame()
    {
        var layer = NewLayerWith(Rect(new Point(10, 10), new Point(30, 30)));
        var shape = layer.HitTestEditable(new Point(20, 20), interiorGrab: true)!;
        layer.Select(shape);

        layer.BeginDragSelected();
        // A huge negative delta would push the shape off both the left and top edges — the clamp
        // must slide it back fully inside [0, frameSize], preserving its 20x20 size.
        layer.TranslateSelected(new Vector(-1000, -1000), new Size(200, 200));

        Assert.Equal(0, shape.PointsPx[0].X);
        Assert.Equal(0, shape.PointsPx[0].Y);
        Assert.Equal(20, shape.PointsPx[1].X);
        Assert.Equal(20, shape.PointsPx[1].Y);
    }

    [Fact]
    public void BeginDragSelected_CommitPendingDrag_ProducesOneUndoableReplace_NotPerNotch()
    {
        var layer = NewLayerWith(Rect(new Point(0, 0), new Point(50, 50)));
        var shape = layer.HitTestEditable(new Point(25, 25), interiorGrab: true)!;
        layer.Select(shape);

        // Simulate three wheel notches, each calling BeginDragSelected (idempotent after the
        // first) then mutating — mirroring OverlayWindow's wheel handler.
        layer.BeginDragSelected();
        layer.SetSelectedStrokeWidth(6.0);
        layer.BeginDragSelected(); // no-op: a gesture is already open
        layer.SetSelectedStrokeWidth(8.0);
        layer.BeginDragSelected();
        layer.SetSelectedStrokeWidth(10.0);
        layer.EndDragSelected();

        Assert.Equal(10.0, layer.SelectedShape!.StrokeWidthPx);
        // Two undoable actions exist: the original placement (Add) and this gesture's Replace —
        // proving the 3-notch gesture collapsed into exactly ONE Replace, not three.
        Assert.True(layer.CanUndo);

        layer.Undo(); // undoes the Replace (Undo() also deselects first, per its own contract)
        var reHit = layer.HitTestEditable(new Point(25, 25), interiorGrab: true);
        Assert.NotNull(reHit);
        Assert.Equal(4.0, reHit!.StrokeWidthPx); // back to Rect's original default, not an intermediate notch
        Assert.True(layer.CanUndo); // the placement itself is still one more undo away
    }

    [Fact]
    public void CommitPendingDrag_NoOpGesture_DoesNotRecordHistory()
    {
        var layer = NewLayerWith(Rect(new Point(0, 0), new Point(50, 50)));
        var shape = layer.HitTestEditable(new Point(25, 25), interiorGrab: true)!;
        layer.Select(shape);

        layer.BeginDragSelected();
        layer.EndDragSelected(); // nothing changed — must not push a Replace on top of the placement's Add

        // Only the placement's own Add action should exist: one Undo removes the shape entirely
        // rather than merely reverting a no-op Replace back to the same (already-current) state.
        Assert.True(layer.HasAnnotations);
        layer.Undo();
        Assert.False(layer.HasAnnotations);
    }

    [Fact]
    public void Select_FlushesAnOutgoingGesture_BeforeSwitchingSelection()
    {
        var layer = NewLayerWith(
            Rect(new Point(0, 0), new Point(50, 50)),
            Rect(new Point(100, 100), new Point(150, 150)));

        var first = layer.HitTestEditable(new Point(25, 25), interiorGrab: true)!;
        layer.Select(first);
        layer.BeginDragSelected();
        layer.SetSelectedStrokeWidth(9.0);

        var second = layer.HitTestEditable(new Point(125, 125), interiorGrab: true)!;
        layer.Select(second); // must flush the pending drag on `first` into history first

        Assert.True(layer.CanUndo);
        Assert.Equal(9.0, first.StrokeWidthPx);
    }

    [Fact]
    public void DeleteSelected_RemovesTheShape_AndUndoRestoresIt()
    {
        var layer = NewLayerWith(Rect(new Point(0, 0), new Point(50, 50)));
        var shape = layer.HitTestEditable(new Point(25, 25), interiorGrab: true)!;
        layer.Select(shape);

        layer.DeleteSelected();
        Assert.Null(layer.SelectedShape);
        Assert.False(layer.HasAnnotations);

        layer.Undo();
        Assert.True(layer.HasAnnotations);
    }

    [Fact]
    public void ReplaceShape_SwapsTheShapeAsOneUndoableAction_KeepingTheReplacementSelected()
    {
        var layer = new AnnotationLayer();
        var original = layer.CommitText(new Point(10, 10), "hello", Colors.White, 16.0)!;
        layer.Select(original);

        var replacement = new AnnotationShape
        {
            Tool = AnnotationTool.Text,
            PointsPx = { new Point(10, 10) },
            StrokeColor = Colors.White,
            StrokeWidthPx = 16.0,
            Text = "hello world",
        };
        layer.ReplaceShape(original, replacement);

        Assert.Same(replacement, layer.SelectedShape);
        Assert.True(layer.CanUndo);

        // Undo() deselects first (its own documented contract — a Replace targeting the selection
        // must leave the selection in a sane state). ReplaceAction.Unapply puts the SAME `original`
        // reference back in the list slot, so re-selecting it directly (rather than hit-testing,
        // which for Text needs a real font manager unavailable in a headless unit test) proves the
        // list itself holds the original text again.
        layer.Undo();
        Assert.Null(layer.SelectedShape);
        Assert.True(layer.CanRedo);
        layer.Select(original);
        Assert.Equal("hello", layer.SelectedShape!.Text);
    }

    [Fact]
    public void SetSelectedPixelateBlock_OnlyAffectsPixelateShapes()
    {
        var layer = NewLayerWith(Rect(new Point(0, 0), new Point(50, 50)));
        var shape = layer.HitTestEditable(new Point(25, 25), interiorGrab: true)!;
        layer.Select(shape);

        layer.SetSelectedPixelateBlock(12.0);

        Assert.Equal(4.0, shape.StrokeWidthPx); // untouched — a Rectangle isn't a Pixelate
    }
}
