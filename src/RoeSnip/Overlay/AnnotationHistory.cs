using System.Collections.Generic;

namespace RoeSnip.Overlay;

/// <summary>Pure, WPF-free action-based undo/redo history — the model AnnotationLayer's shape list
/// is built on (Feature C). Generic over the shape type purely so this file has zero dependency on
/// System.Windows types (AnnotationShape, in AnnotationLayer.cs, is built from WPF Point/Color) and
/// is unit-testable in isolation, matching the rest of Overlay/*'s pure-helper convention (see
/// BoundedColorList, SizeInput).
///
/// Three action kinds cover everything AnnotationLayer needs: <see cref="Add"/> (a newly committed
/// shape), <see cref="Remove"/> (a deleted shape — remembers its list index so Undo re-inserts it in
/// the same position instead of at the end, which would silently reorder draw/overlap order), and
/// <see cref="Replace"/> (an existing shape's list slot swapped for a mutated version — move/resize
/// drags, wheel font-resize, and text re-edit commits all funnel through this one). Undo pops the
/// most recent action and applies its inverse; Redo re-applies it. Any new action clears the redo
/// stack, exactly like the two-list Undo/Redo stack this replaces.</summary>
public sealed class AnnotationHistory<T>
{
    private abstract class HistoryAction
    {
        public abstract void Apply(List<T> shapes);
        public abstract void Unapply(List<T> shapes);
    }

    private sealed class AddAction : HistoryAction
    {
        private readonly T _shape;
        private readonly int _index;
        public AddAction(T shape, int index) { _shape = shape; _index = index; }
        public override void Apply(List<T> shapes) => shapes.Insert(_index, _shape);
        public override void Unapply(List<T> shapes) => shapes.RemoveAt(_index);
    }

    private sealed class RemoveAction : HistoryAction
    {
        private readonly T _shape;
        private readonly int _index;
        public RemoveAction(T shape, int index) { _shape = shape; _index = index; }
        public override void Apply(List<T> shapes) => shapes.RemoveAt(_index);
        public override void Unapply(List<T> shapes) => shapes.Insert(_index, _shape);
    }

    private sealed class ReplaceAction : HistoryAction
    {
        private readonly int _index;
        private readonly T _before;
        private readonly T _after;
        public ReplaceAction(int index, T before, T after) { _index = index; _before = before; _after = after; }
        public override void Apply(List<T> shapes) => shapes[_index] = _after;
        public override void Unapply(List<T> shapes) => shapes[_index] = _before;
    }

    private readonly List<T> _shapes = new();
    private readonly List<HistoryAction> _undoStack = new();
    private readonly List<HistoryAction> _redoStack = new(); // actions popped by Undo, newest last

    public IReadOnlyList<T> Shapes => _shapes;
    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    /// <summary>Reference-equality lookup (List{T}.IndexOf via the default comparer — AnnotationShape
    /// never overrides Equals) — exposed because IReadOnlyList{T} (unlike List{T}/IList{T}) has no
    /// IndexOf of its own, and callers (AnnotationLayer's move/resize/replace/delete) need a shape's
    /// current list position to build a Replace/Remove action.</summary>
    public int IndexOf(T shape) => _shapes.IndexOf(shape);

    /// <summary>Appends a newly committed shape at the end of the list (the drawing order every
    /// caller already relies on for "topmost == most recently drawn").</summary>
    public void Add(T shape) => Perform(new AddAction(shape, _shapes.Count));

    /// <summary>Removes a shape at a caller-supplied index (the caller already knows it, e.g. via
    /// <see cref="Shapes"/>.IndexOf) so Undo can reinsert it in exactly the same list position.</summary>
    public void Remove(T shape, int index) => Perform(new RemoveAction(shape, index));

    /// <summary>Swaps the shape at <paramref name="index"/> from <paramref name="before"/> to
    /// <paramref name="after"/> — the one action kind every in-place edit (move, resize, wheel
    /// font-resize, text re-edit commit) funnels through.</summary>
    public void Replace(int index, T before, T after) => Perform(new ReplaceAction(index, before, after));

    private void Perform(HistoryAction action)
    {
        action.Apply(_shapes);
        _undoStack.Add(action);
        _redoStack.Clear(); // a fresh action forks history — the undone branch is gone
    }

    /// <summary>Returns false (a no-op) when there is nothing to undo.</summary>
    public bool Undo()
    {
        if (_undoStack.Count == 0)
        {
            return false;
        }
        var action = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        action.Unapply(_shapes);
        _redoStack.Add(action);
        return true;
    }

    /// <summary>Returns false (a no-op) when there is nothing to redo.</summary>
    public bool Redo()
    {
        if (_redoStack.Count == 0)
        {
            return false;
        }
        var action = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        action.Apply(_shapes);
        _undoStack.Add(action);
        return true;
    }

    public void Clear()
    {
        _shapes.Clear();
        _undoStack.Clear();
        _redoStack.Clear();
    }
}
