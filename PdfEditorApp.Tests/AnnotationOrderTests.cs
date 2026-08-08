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
}
