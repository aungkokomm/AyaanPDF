using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The buffer the core writes, decoded, and the call sites that carry it.
///
/// The decode is a real unit test. The call sites are read out of the source,
/// because they live in the WinUI project and a net10.0 test assembly cannot
/// load one; that is the same bargain every other wiring test here makes.
/// </summary>
public class PageTextObjectWiringTests
{
    /// <summary>Builds a buffer the way render_core lays one out.</summary>
    private static byte[] Buffer(params (int Index, string Font, string Text)[] objects)
    {
        var bytes = new List<byte>();
        bytes.AddRange(BitConverter.GetBytes((uint)objects.Length));

        foreach (var (index, font, text) in objects)
        {
            bytes.AddRange(BitConverter.GetBytes((uint)index));
            bytes.AddRange(BitConverter.GetBytes(0.1f));   // left
            bytes.AddRange(BitConverter.GetBytes(0.2f));   // top
            bytes.AddRange(BitConverter.GetBytes(0.5f));   // right
            bytes.AddRange(BitConverter.GetBytes(0.3f));   // bottom
            bytes.AddRange(BitConverter.GetBytes(12.5f));  // size
            bytes.AddRange(BitConverter.GetBytes(0x00AABBCCu));
            bytes.AddRange(BitConverter.GetBytes(1u));     // flags: embedded

            byte[] f = Encoding.UTF8.GetBytes(font);
            bytes.AddRange(BitConverter.GetBytes((uint)f.Length));
            bytes.AddRange(f);

            byte[] t = Encoding.UTF8.GetBytes(text);
            bytes.AddRange(BitConverter.GetBytes((uint)t.Length));
            bytes.AddRange(t);
        }

        return bytes.ToArray();
    }

    [Fact]
    public void the_buffer_decodes_field_for_field()
    {
        var found = PageTextObjectReader.Parse(Buffer((7, "Helvetica-Bold", "Chapter One")));

        var t = Assert.Single(found);
        Assert.Equal(7, t.ObjectIndex);
        Assert.Equal(0.1, t.Left, 5);
        Assert.Equal(0.2, t.Top, 5);
        Assert.Equal(0.5, t.Right, 5);
        Assert.Equal(0.3, t.Bottom, 5);
        Assert.Equal(12.5, t.FontSizePts, 5);
        Assert.Equal(0x00AABBCCu, t.ColorRgb);
        Assert.True(t.IsFontEmbedded);
        Assert.Equal("Helvetica-Bold", t.FontName);
        Assert.Equal("Chapter One", t.Text);
    }

    [Fact]
    public void several_objects_decode_in_order()
    {
        var found = PageTextObjectReader.Parse(
            Buffer((0, "A", "first"), (1, "B", "second"), (2, "C", "third")));

        Assert.Equal(3, found.Count);
        Assert.Equal(new[] { "first", "second", "third" },
            new[] { found[0].Text, found[1].Text, found[2].Text });
    }

    [Fact]
    public void text_outside_ascii_survives_the_trip()
    {
        // The strings are UTF-8 on the wire and the whole feature is about
        // words. A font name with a subset tag and text with real diacritics
        // are both ordinary here.
        var found = PageTextObjectReader.Parse(
            Buffer((0, "ABCDEF+NotoSans", "café — déjà vu")));

        Assert.Equal("café — déjà vu", Assert.Single(found).Text);
    }

    [Fact]
    public void an_empty_buffer_is_no_objects_and_not_a_crash()
    {
        Assert.Empty(PageTextObjectReader.Parse(Buffer()));
        Assert.Empty(PageTextObjectReader.Parse([]));
        Assert.Empty(PageTextObjectReader.Parse(null!));
    }

    [Fact]
    public void a_truncated_buffer_keeps_what_it_could_read()
    {
        // A short buffer means this build and the core disagree about the
        // layout, which is a bug, but not one worth taking the app down for:
        // the objects already decoded are still correct.
        byte[] whole = Buffer((0, "A", "first"), (1, "B", "second"));

        var found = PageTextObjectReader.Parse(whole[..(whole.Length - 3)]);

        Assert.Single(found);
        Assert.Equal("first", found[0].Text);
    }

    [Fact]
    public void a_buffer_claiming_more_than_it_holds_does_not_run_off_the_end()
    {
        byte[] lying = Buffer((0, "A", "first"));
        BitConverter.GetBytes(500u).CopyTo(lying, 0);

        Assert.Single(PageTextObjectReader.Parse(lying));
    }

    // ---------------- the call sites ----------------

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

