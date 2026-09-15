using System;
using System.IO;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Adding, renaming and deleting one bookmark from the panel.
///
/// <see cref="OutlineEditsTests"/> covers what the edits DO; this covers the
/// wiring this assembly cannot load, and the two mistakes that would be
/// invisible until they bit: acting on the wrong row, and an icon that says
/// the same thing as the button beside it.
/// </summary>
public class BookmarkEditingWiringTests
{
    private static string Read(params string[] relative)
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

    private static string Xaml() => Read("PdfEditorApp", "MainPage.xaml");
    private static string Code() => Read("PdfEditorApp", "MainPage.xaml.cs");

    private static string MethodBody(string source, string signature)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{signature}' is gone; this test needs rewriting to match");

        int next = source.IndexOf("\n    private ", at + signature.Length, StringComparison.Ordinal);
        int alt = source.IndexOf("\n    public ", at + signature.Length, StringComparison.Ordinal);
        if (alt >= 0 && (next < 0 || alt < next))
        {
            next = alt;
        }

        return next > at ? source[at..next] : source[at..];
    }

    [Fact]
    public void ctrl_b_bookmarks_the_page()
    {
        // Where every reader and every browser puts it. Safe here because this
        // app has no bold: a text box's weight comes from the font it is set in.
        Assert.Equal(
            EditorCommand.AddBookmark,
            KeyboardCommands.Resolve(KeyboardCommands.KeyB, ctrl: true, shift: false, textFocused: false));

        // And not while typing, where Ctrl+B belongs to the text box.
        Assert.Equal(
            EditorCommand.None,
            KeyboardCommands.Resolve(KeyboardCommands.KeyB, ctrl: true, shift: false, textFocused: true));

        // Nor unmodified, which is just the letter b.
        Assert.Equal(
            EditorCommand.None,
            KeyboardCommands.Resolve(KeyboardCommands.KeyB, ctrl: false, shift: false, textFocused: false));
    }

    [Fact]
    public void the_chord_reaches_a_handler()
    {
        Assert.Contains("case EditorCommand.AddBookmark:", Code(), StringComparison.Ordinal);
        Assert.Contains("AddBookmarkHere()", Code(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("AddBookmark_Click")]
    [InlineData("RenameBookmark_Click")]
    [InlineData("DeleteBookmark_Click")]
    public void every_control_is_wired_to_something(string handler)
    {
        Assert.Contains($"Click=\"{handler}\"", Xaml(), StringComparison.Ordinal);
        Assert.Contains(handler, Code(), StringComparison.Ordinal);
    }

    [Fact]
    public void rename_and_delete_act_on_the_row_that_was_right_clicked()
    {
        // NOT on the list's selection. A right-click opens a flyout without
        // selecting the row, so the selection is whatever was clicked last, and
        // renaming would quietly rewrite a different bookmark.
        string body = MethodBody(Code(), "private (int Index, BookmarkItem Item)? BookmarkOf");

        Assert.Contains("DataContext: BookmarkItem item", body, StringComparison.Ordinal);
        Assert.Contains("ViewModel.Bookmarks.IndexOf(item)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedItem", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_title_comes_from_the_selection_and_the_page_from_where_it_is()
    {
        // The page the SELECTION is on, not the one in view: a reader who
        // selected a heading and then scrolled means the heading.
        string body = MethodBody(Code(), "private async Task AddBookmarkHere");

        Assert.Contains("ViewModel.SelectionStart?.Page", body, StringComparison.Ordinal);
        Assert.Contains("OutlineEdits.TitleFrom(", body, StringComparison.Ordinal);
    }

    [Fact]
    public void selecting_nothing_asks_for_a_name_rather_than_refusing()
    {
        // Wanting to mark the page in front of you is at least as common as
        // wanting to mark a heading you can select.
        string body = MethodBody(Code(), "private async Task AddBookmarkHere");

        Assert.Contains("AskForBookmarkName(", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_name_dialog_takes_enter()
    {
        // A single-line TextBox does not handle Enter, so the dialog's default
        // button gets it. Hiding the dialog by hand on Enter would return
        // "cancelled" and silently throw the name away.
        string xaml = Xaml();
        int at = xaml.IndexOf("x:Name=\"BookmarkNameDialog\"", StringComparison.Ordinal);
        Assert.True(at >= 0);

        Assert.Contains("DefaultButton=\"Primary\"", xaml[at..(at + 300)], StringComparison.Ordinal);
        Assert.DoesNotContain("BookmarkNameBox_KeyDown", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void the_suggested_name_is_selected_so_typing_replaces_it()
    {
        string body = MethodBody(Code(), "private async Task<string?> AskForBookmarkName");

        Assert.Contains("BookmarkNameBox.SelectAll()", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_bookmarks_panel_no_longer_wears_a_list_icon()
    {
        // E8A4, which this app used for it, is a BULLETED LIST in Segoe Fluent
        // Icons: near enough identical to the list on the button beside it, so
        // the panel and "make bookmarks from headings" looked like one thing.
        // The ribbon is drawn instead, because the font has no bookmark glyph.
        string xaml = Xaml();

        Assert.DoesNotContain("&#xE8A4;", xaml, StringComparison.Ordinal);
        Assert.Contains("BookmarkRibbonPath", xaml, StringComparison.Ordinal);
        Assert.Contains("BookmarkAddPath", xaml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("BookmarkRibbonPath")]
    [InlineData("BookmarkAddPath")]
    public void a_drawn_icon_is_given_a_box_big_enough_to_hold_it(string key)
    {
        // ⚠️ A PathIcon does NOT scale its geometry to the Width and Height it
        // is given: those CLIP it. The first version of these ran to x=17 in a
        // 15-wide box and shipped with its right-hand edge cut off, which no
        // build or test noticed because both are perfectly valid XAML.
        string xaml = Xaml();

        int at = xaml.IndexOf($"x:Key=\"{key}\"", StringComparison.Ordinal);
        Assert.True(at >= 0, $"{key} is gone");
        int end = xaml.IndexOf("</x:String>", at, StringComparison.Ordinal);
        string data = xaml[at..end];

        double widest = 0;
        double tallest = 0;
        foreach (System.Text.RegularExpressions.Match pair in
                 System.Text.RegularExpressions.Regex.Matches(data, @"(-?\d+(?:\.\d+)?),(-?\d+(?:\.\d+)?)"))
        {
            widest = Math.Max(widest, double.Parse(pair.Groups[1].Value,
                System.Globalization.CultureInfo.InvariantCulture));
            tallest = Math.Max(tallest, double.Parse(pair.Groups[2].Value,
                System.Globalization.CultureInfo.InvariantCulture));
        }

        Assert.True(widest > 0 && tallest > 0, $"{key} has no coordinates to check");

        // Every PathIcon drawing this geometry has to give it room.
        var used = System.Text.RegularExpressions.Regex.Matches(
            xaml,
            $@"<PathIcon Data=""{{StaticResource {key}}}"" Width=""([\d.]+)"" Height=""([\d.]+)""");

        Assert.True(used.Count > 0, $"{key} is declared but nothing draws it");

        foreach (System.Text.RegularExpressions.Match icon in used)
        {
            double width = double.Parse(icon.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            double height = double.Parse(icon.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);

            Assert.True(width >= widest, $"{key} reaches x={widest} in a box {width} wide, so it is clipped");
            Assert.True(height >= tallest, $"{key} reaches y={tallest} in a box {height} tall, so it is clipped");
        }
    }

    [Theory]
    [InlineData("BookmarkRibbonPath")]
    [InlineData("BookmarkAddPath")]
    public void a_drawn_icon_starts_at_the_origin(string key)
    {
        // Because the box clips rather than scales, geometry that starts at
        // x=3 wastes three units of the box and pushes the far edge out of it.
        string xaml = Xaml();
        int at = xaml.IndexOf($"x:Key=\"{key}\"", StringComparison.Ordinal);
        Assert.True(at >= 0);

        int start = xaml.IndexOf("F0 M", at, StringComparison.Ordinal);
        Assert.True(start > 0, $"{key} does not begin with an even-odd move");
        Assert.StartsWith("F0 M0,0", xaml[start..(start + 8)], StringComparison.Ordinal);
    }

    [Fact]
    public void the_drawn_icons_are_rings_rather_than_solids()
    {
        // PathIcon FILLS its geometry, so an outline has to be described as a
        // ring with an even-odd fill. Without the F0 the ribbon renders as a
        // solid black blob among line-art neighbours.
        string xaml = Xaml();

        foreach (string key in new[] { "BookmarkRibbonPath", "BookmarkAddPath" })
        {
            int at = xaml.IndexOf($"x:Key=\"{key}\"", StringComparison.Ordinal);
            Assert.True(at >= 0, $"{key} is gone");

            string data = xaml[at..(at + 400)];
            Assert.Contains("F0 ", data, StringComparison.Ordinal);

            // Two subpaths: the outside and the hole.
            int firstClose = data.IndexOf(" Z", StringComparison.Ordinal);
            Assert.True(firstClose > 0);
            Assert.Contains(" Z", data[(firstClose + 2)..], StringComparison.Ordinal);
        }
    }

    private static string ViewModel() => Read("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");

    [Fact]
    public void an_edit_changes_the_outline_in_memory_rather_than_the_file()
    {
        // Writing an outline rewrites the FILE: PDFium cannot create a
        // bookmark, so the document is closed, the tree written by lopdf, and
        // reopened. Measured at two seconds on a 54 MB book. Per keystroke that
        // made Ctrl+B a freeze on exactly the documents worth bookmarking, and
        // forced a save nobody asked for.
        string body = MethodBody(ViewModel(), "private string? EditOutline");

        Assert.Contains("_pendingOutline = ", body, StringComparison.Ordinal);
        Assert.Contains("ShowOutline(edited)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyOutline", body, StringComparison.Ordinal);
    }

    [Fact]
    public void an_edit_makes_the_document_dirty_so_the_existing_prompt_covers_it()
    {
        // Nothing else has to learn that bookmarks exist: the dirty dot, the
        // unsaved-changes prompt on close and Ctrl+S all key off this one flag.
        Assert.Contains("IsDirty = true;", MethodBody(ViewModel(), "private string? EditOutline"),
                        StringComparison.Ordinal);
    }

    [Fact]
    public void an_unsaved_document_is_refused_before_the_edits_pile_up()
    {
        // Told at the first edit rather than at the write, so a reader does not
        // make a dozen bookmarks that have nowhere to go.
        string body = MethodBody(ViewModel(), "private string? EditOutline");

        Assert.Contains("_currentDocumentPath is null", body, StringComparison.Ordinal);
        Assert.Contains("Save the document first", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_outline_is_written_last_when_the_document_is_saved()
    {
        // The writer works on a CLOSED file and closes and reopens the document
        // to do it, so everything else in the save has to have finished with
        // the handle first.
        string body = MethodBody(ViewModel(), "public bool SaveDocumentAs(string path, bool flatten)");

        // Handed the outline taken before the save: reopening the file clears
        // the pending one. See BookmarksSurvivePageChangesWiringTests.
        int flush = body.IndexOf("FlushPendingOutline(outlineToWrite);", StringComparison.Ordinal);
        Assert.True(flush >= 0, "a save no longer writes pending bookmarks, so they would be lost");

        int save = body.IndexOf("RenderCoreNative.save_document(", StringComparison.Ordinal);
        Assert.True(save >= 0 && save < flush, "the outline is written before the document itself");
    }

    [Fact]
    public void a_failed_write_does_not_stay_pending_forever()
    {
        // Cleared before the attempt. Otherwise a file that cannot take an
        // outline would make every future save try again and fail again.
        string body = MethodBody(ViewModel(), "private void FlushPendingOutline");

        int cleared = body.IndexOf("_pendingOutline = null;", StringComparison.Ordinal);
        int applied = body.IndexOf("ApplyOutline(outline)", StringComparison.Ordinal);

        Assert.True(cleared >= 0 && applied > cleared,
                    "the pending outline is not cleared before it is written");
    }

    [Fact]
    public void the_bulk_features_overtake_pending_hand_edits()
    {
        // "Create bookmarks by style" REPLACES the outline. Leaving hand edits
        // pending would let the next save quietly put the old outline back over
        // the new one.
        Assert.Contains("_pendingOutline = null;",
                        MethodBody(ViewModel(), "public string? ApplyOutline"),
                        StringComparison.Ordinal);
    }

    [Fact]
    public void opening_a_document_drops_another_documents_pending_edits()
    {
        Assert.Contains("_pendingOutline = null;", MethodBody(ViewModel(), "private void LoadBookmarks"),
                        StringComparison.Ordinal);
    }

    [Fact]
    public void deleting_one_bookmark_does_not_ask_first()
    {
        // One entry is a small loss and remaking it is one keystroke. A prompt
        // on every delete is what teaches people to stop reading prompts.
        string body = MethodBody(Code(), "private async void DeleteBookmark_Click");

        Assert.DoesNotContain("ContentDialog", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Are you sure", body, StringComparison.Ordinal);
    }
}
