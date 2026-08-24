using System;
using System.Collections.Generic;
using System.Linq;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The document's own text, as an object in the model.
///
/// STAGE 1: found, modelled, hit-tested and selectable. Nothing edits it, so
/// everything here is about the model answering "what is under this point" for
/// marks the app did not make, and about the one rule that keeps that safe.
///
/// THE RULE: a page text object's index is a position in the page's CONTENT,
/// and every other object in this model carries a position in the page's
/// ANNOTATION list. The app hands that second number to calls that move,
/// restyle and delete annotations. Letting a page text object out of the
/// annotation pick would not fail loudly, it would silently edit an unrelated
/// mark, so several of the tests below exist only to hold that line.
/// </summary>
public class PageTextObjectTests
{
    private const double PageW = 600;

    private static PageTextSnapshot Text(
        int index, double l, double t, double r, double b,
        string words = "words", string font = "Helvetica", double size = 12,
        bool embedded = false) =>
        new(index, l, t, r, b, size, 0x112233u, embedded, font, words);

    private static AnnotationSnapshot Highlight(double l, double t, double r, double b) =>
        new(0, PdfAnnotationSubtype.Highlight, l, t, r, b, 1.0, Guid.NewGuid(), null);

    private static PageModel Page(
        IReadOnlyList<PageTextSnapshot>? text = null,
        IReadOnlyList<AnnotationSnapshot>? annotations = null) =>
        DocumentModelBuilder.BuildPage(0, annotations ?? [], PageW, text ?? []);

    // ---------------- into the model ----------------

    [Fact]
    public void the_pages_own_text_becomes_an_object_in_the_model()
    {
        var page = Page([Text(3, 0.1, 0.1, 0.5, 0.2, "Chapter One", "Helvetica-Bold", 18, true)]);

        var t = Assert.IsType<PageTextObject>(Assert.Single(page.Objects));

        Assert.Equal("Chapter One", t.Text);
        Assert.Equal("Helvetica-Bold", t.FontName);
        Assert.Equal(18, t.FontSizePts);
        Assert.True(t.IsFontEmbedded);
        Assert.Equal(0x112233u, t.ColorRgb);
        Assert.Equal(DocumentObjectKind.PageText, t.Kind);
    }

    [Fact]
    public void it_carries_the_content_index_and_not_an_annotation_index()
    {
        // THE WHOLE SAFETY STORY IN ONE ASSERTION. ZOrder is a position in the
        // model's own list; ObjectIndex is the position PDFium will accept for
        // this object. They are different numbers and must not be confused.
        var page = Page([Text(7, 0.1, 0.1, 0.5, 0.2)], [Highlight(0.6, 0.6, 0.8, 0.7)]);

        var t = page.PageTexts.Single();

        Assert.Equal(7, t.ObjectIndex);
        Assert.Equal(0, t.ZOrder);
    }

    [Fact]
    public void page_content_sits_under_every_annotation()
    {
        // A page's own text is drawn as part of the page and every annotation
        // is drawn over the finished page, so the model's paint order has to
        // say so or the hit test picks the wrong thing where they overlap.
        var page = Page(
            [Text(0, 0.1, 0.1, 0.5, 0.2), Text(1, 0.1, 0.3, 0.5, 0.4)],
            [Highlight(0.1, 0.1, 0.5, 0.2)]);

        Assert.Equal(3, page.Objects.Count);
        Assert.IsType<PageTextObject>(page.Objects[0]);
        Assert.IsType<PageTextObject>(page.Objects[1]);
        Assert.IsNotType<PageTextObject>(page.Objects[2]);
    }

    [Fact]
    public void a_document_with_no_page_text_is_exactly_what_it_was_before()
    {
        // Stage 1 must be invisible to everything that already worked.
        var withArgument = DocumentModelBuilder.BuildPage(
            0, [Highlight(0.1, 0.1, 0.5, 0.2)], PageW, []);
        var without = DocumentModelBuilder.BuildPage(
            0, [Highlight(0.1, 0.1, 0.5, 0.2)], PageW);

        Assert.Single(withArgument.Objects);
        Assert.Equal(without.Objects.Count, withArgument.Objects.Count);
        Assert.Empty(withArgument.PageTexts);
    }