    [Fact]
    public void the_model_is_built_with_the_pages_own_text()
    {
        string code = ViewModel();

        Assert.Contains("PageTextObjectLoader.Load(", code, StringComparison.Ordinal);
        Assert.Contains(
            "DocumentModelBuilder.BuildPage(pageIndex, snapshots, pageWidthPts, pageText)",
            code, StringComparison.Ordinal);
    }

    [Fact]
    public void selecting_page_text_does_not_change_what_the_annotation_pick_reports()
    {
        // Stage 1 adds a frame and takes nothing away. False still has to mean
        // "no annotation was picked", because that is what starts the selection
        // marquee and the drag-to-select-text every reader already has.
        string code = ViewModel();

        int call = code.IndexOf("SelectPageTextAt(pageIndex, normX, normY);", StringComparison.Ordinal);
        Assert.True(call > 0, "the page-text pick is never reached from a click");

        int returns = code.IndexOf("return false;", call, StringComparison.Ordinal);
        int nextMethod = code.IndexOf("\n    public ", call, StringComparison.Ordinal);

        Assert.True(
            returns > call && (nextMethod < call || returns < nextMethod),
            "the click path no longer returns false after picking page text");

        // And it is a statement, not a condition: wrapping it in an if that
        // returned true would take the marquee and text selection away.
        Assert.DoesNotContain(
            "if (SelectPageTextAt(", code, StringComparison.Ordinal);
    }

    [Fact]
    public void clearing_the_selection_clears_the_page_text_too()
    {
        // It is an independent field, so it is not covered by the guard that
        // protects the annotation one; a leftover frame would survive every
        // "clear the selection" in the app.
        string code = ViewModel();

        int clear = code.IndexOf("public void ClearAnnotationSelection()", StringComparison.Ordinal);
        Assert.True(clear > 0);

        int drops = code.IndexOf("ClearPageTextSelection();", clear, StringComparison.Ordinal);
        int guard = code.IndexOf("_selectedAnnotationId is null", clear, StringComparison.Ordinal);

        Assert.True(drops > clear && drops < guard,
            "the page-text selection is cleared after the early return, so it can survive");
    }

    [Fact]
    public void nothing_plans_a_reorder_against_the_whole_object_list()
    {
        // THE REGRESSION THIS LOCKS DOWN. Both z-order sites read the model's
        // whole object list, which since Stage 1 also holds the document's own
        // text. Page text has no id, so each one arrived as Guid.Empty in an
        // order that is rewritten by position, and "Send to back" refused on
        // every page containing a word.
        //
        // Asserted on the SOURCE because these live in the WinUI project, and
        // as an absence because the mistake is easy to make again: .Objects and
        // .Annotations read the same at a glance and differ only on real pages.
        string code = ViewModel();

        Assert.DoesNotContain("PageModelFor(page).Objects", code, StringComparison.Ordinal);
        Assert.Equal(2, code.Split("PageModelFor(page).Annotations").Length - 1);
    }

    [Fact]
    public void the_frame_does_not_tint_the_words_it_is_around()
    {
        // The reader's own document is underneath this frame. A fill, at any
        // alpha, sits OVER the glyphs and changes the colour of text they came
        // to read; the first version washed every selected line in pale purple.
        // A dashed rule reads as provisional, which selection chrome in a PDF
        // editor is not. One thin solid rule, no fill.
        string xaml = Source("PdfEditorApp", "MainPage.xaml");

        int at = xaml.IndexOf("{x:Bind PageTextOutline}", StringComparison.Ordinal);
        Assert.True(at > 0, "the page-text frame is no longer bound");

        int end = xaml.IndexOf("</ItemsControl>", at, StringComparison.Ordinal);
        string frame = xaml[at..end];

        Assert.DoesNotContain("Fill=", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("StrokeDashArray", frame, StringComparison.Ordinal);
        Assert.Contains("StrokeThickness=\"1\"", frame, StringComparison.Ordinal);
    }

    [Fact]
    public void the_frame_is_drawn_from_its_own_collection()
    {
        // Separate from SelectionOutline on purpose: that frame promises
        // dragging, resizing and deleting, and none of those exist here yet.
        string xaml = Source("PdfEditorApp", "MainPage.xaml");

        Assert.Contains("{x:Bind PageTextOutline}", xaml, StringComparison.Ordinal);
        Assert.Contains("PageTextOutline", Source("PdfEditorApp", "ViewModels", "PageSlot.cs"),
            StringComparison.Ordinal);
    }
}
