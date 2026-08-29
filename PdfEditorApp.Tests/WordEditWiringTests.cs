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
        string code = Page();

        int open = code.IndexOf("private bool OpenUnitEditor(", StringComparison.Ordinal);
        Assert.True(open > 0);

        int next = code.IndexOf("\n    /// <summary>", open, StringComparison.Ordinal);
        string body = code[open..next];

        int guard = body.IndexOf("!unit.CanEdit", StringComparison.Ordinal);
        int build = body.IndexOf("new TextBox", StringComparison.Ordinal);

        Assert.True(guard > 0 && guard < build, "the editor is built before the unit is checked");
    }

    [Fact]
    public void closing_the_editor_cannot_re_enter_the_commit()
    {
        // Removing a focused TextBox raises LostFocus, and LostFocus commits.
        // Unhooking after removal would run the commit a second time, on an
        // editor that is already being torn down.
        string code = Page();

        int tear = code.IndexOf("private void TearDownUnitEditor()", StringComparison.Ordinal);
        Assert.True(tear > 0);

        string body = code[tear..(tear + 900)];
        int unhook = body.IndexOf("LostFocus -= UnitEditor_LostFocus", StringComparison.Ordinal);
        int remove = body.IndexOf("EditCanvas.Children.Remove", StringComparison.Ordinal);

        Assert.True(unhook > 0 && remove > unhook,
            "the editor is removed before its handlers are unhooked");
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
        Assert.True(outline > 0, "the word frame is no longer drawn");

        // Look back over the block that builds the rect.
        string block = code[Math.Max(0, outline - 1200)..outline];
        Assert.Contains("padX", block, StringComparison.Ordinal);
        Assert.Contains("padY", block, StringComparison.Ordinal);

        // And the padding must be derived from the word, not a fixed number of
        // pixels, or it would swamp small type and vanish on large.
        Assert.Contains("word.Bottom - word.Top", block, StringComparison.Ordinal);

        // And the geometry handed to the core stays the tight box: the write
        // takes the cluster straight from the model, so a padded rect could
        // only get there by someone padding the model itself.
        Assert.Contains("WordClusterGateway.Write(_documentHandle, page, word, newText)",
            code, StringComparison.Ordinal);
    }

    [Fact]
    public void opening_the_editor_shows_the_layer_it_is_built_on()
    {
        // ⚠️ THE DEFECT THIS EXISTS FOR. EditOverlay is Collapsed in the XAML
        // until an edit begins. An editor added to it without showing it is
        // built, focused and typed into entirely invisibly: every unit test
        // passes, the core does the right thing, and the reader sees nothing
        // happen when they click.
        string code = Page();

        int open = code.IndexOf("private bool OpenUnitEditor(", StringComparison.Ordinal);
        int next = code.IndexOf("/// <summary>", open, StringComparison.Ordinal);
        string body = code[open..next];

        Assert.Contains("EditOverlay.Visibility = Visibility.Visible",
            body, StringComparison.Ordinal);

        int add = body.IndexOf("EditCanvas.Children.Add", StringComparison.Ordinal);
        int show = body.IndexOf("EditOverlay.Visibility", StringComparison.Ordinal);
        Assert.True(add > 0 && show > add, "the layer is shown before the editor exists");
    }

    [Fact]
    public void closing_the_editor_hides_the_layer_but_not_from_under_the_other_one()
    {
        // The scrim dims the whole page, so leaving the layer up would grey the
        // document for the rest of the session. The text-box editor shares this
        // layer, though, so hiding it unconditionally would close that one too.
        string code = Page();

        int tear = code.IndexOf("private void TearDownUnitEditor()", StringComparison.Ordinal);
        string body = code[tear..(tear + 1200)];

        Assert.Contains("_textEditor is null", body, StringComparison.Ordinal);
        Assert.Contains("EditOverlay.Visibility = Visibility.Collapsed",
            body, StringComparison.Ordinal);
    }

    [Fact]
    public void clicking_away_commits_either_kind_of_editor()
    {
        // Both live on the same dimmed layer, and clicking off the box means
        // the same thing for both.
        string code = Page();

        int scrim = code.IndexOf(
            "private void EditScrim_PointerPressed(", StringComparison.Ordinal);
        string body = code[scrim..(scrim + 600)];

        Assert.Contains("CommitTextEdit();", body, StringComparison.Ordinal);
        Assert.Contains("CommitUnitEdit();", body, StringComparison.Ordinal);
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

        int open = code.IndexOf("private bool OpenUnitEditor(", StringComparison.Ordinal);
        int next = code.IndexOf("/// <summary>", open, StringComparison.Ordinal);
        string body = code[open..next];

        Assert.Contains("DipsPerPointOn(unit.Page)", body, StringComparison.Ordinal);

        // The font size must come from the point converter, never from the
        // normalized one. Asserted as an exact string so the two cannot be
        // swapped back without this failing.
        Assert.Contains("FontSize = Math.Max(8, fontDip)",
            body, StringComparison.Ordinal);

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
