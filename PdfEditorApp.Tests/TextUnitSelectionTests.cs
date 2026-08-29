using System;
using System.Collections.Generic;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The unit a click on the document's own text selects, and where the caret
/// goes when the next click lands inside it.
///
/// ⚠️ WRITTEN AFTER THE GESTURE WAS REPLACED. Double-click meant a word and
/// triple-click meant a line. Nothing on screen ever said which unit an edit
/// was about to change, and the second click of the triple opened an editor
/// directly over the word, so the third landed on that editor instead of the
/// page. One click now selects and shows; the next says where to type.
/// </summary>
public class TextUnitSelectionTests
{
    private static WordClusterSnapshot Word(
        string text, double left, double right, ClusterRefusal refusal = ClusterRefusal.None) =>
        new(0, new[] { 0 }, left, 0.20, right, 0.24, 0.238, 12, 0, refusal, 0, text, "Times");

    private static LineSnapshot Line(
        string text, double left, double right, LineRefusal refusal = LineRefusal.None) =>
        new(0, 0, 0, 5, left, 0.20, right, 0.24, 0.238, 12, 0, refusal, text, "Times");

    // ---------------- which unit ----------------

    [Fact]
    public void a_line_that_can_be_retyped_is_what_a_click_gets()
    {
        var unit = TextUnitSelection.From(3, Line("the whole line of it", 0.10, 0.60));

        Assert.Equal(TextUnitKind.Line, unit.Kind);
        Assert.Equal(3, unit.Page);
        Assert.Equal("the whole line of it", unit.Text);
        Assert.True(unit.CanEdit);
    }

    [Fact]
    public void a_word_carries_its_own_bounds_and_not_its_lines()
    {
        // The box is drawn from these, so a word inheriting the line's width
        // would box the whole line and then edit only part of it.
        var unit = TextUnitSelection.From(0, Word("word", 0.30, 0.36));

        Assert.Equal(TextUnitKind.Word, unit.Kind);
        Assert.Equal(0.30, unit.Left, 4);
        Assert.Equal(0.36, unit.Right, 4);
    }

    [Fact]
    public void a_refused_unit_still_says_why()
    {
        // It is still selected and still boxed. Showing the reader what they
        // clicked and saying why it cannot change is the difference between a
        // limitation and a click that did nothing.
        var unit = TextUnitSelection.From(0, Line("justified text", 0.10, 0.60, LineRefusal.Justified));

        Assert.False(unit.CanEdit);
        Assert.NotEqual(string.Empty, unit.RefusalReason);
        Assert.Contains(unit.RefusalReason, unit.Description);
    }

