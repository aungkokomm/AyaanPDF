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

        public HistoryEntry Capture(HistoryScope scope, string label) => new()
        {
            Scope = scope,
            Label = label,
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
            h.Push(Capture(HistoryScope.Annotations, mark));
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
