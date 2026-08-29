using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Underline and strikeout: the highlight writer with a different subtype.
///
/// One tool row and one drag, the way the shape tool holds four shapes. What
/// differs per kind is the subtype written into the PDF and the rectangle
/// DRAWN on screen. The marked band, the colour, its alpha, the hit test,
/// undo, save and delete are all the highlight's, unchanged.
/// </summary>
public class MarkupKindTests
{
    private static readonly IReadOnlyList<TextRect> Band =
        new[] { new TextRect(0.10, 0.20, 0.50, 0.24) };

    private static HighlightAnnotation Mark(MarkupKind kind) =>
        new(0, Band, "#66FFD400") { Kind = kind };

    // ---------------- what gets drawn ----------------

    [Fact]
    public void a_highlight_still_fills_the_whole_band()
    {
        // The existing behaviour, and the reason Kind is an init property with
        // a default: every construction site that meant Highlight keeps meaning
        // it without being edited.
        var drawn = Assert.Single(Mark(MarkupKind.Highlight).ColoredRects);

        Assert.Equal(0.20, drawn.Top, 6);
        Assert.Equal(0.24, drawn.Bottom, 6);
        Assert.Equal(new HighlightAnnotation(0, Band, "#66FFD400").ColoredRects[0], drawn);
    }

    [Fact]
    public void an_underline_is_a_rule_at_the_foot_of_the_band()
    {
        var drawn = Assert.Single(Mark(MarkupKind.Underline).ColoredRects);

        Assert.Equal(0.24, drawn.Bottom, 6);
        Assert.True(drawn.Top > 0.23, $"the rule reaches too far up the band: {drawn.Top}");
        Assert.Equal(0.10, drawn.Left, 6);
        Assert.Equal(0.50, drawn.Right, 6);
    }

    [Fact]
    public void a_strikeout_is_a_rule_through_the_middle_of_the_band()
    {
        var drawn = Assert.Single(Mark(MarkupKind.Strikeout).ColoredRects);
        double middle = (0.20 + 0.24) / 2;

        Assert.True(drawn.Top < middle && drawn.Bottom > middle,
            $"the rule misses the middle: {drawn.Top} to {drawn.Bottom}");
        Assert.True(drawn.Bottom - drawn.Top < 0.24 - 0.20,
            "a strikeout must be thinner than the band it crosses");
    }

    [Fact]
    public void the_marked_band_itself_is_the_same_whatever_the_kind()
    {
        // ⚠️ THE MOVE THAT KEPT THIS SMALL. Rects stays the text band, because
        // that is what the hit test, the selection frame and the write to the
        // core all need. Only what is DRAWN varies, so the overlay keeps its
        // one filled-rectangle template and gains no code at all.
        foreach (var kind in new[]
                 { MarkupKind.Highlight, MarkupKind.Underline, MarkupKind.Strikeout })
        {
            var mark = Mark(kind);

            Assert.Equal(Band, mark.Rects);
            Assert.True(mark.HitTest(0.30, 0.22, 0), $"{kind} lost its clickable band");
            Assert.Equal("#66FFD400", mark.ColoredRects[0].ColorHex);
        }
    }

    [Fact]
    public void a_move_carries_the_kind_with_it()
    {
        var moved = (HighlightAnnotation)Mark(MarkupKind.Strikeout).Translate(0.05, 0.01);

        Assert.Equal(MarkupKind.Strikeout, moved.Kind);
    }

    // ---------------- what the file says ----------------

    [Fact]
    public void the_three_kinds_carry_the_numbers_the_core_writes()
    {
        Assert.Equal(0, (int)MarkupKind.Highlight);
        Assert.Equal(1, (int)MarkupKind.Underline);
        Assert.Equal(2, (int)MarkupKind.Strikeout);
    }

    [Fact]
    public void an_underline_or_strikeout_in_a_file_is_recognised_and_not_unknown()
    {
        // Left in Unknown they would carry z-order and nothing else: no
        // selection, no move, no delete.
        var page = DocumentModelBuilder.BuildPage(0,
        [
            new AnnotationSnapshot(0, PdfAnnotationSubtype.Underline,
                0.1, 0.2, 0.5, 0.24, 1.0, Guid.NewGuid(), null),
            new AnnotationSnapshot(1, PdfAnnotationSubtype.Strikeout,
                0.1, 0.3, 0.5, 0.34, 1.0, Guid.NewGuid(), null),
        ]);

        Assert.Equal(DocumentObjectKind.Underline, page.Objects[0].Kind);
        Assert.Equal(DocumentObjectKind.Strikeout, page.Objects[1].Kind);
    }

    // ---------------- the boundary ----------------

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

    [Fact]
    public void both_languages_agree_on_what_the_spec_measures()
    {
        // ⚠️ THE ONLY THING THAT CATCHES A FIELD ADDED TO ONE SIDE ONLY.
        // HighlightSpec crosses the FFI by layout alone: neither compiler can
        // see the other, and a struct that has grown on one side reads every
        // colour and count after the divergence from the wrong offset. It marks
        // the document with rubbish rather than failing.
        //
        // The app assembly cannot be referenced from here, so both sizes are
        // read from the source of both languages and compared.
        string rust = Read("render_core", "src", "lib.rs");
        string cs = Read("PdfEditorApp", "Interop", "RenderCoreNative.cs");

        var fromRust = Regex.Match(rust, "MARKUP_SPEC_BYTES: usize = ([0-9]+)");
        var fromCs = Regex.Match(cs, "public const int Bytes = ([0-9]+)");

        Assert.True(fromRust.Success, "the core no longer states the spec size");
        Assert.True(fromCs.Success, "the interop struct no longer states its size");
        Assert.Equal(fromRust.Groups[1].Value, fromCs.Groups[1].Value);

        // And the field exists on both sides, since two sizes can agree while
        // the fields inside them do not.
        Assert.Contains("pub kind: u32", rust, StringComparison.Ordinal);
        Assert.Contains("public uint Kind;", cs, StringComparison.Ordinal);
    }