    [Fact]
    public void the_annotations_view_leaves_page_text_out()
    {
        var page = Page([Text(0, 0.1, 0.1, 0.5, 0.2)], [Highlight(0.6, 0.6, 0.8, 0.7)]);

        Assert.Single(page.Annotations);
        Assert.Single(page.PageTexts);
        Assert.Equal(2, page.Objects.Count);
    }

    // ---------------- hit-testing ----------------

    [Fact]
    public void a_click_on_the_words_finds_them()
    {
        var page = Page([Text(4, 0.1, 0.1, 0.5, 0.2, "Chapter One")]);

        var hit = ObjectHitTest.PickTopmostPageText(page, 0.3, 0.15);

        Assert.NotNull(hit);
        Assert.Equal("Chapter One", hit!.Text);
        Assert.Equal(4, hit.ObjectIndex);
    }

    [Fact]
    public void a_click_on_empty_page_finds_nothing()
    {
        var page = Page([Text(0, 0.1, 0.1, 0.5, 0.2)]);

        Assert.Null(ObjectHitTest.PickTopmostPageText(page, 0.8, 0.8));
    }

    [Fact]
    public void the_annotation_pick_refuses_to_return_page_text()
    {
        // THE LINE THAT MATTERS. Its caller turns whatever comes back into an
        // AnnotationBox whose Index is the object's ZOrder and hands that to
        // annotation calls. A page text object leaking through here edits a
        // different mark, or none.
        var page = Page([Text(0, 0.1, 0.1, 0.5, 0.2)]);

        Assert.Null(ObjectHitTest.PickTopmost(page, 0.3, 0.15));
        Assert.NotNull(ObjectHitTest.PickTopmostPageText(page, 0.3, 0.15));
    }

    [Fact]
    public void an_annotation_over_the_words_is_still_picked_by_the_annotation_pick()
    {
        // The two picks are independent, so covering text with a highlight must
        // not stop the highlight being selectable.
        var page = Page([Text(0, 0.1, 0.1, 0.5, 0.2)], [Highlight(0.1, 0.1, 0.5, 0.2)]);

        Assert.NotNull(ObjectHitTest.PickTopmost(page, 0.3, 0.15));
    }

    [Fact]
    public void the_topmost_of_two_overlapping_pieces_of_text_wins()
    {
        // Later objects are painted over earlier ones, so the search runs
        // backwards, exactly as it does for annotations.
        var page = Page([
            Text(0, 0.1, 0.1, 0.5, 0.2, "under"),
            Text(1, 0.1, 0.1, 0.5, 0.2, "over"),
        ]);

        Assert.Equal("over", ObjectHitTest.PickTopmostPageText(page, 0.3, 0.15)!.Text);
    }

    [Fact]
    public void text_is_hit_across_its_whole_box_and_not_only_on_its_outline()
    {
        // A word is a solid thing to click, not a frame to trace. The default
        // case in ObjectHitTest.Hit already does this; the test is here because
        // it would be easy to "improve" it into a rectangle-edge test.
        var page = Page([Text(0, 0.2, 0.2, 0.8, 0.3)]);

        foreach (var (x, y) in new[] { (0.25, 0.25), (0.5, 0.25), (0.75, 0.25) })
        {
            Assert.NotNull(ObjectHitTest.PickTopmostPageText(page, x, y));
        }
    }

    // ---------------- what the reader is told ----------------

    [Fact]
    public void an_empty_page_model_is_safe_to_ask()
    {
        Assert.Null(ObjectHitTest.PickTopmostPageText(null, 0.5, 0.5));
        Assert.Empty(Page().PageTexts);
    }
}
