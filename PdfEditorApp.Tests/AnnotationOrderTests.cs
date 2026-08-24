using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Paint order maths. Every reorder on a real page is paid for in removals and
/// rebuilds, so the order has to be right BEFORE anything is destroyed.
/// </summary>
public class AnnotationOrderTests
{
    // Named so a failure message reads as a stack rather than as five GUIDs.
    private static readonly Guid A = new("aaaaaaaa-0000-0000-0000-000000000000");
    private static readonly Guid B = new("bbbbbbbb-0000-0000-0000-000000000000");
    private static readonly Guid C = new("cccccccc-0000-0000-0000-000000000000");
    private static readonly Guid D = new("dddddddd-0000-0000-0000-000000000000");

    private static HashSet<Guid> Moving(params Guid[] ids) => new(ids);

    [Fact]
    public void send_to_back_puts_the_selection_underneath_everything()
    {
        var r = AnnotationOrder.SendToBack(new[] { A, B, C }, Moving(C));
        Assert.Equal(new[] { C, A, B }, r);
    }

    [Fact]
    public void bring_to_front_puts_the_selection_on_top()
    {
        var r = AnnotationOrder.BringToFront(new[] { A, B, C }, Moving(A));
        Assert.Equal(new[] { B, C, A }, r);
    }

    [Fact]
    public void a_multi_selection_keeps_its_own_relative_order_when_sent_to_back()
    {
        // A below C in the original, so A must still be below C afterwards.
        var r = AnnotationOrder.SendToBack(new[] { A, B, C, D }, Moving(C, A));
        Assert.Equal(new[] { A, C, B, D }, r);
    }

    [Fact]
    public void bring_forward_moves_exactly_one_step()
    {
        var r = AnnotationOrder.BringForward(new[] { A, B, C }, Moving(A));
        Assert.Equal(new[] { B, A, C }, r);
    }

    [Fact]
    public void send_backward_moves_exactly_one_step()
    {
        var r = AnnotationOrder.SendBackward(new[] { A, B, C }, Moving(C));
        Assert.Equal(new[] { A, C, B }, r);
    }

    [Fact]
    public void bring_forward_on_the_topmost_item_changes_nothing()
    {
        var r = AnnotationOrder.BringForward(new[] { A, B, C }, Moving(C));
        Assert.Equal(new[] { A, B, C }, r);
    }

    [Fact]
    public void send_backward_on_the_bottom_item_changes_nothing()
    {
        var r = AnnotationOrder.SendBackward(new[] { A, B, C }, Moving(A));
        Assert.Equal(new[] { A, B, C }, r);
    }

    [Fact]
    public void a_selected_run_rises_as_a_block_rather_than_collapsing()
    {
        // The bug this guards: a naive upward walk raises A past B, then meets A
        // again and raises it past C, so one press moves it two places and the
        // pair ends up reversed.
        var r = AnnotationOrder.BringForward(new[] { A, B, C }, Moving(A, B));
        Assert.Equal(new[] { C, A, B }, r);
    }

    [Fact]
    public void a_selected_run_sinks_as_a_block()
    {
        var r = AnnotationOrder.SendBackward(new[] { A, B, C }, Moving(B, C));
        Assert.Equal(new[] { B, C, A }, r);
    }

    [Fact]
    public void repeated_bring_forward_walks_all_the_way_up_and_then_stops()
    {
        var order = new List<Guid> { A, B, C };
        for (int i = 0; i < 5; i++)
        {
            order = AnnotationOrder.BringForward(order, Moving(A));
        }
        Assert.Equal(new[] { B, C, A }, order);
    }

    [Fact]
    public void rewrite_starts_at_the_first_disagreement()
    {
        int from = AnnotationOrder.RewriteFrom(
            new[] { A, B, C, D },
            new[] { A, C, B, D });

        Assert.Equal(1, from);
    }

    [Fact]
    public void an_unchanged_order_needs_no_rewrite_at_all()
    {
        int from = AnnotationOrder.RewriteFrom(new[] { A, B, C }, new[] { A, B, C });
        Assert.Equal(3, from);
    }

    [Fact]
    public void re_adding_the_tail_in_target_order_reproduces_the_target()
    {
        // This is the whole contract between the maths and the document: the
        // engine can only APPEND, so the claim is that appending target[from..]
        // in order lands on target exactly. Simulated here against a list.
        var current = new List<Guid> { A, B, C, D };
        var target = AnnotationOrder.SendToBack(current, Moving(D, C));

        int from = AnnotationOrder.RewriteFrom(current, target);
        var page = new List<Guid>(current);
        for (int i = from; i < target.Count; i++)
        {
            page.Remove(target[i]);   // delete
            page.Add(target[i]);      // re-add, always at the end
        }

        Assert.Equal(target, page);
    }

