using System;
using System.IO;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The call sites that carry a word edit.
///
/// Read out of the source, because they live in the WinUI project and a net10.0
/// test assembly cannot load one; that is the same bargain every other wiring
/// test here makes. What they hold down is the handful of rules that are easy
/// to break by accident and expensive to notice.
/// </summary>
public class WordEditWiringTests
{
    private static string Source(params string[] relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string path = Path.Combine(relative);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, path)))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, path));
    }

    private static string ViewModel() =>
        Source("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");

    private static string Page() => Source("PdfEditorApp", "MainPage.xaml.cs");

    private static string Xaml() => Source("PdfEditorApp", "MainPage.xaml");

    [Fact]
    public void an_edit_is_one_history_entry_and_not_several()
    {
        // A word edit can touch a dozen PDF objects: the first takes the new
        // text, the rest are emptied, and the remainder of the line shifts. The
        // reader pressed one key, so Ctrl+Z has to put it all back in one step.
        string code = ViewModel();

        int edit = code.IndexOf("public bool EditSelectedWord(", StringComparison.Ordinal);
        Assert.True(edit > 0, "there is no word edit");

        int next = code.IndexOf("\n    /// <summary>", edit, StringComparison.Ordinal);
        string body = code[edit..(next > edit ? next : code.Length)];

        Assert.Contains("BeginEdit(", body, StringComparison.Ordinal);
        Assert.Contains("RecordEdit(new WordTextRecord(", body, StringComparison.Ordinal);
        Assert.Equal(1, Count(body, "CommitEdit()"));
    }

    [Fact]
    public void a_refusal_leaves_no_history_entry_behind()
    {
        // The core puts the page back as it found it when it refuses, so there
        // is nothing to undo. Committing anyway would leave a Ctrl+Z that does
        // nothing, which reads as the app having lost the edit.
        string code = ViewModel();

        int edit = code.IndexOf("public bool EditSelectedWord(", StringComparison.Ordinal);
        int next = code.IndexOf("\n    /// <summary>", edit, StringComparison.Ordinal);
        string body = code[edit..next];

        int refused = body.IndexOf("!= RenderStatus.OkPdfium", StringComparison.Ordinal);
        int abandon = body.IndexOf("AbandonEdit()", StringComparison.Ordinal);
        int commit = body.IndexOf("CommitEdit()", StringComparison.Ordinal);

        Assert.True(refused > 0 && abandon > refused, "a refusal does not abandon the batch");
        Assert.True(abandon < commit, "the batch is committed before the refusal is handled");
    }

    [Fact]
    public void a_word_too_long_for_the_line_says_so_rather_than_blaming_the_page()
    {
        // ⚠️ THE MESSAGE THE WIDTH GUARD NEEDS. The core refuses a
        // replacement that would run off the right of the paper, which matters
        // most for INSERTING: typing inside a word only ever makes the run
        // longer. It is also the one refusal with a remedy, since the same edit
        // with fewer letters goes through, and reporting it as "that word no
        // longer matches the page" would send the reader off to re-select a
        // word that is exactly where they left it.
        string code = ViewModel();

        int edit = code.IndexOf("public bool EditSelectedWord(", StringComparison.Ordinal);
        int next = code.IndexOf("\n    /// <summary>", edit, StringComparison.Ordinal);
        string body = code[edit..(next > edit ? next : code.Length)];

        Assert.Contains("RenderStatus.TooWide", body, StringComparison.Ordinal);
        Assert.Contains("too long to fit", body, StringComparison.Ordinal);

        // And the line's own message is untouched, so the two cannot drift into
        // one another.
        int line = code.IndexOf("public bool EditSelectedLine(", StringComparison.Ordinal);
        Assert.True(line > 0 && line != edit);
    }

    [Fact]
    public void undo_checks_what_the_page_says_before_it_writes()
    {
        // ⚠️ THE ONE REAL HAZARD. Every other record is keyed by a Guid that
        // finds its annotation wherever it moved to. A word has no identity we
        // put there and is keyed by where it sits in the page's content, so an
        // unverified undo could overwrite text it was never about.
        string code = ViewModel();

        int applier = code.IndexOf("private void ApplyWordText(", StringComparison.Ordinal);
        Assert.True(applier > 0, "there is no word undo");

        int next = code.IndexOf("\n    /// <summary>", applier, StringComparison.Ordinal);
        string body = code[applier..(next > applier ? next : code.Length)];

        int check = body.IndexOf("expected.Trim()", StringComparison.Ordinal);
        int write = body.IndexOf("WordClusterGateway.Write", StringComparison.Ordinal);

        Assert.True(check > 0, "undo does not check what the page currently says");
        Assert.True(write > check, "undo writes before it checks");
        Assert.Contains("return;", body[check..write], StringComparison.Ordinal);
    }

    [Fact]
    public void the_words_of_a_page_are_dropped_when_that_page_changes()
    {
        // A cached word carries object indices that an edit may have moved.
        string code = ViewModel();

        int invalidate = code.IndexOf(
            "private void InvalidateAnnotationCache(", StringComparison.Ordinal);
        Assert.True(invalidate > 0);

        int next = code.IndexOf("\n    private ", invalidate + 10, StringComparison.Ordinal);
        Assert.Contains("_clustersByPage.Remove(pageIndex);",
            code[invalidate..next], StringComparison.Ordinal);
    }

    [Fact]
    public void a_word_that_cannot_be_edited_is_refused_before_the_editor_opens()
    {
        // Being told before typing is the difference between a limitation and a
        // bug. Rotated text, mixed styling and out-of-order scripts are all real
        // cases measured on real documents.
        string code = ViewModel();

        int open = code.IndexOf("public bool BeginInPlaceEdit(", StringComparison.Ordinal);
        Assert.True(open > 0);

        int next = code.IndexOf("\n    /// <summary>", open, StringComparison.Ordinal);
        string body = code[open..next];

        int guard = body.IndexOf("!unit.CanEdit", StringComparison.Ordinal);
        int build = body.IndexOf("new LineEditBuffer(", StringComparison.Ordinal);

        Assert.True(guard > 0 && build > 0 && guard < build,
            "the caret is placed before the unit is checked");
    }

    [Fact]
    public void committing_cannot_re_enter_itself()
    {
        // Removing a focused TextBox raised LostFocus, and LostFocus committed,
        // so the old editor had to unhook before it removed. There is no focus
        // to lose any more, and the same protection comes from the state
        // instead: the edit is ended first, and a commit with nothing open
        // returns immediately.
        string code = ViewModel();

        int at = code.IndexOf("public bool CommitInPlaceEdit()", StringComparison.Ordinal);
        Assert.True(at > 0);

        string body = code[at..Math.Min(code.Length, at + 900)];
        int guard = body.IndexOf("if (_lineEdit is null) { return false; }", StringComparison.Ordinal);
        int end = body.IndexOf("EndInPlaceEdit();", StringComparison.Ordinal);
        int commit = body.IndexOf("CommitTextUnit(typed)", StringComparison.Ordinal);

        Assert.True(guard >= 0, "a commit with nothing open does not return early");
        Assert.True(end > guard && commit > end,
            "the edit is still open while the write runs");
    }

    [Fact]
    public void the_frame_is_padded_but_the_words_bounds_are_not()
    {
        // A word's bounds are the TIGHT box around its glyphs, which is what the
        // hit test and the reflow measurement need. Drawn at that size the rule
        // lands on the letterforms: an all-caps word has no descenders, so its
        // box stops at the baseline and the stroke cuts through the feet of the
        // type. The padding therefore belongs to the FRAME and nowhere else.
        string code = ViewModel();

        int outline = code.IndexOf("PageTextOutline.Add(", StringComparison.Ordinal);
        Assert.True(outline > 0, "the text frame is no longer drawn");

        // ⚠️ THE PADDING LIVES IN THE SELECTION NOW, NOT IN THE DRAWING CODE.
        // The overlay used to work it out itself and could have worked it out
        // wrong; TextBlockSelection.Frame is the one padded box and Contains is
        // defined in terms of it, so what is drawn and what the pointer can
        // enter cannot differ.
        Assert.Contains("var (bl, bt, br, bb) = block.Frame;", code, StringComparison.Ordinal);

        string frame = Source("PdfEditorApp.Viewport", "TextBlockSelection.cs");

        // Derived from the type's own height, not a fixed number of pixels, or
        // it would swamp small type and vanish on large.
        Assert.Contains("LineHeight * TextUnitSelection.FramePadXFactor", frame,
            StringComparison.Ordinal);
        Assert.Contains("LineHeight * TextUnitSelection.FramePadYFactor", frame,
            StringComparison.Ordinal);

        // And the geometry handed to the core stays the TIGHT box: the write
        // takes the cluster straight from the model, so a padded rect could
        // only get there by someone padding the model itself.
        Assert.Contains("WordClusterGateway.Write(_documentHandle, page, word, newText)",
            code, StringComparison.Ordinal);
    }

    [Fact]
    public void the_in_place_layer_is_never_hidden_from_what_it_draws()
    {
        // ⚠️ THE DEFECT THIS EXISTS FOR, KEPT. EditOverlay is Collapsed in
        // the XAML until an edit begins, and an editor added to it without
        // showing it was built, focused and typed into entirely invisibly:
        // every unit test passed, the core did the right thing, and the reader
        // saw nothing happen when they clicked.
        //
        // The in-place layer answers that by never being collapsed at all. It
        // holds nothing until there is something to draw, so there is no
        // visibility to forget to set.
        string xaml = Xaml();

        int at = xaml.IndexOf("InPlaceLayer", StringComparison.Ordinal);
        Assert.True(at > 0, "there is no in-place layer");

        string element = xaml[at..Math.Min(xaml.Length, at + 200)];
        Assert.DoesNotContain("Visibility=", element);

        // And what it draws is added to it, not to the dimmed layer.
        string draw = Page();
        int render = draw.IndexOf("private void RenderInPlaceEdit()", StringComparison.Ordinal);
        Assert.True(render > 0);
        string body = draw[render..Math.Min(draw.Length, render + 2000)];
        Assert.Contains("InPlaceLayer.Children.Clear();", body, StringComparison.Ordinal);
        Assert.DoesNotContain("EditCanvas", body);
        Assert.DoesNotContain("EditOverlay", body);
    }

    [Fact]
    public void editing_page_text_never_touches_the_dimmed_layer()
    {
        // ⚠️ THE SCRIM DIMS THE WHOLE PAGE, which is exactly why editing the
        // document's own text no longer goes anywhere near it. The page has to
        // stay visually stable while the reader works in it. Add Text still
        // uses that layer to place a NEW box, which is a different thing and
        // keeps the behaviour it had.
        string code = Page();

        int at = code.IndexOf("private void EditScrim_PointerPressed(", StringComparison.Ordinal);
        Assert.True(at > 0);
        string scrim = code[at..(at + 700)];

        Assert.Contains("CommitTextEdit();", scrim, StringComparison.Ordinal);
        Assert.DoesNotContain("InPlace", scrim);

        // Nothing anywhere reopens a TextBox over the document's own text.
        Assert.DoesNotContain("_unitEditor", code);
        Assert.DoesNotContain("OpenUnitEditor", code);
    }

    [Fact]
    public void clicking_away_commits_whichever_edit_is_open()
    {
        // Clicking off means "done" for both kinds, but they are reached in
        // different places now: Add Text through its scrim, the document's own
        // text through the ordinary press on the page, because there is no
        // scrim over it to click.
        string code = Page();

        int scrim = code.IndexOf(
            "private void EditScrim_PointerPressed(", StringComparison.Ordinal);
        Assert.Contains("CommitTextEdit();", code[scrim..(scrim + 700)], StringComparison.Ordinal);

        // ⚠️ AND THE DOCUMENT'S OWN TEXT IS COMMITTED BY THE MOVE. This
        // used to look for a CommitInPlaceEdit() of its own on this path, and
        // that call is gone because it had become unreachable: the move commits
        // before it looks at where the click landed, so by the time it returns
        // false there is nothing left to commit. What matters is unchanged and
        // is asserted here in two halves.
        int away = code.IndexOf("Any other press drops the box", StringComparison.Ordinal);
        Assert.True(away > 0);
        Assert.Contains("MoveInPlaceEditTo(",
            code[away..Math.Min(code.Length, away + 2000)], StringComparison.Ordinal);

        string vm = Source("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");
        int move = vm.IndexOf("public bool MoveInPlaceEditTo(", StringComparison.Ordinal);
        Assert.True(move > 0);
        Assert.Contains("CommitInPlaceEdit();",
            vm[move..Math.Min(vm.Length, move + 1400)], StringComparison.Ordinal);
    }

    [Fact]
    public void the_editor_sizes_type_in_points_and_geometry_in_normalized_units()
    {
        // ⚠️ TWO DIFFERENT SCALES SHARE THIS METHOD. A word's BOUNDS are
        // normalized (0..1 across the page) and convert with OverlayScale; its
        // FONT SIZE is an absolute point size and converts with DIPs-per-point.
        // Mixing them is not a small error: a 22pt word scaled by OverlayScale
        // asked for a font size of about 14,850 DIPs and filled the window with
        // two letters, which is exactly what happened.
        string code = Page();

        int open = code.IndexOf("private void RenderInPlaceEdit()", StringComparison.Ordinal);
        Assert.True(open > 0);
        string body = code[open..Math.Min(code.Length, open + 3600)];

        Assert.Contains("DipsPerPointOn(page)", body, StringComparison.Ordinal);
        Assert.Contains("ViewModel.OverlayScale", body, StringComparison.Ordinal);

        // The font size must come from the point converter, never from the
        // normalized one. Asserted as an exact string so the two cannot be
        // swapped back without this failing.
        Assert.Contains("tail.FontSizePts * dipsPerPoint", body, StringComparison.Ordinal);

        // And an unreadable page size means "cannot size this", not "scale by
        // nothing", which would collapse the editor to a sliver.
        Assert.Contains("dipsPerPoint <= 0", body, StringComparison.Ordinal);
    }

    private static int Count(string haystack, string needle)
    {
        int n = 0, at = 0;
        while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
        {
            n++;
            at += needle.Length;
        }
        return n;
    }
}
