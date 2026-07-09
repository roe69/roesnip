using RoeSnip.Overlay;
using Xunit;

namespace RoeSnip.Tests;

/// <summary>Feature C: the pure, WPF-free action-based undo/redo model behind AnnotationLayer's
/// shape list. Tested against a plain string payload (not AnnotationShape) to prove the class is
/// genuinely generic/WPF-independent, matching the rest of Overlay/*'s pure-helper convention (see
/// BoundedColorList, SizeInput).</summary>
public class AnnotationHistoryTests
{
    [Fact]
    public void NewHistory_HasNothingToUndoOrRedo()
    {
        var history = new AnnotationHistory<string>();
        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);
        Assert.Empty(history.Shapes);
    }

    [Fact]
    public void Add_AppendsAtTheEnd_AndBecomesUndoable()
    {
        var history = new AnnotationHistory<string>();
        history.Add("a");
        history.Add("b");

        Assert.Equal(new[] { "a", "b" }, history.Shapes);
        Assert.True(history.CanUndo);
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void Undo_OfAdd_RemovesTheJustAddedShape()
    {
        var history = new AnnotationHistory<string>();
        history.Add("a");
        history.Add("b");

        Assert.True(history.Undo());

        Assert.Equal(new[] { "a" }, history.Shapes);
        Assert.True(history.CanUndo);
        Assert.True(history.CanRedo);
    }

    [Fact]
    public void Redo_OfAdd_ReappliesTheShapeAtTheSamePosition()
    {
        var history = new AnnotationHistory<string>();
        history.Add("a");
        history.Add("b");
        history.Undo();

        Assert.True(history.Redo());

        Assert.Equal(new[] { "a", "b" }, history.Shapes);
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void Undo_WithNothingToUndo_ReturnsFalseAndIsANoOp()
    {
        var history = new AnnotationHistory<string>();
        Assert.False(history.Undo());
        Assert.Empty(history.Shapes);
    }

    [Fact]
    public void Redo_WithNothingToRedo_ReturnsFalseAndIsANoOp()
    {
        var history = new AnnotationHistory<string>();
        history.Add("a");
        Assert.False(history.Redo());
        Assert.Equal(new[] { "a" }, history.Shapes);
    }

    [Fact]
    public void NewAction_AfterUndo_ClearsTheRedoBranch()
    {
        var history = new AnnotationHistory<string>();
        history.Add("a");
        history.Add("b");
        history.Undo(); // redo now holds "b"
        Assert.True(history.CanRedo);

        history.Add("c"); // a fresh action forks history — "b" is gone for good

        Assert.False(history.CanRedo);
        Assert.Equal(new[] { "a", "c" }, history.Shapes);
    }

    [Fact]
    public void Remove_DeletesAtTheGivenIndex()
    {
        var history = new AnnotationHistory<string>();
        history.Add("a");
        history.Add("b");
        history.Add("c");

        history.Remove("b", 1);

        Assert.Equal(new[] { "a", "c" }, history.Shapes);
    }

    [Fact]
    public void Undo_OfRemove_ReinsertsAtTheOriginalIndex_NotAtTheEnd()
    {
        var history = new AnnotationHistory<string>();
        history.Add("a");
        history.Add("b");
        history.Add("c");

        history.Remove("b", 1);
        Assert.True(history.Undo());

        // "b" must land back BETWEEN "a" and "c" — reinserting at the end would silently reorder
        // draw/overlap order (topmost == most recently drawn depends on list order).
        Assert.Equal(new[] { "a", "b", "c" }, history.Shapes);
    }

    [Fact]
    public void Redo_OfRemove_DeletesAgain()
    {
        var history = new AnnotationHistory<string>();
        history.Add("a");
        history.Add("b");
        history.Remove("b", 1);
        history.Undo();

        Assert.True(history.Redo());

        Assert.Equal(new[] { "a" }, history.Shapes);
    }

    [Fact]
    public void Replace_SwapsTheShapeAtTheIndex()
    {
        var history = new AnnotationHistory<string>();
        history.Add("a");
        history.Add("b-before");

        history.Replace(1, "b-before", "b-after");

        Assert.Equal(new[] { "a", "b-after" }, history.Shapes);
    }

    [Fact]
    public void Undo_OfReplace_RestoresTheBeforeValue()
    {
        var history = new AnnotationHistory<string>();
        history.Add("a");
        history.Add("b-before");
        history.Replace(1, "b-before", "b-after");

        Assert.True(history.Undo());

        Assert.Equal(new[] { "a", "b-before" }, history.Shapes);
    }

    [Fact]
    public void Redo_OfReplace_ReappliesTheAfterValue()
    {
        var history = new AnnotationHistory<string>();
        history.Add("a");
        history.Add("b-before");
        history.Replace(1, "b-before", "b-after");
        history.Undo();

        Assert.True(history.Redo());

        Assert.Equal(new[] { "a", "b-after" }, history.Shapes);
    }

    [Fact]
    public void MultipleUndo_UnwindsInReverseOrder()
    {
        var history = new AnnotationHistory<string>();
        history.Add("a");
        history.Add("b");
        history.Remove("b", 1);
        history.Replace(0, "a", "a2");

        Assert.Equal(new[] { "a2" }, history.Shapes);

        Assert.True(history.Undo()); // undoes the Replace
        Assert.Equal(new[] { "a" }, history.Shapes);

        Assert.True(history.Undo()); // undoes the Remove
        Assert.Equal(new[] { "a", "b" }, history.Shapes);

        Assert.True(history.Undo()); // undoes the second Add
        Assert.Equal(new[] { "a" }, history.Shapes);

        Assert.True(history.Undo()); // undoes the first Add
        Assert.Empty(history.Shapes);

        Assert.False(history.CanUndo);
    }

    [Fact]
    public void Clear_WipesShapesAndBothStacks()
    {
        var history = new AnnotationHistory<string>();
        history.Add("a");
        history.Add("b");
        history.Undo();

        history.Clear();

        Assert.Empty(history.Shapes);
        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void IndexOf_UsesReferenceEqualityForDuplicateContent()
    {
        // Two distinct object references with equal content — IndexOf must resolve the SPECIFIC
        // instance a caller (AnnotationLayer's move/resize/delete/re-edit) is holding, not just any
        // shape that happens to look the same.
        var shapeA = new object();
        var history = new AnnotationHistory<object>();
        history.Add(shapeA);
        history.Add(new object());

        Assert.Equal(0, history.IndexOf(shapeA));
    }
}