    /// <summary>Replays the engine's only write primitive: remove and append.</summary>
    private static List<Guid> ApplyOrder(IReadOnlyList<Guid> page, IReadOnlyList<Guid> target)
    {
        var result = new List<Guid>(page);
        for (int i = AnnotationOrder.RewriteFrom(page, target); i < target.Count; i++)
        {
            result.Remove(target[i]);
            result.Add(target[i]);
        }
        return result;
    }

    [Theory]
    [InlineData("SendToBack")]
    [InlineData("BringToFront")]
    [InlineData("BringForward")]
    [InlineData("SendBackward")]
    public void undoing_a_reorder_returns_the_original_order(string command)
    {
        // A z-order command records the whole before and after order and undoes
        // by rewriting to Before with the SAME code that applied After. If that
        // rewrite is not a true inverse, undo silently leaves the page in a
        // third order that is neither.
        var page = new List<Guid> { A, B, C, D };
        var moving = Moving(B);

        var after = command switch
        {
            "SendToBack" => AnnotationOrder.SendToBack(page, moving),
            "BringToFront" => AnnotationOrder.BringToFront(page, moving),
            "BringForward" => AnnotationOrder.BringForward(page, moving),
            _ => AnnotationOrder.SendBackward(page, moving),
        };

        var applied = ApplyOrder(page, after);
        Assert.Equal(after, applied);

        var undone = ApplyOrder(applied, page);
        Assert.Equal(page, undone);

        // And redo lands back on the same result, so the pair can be walked
        // repeatedly without the order creeping.
        Assert.Equal(after, ApplyOrder(undone, after));
    }

    [Fact]
    public void undo_and_redo_can_be_walked_repeatedly_without_drifting()
    {
        var original = new List<Guid> { A, B, C, D };
        var target = AnnotationOrder.SendToBack(original, Moving(C, D));

        var page = new List<Guid>(original);
        for (int i = 0; i < 5; i++)
        {
            page = ApplyOrder(page, target);
            Assert.Equal(target, page);
            page = ApplyOrder(page, original);
            Assert.Equal(original, page);
        }
    }

    [Fact]
    public void the_untouched_prefix_is_never_rewritten()
    {
        // Why this matters: an annotation the app cannot rebuild (an Acrobat
        // comment) survives the reorder as long as it sits in the prefix. If
        // RewriteFrom ever returned 0 here, that comment would be destroyed.
        var current = new[] { A, B, C, D };
        var target = AnnotationOrder.BringForward(current, Moving(C));

        Assert.Equal(2, AnnotationOrder.RewriteFrom(current, target));
    }

    [Fact]
    public void a_lone_object_on_a_page_cannot_be_reordered_at_all()
    {
        // Reported as "z-order is broken": every command, from the toolbar and
        // from the keyboard, appeared to do nothing. It was doing exactly the
        // right thing. A page holding one object has nothing to order it
        // against, so every plan returns the stack unchanged and the reorder
        // declines with "Already at the front."
        //
        // Worth pinning because the symptom is indistinguishable from a real
        // failure: the command runs, the page repaints, the object does not
        // move, and the only thing that says why is one line in the status bar.
        var current = new[] { A };

        foreach (var plan in new Func<IReadOnlyList<Guid>, ISet<Guid>, List<Guid>>[]
        {
            AnnotationOrder.BringToFront,
            AnnotationOrder.SendToBack,
            AnnotationOrder.BringForward,
            AnnotationOrder.SendBackward,
        })
        {
            var target = plan(current, Moving(A));

            Assert.Equal(current, target);
            Assert.True(
                AnnotationOrder.RewriteFrom(current, target) >= target.Count,
                "a lone object must produce no rewrite, so the reorder declines");
        }
    }

    [Fact]
    public void two_objects_are_enough_to_reorder()
    {
        // The other half, so the test above cannot pass by the ordering being
        // broken for everything.
        var current = new[] { A, B };
        var target = AnnotationOrder.BringForward(current, Moving(A));

        Assert.Equal(new[] { B, A }, target);
        Assert.Equal(0, AnnotationOrder.RewriteFrom(current, target));
    }

