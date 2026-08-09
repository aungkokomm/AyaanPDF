using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The whole point of the queue is the count. These pin it.
/// </summary>
public class RepaintQueueTests
{
    [Fact]
    public void a_mark_outside_a_command_repaints_immediately()
    {
        // The safe default, and what all 38 existing call sites already do. A
        // queue that deferred by default would mean any caller not yet taught
        // to open a command silently stops repainting, and edits go invisible.
        var q = new RepaintQueue();
        Assert.True(q.Mark(0));
        Assert.Equal(0, q.PendingCount);
    }

    [Fact]
    public void a_mark_inside_a_command_defers()
    {
        var q = new RepaintQueue();
        q.Begin();
        Assert.False(q.Mark(0));
        Assert.Equal(1, q.PendingCount);
    }

    [Fact]
    public void five_marks_on_one_page_flush_as_a_single_repaint()
    {
        // The actual bug, in miniature: CommitLoadedMove invalidates once per
        // moved member so FindLoadedById sees fresh indices, and each of those
        // used to repaint the whole page.
        var q = new RepaintQueue();
        q.Begin();
        for (int i = 0; i < 5; i++)
        {
            Assert.False(q.Mark(3));
        }

        Assert.Equal(new[] { 3 }, q.End());
    }

    [Fact]
    public void separate_pages_each_get_one_repaint()
    {
        var q = new RepaintQueue();
        q.Begin();
        q.Mark(2);
        q.Mark(0);
        q.Mark(2);
        q.Mark(1);

        // In page order, so a multi-page move repaints top-down.
        Assert.Equal(new[] { 0, 1, 2 }, q.End());
    }

    [Fact]
    public void only_the_outermost_command_flushes()
    {
        // Commands nest: a group move commits each member, and undo replays a
        // whole operation. An inner flush would repaint mid-operation and then
        // be repainted again at the end.
        var q = new RepaintQueue();
        q.Begin();
        q.Mark(0);
        q.Begin();
        q.Mark(0);

        Assert.Empty(q.End());
        Assert.True(q.InCommand);

        Assert.Equal(new[] { 0 }, q.End());
        Assert.False(q.InCommand);
    }

    [Fact]
    public void flushing_clears_so_the_next_command_starts_empty()
    {
        var q = new RepaintQueue();
        q.Begin();
        q.Mark(7);
        Assert.Equal(new[] { 7 }, q.End());

        q.Begin();
        Assert.Equal(0, q.PendingCount);
        Assert.Empty(q.End());
    }

    [Fact]
    public void an_unbalanced_end_does_not_go_negative()
    {
        // A command that returns early on an error path can leave End unpaired.
        // That must not push the depth below zero, or the NEXT command's marks
        // would be treated as being outside a command and repaint one by one.
        var q = new RepaintQueue();
        Assert.Empty(q.End());
        Assert.Empty(q.End());

        q.Begin();
        Assert.False(q.Mark(0));
        Assert.Equal(new[] { 0 }, q.End());
    }

    [Fact]
    public void marks_resume_immediate_repaints_once_the_command_closes()
    {
        var q = new RepaintQueue();
        q.Begin();
        q.Mark(1);
        q.End();

        Assert.True(q.Mark(1));
    }
}
