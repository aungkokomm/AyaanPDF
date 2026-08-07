using System.Collections.Generic;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

public class DocumentHistoryTests
{
    // A tiny model of the view model's state, so the tests exercise the real
    // capture/apply round trip rather than the history stacks in isolation.
    private sealed class FakeDoc
    {
        public List<string> Marks { get; } = new();
        public bool Dirty { get; set; }

        // Takes the entry being reversed, not just its scope and label, since
        // a per-annotation step also needs to know which annotation.
        public HistoryEntry Capture(HistoryEntry target) => new()
        {
            Scope = target.Scope,
            Label = target.Label,
            WasDirty = Dirty,
            // Reuse the Notes list as a generic string carrier for the test.
            Notes = Marks.ConvertAll(m => new NoteState(0, 0, 0, m)),
        };

        public void Apply(HistoryEntry e)
        {
            Marks.Clear();
            foreach (var n in e.Notes)
            {
                Marks.Add(n.Text);
            }
            Dirty = e.WasDirty;
        }

        public void Edit(DocumentHistory h, string mark)
        {
            h.Push(Capture(new HistoryEntry { Scope = HistoryScope.Annotations, Label = mark }));
            Marks.Add(mark);
            Dirty = true;
        }
    }

    [Fact]
    public void undo_then_redo_returns_to_the_same_state()
    {
        var doc = new FakeDoc();
        var h = new DocumentHistory();

        doc.Edit(h, "a");
        doc.Edit(h, "b");
        Assert.Equal(new[] { "a", "b" }, doc.Marks);

        if (h.Undo(doc.Capture) is { } u) doc.Apply(u);
        Assert.Equal(new[] { "a" }, doc.Marks);

        if (h.Redo(doc.Capture) is { } r) doc.Apply(r);
        Assert.Equal(new[] { "a", "b" }, doc.Marks);
    }

    [Fact]
    public void a_new_edit_after_undo_clears_the_redo_stack()
    {
        var doc = new FakeDoc();
        var h = new DocumentHistory();

        doc.Edit(h, "a");
        doc.Edit(h, "b");
        if (h.Undo(doc.Capture) is { } u) doc.Apply(u);   // back to [a]
        Assert.True(h.CanRedo);

        doc.Edit(h, "c");                                  // branch
        Assert.False(h.CanRedo);
        Assert.Equal(new[] { "a", "c" }, doc.Marks);
    }

    [Fact]
    public void undo_past_the_beginning_is_a_no_op()
    {
        var doc = new FakeDoc();
        var h = new DocumentHistory();
        doc.Edit(h, "a");

        if (h.Undo(doc.Capture) is { } u) doc.Apply(u);
        Assert.False(h.CanUndo);
        Assert.Null(h.Undo(doc.Capture));                  // nothing captured
        Assert.True(h.CanRedo);                            // redo stack intact
        Assert.Empty(doc.Marks);
    }

    [Fact]
    public void repeated_undo_redo_stays_consistent()
    {
        var doc = new FakeDoc();
        var h = new DocumentHistory();
        doc.Edit(h, "a");
        doc.Edit(h, "b");
        doc.Edit(h, "c");

        for (int i = 0; i < 5; i++)
        {
            while (h.CanUndo)
            {
                if (h.Undo(doc.Capture) is { } u) doc.Apply(u);
            }
            Assert.Empty(doc.Marks);

            while (h.CanRedo)
            {
                if (h.Redo(doc.Capture) is { } r) doc.Apply(r);
            }
            Assert.Equal(new[] { "a", "b", "c" }, doc.Marks);
        }
    }

    [Fact]
    public void undoing_to_a_saved_state_restores_the_saved_dirty_flag()
    {
        var doc = new FakeDoc();
        var h = new DocumentHistory();

        // Edit, then "save": the state on disk is [a] and clean.
        doc.Edit(h, "a");
        doc.Dirty = false;

        // Another edit makes it dirty again.
        doc.Edit(h, "b");
        Assert.True(doc.Dirty);

        // Undoing back over that edit must return to the CLEAN [a] state,
        // not leave the user prompted to save what already matches disk.
        if (h.Undo(doc.Capture) is { } u) doc.Apply(u);
        Assert.Equal(new[] { "a" }, doc.Marks);
        Assert.False(doc.Dirty);
    }

