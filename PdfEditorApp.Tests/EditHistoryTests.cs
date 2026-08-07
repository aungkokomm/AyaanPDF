using System;
using System.Collections.Generic;
using System.Linq;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The record-based history, exercised through the same two-stack machinery the
/// app uses.
///
/// A fake applier stands in for the document: it holds a set of object ids with
/// rectangles and tags, and the tests drive it with the same records the view
/// model emits. That covers the ordering, stack and batching rules, which is
/// where undo/redo actually goes wrong. What it cannot cover is the PDFium
/// writes themselves; those are pinned by the Rust suite (see
/// undoing_a_shape_move_puts_the_drawing_back).
/// </summary>
public class EditHistoryTests
{
    /// <summary>A stand-in document: id to (rect, tag), plus the grouping.</summary>
    private sealed class FakeDoc
    {
        public Dictionary<Guid, (EditRect Rect, string Tag)> Objects { get; } = new();
        public List<List<Guid>> Groups { get; } = new();

        public void Apply(HistoryEntry entry, bool backwards)
        {
            var order = Enumerable.Range(0, entry.Records.Count).ToList();
            if (backwards) { order.Reverse(); }

            foreach (int i in order)
            {
                switch (entry.Records[i])
                {
                    case BoundsRecord b:
                        if (Objects.TryGetValue(b.Id, out var cur))
                        {
                            Objects[b.Id] = (backwards ? b.Before : b.After, cur.Tag);
                        }
                        break;

                    case TagRecord t:
                        if (Objects.TryGetValue(t.Id, out var c2))
                        {
                            Objects[t.Id] = (c2.Rect, backwards ? t.BeforeTag : t.AfterTag);
                        }
                        break;

                    case ExistenceRecord e:
                        bool shouldExist = backwards ? !e.ExistsAfter : e.ExistsAfter;
                        if (shouldExist) { Objects[e.Id] = (e.Rect, e.Tag); }
                        else { Objects.Remove(e.Id); }
                        break;

                    case GroupsRecord g:
                        Groups.Clear();
                        foreach (var m in backwards ? g.Before : g.After)
                        {
                            Groups.Add(new List<Guid>(m));
                        }
                        break;
                }
            }
        }
    }

    private static HistoryEntry Entry(string label, params EditRecord[] records) =>
        new() { Scope = HistoryScope.Records, Label = label, Records = records };

    /// <summary>Mirrors the view model: a Records entry is its own inverse, so
    /// it goes onto the opposite stack unchanged.</summary>
    private static HistoryEntry Identity(HistoryEntry e) => e;

    private static EditRect R(double v) => new(v, v, v + 10, v + 10);

    private static ExistenceRecord Created(Guid id, double at) =>
        new(id, 0, "AyaanShape:0:FF0000FF:2.0000:1:1", R(at), ExistsAfter: true, Recoverable: true);

    [Fact]
    public void Create_then_undo_removes_it_and_redo_brings_it_back()
    {
        var doc = new FakeDoc();
        var history = new DocumentHistory();
        var id = Guid.NewGuid();

        var e = Entry("Draw shape", Created(id, 10));
        history.Push(e);
        doc.Apply(e, backwards: false);
        Assert.True(doc.Objects.ContainsKey(id));

        doc.Apply(history.Undo(Identity)!, backwards: true);
        Assert.False(doc.Objects.ContainsKey(id));

        doc.Apply(history.Redo(Identity)!, backwards: false);
        Assert.True(doc.Objects.ContainsKey(id));
    }

    [Fact]
    public void Delete_then_undo_restores_it_and_redo_removes_it_again()
    {
        var doc = new FakeDoc();
        var history = new DocumentHistory();
        var id = Guid.NewGuid();
        doc.Objects[id] = (R(5), "AyaanShape:0:00FF00FF:2.0000:1:1");

        var e = Entry("Delete annotation",
            new ExistenceRecord(id, 0, "AyaanShape:0:00FF00FF:2.0000:1:1", R(5),
                                ExistsAfter: false, Recoverable: true));
        history.Push(e);
        doc.Apply(e, backwards: false);
        Assert.False(doc.Objects.ContainsKey(id));

        doc.Apply(history.Undo(Identity)!, backwards: true);
        Assert.True(doc.Objects.ContainsKey(id));

        doc.Apply(history.Redo(Identity)!, backwards: false);
        Assert.False(doc.Objects.ContainsKey(id));
    }