    // ---------------- pages that also hold the document's own text ----------------
    //
    // A REGRESSION. Text Stage 1 put the document's own text into the page
    // model, and both z-order call sites were reading the model's whole object
    // list. Page text carries no identity, so each one arrived as Guid.Empty
    // and sat UNDERNEATH every annotation, which is where "send to back" wants
    // to write. The plan is rewritten by POSITION, so a list holding things
    // that are not annotations is not a list this code can act on at all.
    //
    // It failed safe, because the guard refuses anything it cannot rebuild and
    // page text is not rebuildable. But "Send to back" and "Send backward" then
    // refused on every page containing a word, which is nearly every real one.
    //
    // The controls matter more than the assertions here: each command is run
    // twice on the same page, once with text and once without, and the two have
    // to agree. A test that only ran the WITH case could pass while the whole
    // operation was broken for a different reason.

    private const double PageW = 600;
    private const string Rect = "AyaanShape:0:FF0000FF:2.5000:1:1";

    private static PageTextSnapshot Words(int index, double top) =>
        new(index, 0.1, top, 0.5, top + 0.05, 12, 0x112233u, false, "Helvetica", "words");

    private static AnnotationSnapshot Shape(Guid id, double top) =>
        new(0, PdfAnnotationSubtype.Square, 0.1, top, 0.5, top + 0.05, 1.0, id, Rect);

    /// <summary>The page as the z-order commands see it. LOW is the lower of
    /// the two shapes, so sending it back has to cross the page text and
    /// bringing it forward does not.</summary>
    private static PageModel PageOf(bool withText) =>
        DocumentModelBuilder.BuildPage(
            0, [Shape(A, 0.4), Shape(B, 0.5)], PageW,
            withText ? [Words(0, 0.1), Words(1, 0.2)] : []);

    /// <summary>What the command's own guard would refuse, run against the same
    /// list the command builds.</summary>
    private static string Refusals(
        PageModel page, Func<IReadOnlyList<Guid>, ISet<Guid>, List<Guid>> plan, Guid moving)
    {
        var stack = page.Annotations.ToList();
        var current = stack.Select(o => o.Id).ToList();
        var target = plan(current, Moving(moving));
        int from = AnnotationOrder.RewriteFrom(current, target);

        var refused = new List<string>();
        for (int i = from; i < target.Count; i++)
        {
            var obj = stack.FirstOrDefault(o => o.Id == target[i]);
            if (obj is null || !obj.IsRebuildable)
            {
                refused.Add($"i={i}:{(obj is null ? "not in the stack" : obj.Kind.ToString())}");
            }
        }

        return $"from={from} refused=[{string.Join(",", refused)}]";
    }

    [Theory]
    [InlineData("Bring to front")]
    [InlineData("Bring forward")]
    [InlineData("Send backward")]
    [InlineData("Send to back")]
    public void every_command_behaves_the_same_whether_or_not_the_page_has_text(string name)
    {
        Func<IReadOnlyList<Guid>, ISet<Guid>, List<Guid>> plan = name switch
        {
            "Bring to front" => AnnotationOrder.BringToFront,
            "Bring forward" => AnnotationOrder.BringForward,
            "Send backward" => AnnotationOrder.SendBackward,
            _ => AnnotationOrder.SendToBack,
        };

        string without = Refusals(PageOf(false), plan, A);
        string with = Refusals(PageOf(true), plan, A);

        Assert.True(without.Contains("refused=[]", StringComparison.Ordinal),
            $"{name}: the CONTROL refused, so this test is measuring the wrong thing: {without}");
        Assert.Equal(without, with);
    }

    [Fact]
    public void the_list_a_reorder_is_planned_against_holds_only_annotations()
    {
        // The defect in one assertion. Guid.Empty in this list means the plan
        // is being built against something that has no identity to rewrite.
        var ids = PageOf(withText: true).Annotations.Select(o => o.Id).ToList();

        Assert.Equal(2, ids.Count);
        Assert.DoesNotContain(Guid.Empty, ids);
    }

    [Fact]
    public void page_text_does_not_shift_the_index_an_annotation_is_addressed_by()
    {
        // The worse bug this one was next door to. ZOrder is handed to calls
        // that move, restyle and delete annotations BY INDEX. Had it been
        // assigned from the position in the model's list, every annotation on a
        // page with text would address a different one.
        var withText = PageOf(true).Annotations.Select(o => o.ZOrder).ToList();
        var without = PageOf(false).Annotations.Select(o => o.ZOrder).ToList();

        Assert.Equal(without, withText);
    }
}
