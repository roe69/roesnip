using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

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
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;
using Size = System.Windows.Size;
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
    /// <summary>Translucent wide marker stroke (freehand gesture).</summary>
    Highlight,
    /// <summary>Censor rectangle: mosaics the preview pixels under it. StrokeWidthPx doubles as
    /// the mosaic block size (same scroll-wheel/size-box plumbing as the stroke tools).</summary>
    Pixelate,
}

/// <summary>One committed (or in-progress) annotation shape. All points are physical pixels,
/// monitor-local — the same coordinate space as the CapturedFrame/SdrImage for that monitor.
/// Never DIPs; DIP conversion happens only where AnnotationLayer renders to screen.</summary>
public sealed class AnnotationShape
{
    public AnnotationTool Tool { get; init; }
    public List<Point> PointsPx { get; init; } = new();
    public Color StrokeColor { get; init; } = Colors.Red;

    // Deliberately "set" rather than "init": Feature B's select/move/resize (drag-resize a placed
    // Pixelate) and wheel font-resize (a selected Text annotation) mutate this and PointsPx IN
    // PLACE on the live shape while a gesture is in progress — AnnotationLayer snapshots a deep
    // Clone() before the gesture starts and diffs against it at gesture-end to build the one
    // Replace history action the gesture produces (see AnnotationLayer.CommitPendingDrag).
    public double StrokeWidthPx { get; set; } = 3.0;
    public string Text { get; set; } = string.Empty;

    // Text-only styling (item 4) — unused by every other tool. StrokeWidthPx doubles as the text's
    // font size (matching the pre-existing convention CommitText already used).
    public string TextFontFamily { get; init; } = "Segoe UI";
    public bool TextBold { get; init; }
    public bool TextItalic { get; init; }

    /// <summary>Deep clone — PointsPx is a mutable List, so a shallow copy would alias the same list
    /// between the clone and the original. Feature C's "snapshot before a drag, diff after" undo
    /// model only works if a later in-place mutation of the live shape can't also silently mutate
    /// the snapshot taken before it started.</summary>
    public AnnotationShape Clone() => new()
    {
        Tool = Tool,
        PointsPx = new List<Point>(PointsPx),
        StrokeColor = StrokeColor,
        StrokeWidthPx = StrokeWidthPx,
        Text = Text,
        TextFontFamily = TextFontFamily,
        TextBold = TextBold,
        TextItalic = TextItalic,
    };
}

/// <summary>Vector annotation shapes drawn over an overlay's frozen preview. Renders itself scaled
/// down to DIPs for on-screen display (via <see cref="DeviceScaleX"/>/<see cref="DeviceScaleY"/>,
/// kept in sync by the owning OverlayWindow from CompositionTarget.TransformToDevice), and
/// separately offers <see cref="RenderForExport"/> for 1:1 physical-pixel rasterization at export
/// time. Maintains a linear undo/redo action history (Feature C, <see cref="AnnotationHistory{T}"/>)
/// — Ctrl+Z pops the most recently performed action (Add/Remove/Replace) and applies its inverse;
/// Ctrl+Y/Ctrl+Shift+Z re-applies it; a fresh action clears the redo branch.
///
/// Feature B (Select tool: editable Pixelate/Text) also lives here: <see cref="SelectedShape"/> plus
/// the hit-test/drag/replace surface OverlayWindow drives from its mouse/keyboard handlers. All
/// selection/handle/drag chrome is drawn in a screen-only pass at the end of <see cref="OnRender"/>
/// — never in <see cref="RenderForExport"/>, so it can never leak into an exported/burned-in image.</summary>
public sealed class AnnotationLayer : FrameworkElement
{
    private readonly AnnotationHistory<AnnotationShape> _history = new();
    private AnnotationShape? _inProgress;

    /// <summary>The pre-gesture deep clone for whatever move/resize/wheel-font-resize gesture is
    /// currently open on <see cref="SelectedShape"/> — null when no gesture is open. Set by
    /// <see cref="BeginDragSelected"/>, consumed (diffed against the now-mutated live shape and
    /// turned into one Replace action, or discarded if nothing actually changed) by
    /// <see cref="CommitPendingDrag"/>.</summary>
    private AnnotationShape? _dragSnapshotBefore;

    private const double HandleHitPaddingPx = 7.0; // mirrors SelectionAdorner's own handle padding
    private static readonly Color ChromeGold = Color.FromRgb(0xFF, 0x6B, 0x35); // roeshare primary (orange) accent

    /// <summary>1 physical pixel == 1/DeviceScaleX (or Y) DIPs on this window's monitor.</summary>
    public double DeviceScaleX { get; set; } = 1.0;
    public double DeviceScaleY { get; set; } = 1.0;

    /// <summary>The monitor's frozen tone-mapped preview at 1:1 physical pixels — the pixels the
    /// Pixelate tool mosaics. Set once by OverlayWindow (OnSourceInitialized); Pixelate degrades
    /// to drawing nothing if it was never provided.</summary>
    public BitmapSource? PreviewSource { get; set; }

    public bool HasAnnotations => _history.Shapes.Count > 0;

    public bool CanUndo => _history.CanUndo;
    public bool CanRedo => _history.CanRedo;