    [Fact]
    public void Move_then_undo_returns_the_original_rectangle()
    {
        var doc = new FakeDoc();
        var history = new DocumentHistory();
        var id = Guid.NewGuid();
        doc.Objects[id] = (R(0), "t");

        var e = Entry("Move annotation", new BoundsRecord(id, 0, R(0), R(50)));
        history.Push(e);
        doc.Apply(e, backwards: false);
        Assert.Equal(R(50), doc.Objects[id].Rect);

        doc.Apply(history.Undo(Identity)!, backwards: true);
        Assert.Equal(R(0), doc.Objects[id].Rect);

        doc.Apply(history.Redo(Identity)!, backwards: false);
        Assert.Equal(R(50), doc.Objects[id].Rect);
    }

    [Fact]
    public void A_property_change_round_trips_through_the_tag()
    {
        var doc = new FakeDoc();
        var history = new DocumentHistory();
        var id = Guid.NewGuid();
        doc.Objects[id] = (R(0), "before-tag");

        var e = Entry("Shape fill", new TagRecord(id, 0, "before-tag", "after-tag", R(0)));
        history.Push(e);
        doc.Apply(e, backwards: false);
        Assert.Equal("after-tag", doc.Objects[id].Tag);

        doc.Apply(history.Undo(Identity)!, backwards: true);
        Assert.Equal("before-tag", doc.Objects[id].Tag);

        doc.Apply(history.Redo(Identity)!, backwards: false);
        Assert.Equal("after-tag", doc.Objects[id].Tag);
    }

    [Fact]
    public void Eight_creations_undo_all_the_way_back_and_redo_all_the_way_forward()
    {
        // The reported failure: draw several shapes, then undo repeatedly and
        // expect to walk back through every one of them.
        var doc = new FakeDoc();
        var history = new DocumentHistory();
        var ids = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToList();

        for (int i = 0; i < ids.Count; i++)
        {
            var e = Entry($"Draw shape {i}", Created(ids[i], i * 10));
            history.Push(e);
            doc.Apply(e, backwards: false);
        }
        Assert.Equal(8, doc.Objects.Count);

        for (int i = 8; i > 0; i--)
        {
            Assert.True(history.CanUndo);
            doc.Apply(history.Undo(Identity)!, backwards: true);
            Assert.Equal(i - 1, doc.Objects.Count);
        }

        Assert.False(history.CanUndo);

        for (int i = 1; i <= 8; i++)
        {
            Assert.True(history.CanRedo);
            doc.Apply(history.Redo(Identity)!, backwards: false);
            Assert.Equal(i, doc.Objects.Count);
        }

        Assert.False(history.CanRedo);
    }