    [Fact]
    public void count_bound_drops_the_oldest_but_keeps_the_most_recent()
    {
        var doc = new FakeDoc();
        var h = new DocumentHistory(maxEntries: 3);

        for (int i = 0; i < 10; i++)
        {
            doc.Edit(h, i.ToString());
        }

        Assert.Equal(3, h.UndoDepth);

        // The three most recent edits (7, 8, 9) must still undo cleanly.
        for (int i = 0; i < 3; i++)
        {
            Assert.True(h.CanUndo);
            if (h.Undo(doc.Capture) is { } u) doc.Apply(u);
        }
        Assert.False(h.CanUndo);
    }

    [Fact]
    public void byte_budget_keeps_at_least_the_latest_snapshot()
    {
        // A single document snapshot larger than the whole budget must remain
        // undoable rather than being trimmed to nothing.
        var h = new DocumentHistory(maxEntries: 50, byteBudget: 1024);
        h.Push(new HistoryEntry
        {
            Scope = HistoryScope.Document,
            Label = "huge",
            DocumentBytes = new byte[64 * 1024],
        });

        Assert.True(h.CanUndo);
        Assert.Equal(1, h.UndoDepth);
    }
}

/// <summary>
/// The cheap per-annotation undo step, whose whole point is not being a
/// document snapshot.
/// </summary>
public class AnnotationBoundsHistoryTests
{
    private static HistoryEntry BoundsEntry(int index, double left, double top) => new()
    {
        Scope = HistoryScope.AnnotationBounds,
        Label = "Move annotation",
        Bounds = [new AnnotationBoundsState(0, index, left, top, left + 0.2, top + 0.1, System.Guid.Empty)],
    };

    [Fact]
    public void a_bounds_step_costs_a_fraction_of_a_document_snapshot()
    {
        // The reason this scope exists. A 5MB PDF snapshotted for every nudge
        // fills the 256MB budget in about fifty drags; a rectangle is 64 bytes.
        var bounds = BoundsEntry(0, 0.2, 0.2);
        var snapshot = new HistoryEntry
        {
            Scope = HistoryScope.Document,
            DocumentBytes = new byte[5 * 1024 * 1024],
        };

        Assert.True(bounds.Cost < 1024, $"a bounds step costs {bounds.Cost} bytes");
        Assert.True(snapshot.Cost > bounds.Cost * 10_000,
                    "the comparison this scope exists to avoid should be enormous");
    }

    [Fact]
    public void many_drags_do_not_exhaust_the_history_budget()
    {
        // Dragging a stamp around is the most repeated edit there is, and each
        // drag used to record nothing at all; now it must record something
        // that a thousand repetitions still fit inside.
        var history = new DocumentHistory();
        for (int i = 0; i < 1000; i++)
        {
            history.Push(BoundsEntry(0, i / 1000.0, 0.3));
        }

        Assert.True(history.TotalCost < 1024 * 1024,
                    $"a thousand drags cost {history.TotalCost} bytes");
    }

    [Fact]
    public void undoing_a_bounds_step_hands_back_the_rectangle_to_restore()
    {
        var history = new DocumentHistory();
        history.Push(BoundsEntry(3, 0.20, 0.20));

        // Capture the inverse from the entry being reversed, which is what
        // makes redo possible without a snapshot.
        var target = history.Undo(t => new HistoryEntry
        {
            Scope = t.Scope,
            Label = t.Label,
            Bounds = [new AnnotationBoundsState(0, t.Bounds[0].Index, 0.50, 0.50, 0.70, 0.60, System.Guid.Empty)],
        });

        Assert.NotNull(target);
        Assert.Equal(HistoryScope.AnnotationBounds, target!.Scope);
        Assert.Equal(3, target.Bounds[0].Index);
        Assert.Equal(0.20, target.Bounds[0].Left, 6);

        // And redo goes back to where undo took it from.
        Assert.True(history.CanRedo);
        var back = history.Redo(t => t);
        Assert.Equal(0.50, back!.Bounds[0].Left, 6);
    }

    [Fact]
    public void a_bounds_step_carries_no_document_bytes()
    {
        // If one ever did, the scope would have quietly become a snapshot
        // again and the saving would be gone.
        Assert.Null(BoundsEntry(0, 0.1, 0.1).DocumentBytes);
    }
}