    /// <summary>The editable annotation (Pixelate/Text/Rectangle/Ellipse) currently selected via
    /// the Select tool (Feature B) — null when nothing is selected. Change it only through
    /// <see cref="Select"/>/<see cref="Deselect"/>.</summary>
    public AnnotationShape? SelectedShape { get; private set; }

    /// <summary>Set by OverlayWindow while a placed text annotation's inline re-edit TextBox floats
    /// over it (item 4) — the editor already shows the (possibly modified) live text, so this
    /// shape's normal render is skipped for the duration or the user would see doubled text.</summary>
    public AnnotationShape? SuppressedFromRender { get; set; }

    /// <summary>Raised whenever the undo/redo history changes shape (shape committed, undone,
    /// redone, or everything cleared) — OverlayWindow relays CanUndo/CanRedo to the toolbar so
    /// the Undo/Redo buttons gray out exactly when they would be no-ops.</summary>
    public event Action? HistoryChanged;

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
        if (tool is not (AnnotationTool.Freehand or AnnotationTool.Highlight))
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

        if (_inProgress.Tool is AnnotationTool.Freehand or AnnotationTool.Highlight)
        {
            _inProgress.PointsPx.Add(physicalPt);
        }
        else if (_inProgress.PointsPx.Count >= 2)
        {
            _inProgress.PointsPx[1] = physicalPt;
        }
        InvalidateVisual();
    }

    /// <summary>Commits the in-progress drag shape. Returns the committed shape (null for a
    /// degenerate/no-op gesture) so OverlayWindow can auto-select a just-placed Pixelate — its
    /// selection chrome appearing immediately is what tells the user the shape is grabbable.</summary>
    public AnnotationShape? EndShape()
    {
        if (_inProgress is null)
        {
            return null;
        }

        // Discard degenerate shapes (a click with no meaningful drag).
        bool isDegenerate = _inProgress.Tool is not (AnnotationTool.Freehand or AnnotationTool.Highlight)
            && _inProgress.PointsPx.Count >= 2
            && (_inProgress.PointsPx[0] - _inProgress.PointsPx[1]).LengthSquared < 4.0;

        AnnotationShape? committed = null;
        if (!isDegenerate)
        {
            _history.Add(_inProgress);
            committed = _inProgress;
            HistoryChanged?.Invoke();
        }
        _inProgress = null;
        InvalidateVisual();
        return committed;
    }

    public void CancelShape()
    {
        _inProgress = null;
        InvalidateVisual();
    }

    /// <summary>Commits a completed text annotation. Used by OverlayWindow's inline text editor
    /// (a real WPF TextBox, for native IME support) rather than the Begin/Update/EndShape drag
    /// pipeline, since text entry isn't a drag gesture. fontFamily/bold/italic (item 4) default to
    /// the pre-existing plain style so any other caller keeps working unchanged. This always ADDS a
    /// new shape — re-editing an existing text annotation (item 4) goes through
    /// <see cref="ReplaceShape"/> instead, driven by OverlayWindow's text-editor machinery. Returns
    /// the committed shape (null for the degenerate empty-text no-op) mirroring <see cref="EndShape"/>'s
    /// contract, so OverlayWindow can auto-select a just-typed text exactly like every other
    /// just-placed click-editable shape (item 1 of the second editing round).</summary>
    public AnnotationShape? CommitText(
        Point physicalPt, string text, Color color, double fontSizePx,
        string fontFamily = "Segoe UI", bool bold = false, bool italic = false)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
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
        _history.Add(shape);
        HistoryChanged?.Invoke();
        InvalidateVisual();
        return shape;
    }

    public void Undo()
    {
        // Spec (Feature B/C): undo/redo of a Replace that targets the selected shape must leave the
        // selection in a sane state — simplest is to always deselect first. This also flushes any
        // pending-but-uncommitted gesture (e.g. mid wheel font-resize) into its own Replace action
        // first, so a single Ctrl+Z fully reverts the whole gesture rather than needing one press per
        // wheel notch.
        Deselect();
        if (!_history.Undo())
        {
            return;
        }
        HistoryChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>Re-applies the most recently undone action (Ctrl+Y / Ctrl+Shift+Z / toolbar Redo).
    /// No-op when nothing was undone; the redo branch empties whenever a NEW action is performed.</summary>
    public void Redo()
    {
        Deselect();
        if (!_history.Redo())
        {
            return;
        }
        HistoryChanged?.Invoke();
        InvalidateVisual();
    }

    public void Clear()
    {
        _history.Clear();
        _inProgress = null;
        SelectedShape = null;
        _dragSnapshotBefore = null; // discarded, not committed — the whole history is being wiped anyway
        HistoryChanged?.Invoke();
        InvalidateVisual();
    }

    // ---------- Select tool: editable annotations (Feature B) ----------

    /// <summary>Which shape kinds are selectable/editable via the Select tool — centralized here so
    /// extending the set later is a one-line change instead of scattered Tool == checks throughout
    /// this file and OverlayWindow.</summary>
    private static bool IsEditable(AnnotationTool tool) =>
        tool is AnnotationTool.Pixelate or AnnotationTool.Text
             or AnnotationTool.Rectangle or AnnotationTool.Ellipse
             or AnnotationTool.Line or AnnotationTool.Arrow;

    /// <summary>The editable kinds whose geometry IS a two-corner rect — these get the 8 resize
    /// handles and the generic rect-resize drag (Text is border-only chrome: its size is
    /// font-driven, not handle-draggable; Line/Arrow are movable-only: their geometry is a
    /// segment, and rect handles would distort rather than resize it).</summary>
    internal static bool IsRectResizable(AnnotationTool tool) =>
        tool is AnnotationTool.Pixelate or AnnotationTool.Rectangle or AnnotationTool.Ellipse;

    /// <summary>The shape kinds that get auto-selected the moment they're placed/committed (so
    /// their chrome/handles appear immediately). Clicking an already-placed shape now edits it
    /// under EVERY tool (editsExistingShape dropped its original same-kind gate — a placed object
    /// must always be grabbable no matter which tool is active; tool-scoping only clears STALE
    /// selections on a tool switch), so this predicate's remaining job is the placement
    /// auto-select: Freehand/Highlight stay out because they aren't editable shapes at all.</summary>
    internal static bool IsClickEditableTool(AnnotationTool tool) =>
        IsRectResizable(tool) || tool is AnnotationTool.Line or AnnotationTool.Arrow or AnnotationTool.Text;

    /// <summary>The shape's screen/export bounds, used for hit-testing and selection chrome —
    /// the rect-resizable kinds are just their two corner points; Text is measured the same way
    /// <see cref="Draw"/> lays it out (via FormattedText), since PointsPx only stores its origin.</summary>
    private static Rect? EditableBounds(AnnotationShape shape) => shape.Tool switch
    {
        _ when IsRectResizable(shape.Tool) && shape.PointsPx.Count >= 2 =>
            new Rect(shape.PointsPx[0], shape.PointsPx[1]),
        AnnotationTool.Line or AnnotationTool.Arrow when shape.PointsPx.Count >= 2 =>
            new Rect(shape.PointsPx[0], shape.PointsPx[1]),
        AnnotationTool.Text when shape.PointsPx.Count >= 1 && !string.IsNullOrEmpty(shape.Text) =>
            MeasureTextBounds(shape),
        _ => null,
    };

    /// <summary>Body hit-test tolerance around an outline shape's stroke, over and above half the
    /// stroke width — generous enough to grab a hairline rectangle without hunting for the exact
    /// pixel, tight enough that the interior stays free for other clicks.</summary>
    private const double OutlineHitPaddingPx = 5.0;

    /// <summary>Per-kind body hit rule. Pixelate/Text are FILLED areas, so anywhere inside their
    /// bounds counts. Rectangle/Ellipse are OUTLINES: only a click near the stroke itself counts —
    /// bounds-contains would make a box drawn around content swallow every interior click (you
    /// could no longer draw a second overlapping shape starting inside it, and the Select tool
    /// would grab the frame when aiming at what it frames). <paramref name="interiorGrab"/>
    /// (Shift/Ctrl held — OverlayWindow's modifier-grab) deliberately relaxes exactly that: the
    /// user is explicitly asking to grab, so the outline kinds hit on their whole interior too
    /// (segments keep the distance test — a diagonal line's bounding box is mostly empty space
    /// even for a deliberate grab).</summary>
    private static bool HitsShapeBody(AnnotationShape shape, Rect bounds, Point pt, bool interiorGrab)
    {
        // Line/Arrow: a segment, so "body" is proximity to the segment itself — the bounding box
        // of a diagonal line is mostly empty space and must not swallow clicks. The arrow head is
        // wider than the shaft, but the padding already covers grabbing it comfortably.
        if (shape.Tool is AnnotationTool.Line or AnnotationTool.Arrow)
        {
            return shape.PointsPx.Count >= 2
                && DistanceToSegment(pt, shape.PointsPx[0], shape.PointsPx[1])
                    <= shape.StrokeWidthPx / 2.0 + OutlineHitPaddingPx;
        }

        if (interiorGrab || shape.Tool is not (AnnotationTool.Rectangle or AnnotationTool.Ellipse))
        {
            return bounds.Contains(pt);
        }

        double t = shape.StrokeWidthPx / 2.0 + OutlineHitPaddingPx;
        if (shape.Tool == AnnotationTool.Rectangle)
        {
            var outer = Rect.Inflate(bounds, t, t);
            if (!outer.Contains(pt))
            {
                return false;
            }
            double innerW = bounds.Width - 2 * t, innerH = bounds.Height - 2 * t;
            if (innerW <= 0 || innerH <= 0)
            {
                return true; // stroke rings overlap — the whole (small) shape is grabbable
            }
            return !new Rect(bounds.X + t, bounds.Y + t, innerW, innerH).Contains(pt);
        }

        // Ellipse: inside the (rx+t, ry+t) ellipse but outside the (rx-t, ry-t) one.
        double cx = bounds.X + bounds.Width / 2.0, cy = bounds.Y + bounds.Height / 2.0;
        double rx = bounds.Width / 2.0, ry = bounds.Height / 2.0;
        double dx = pt.X - cx, dy = pt.Y - cy;
        double NormSq(double a, double b) =>
            a <= 0 || b <= 0 ? double.PositiveInfinity : (dx * dx) / (a * a) + (dy * dy) / (b * b);
        if (NormSq(rx + t, ry + t) > 1.0)
        {
            return false;
        }
        return NormSq(rx - t, ry - t) >= 1.0;
    }

    /// <summary>Plain point-to-segment distance (the Line/Arrow body hit test).</summary>
    private static double DistanceToSegment(Point pt, Point a, Point b)
    {
        var ab = b - a;
        double lengthSq = ab.LengthSquared;
        if (lengthSq < 1e-9)
        {
            return (pt - a).Length; // degenerate segment — distance to the single point
        }
        double u = Math.Clamp(((pt - a) * ab) / lengthSq, 0.0, 1.0);
        return (pt - (a + ab * u)).Length;
    }

    private static Rect MeasureTextBounds(AnnotationShape shape)
    {
        var formatted = BuildTextFormattedText(shape, Brushes.Black); // brush is irrelevant to metrics
        return new Rect(shape.PointsPx[0], new Size(formatted.Width, formatted.Height));
    }

    /// <summary>Topmost (most recently drawn) editable shape whose bounds contain a physical-pixel
    /// point — the Select tool's click/hover hit test (OverlayWindow's item 7/8 priority chains).
    /// Never changes the selection itself; callers decide what a hit means.
    /// <paramref name="interiorGrab"/> = the Shift/Ctrl modifier-grab: outline interiors count as
    /// hits too (see <see cref="HitsShapeBody"/>).</summary>
    public AnnotationShape? HitTestEditable(Point physicalPt, bool interiorGrab = false)
    {
        var shapes = _history.Shapes;
        for (int i = shapes.Count - 1; i >= 0; i--)
        {
            var shape = shapes[i];
            if (IsEditable(shape.Tool) && EditableBounds(shape) is { } bounds
                && HitsShapeBody(shape, bounds, physicalPt, interiorGrab))
            {
                return shape;
            }
        }
        return null;
    }

    /// <summary>Hit-tests the CURRENTLY SELECTED shape's own resize handles — every rect-resizable
    /// kind has them (Text is border-only chrome per item 2); mirrors SelectionAdorner.HitTestHandle's
    /// 8-position corner/edge algorithm exactly, just against this shape's own bounds instead of the
    /// crop selection's. Deliberately never returns Body — a body hit is "start a move", which
    /// OverlayWindow's click priority routes through <see cref="HitTestEditable"/> instead.</summary>
    public SelectionHandle HitTestSelectedHandle(Point physicalPt)
    {
        if (SelectedShape is not { } shape || !IsRectResizable(shape.Tool) || shape.PointsPx.Count < 2)
        {
            return SelectionHandle.None;
        }

        var r = new Rect(shape.PointsPx[0], shape.PointsPx[1]);
        const double pad = HandleHitPaddingPx;

        bool nearLeft = Math.Abs(physicalPt.X - r.Left) <= pad;
        bool nearRight = Math.Abs(physicalPt.X - r.Right) <= pad;
        bool nearTop = Math.Abs(physicalPt.Y - r.Top) <= pad;
        bool nearBottom = Math.Abs(physicalPt.Y - r.Bottom) <= pad;
        bool withinX = physicalPt.X >= r.Left - pad && physicalPt.X <= r.Right + pad;
        bool withinY = physicalPt.Y >= r.Top - pad && physicalPt.Y <= r.Bottom + pad;

        if (nearLeft && nearTop) return SelectionHandle.TopLeft;
        if (nearRight && nearTop) return SelectionHandle.TopRight;
        if (nearLeft && nearBottom) return SelectionHandle.BottomLeft;
        if (nearRight && nearBottom) return SelectionHandle.BottomRight;
        if (nearTop && withinX) return SelectionHandle.Top;
        if (nearBottom && withinX) return SelectionHandle.Bottom;
        if (nearLeft && withinY) return SelectionHandle.Left;
        if (nearRight && withinY) return SelectionHandle.Right;

        return SelectionHandle.None;
    }

    /// <summary>Hit-tests the CURRENTLY SELECTED shape's two ENDPOINT handles (item 2 of the second
    /// editing round) — Line/Arrow's analogue of <see cref="HitTestSelectedHandle"/>'s rect corners:
    /// dragging an endpoint IS how these segment shapes "resize" (there's no rect to grow/shrink).
    /// Same hit tolerance as the rect handles. Returns -1 (no hit), 0 (P0 — the start point), or 1
    /// (P1 — the end point / arrowhead).</summary>
    public int HitTestSelectedEndpoint(Point physicalPt)
    {
        if (SelectedShape is not { } shape
            || shape.Tool is not (AnnotationTool.Line or AnnotationTool.Arrow)
            || shape.PointsPx.Count < 2)
        {
            return -1;
        }

        const double pad = HandleHitPaddingPx;
        if ((physicalPt - shape.PointsPx[0]).Length <= pad)
        {
            return 0;
        }
        if ((physicalPt - shape.PointsPx[1]).Length <= pad)
        {
            return 1;
        }
        return -1;
    }

    /// <summary>Changes the selection (Select-tool click, Esc/empty-space/new-gesture deselect, text
    /// re-edit). Flushes any pending uncommitted gesture on the OUTGOING selection first (item 9:
    /// "commit Replace when selection changes") so switching straight from one shape to another, or
    /// deselecting, never silently drops an in-flight wheel font-resize.</summary>
    public void Select(AnnotationShape? shape)
    {
        if (ReferenceEquals(SelectedShape, shape))
        {
            return;
        }
        CommitPendingDrag();
        SelectedShape = shape;
        InvalidateVisual();
    }

    public void Deselect() => Select(null);

    /// <summary>Opens a move/resize/wheel-font-resize gesture on the current selection by snapshotting
    /// it — idempotent within one gesture (repeated wheel notches must keep the ORIGINAL pre-gesture
    /// snapshot as the undo baseline, not the last notch's), so this is a no-op if a gesture is
    /// already open.</summary>
    public void BeginDragSelected()
    {
        if (_dragSnapshotBefore is not null)
        {
            return;
        }
        if (SelectedShape is { } shape)
        {
            _dragSnapshotBefore = shape.Clone();
        }
    }

    /// <summary>Closes whatever gesture <see cref="BeginDragSelected"/> opened (mouse-up for
    /// move/resize; OverlayWindow calls this on selection-change for the wheel-resize case via
    /// <see cref="Select"/> instead, since wheel gestures have no explicit end event).</summary>
    public void EndDragSelected() => CommitPendingDrag();

    private void CommitPendingDrag()
    {
        if (_dragSnapshotBefore is not { } before)
        {
            return;
        }
        _dragSnapshotBefore = null;

        if (SelectedShape is not { } shape || ShapeContentEquals(before, shape))
        {
            return; // no-op gesture (e.g. a 0-pixel move, or a click that never actually dragged)
        }

        int index = _history.IndexOf(shape);
        if (index < 0)
        {
            return; // the shape vanished from under the gesture somehow — nothing sane to record
        }

        var after = shape.Clone();
        _history.Replace(index, before, after);
        SelectedShape = after; // the list slot now holds 'after' — keep the selection pointing at it
        HistoryChanged?.Invoke();
        InvalidateVisual();
    }

    private static bool ShapeContentEquals(AnnotationShape a, AnnotationShape b)
    {
        if (a.PointsPx.Count != b.PointsPx.Count)
        {
            return false;
        }
        for (int i = 0; i < a.PointsPx.Count; i++)
        {
            if (a.PointsPx[i] != b.PointsPx[i])
            {
                return false;
            }
        }
        return a.StrokeWidthPx.Equals(b.StrokeWidthPx) && a.Text == b.Text && a.StrokeColor == b.StrokeColor
            && a.TextFontFamily == b.TextFontFamily && a.TextBold == b.TextBold && a.TextItalic == b.TextItalic;
    }

    /// <summary>Live-mutates the selected shape's points during a move drag (item 3): shifts every
    /// point of the immutable pre-drag snapshot by the delta measured from the drag anchor (not
    /// accumulated per-tick deltas — mirrors OverlayWindow's own crop-Move pattern, so mouse jitter
    /// can never accumulate rounding error), then clamps the whole shape's bounds — not each point
    /// independently — inside [0, frameSizePx] so a move can reposition but never resize it.</summary>
    public void TranslateSelected(Vector deltaFromAnchor, Size frameSizePx)
    {
        if (SelectedShape is not { } shape || _dragSnapshotBefore is not { } before)
        {
            return;
        }

        var moved = new List<Point>(before.PointsPx.Count);
        foreach (var p in before.PointsPx)
        {
            moved.Add(p + deltaFromAnchor);
        }

        var scratch = before.Clone();
        scratch.PointsPx.Clear();
        scratch.PointsPx.AddRange(moved);
        if (EditableBounds(scratch) is { } bounds)
        {
            double dx = 0, dy = 0;
            if (bounds.Left < 0) dx = -bounds.Left;
            else if (bounds.Right > frameSizePx.Width) dx = frameSizePx.Width - bounds.Right;
            if (bounds.Top < 0) dy = -bounds.Top;
            else if (bounds.Bottom > frameSizePx.Height) dy = frameSizePx.Height - bounds.Bottom;
            if (dx != 0 || dy != 0)
            {
                for (int i = 0; i < moved.Count; i++)
                {
                    moved[i] = new Point(moved[i].X + dx, moved[i].Y + dy);
                }
            }
        }

        shape.PointsPx.Clear();
        shape.PointsPx.AddRange(moved);
        InvalidateVisual();
    }

    /// <summary>Live-sets the selected rect-resizable shape's corner points during a resize drag
    /// (item 3) — a dumb setter; OverlayWindow computes the new corners by reusing its OWN
    /// ApplyResize/ClampToFrame (the exact same math the crop selection's own resize uses) and
    /// hands the already-clamped result down, per the design brief's explicit reuse instruction.</summary>
    public void SetSelectedRect(Point p0, Point p1)
    {
        if (SelectedShape is not { } shape || !IsRectResizable(shape.Tool))
        {
            return;
        }
        shape.PointsPx.Clear();
        shape.PointsPx.Add(p0);
        shape.PointsPx.Add(p1);
        InvalidateVisual();
    }

    /// <summary>Live-sets ONE endpoint of the selected Line/Arrow during an endpoint-drag resize
    /// (item 2) — the other endpoint stays fixed, exactly like dragging a single rect handle only
    /// moves that corner. A dumb setter, same contract as <see cref="SetSelectedRect"/>: OverlayWindow
    /// clamps the point to the frame before handing it down.</summary>
    public void SetSelectedEndpoint(int index, Point physicalPt)
    {
        if (SelectedShape is not { } shape
            || shape.Tool is not (AnnotationTool.Line or AnnotationTool.Arrow)
            || index is < 0 or > 1
            || shape.PointsPx.Count < 2)
        {
            return;
        }
        shape.PointsPx[index] = physicalPt;
        InvalidateVisual();
    }

    /// <summary>Live-sets the selected Rectangle/Ellipse/Line/Arrow's stroke width during a wheel
    /// gesture — the stroked shapes' analogue of the Pixelate block-size / Text font-size wheels,
    /// same BeginDragSelected/CommitPendingDrag lifecycle so the whole gesture undoes as one
    /// Replace.</summary>
    public void SetSelectedStrokeWidth(double strokeWidthPx)
    {
        if (SelectedShape is not { Tool: AnnotationTool.Rectangle or AnnotationTool.Ellipse
                                        or AnnotationTool.Line or AnnotationTool.Arrow } shape)
        {
            return;
        }
        shape.StrokeWidthPx = strokeWidthPx;
        InvalidateVisual();
    }

    /// <summary>Live-sets the selected Pixelate's block size during a wheel gesture — the mosaic
    /// coarseness knob (StrokeWidthPx doubles as the block size, see DrawPixelate). Same
    /// BeginDragSelected/CommitPendingDrag lifecycle as the text wheel-resize, so the whole wheel
    /// gesture undoes as one Replace.</summary>
    public void SetSelectedPixelateBlock(double blockPx)
    {
        if (SelectedShape is not { Tool: AnnotationTool.Pixelate } shape)
        {
            return;
        }
        shape.StrokeWidthPx = blockPx;
        InvalidateVisual();
    }

    /// <summary>Live-sets the selected Text's font size during a wheel gesture (item 9).
    /// StrokeWidthPx doubles as font size, matching every other text code path's convention.</summary>
    public void SetSelectedFontSize(double fontSizePx)
    {
        if (SelectedShape is not { Tool: AnnotationTool.Text } shape)
        {
            return;
        }
        shape.StrokeWidthPx = fontSizePx;
        InvalidateVisual();
    }

    /// <summary>Delete/Back with an annotation selected (item 5).</summary>
    public void DeleteSelected()
    {
        CommitPendingDrag();
        if (SelectedShape is not { } shape)
        {
            return;
        }
        int index = _history.IndexOf(shape);
        SelectedShape = null;
        if (index < 0)
        {
            InvalidateVisual();
            return;
        }
        _history.Remove(shape, index);
        HistoryChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>Text re-edit commit (item 4): swaps the original shape for a freshly built
    /// replacement as one Replace action, rather than an Add — undoing it restores the ORIGINAL
    /// text, not an empty canvas. Keeps the replacement selected (the just-edited text stays
    /// "picked", ready for another move/re-edit).</summary>
    public void ReplaceShape(AnnotationShape original, AnnotationShape replacement)
    {
        int index = _history.IndexOf(original);
        if (index < 0)
        {
            return; // the shape vanished from under the edit somehow — safest to no-op
        }
        // Resolve the index BEFORE flushing: CommitPendingDrag() can itself replace `original`'s
        // list slot (a wheel gesture left dangling on this exact shape by a double-click re-edit
        // that began without going through Select()'s normal flush — see the same-shape reference
        // fast path below) with a fresh clone, which would make a post-flush IndexOf(original) miss
        // and silently drop this whole edit. The slot's POSITION is stable across a Replace (only
        // Add/Remove shift indices), so re-reading whatever now actually sits at that index — rather
        // than trusting the possibly-stale `original` reference — keeps this correct either way and
        // never loses the caller's `replacement`.
        CommitPendingDrag();
        var current = index < _history.Shapes.Count ? _history.Shapes[index] : original;
        _history.Replace(index, current, replacement);
        SelectedShape = replacement;
        HistoryChanged?.Invoke();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.PushTransform(new ScaleTransform(1.0 / DeviceScaleX, 1.0 / DeviceScaleY));
        try
        {
            foreach (var shape in _history.Shapes)
            {
                if (ReferenceEquals(shape, SuppressedFromRender))
                {
                    continue; // its inline re-edit TextBox already shows this text live (item 4)
                }
                Draw(dc, shape, translate: default);
            }
            if (_inProgress is not null)
            {
                Draw(dc, _inProgress, translate: default);
                if (_inProgress.Tool == AnnotationTool.Pixelate && _inProgress.PointsPx.Count >= 2)
                {
                    // Feature A: a plain mosaic mid-drag is illegible against busy content — outline
                    // the in-progress region so the user can see exactly what they're about to
                    // censor. Screen-only chrome: RenderForExport never touches _inProgress at all
                    // (see its own doc comment below), so this can never leak into an export.
                    dc.DrawRectangle(null, BuildChromePen(), new Rect(_inProgress.PointsPx[0], _inProgress.PointsPx[1]));
                }
            }
            if (SelectedShape is { } selected && EditableBounds(selected) is { } selectedBounds)
            {
                DrawSelectionChrome(dc, selected, selectedBounds);
            }
        }
        finally
        {
            dc.Pop();
        }
    }

    // ---------- Screen-only chrome: Feature A's live-drag pixelate outline + Feature B's selection
    // chrome. Deliberately kept OUT of Draw/RenderForExport so nothing drawn here can ever leak into
    // an exported/burned-in image. Flat dashed line + flat filled squares — no gradients/glows. ----

    private Pen BuildChromePen()
    {
        // Physical-pixel space here (OnRender already pushed the 1/DeviceScale transform below), so
        // a pen thickness of DeviceScaleX renders as ~1 DIP on screen regardless of monitor scaling.
        double thicknessPx = Math.Max(1.0, DeviceScaleX);
        return new Pen(new SolidColorBrush(ChromeGold), thicknessPx)
        {
            DashStyle = new DashStyle(new double[] { 4, 3 }, 0),
            DashCap = PenLineCap.Flat,
        };
    }

    private void DrawSelectionChrome(DrawingContext dc, AnnotationShape shape, Rect bounds)
    {
        dc.DrawRectangle(null, BuildChromePen(), bounds);
        if (IsRectResizable(shape.Tool))
        {
            DrawSelectionHandles(dc, bounds, shape.StrokeColor);
        }
        else if (shape.Tool is AnnotationTool.Line or AnnotationTool.Arrow && shape.PointsPx.Count >= 2)
        {
            // Item 2 (second editing round): Line/Arrow have no rect to resize, so each ENDPOINT
            // gets its own handle instead — dragging one repositions that endpoint, the other stays
            // fixed. The dashed bounds box (drawn above, unconditionally) stays as the selection
            // outline even though it's not itself draggable for these two kinds.
            DrawHandleSquare(dc, shape.PointsPx[0], shape.StrokeColor);
            DrawHandleSquare(dc, shape.PointsPx[1], shape.StrokeColor);
        }
        // Text (item 2): border only — its size is font-size-driven, not handle-draggable.
    }

    /// <summary>8 flat square handles at the corners + edge midpoints — the same positions
    /// SelectionAdorner's crop handles hit-test, rendered as plain filled squares (no glow/gradient)
    /// per the design brief's "square resize handles" wording (distinct from the crop's own
    /// L-bracket style, which stays untouched).</summary>
    private void DrawSelectionHandles(DrawingContext dc, Rect r, Color strokeColor)
    {
        var positions = new[]
        {
            new Point(r.Left, r.Top), new Point(r.Left + r.Width / 2, r.Top), new Point(r.Right, r.Top),
            new Point(r.Left, r.Top + r.Height / 2), new Point(r.Right, r.Top + r.Height / 2),
            new Point(r.Left, r.Bottom), new Point(r.Left + r.Width / 2, r.Bottom), new Point(r.Right, r.Bottom),
        };
        foreach (var p in positions)
        {
            DrawHandleSquare(dc, p, strokeColor);
        }
    }

    /// <summary>One flat filled square handle (item 3, contrast fix): plain gold handles were
    /// invisible against gold/amber/orange shapes, so the fill is now the shape's own StrokeColor
    /// inverted — and, for colors whose inverse is barely different (near-mid-gray tones roughly
    /// invert to themselves), a plain black-or-white fallback chosen by the shape color's own
    /// luminance (see <see cref="HandleFillColor"/>). The border is always the OPPOSITE pole of the
    /// fill (black border on a light fill, white on a dark one) so the handle reads against any
    /// background behind it, not just against the shape it belongs to.</summary>
    private void DrawHandleSquare(DrawingContext dc, Point center, Color strokeColor)
    {
        double size = 8.0 * Math.Max(DeviceScaleX, 0.1);
        double half = size / 2.0;
        var fillColor = HandleFillColor(strokeColor);
        var fill = new SolidColorBrush(fillColor);
        var outline = new Pen(new SolidColorBrush(HandleBorderColor(fillColor)), Math.Max(1.0, DeviceScaleX * 0.5));
        dc.DrawRectangle(fill, outline, new Rect(center.X - half, center.Y - half, size, size));
    }

    /// <summary>The handle fill color for a shape with the given StrokeColor (item 3): the plain RGB
    /// inverse, unless that inverse is too close in luminance to the original (the near-mid-gray
    /// case — e.g. StrokeColor ~128,128,128 inverts to ~127,127,127, which is indistinguishable),
    /// in which case fall back to plain black or white chosen by the ORIGINAL color's luminance so
    /// the handle is guaranteed visible against the shape it's attached to.</summary>
    private static Color HandleFillColor(Color strokeColor)
    {
        var inverse = Color.FromRgb(
            (byte)(255 - strokeColor.R), (byte)(255 - strokeColor.G), (byte)(255 - strokeColor.B));
        double originalLuminance = RelativeLuminance(strokeColor);
        double inverseLuminance = RelativeLuminance(inverse);
        if (Math.Abs(originalLuminance - inverseLuminance) >= 0.3)
        {
            return inverse;
        }
        return originalLuminance < 0.5 ? Colors.White : Colors.Black;
    }

    /// <summary>The handle's 1px-ish border color: whichever of black/white is the OPPOSITE pole of
    /// the fill's own luminance, so the border reads against the fill itself (and, transitively,
    /// against backgrounds the fill alone wouldn't separate from).</summary>
    private static Color HandleBorderColor(Color fill) =>
        RelativeLuminance(fill) < 0.5 ? Colors.White : Colors.Black;

    /// <summary>WCAG relative luminance (sRGB) — a standard, cheap "how light does this read"
    /// metric; more than precise enough for a coarse contrast gate like this one.</summary>
    private static double RelativeLuminance(Color c)
    {
        static double Linearize(byte channel)
        {
            double s = channel / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linearize(c.R) + 0.7152 * Linearize(c.G) + 0.0722 * Linearize(c.B);
    }

    /// <summary>Rasterizes all committed shapes at 1:1 physical-pixel scale (no DIP scaling),
    /// translated so that <paramref name="originPx"/> maps to (0,0) — used by OverlayController
    /// when burning annotations into the exported crop. The in-progress (uncommitted) shape and all
    /// Feature A/B screen-only chrome are intentionally excluded — export only ever runs after
    /// EndShape/CommitText/ReplaceShape, with nothing selected or mid-drag at that point.</summary>
    public void RenderForExport(DrawingContext dc, Point originPx)
    {
        var translate = new Vector(-originPx.X, -originPx.Y);
        foreach (var shape in _history.Shapes)
        {
            Draw(dc, shape, translate);
        }
    }

    private void Draw(DrawingContext dc, AnnotationShape shape, Vector translate)
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
            case AnnotationTool.Highlight:
                if (shape.PointsPx.Count >= 2)
                {
                    if (shape.Tool == AnnotationTool.Highlight)
                    {
                        // Marker ink: same polyline as Freehand but translucent and much wider than
                        // the nominal stroke width (a highlighter's whole point is a broad band),
                        // with flat caps so overlapping strokes read as one swipe rather than dots.
                        var ink = shape.StrokeColor;
                        ink.A = 0x5A; // ~35% — text under it must stay legible
                        pen = new Pen(new SolidColorBrush(ink), Math.Max(6.0, shape.StrokeWidthPx * 3.0))
                        {
                            StartLineCap = PenLineCap.Flat,
                            EndLineCap = PenLineCap.Flat,
                            LineJoin = PenLineJoin.Round,
                        };
                    }
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

            case AnnotationTool.Pixelate:
                if (shape.PointsPx.Count >= 2)
                {
                    DrawPixelate(dc, shape, translate);
                }
                break;

            case AnnotationTool.Text:
                if (shape.PointsPx.Count >= 1 && !string.IsNullOrEmpty(shape.Text))
                {
                    var formatted = BuildTextFormattedText(shape, new SolidColorBrush(shape.StrokeColor));
                    dc.DrawText(formatted, P(0));
                }
                break;
        }
    }

    /// <summary>Shared FormattedText construction for both actually drawing a Text annotation
    /// (<see cref="Draw"/>) and merely measuring its bounds (<see cref="MeasureTextBounds"/>, for
    /// Feature B's hit-testing/selection chrome) — one place defining "how a text shape lays out".</summary>
    private static FormattedText BuildTextFormattedText(AnnotationShape shape, Brush foreground)
    {
        var typeface = new Typeface(
            new FontFamily(shape.TextFontFamily),
            shape.TextItalic ? FontStyles.Italic : FontStyles.Normal,
            shape.TextBold ? FontWeights.Bold : FontWeights.SemiBold,
            FontStretches.Normal);
        return new FormattedText(
            shape.Text,
            CultureInfo.InvariantCulture,
            FlowDir.LeftToRight,
            typeface,
            Math.Max(8.0, shape.StrokeWidthPx),
            foreground,
            1.0);
    }

    /// <summary>Mosaics the preview pixels under the shape's rect: crop the region out of
    /// <see cref="PreviewSource"/>, shrink it by the block size, and draw it back scaled up with
    /// NearestNeighbor (via a DrawingGroup — DrawingContext has no per-draw scaling-mode knob).
    /// Because the source is the frozen preview, this works identically on screen (OnRender, DIP
    /// transform already pushed) and at export (RenderForExport, 1:1) — both draw the same blocks
    /// at the same physical rect. StrokeWidthPx is the block size (min 3 px so it always censors).</summary>
    private void DrawPixelate(DrawingContext dc, AnnotationShape shape, Vector translate)
    {
        if (PreviewSource is not { } preview)
        {
            return;
        }

        var r = new Rect(shape.PointsPx[0], shape.PointsPx[1]);
        r.Intersect(new Rect(0, 0, preview.PixelWidth, preview.PixelHeight));
        int x = (int)r.X, y = (int)r.Y;
        int w = (int)r.Width, h = (int)r.Height;
        if (w < 4 || h < 4)
        {
            return;
        }

        // Block size capped at the region's short side so the shrunk bitmap never rounds to zero
        // pixels (a whole-region block just averages everything into one tile).
        double block = Math.Clamp(shape.StrokeWidthPx, 3.0, Math.Min(w, h));
        var cropped = new CroppedBitmap(preview, new Int32Rect(x, y, w, h));
        var shrunk = new TransformedBitmap(cropped, new ScaleTransform(1.0 / block, 1.0 / block));

        var target = new Rect(new Point(x, y) + translate, new Size(w, h));
        var group = new DrawingGroup();
        RenderOptions.SetBitmapScalingMode(group, BitmapScalingMode.NearestNeighbor);
        group.Children.Add(new ImageDrawing(shrunk, target));
        dc.DrawDrawing(group);
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