    [Fact]
    public void an_editable_unit_says_what_to_do_next()
    {
        // The gesture is two clicks and the second one is not obvious, so the
        // status bar is where it gets said.
        var unit = TextUnitSelection.From(0, Line("ordinary text", 0.10, 0.60));

        Assert.Contains("click again", unit.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void exactly_one_snapshot_travels_with_the_selection()
    {
        // The commit needs the original to call the right core write with the
        // identity that write needs, and calling both would write twice.
        var line = TextUnitSelection.From(0, Line("a line", 0.1, 0.5));
        var word = TextUnitSelection.From(0, Word("a", 0.1, 0.2));

        Assert.NotNull(line.Line);
        Assert.Null(line.Word);
        Assert.NotNull(word.Word);
        Assert.Null(word.Line);
    }

    // ---------------- the box ----------------

    [Fact]
    public void the_box_holds_the_points_inside_it_and_not_the_ones_outside()
    {
        var unit = TextUnitSelection.From(2, Line("text", 0.20, 0.60));

        Assert.True(unit.Contains(2, 0.40, 0.22));
        Assert.False(unit.Contains(2, 0.80, 0.22));
        Assert.False(unit.Contains(2, 0.40, 0.60));
    }

    [Fact]
    public void the_box_belongs_to_its_own_page_only()
    {
        // Slots are stacked in one scroller and the same normalized point
        // exists on every page in it.
        var unit = TextUnitSelection.From(2, Line("text", 0.20, 0.60));

        Assert.True(unit.Contains(2, 0.40, 0.22));
        Assert.False(unit.Contains(3, 0.40, 0.22));
    }

    [Fact]
    public void the_box_reaches_as_far_as_the_frame_the_reader_can_see()
    {
        // ⚠️ THE FRAME IS PADDED OFF THE GLYPHS, because a rule at the tight
        // bounds cuts through the feet of the letters. If the hit test used the
        // tight bounds, the strip between the letters and the visible rule would
        // LOOK inside the box and BEHAVE outside it, and clicking there would
        // dismiss the selection instead of putting a caret in it.
        var unit = TextUnitSelection.From(0, Line("text", 0.20, 0.60));

        double height = unit.Bottom - unit.Top;
        double padX = height * TextUnitSelection.FramePadXFactor;
        double padY = height * TextUnitSelection.FramePadYFactor;

        // Just inside the drawn frame, on every side.
        Assert.True(unit.Contains(0, unit.Left - (padX * 0.9), unit.Top - (padY * 0.9)));
        Assert.True(unit.Contains(0, unit.Right + (padX * 0.9), unit.Bottom + (padY * 0.9)));

        // And just outside it.
        Assert.False(unit.Contains(0, unit.Right + (padX * 1.5), unit.Top));
        Assert.False(unit.Contains(0, unit.Left, unit.Bottom + (padY * 1.5)));
    }

    [Fact]
    public void the_padding_is_the_one_the_frame_is_drawn_with()
    {
        // Both sides multiply by these, and the constants live beside the hit
        // test so the drawing code has to reach for them rather than repeat
        // them. Two copies of a number that must agree do not stay agreed.
        string vm = System.IO.File.ReadAllText(FindUp(
            "PdfEditorApp", "ViewModels", "ViewportViewModel.cs"));

        Assert.Contains("TextUnitSelection.FramePadXFactor", vm, StringComparison.Ordinal);
        Assert.Contains("TextUnitSelection.FramePadYFactor", vm, StringComparison.Ordinal);
    }

    // ---------------- saying why it cannot be edited ----------------

    /// <summary>One method's CODE, comment lines dropped, so that commenting a
    /// call out counts as removing it.</summary>
    private static string Body(string signature)
    {
        string src = System.IO.File.ReadAllText(
            FindUp("PdfEditorApp", "ViewModels", "ViewportViewModel.cs")).Replace("\r\n", "\n");
        int at = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at > 0, $"there is no {signature}");

        int next = src.IndexOf("\n    /// <summary>", at, StringComparison.Ordinal);
        string body = src[at..(next > at ? next : src.Length)];

        return string.Join('\n', Array.FindAll(
            body.Split('\n'), l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
    }

    [Fact]
    public void a_refusal_is_shown_on_the_page_and_not_only_written_to_status()
    {
        // ⚠️ THE REAL FAILURE THIS ANSWERS. A reader clicked justified text, the
        // core refused it correctly, the app assigned the sentence to Status,
        // and NOTHING ON SCREEN SHOWS Status: the full-width status bar became
        // the compact pill carrying page, zoom, fit and find, and eighty
        // messages were left writing into a void. The frame appeared and the
        // reader was told nothing.
        string vm = System.IO.File.ReadAllText(
            FindUp("PdfEditorApp", "ViewModels", "ViewportViewModel.cs"));

        Assert.Contains("PageTextNotice.Add(new PageNotice(", vm, StringComparison.Ordinal);
        Assert.Contains("ShowUnitNotice(", Body("public bool SelectTextUnitAt("), StringComparison.Ordinal);
    }

    [Fact]
    public void only_a_refusal_is_explained_and_an_editable_unit_says_nothing()
    {
        // A label over every click would be noise. An editable unit already
        // says what it is by being framed in the editable colour.
        string select = Body("public bool SelectTextUnitAt(");

        Assert.Contains("CanEdit: false", select, StringComparison.Ordinal);
        Assert.Contains("RefusalReason", select, StringComparison.Ordinal);
    }

    [Fact]
    public void the_label_takes_itself_away()
    {
        // It explains a click that has just been made. Left on the page it
        // would be one more thing to dismiss, sitting over the words it is
        // about.
        string body = Body("private DispatcherQueueTimer? CreateUnitNoticeTimer()");

        Assert.Contains("UnitNoticeSeconds", body, StringComparison.Ordinal);
        Assert.Contains("_unitNotice = null;", body, StringComparison.Ordinal);
        Assert.Contains("RefreshSelectionOutline();", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_label_cannot_swallow_the_click_it_is_explaining()
    {
        // ⚠️ THE GESTURE RUNS THROUGH WHERE THIS SITS. The reader's next click
        // has to reach the page, and the label is drawn right beside the box
        // they are aiming at. The frame itself is not hit-testable for exactly
        // this reason; a label that ate the click would break the gesture it
        // exists to explain.
        string xaml = System.IO.File.ReadAllText(FindUp("PdfEditorApp", "MainPage.xaml"));

        int at = xaml.IndexOf("x:Bind PageTextNotice}", StringComparison.Ordinal);
        Assert.True(at > 0, "the notice overlay is gone");

        int lineEnd = xaml.IndexOf('\n', at);
        Assert.Contains("IsHitTestVisible=\"False\"", xaml[at..lineEnd], StringComparison.Ordinal);
    }

    [Fact]
    public void the_label_is_placed_without_having_to_know_how_tall_it_is()
    {
        // It goes ABOVE the frame, and how tall it is depends on where its
        // sentence wraps, so it is measured from the BOTTOM of the page and
        // bottom-aligned. A frame on the first line of a page has no room above
        // it and takes the other anchor. Estimating the height instead would
        // drop the label onto the words it is about whenever the guess came out
        // short.
        string page = System.IO.File.ReadAllText(FindUp("PdfEditorApp", "MainPage.xaml.cs"));

        Assert.Contains("public static Thickness NoticeMargin(", page, StringComparison.Ordinal);
        Assert.Contains("public static VerticalAlignment NoticeAlign(", page, StringComparison.Ordinal);

        var above = new PageNotice(10, 0, 400, 360, true, "why", "#FFB0700F");
        var below = new PageNotice(10, 250, 0, 360, false, "why", "#FFB0700F");

        Assert.True(above.Above);
        Assert.Equal(400, above.BottomMargin);
        Assert.False(below.Above);
        Assert.Equal(250, below.TopMargin);
    }

    private static string FindUp(params string[] relative)
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        string path = System.IO.Path.Combine(relative);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, path)))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return System.IO.Path.Combine(dir!.FullName, path);
    }

    // ---------------- the caret ----------------

    /// <summary>Characters laid out left to right on one line, 10 wide each.</summary>
    private static PageTextLayer Row(string text, double top = 100, double height = 12)
    {
        var chars = new List<CharGlyph>();
        for (int i = 0; i < text.Length; i++)
        {
            chars.Add(new CharGlyph(i * 10, top, (i * 10) + 10, top + height, text[i]));
        }
        return new PageTextLayer(chars);
    }

    [Fact]
    public void a_click_before_the_first_letter_puts_the_caret_at_the_start()
    {
        Assert.Equal(0, Row("hello").CaretOffsetOnLine(-5, 95, 115));
        Assert.Equal(0, Row("hello").CaretOffsetOnLine(2, 95, 115));
    }

    [Fact]
    public void a_click_past_the_last_letter_puts_the_caret_at_the_end()
    {
        Assert.Equal(5, Row("hello").CaretOffsetOnLine(500, 95, 115));
    }

    [Fact]
    public void a_click_inside_a_letter_lands_on_the_nearer_side_of_it()
    {
        // The third letter spans 20..30, so its centre is 25. Clicking left of
        // that means "before this letter", right of it means "after".
        var row = Row("hello");

        Assert.Equal(2, row.CaretOffsetOnLine(23, 95, 115));
        Assert.Equal(3, row.CaretOffsetOnLine(27, 95, 115));
    }

    [Fact]
    public void the_caret_walks_the_whole_line_one_letter_at_a_time()
    {
        var row = Row("abcdefgh");

        for (int i = 0; i < 8; i++)
        {
            // Just past the centre of letter i is offset i + 1.
            Assert.Equal(i + 1, row.CaretOffsetOnLine((i * 10) + 6, 95, 115));
        }
    }

    [Fact]
    public void only_the_clicked_line_is_counted()
    {
        // ⚠️ THE ONE THAT MATTERS ON A REAL PAGE. Every character of every line
        // is in this layer, so counting without the band would put the caret
        // hundreds of characters along.
        var chars = new List<CharGlyph>();
        for (int i = 0; i < 5; i++)
        {
            chars.Add(new CharGlyph(i * 10, 100, (i * 10) + 10, 112, "first"[i]));
        }
        for (int i = 0; i < 6; i++)
        {
            chars.Add(new CharGlyph(i * 10, 130, (i * 10) + 10, 142, "second"[i]));
        }
        var layer = new PageTextLayer(chars);

        Assert.Equal(5, layer.CaretOffsetOnLine(500, 95, 115));
        Assert.Equal(6, layer.CaretOffsetOnLine(500, 125, 145));
    }

    [Fact]
    public void a_tall_letter_and_a_short_one_are_both_on_their_line()
    {
        // Glyph boxes differ in height on one line. Testing containment of the
        // whole box rather than its centre would drop the tall ones.
        var chars = new List<CharGlyph>
        {
            new(0, 100, 10, 112, 'o'),
            new(10, 94, 20, 112, 'f'),
            new(20, 100, 30, 116, 'g'),
        };
        var layer = new PageTextLayer(chars);

        Assert.Equal(3, layer.CaretOffsetOnLine(500, 96, 114));
    }

    [Fact]
    public void a_line_break_is_not_something_to_put_a_caret_in_front_of()
    {
        var chars = new List<CharGlyph>
        {
            new(0, 100, 10, 112, 'a'),
            new(10, 100, 20, 112, 'b'),
            new(20, 100, 30, 112, '\n'),
        };
        var layer = new PageTextLayer(chars);

        Assert.Equal(2, layer.CaretOffsetOnLine(500, 95, 115));
    }

    [Fact]
    public void an_upside_down_band_is_read_the_right_way_up()
    {
        Assert.Equal(5, Row("hello").CaretOffsetOnLine(500, 115, 95));
    }

    [Fact]
    public void a_band_with_nothing_in_it_gives_the_start()
    {
        Assert.Equal(0, Row("hello").CaretOffsetOnLine(500, 300, 320));
        Assert.Equal(0, new PageTextLayer(Array.Empty<CharGlyph>()).CaretOffsetOnLine(5, 0, 10));
    }
}
