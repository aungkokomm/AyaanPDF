using System;
using System.Collections.Generic;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The block of the document's own text that a click selects.
///
/// ⚠️ REAL BEHAVIOUR, not a reading of the source. This type lives in the
/// Viewport assembly precisely so it can be exercised, and what it protects is
/// the one thing the block model exists for: the box the reader can see and the
/// lines that will move are the same object, so they cannot disagree.
/// </summary>
public class TextBlockSelectionTests
{
    private static TextLine Line(double left, double top, double right, double bottom) =>
        new("text", 0, 0, left, top, right, bottom,
            Baseline: bottom, TextRegionStatus.Ok, Array.Empty<TextRun>());

    private static TextRegion Region(params TextLine[] lines)
    {
        double l = double.MaxValue, t = double.MaxValue;
        double r = double.MinValue, b = double.MinValue;
        foreach (var line in lines)
        {
            l = Math.Min(l, line.Left); t = Math.Min(t, line.Top);
            r = Math.Max(r, line.Right); b = Math.Max(b, line.Bottom);
        }
        return new TextRegion(l, t, r, b, lines);
    }

    [Fact]
    public void a_block_carries_every_line_it_is_drawn_around()
    {
        var block = TextBlockSelection.Of(3, Region(
            Line(0.10, 0.20, 0.80, 0.22),
            Line(0.10, 0.24, 0.75, 0.26),
            Line(0.10, 0.28, 0.60, 0.30)));

        Assert.Equal(3, block.Page);
        Assert.Equal(new[] { 0.22, 0.26, 0.30 }, block.Baselines);
        Assert.Equal(0.10, block.Left, 6);
        Assert.Equal(0.30, block.Bottom, 6);
    }

    /// <summary>
    /// ⚠️ THE WAY OUT WHEN THE SEGMENTER IS WRONG. A block comes from a guess
    /// about where a paragraph ends, and on a contents page that guess is the
    /// whole list. Alt gets the one line, and it is not a lesser kind of block:
    /// the segmenter itself makes a block of one out of every line no larger
    /// block claimed.
    /// </summary>
    [Fact]
    public void one_line_is_a_block_of_one()
    {
        var block = TextBlockSelection.Of(0, Line(0.10, 0.20, 0.80, 0.22));

        Assert.Single(block.Baselines);
        Assert.Equal(0.22, block.Baselines[0], 6);
        Assert.Equal(0.02, block.LineHeight, 6);
    }

    /// <summary>
    /// ⚠️ WHAT IS DRAWN IS WHAT THE POINTER CAN ENTER. A frame the reader can
    /// see and a hit box that disagree by a few points is a click that lands
    /// inside the rule and dismisses the selection they were aiming at.
    /// </summary>
    [Fact]
    public void the_drawn_box_and_the_hit_box_are_the_same_box()
    {
        var block = TextBlockSelection.Of(1, Region(
            Line(0.10, 0.20, 0.80, 0.24),
            Line(0.10, 0.26, 0.70, 0.30)));

        var (l, t, r, b) = block.Frame;

        // Every corner of the drawn frame is inside the hit box, and a point
        // just outside each edge is not.
        Assert.True(block.Contains(1, l, t));
        Assert.True(block.Contains(1, r, b));
        Assert.False(block.Contains(1, l - 0.001, t));
        Assert.False(block.Contains(1, r + 0.001, b));
        Assert.False(block.Contains(1, l, t - 0.001));
        Assert.False(block.Contains(1, r, b + 0.001));
    }

    /// <summary>
    /// The frame stands OFF the type. Drawn at the tight bounds the rule lands
    /// on the letterforms: a line of capitals has no descenders, so its box
    /// stops at the baseline and the stroke cuts through the feet.
    /// </summary>
    [Fact]
    public void the_frame_stands_off_the_type_and_the_bounds_do_not()
    {
        var block = TextBlockSelection.Of(0, Line(0.10, 0.20, 0.80, 0.24));
        var (l, t, r, b) = block.Frame;

        Assert.True(l < block.Left, "the frame does not stand off the left");
        Assert.True(r > block.Right, "the frame does not stand off the right");
        Assert.True(t < block.Top && b > block.Bottom, "the frame does not stand off top or foot");

        // ⚠️ AND SCALED FROM THE TYPE'S OWN HEIGHT, not a fixed number of
        // pixels, or the same rule would swamp small type and vanish on large.
        var big = TextBlockSelection.Of(0, Line(0.10, 0.20, 0.80, 0.40));
        Assert.True(big.Left - big.Frame.Left > block.Left - l);
    }

    /// <summary>
    /// The FIRST line's height, not the tallest. A block whose padding grew
    /// with its biggest line would stand further off a heading than off the
    /// body text under it, and the reader would read that as a mistake.
    /// </summary>
    [Fact]
    public void the_padding_comes_from_the_first_line_not_the_tallest()
    {
        var block = TextBlockSelection.Of(0, Region(
            Line(0.10, 0.20, 0.80, 0.22),
            Line(0.10, 0.24, 0.80, 0.40)));

        Assert.Equal(0.02, block.LineHeight, 6);
    }