    [Fact]
    public void A_multi_object_move_is_one_undo_step()
    {
        // A group drag touches many marks but must undo as ONE step, and must
        // return every one of them, not just the anchor.
        var doc = new FakeDoc();
        var history = new DocumentHistory();
        var ids = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToList();
        foreach (var id in ids) { doc.Objects[id] = (R(0), "t"); }

        var e = Entry("Move annotation",
            ids.Select(id => (EditRecord)new BoundsRecord(id, 0, R(0), R(40))).ToArray());
        history.Push(e);
        doc.Apply(e, backwards: false);
        Assert.All(ids, id => Assert.Equal(R(40), doc.Objects[id].Rect));

        Assert.Equal(1, history.UndoDepth);
        doc.Apply(history.Undo(Identity)!, backwards: true);
        Assert.All(ids, id => Assert.Equal(R(0), doc.Objects[id].Rect));
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void Group_and_ungroup_round_trip()
    {
        var doc = new FakeDoc();
        var history = new DocumentHistory();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var grouped = new List<IReadOnlyList<Guid>> { new List<Guid> { a, b } };
        var group = Entry("Group", new GroupsRecord([], grouped));
        history.Push(group);
        doc.Apply(group, backwards: false);
        Assert.Single(doc.Groups);

        doc.Apply(history.Undo(Identity)!, backwards: true);
        Assert.Empty(doc.Groups);

        doc.Apply(history.Redo(Identity)!, backwards: false);
        Assert.Single(doc.Groups);

        // ...and ungrouping from there is its own reversible step.
        var ungroup = Entry("Ungroup", new GroupsRecord(grouped, []));
        history.Push(ungroup);
        doc.Apply(ungroup, backwards: false);
        Assert.Empty(doc.Groups);

        doc.Apply(history.Undo(Identity)!, backwards: true);
        Assert.Single(doc.Groups);
    }

    [Fact]
    public void A_new_edit_after_undo_discards_the_redo_stack()
    {
        var doc = new FakeDoc();
        var history = new DocumentHistory();

        var first = Entry("Draw shape", Created(Guid.NewGuid(), 10));
        history.Push(first);
        doc.Apply(first, backwards: false);

        doc.Apply(history.Undo(Identity)!, backwards: true);
        Assert.True(history.CanRedo);

        var second = Entry("Draw shape", Created(Guid.NewGuid(), 20));
        history.Push(second);
        doc.Apply(second, backwards: false);

        Assert.False(history.CanRedo);
        Assert.True(history.CanUndo);
    }

    [Fact]
    public void Undo_and_redo_on_an_empty_history_do_nothing()
    {
        var history = new DocumentHistory();
        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);
        Assert.Null(history.Undo(Identity));
        Assert.Null(history.Redo(Identity));
    }

    [Fact]
    public void Interleaved_operations_undo_in_reverse_order()
    {
        var doc = new FakeDoc();
        var history = new DocumentHistory();
        var id = Guid.NewGuid();

        var create = Entry("Draw shape", Created(id, 0));
        history.Push(create);
        doc.Apply(create, backwards: false);

        var move = Entry("Move annotation", new BoundsRecord(id, 0, R(0), R(30)));
        history.Push(move);
        doc.Apply(move, backwards: false);

        var restyle = Entry("Shape fill", new TagRecord(id, 0, "old", "new", R(30)));
        history.Push(restyle);
        doc.Apply(restyle, backwards: false);

        // Newest first.
        doc.Apply(history.Undo(Identity)!, backwards: true);
        Assert.Equal("old", doc.Objects[id].Tag);

        doc.Apply(history.Undo(Identity)!, backwards: true);
        Assert.Equal(R(0), doc.Objects[id].Rect);

        doc.Apply(history.Undo(Identity)!, backwards: true);
        Assert.False(doc.Objects.ContainsKey(id));
    }