    [Fact]
    public void the_kind_reaches_the_core_and_the_colour_is_left_alone()
    {
        string vm = Read("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");
        int at = vm.IndexOf("specs.Add(new HighlightSpec", StringComparison.Ordinal);
        Assert.True(at > 0, "the spec is no longer built here");

        Assert.Contains("Kind = (uint)h.Kind", vm.Substring(at, 700), StringComparison.Ordinal);

        // ⚠️ ONE ALPHA RULE FOR ALL THREE. A per-kind alpha was considered and
        // refused: the appearance model is not being redesigned here.
        Assert.Contains("defaultAlpha: 0x88", vm, StringComparison.Ordinal);
    }


    // ---------------- what actually reaches the screen ----------------

    [Fact]
    public void the_overlay_draws_the_kind_and_not_the_marked_band()
    {
        // ⚠️ THE DEFECT THIS TEST EXISTS FOR, and it shipped. ColoredRects was
        // correct and NOTHING CALLED IT: the overlay flattened the marked band
        // instead, so all three kinds drew the same full wash and neither
        // underline nor strikeout was ever visible while working. Asserting
        // that geometry is computed proves nothing if no one asks for it.
        string slot = Read("PdfEditorApp", "ViewModels", "PageSlot.cs");
        int at = slot.IndexOf("public void RebuildHighlightRects()", StringComparison.Ordinal);
        Assert.True(at > 0, "the overlay no longer flattens the marks here");

        int next = slot.IndexOf("/// <summary>", at, StringComparison.Ordinal);
        string body = slot[at..(next > at ? next : slot.Length)];

        Assert.Contains("h.ColoredRects", body, StringComparison.Ordinal);
        Assert.DoesNotContain("h.Rects", body, StringComparison.Ordinal);
    }

    [Fact]
    public void a_coloured_rect_scales_into_the_overlay_keeping_its_own_colour()
    {
        // The step the overlay takes straight after asking for the geometry.
        // The colour travels WITH the rect now, because the rect being drawn is
        // no longer the one the annotation was built from.
        var drawn = Mark(MarkupKind.Underline).ColoredRects[0];
        var scaled = ScaledRect.From(drawn, 1000);

        Assert.Equal(drawn.Left * 1000, scaled.Left, 6);
        Assert.Equal(drawn.Width * 1000, scaled.Width, 6);
        Assert.Equal(drawn.Height * 1000, scaled.Height, 6);
        Assert.Equal("#66FFD400", scaled.ColorHex);
    }

    [Fact]
    public void a_rule_is_thick_enough_to_survive_the_degenerate_rect_filter()
    {
        // ⚠️ THE OVERLAY DROPS ANYTHING UNDER HALF A PIXEL, which is right for
        // the empty boxes a line break leaves behind and would be fatal here: a
        // rule is thin by design. Checked narrow as well as wide.
        var rule = Mark(MarkupKind.Strikeout).ColoredRects[0];

        Assert.True(ScaledRect.From(rule, 400).IsVisible,
            "a strikeout vanishes on a narrow page");
        Assert.True(ScaledRect.From(rule, 1000).IsVisible);
    }

    // ---------------- the picker ----------------

    [Fact]
    public void the_highlighter_offers_the_picker_and_nothing_else_does()
    {
        var highlight = ToolCatalog.For(ToolMode.Highlight);
        Assert.True(highlight.Offers(ToolOptions.Markup));

        foreach (var other in ToolCatalog.All.Where(t => t.Mode != ToolMode.Highlight))
        {
            Assert.False(other.Offers(ToolOptions.Markup), other.Name);
        }
    }

    [Fact]
    public void the_picker_shows_for_the_highlight_tool_and_not_for_a_plain_colour_tool()
    {
        var state = new PropertyBarState(
            ToolOptions.Color | ToolOptions.Markup, ShapeKind.Rectangle,
            ToolIsShape: false, HasSelectedShape: false, HasSelectedTextBox: false,
            HasSelectedRoundedRect: false, HasMultiSelection: false,
            HasSelectedAnnotation: false);

        Assert.True(PropertyBarLayout.For(state).Markup);
        Assert.False(PropertyBarLayout.For(state with { ToolOptions = ToolOptions.Color }).Markup);
    }

    [Fact]
    public void the_three_marks_stay_one_rail_row_and_stay_available_while_reading()
    {
        // The rail is nine rows deep and scroll-disabled, which is why this is
        // a picker rather than two more tools. And marking a page up is part of
        // reading it, so that row belongs in both modes.
        Assert.Single(ToolCatalog.All, t => t.Mode == ToolMode.Highlight);
        Assert.True(ToolCatalog.Offers(AppMode.View, ToolMode.Highlight));
        Assert.True(ToolCatalog.Offers(AppMode.Edit, ToolMode.Highlight));
    }
}