    /// <summary>
    /// ⚠️ SHIFT-CLICKING THE SAME BLOCK TWICE MUST NOT SELECT IT TWICE, or its
    /// lines would be sent to the mover twice over.
    /// </summary>
    [Fact]
    public void a_block_is_the_same_as_another_holding_the_same_lines()
    {
        var region = Region(Line(0.10, 0.20, 0.80, 0.22), Line(0.10, 0.24, 0.70, 0.26));

        Assert.True(TextBlockSelection.Of(0, region).IsSameAs(TextBlockSelection.Of(0, region)));

        // A different page is a different block even with the same geometry,
        // because a move rewrites one page's content.
        Assert.False(TextBlockSelection.Of(0, region).IsSameAs(TextBlockSelection.Of(1, region)));

        // And one line of it is not the block.
        Assert.False(TextBlockSelection.Of(0, region)
            .IsSameAs(TextBlockSelection.Of(0, Line(0.10, 0.20, 0.80, 0.22))));
    }

    private static RecoveredLine Recovered(
        int paragraph, double left, double right, double baseline) =>
        new(PdfBaseline: 700, Left: left, Top: baseline - 0.015, Right: right,
            Bottom: baseline + 0.004, Baseline: baseline, FontSizePts: 11,
            Text: "မြန်မာ", FontName: "Pyidaungsu", FontPath: "",
            Clusters: Array.Empty<RecoveredCluster>(), Paragraph: paragraph);

    /// <summary>
    /// ⚠️ THE READER'S PYIDAUNGSU PAGE. A click on any line of a paragraph the
    /// core numbered gets all of it, framed from the lines' own edges, and
    /// nothing from the paragraph beside it.
    /// </summary>
    [Fact]
    public void a_recovered_paragraph_is_every_line_the_core_numbered_with_it()
    {
        var heading = Recovered(0, 0.30, 0.70, 0.10);
        var first = Recovered(1, 0.10, 0.90, 0.20);
        var second = Recovered(1, 0.10, 0.92, 0.23);
        var last = Recovered(1, 0.10, 0.50, 0.26);
        var page = new[] { heading, first, second, last };

        var block = TextBlockSelection.Of(4, page, second);

        Assert.Equal(new[] { 0.20, 0.23, 0.26 }, block.Baselines);
        Assert.Equal(0.10, block.Left, 6);
        Assert.Equal(0.92, block.Right, 6);
        Assert.Equal(first.Top, block.Top, 6);
        Assert.Equal(last.Bottom, block.Bottom, 6);

        // The same block whichever of its lines was clicked.
        Assert.True(block.IsSameAs(TextBlockSelection.Of(4, page, first)));
        Assert.True(block.IsSameAs(TextBlockSelection.Of(4, page, last)));
        Assert.False(block.IsSameAs(TextBlockSelection.Of(4, page, heading)));
    }

    /// <summary>
    /// A line the core put in no paragraph is a block of one, even beside
    /// other lines in none.
    /// </summary>
    [Fact]
    public void a_recovered_line_in_no_paragraph_is_a_block_of_one()
    {
        var alone = Recovered(-1, 0.10, 0.90, 0.20);
        var other = Recovered(-1, 0.10, 0.90, 0.23);

        var block = TextBlockSelection.Of(0, new[] { alone, other }, alone);

        Assert.Equal(new[] { 0.20 }, block.Baselines);
        Assert.Equal(alone.Bottom - alone.Top, block.LineHeight, 6);
    }

    /// <summary>
    /// Two runs on one line of type are one baseline, or a shift-click could
    /// not tell the paragraph from itself.
    /// </summary>
    [Fact]
    public void two_runs_on_one_line_of_type_are_one_baseline()
    {
        var left = Recovered(2, 0.10, 0.40, 0.20);
        var right = Recovered(2, 0.42, 0.90, 0.2005);
        var below = Recovered(2, 0.10, 0.90, 0.23);

        var block = TextBlockSelection.Of(0, new[] { left, right, below }, left);

        Assert.Equal(2, block.Baselines.Count);
        Assert.Equal(0.90, block.Right, 6);
    }

    [Fact]
    public void a_point_on_another_page_is_never_inside()
    {
        var block = TextBlockSelection.Of(2, Line(0.10, 0.20, 0.80, 0.24));
        Assert.True(block.Contains(2, 0.4, 0.22));
        Assert.False(block.Contains(1, 0.4, 0.22));
    }

    /// <summary>
    /// A region whose lines are all degenerate leaves the padding at zero
    /// rather than throwing, and the box is then simply the bounds.
    /// </summary>
    [Fact]
    public void a_block_with_no_height_still_answers()
    {
        var block = TextBlockSelection.Of(0, Region(Line(0.10, 0.20, 0.80, 0.20)));

        Assert.Equal(0, block.LineHeight);
        Assert.Equal(block.Left, block.Frame.Left, 6);
        Assert.True(block.Contains(0, 0.4, 0.20));
    }
}