    [Fact]
    public void Three_different_operations_undo_and_redo_in_order()
    {
        // A, B, C of DIFFERENT kinds, then three undos and three redos. Mixed
        // kinds matter: a bug in one record type would otherwise hide behind
        // the others in a uniform sequence.
        var doc = new FakeDoc();
        var history = new DocumentHistory();
        var kept = Guid.NewGuid();
        doc.Objects[kept] = (R(0), "orig");

        var a = Entry("A create", Created(Guid.NewGuid(), 100));
        var b = Entry("B move", new BoundsRecord(kept, 0, R(0), R(60)));
        var c = Entry("C restyle", new TagRecord(kept, 0, "orig", "styled", R(60)));

        foreach (var e in new[] { a, b, c })
        {
            history.Push(e);
            doc.Apply(e, backwards: false);
        }
        Assert.Equal(2, doc.Objects.Count);
        Assert.Equal("styled", doc.Objects[kept].Tag);
        Assert.Equal(R(60), doc.Objects[kept].Rect);

        doc.Apply(history.Undo(Identity)!, backwards: true);
        Assert.Equal("orig", doc.Objects[kept].Tag);
        doc.Apply(history.Undo(Identity)!, backwards: true);
        Assert.Equal(R(0), doc.Objects[kept].Rect);
        doc.Apply(history.Undo(Identity)!, backwards: true);
        Assert.Single(doc.Objects);
        Assert.False(history.CanUndo);

        doc.Apply(history.Redo(Identity)!, backwards: false);
        Assert.Equal(2, doc.Objects.Count);
        doc.Apply(history.Redo(Identity)!, backwards: false);
        Assert.Equal(R(60), doc.Objects[kept].Rect);
        doc.Apply(history.Redo(Identity)!, backwards: false);
        Assert.Equal("styled", doc.Objects[kept].Tag);
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void Repeated_edits_to_the_same_object_unwind_one_at_a_time()
    {
        // Four moves of ONE object. Each undo must step back exactly one hop,
        // not jump to the original or collapse the lot, which is what happens
        // if records share state or the stack coalesces them.
        var doc = new FakeDoc();
        var history = new DocumentHistory();
        var id = Guid.NewGuid();
        doc.Objects[id] = (R(0), "t");

        for (int i = 0; i < 4; i++)
        {
            var e = Entry($"Move {i}", new BoundsRecord(id, 0, R(i * 10), R((i + 1) * 10)));
            history.Push(e);
            doc.Apply(e, backwards: false);
        }
        Assert.Equal(R(40), doc.Objects[id].Rect);

        for (int i = 3; i >= 0; i--)
        {
            doc.Apply(history.Undo(Identity)!, backwards: true);
            Assert.Equal(R(i * 10), doc.Objects[id].Rect);
        }
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void A_recreated_object_keeps_the_identity_it_had_before_deletion()
    {
        // The app deletes and re-adds constantly, so identity has to be carried
        // by the record rather than by whatever index the rebuild lands on.
        // Anything still pointing at the object - a group, a later history
        // record - resolves only if the SAME Guid comes back.
        var doc = new FakeDoc();
        var history = new DocumentHistory();
        var id = Guid.NewGuid();
        var other = Guid.NewGuid();
        doc.Objects[id] = (R(5), "AyaanShape:0:00FF00FF:2.0000:1:1");
        doc.Objects[other] = (R(90), "AyaanShape:1:0000FFFF:2.0000:1:1");
        doc.Groups.Add(new List<Guid> { id, other });

        var del = Entry("Delete annotation",
            new ExistenceRecord(id, 0, "AyaanShape:0:00FF00FF:2.0000:1:1", R(5),
                                ExistsAfter: false, Recoverable: true));
        history.Push(del);
        doc.Apply(del, backwards: false);
        Assert.False(doc.Objects.ContainsKey(id));

        doc.Apply(history.Undo(Identity)!, backwards: true);

        // Same Guid, same rectangle, and the group that referenced it still
        // resolves to a live object.
        Assert.True(doc.Objects.ContainsKey(id));
        Assert.Equal(R(5), doc.Objects[id].Rect);
        Assert.All(doc.Groups[0], member => Assert.True(doc.Objects.ContainsKey(member)));
    }

    [Fact]
    public void A_batch_of_records_is_applied_forwards_and_reversed_backwards()
    {
        // Ordering within one entry matters when records touch the same object:
        // undo has to unwind them in the opposite order they were applied.
        var doc = new FakeDoc();
        var id = Guid.NewGuid();
        doc.Objects[id] = (R(0), "start");

        var e = Entry("Compound",
            new TagRecord(id, 0, "start", "middle", R(0)),
            new TagRecord(id, 0, "middle", "end", R(0)));

        doc.Apply(e, backwards: false);
        Assert.Equal("end", doc.Objects[id].Tag);

        doc.Apply(e, backwards: true);
        Assert.Equal("start", doc.Objects[id].Tag);
    }
}
