using System;
using System.IO;
using System.Text.RegularExpressions;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Where view rotation is applied, and the promises it makes.
///
/// The geometry is covered by PageTransformTests and the stack by
/// ContinuousLayoutTests. This covers the two things neither can see: that the
/// rotation reaches every place that maps between the card and the page, and
/// that it reaches NOTHING that writes to the document.
/// </summary>
public class ViewRotationWiringTests
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

    private static string ViewModel() => Read("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");
    private static string PageCode() => Read("PdfEditorApp", "MainPage.xaml.cs");
    private static string PageXaml() => Read("PdfEditorApp", "MainPage.xaml");

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
    public void turning_the_view_never_touches_the_document()
    {
        // THE test in this file, and the whole reason view rotation exists
        // separately from the Rotate page commands. A reader who turns a
        // sideways scan to read it must not be asked to save anything, must not
        // find an undo step waiting, and must not be blocked on a file that
        // cannot be written to.
        string body = MethodBody(ViewModel(), "private void SetViewRotation");

        Assert.DoesNotContain("IsDirty", body, StringComparison.Ordinal);
        Assert.DoesNotContain("PushHistory", body, StringComparison.Ordinal);
        Assert.DoesNotContain("rotate_page", body, StringComparison.Ordinal);
        Assert.DoesNotContain("save", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void turning_the_view_costs_no_page_parsing()
    {
        // Document rotation has to touch every page, and getting a page from
        // PDFium parses it: on a 3,352-page book that is over a minute. View
        // rotation must stay a layout rebuild, so it cannot grow a loop that
        // asks the document about pages one at a time.
        string body = MethodBody(ViewModel(), "private void SetViewRotation");

        Assert.Contains("RebuildContinuousLayout()", body, StringComparison.Ordinal);
        Assert.DoesNotContain("RenderCoreNative.", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_layout_is_told_which_way_the_view_is_turned()
    {
        // Both Rebuild calls, including the empty one: a rebuild that dropped
        // the rotation would put upright cards under turned content.
        string body = MethodBody(ViewModel(), "private void RebuildContinuousLayout");

        Assert.Equal(2, Regex.Matches(body, @"_layout\.Rebuild\([^)]*ViewRotation\)").Count);
    }

    [Fact]
    public void the_slots_are_built_from_the_layout_transform()
    {
        // Recomputing the geometry here instead of taking what the layout
        // worked out is how the card and its content end up disagreeing.
        Assert.Contains(
            "new PageSlot(slot.PageIndex, slot.Transform)",
            MethodBody(ViewModel(), "private void RebuildContinuousLayout"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void a_click_is_mapped_back_onto_the_page()
    {
        // Everything above this one call site works in page coordinates. If the
        // mapping is missing, every tool in the app aims at the wrong place the
        // moment the view is turned, and nothing else would notice.
        Assert.Contains(
            "Transform.ToContent(",
            MethodBody(ViewModel(), "public bool HitTestSlotSpace"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void the_tile_pass_asks_for_the_part_of_the_page_that_is_on_screen()
    {
        // Tiles are addressed on the page but chosen from what is visible,
        // which is the card. Turned a quarter, the strip across the top of the
        // screen is one SIDE of the page: without the mapping the visible tiles
        // stay blank while invisible ones are fetched.
        string body = MethodBody(ViewModel(), "private void RenderVisibleTiles");

        Assert.Contains("View.ContentBounds(", body, StringComparison.Ordinal);
        Assert.Contains("slot.ContentWidth", body, StringComparison.Ordinal);
        Assert.Contains("slot.ContentHeight", body, StringComparison.Ordinal);
        Assert.Contains("EffectiveZoom(", body, StringComparison.Ordinal);
    }

    [Fact]
    public void renders_are_sized_from_the_content_not_the_card()
    {
        // The card has the other shape entirely once turned. Sizing a render
        // from it asks for a bitmap of the wrong aspect, and ignoring the scale
        // asks for the wrong number of pixels.
        string sharpen = MethodBody(ViewModel(), "private void SharpenVisiblePages");

        Assert.Contains("_budget.NeedsTiles(slot.ContentWidth", sharpen, StringComparison.Ordinal);
        Assert.Contains("_budget.SharpWidthFor(slot.ContentWidth", sharpen, StringComparison.Ordinal);
        Assert.Contains("EffectiveZoom(_currentZoomFactor)", sharpen, StringComparison.Ordinal);
    }

    [Fact]
    public void opening_a_document_puts_the_view_back_upright()
    {
        // A rotation belongs to the reading session. Carrying it into the next
        // file would show an upright document on its side with no clue why.
        string body = MethodBody(ViewModel(), "public void OpenDocument");

        Assert.Contains("ViewRotation = 0", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_card_markup_turns_the_content_as_one_piece()
    {
        // One transform on the container, so the page, its tiles, its
        // highlights and its selection turn together. Per-layer rotation is
        // how they come apart.
        string xaml = PageXaml();

        Assert.Contains("<CompositeTransform ScaleX=\"{x:Bind ViewScale}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Rotation=\"{x:Bind ViewRotation}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("TranslateX=\"{x:Bind ViewTranslateX}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("TranslateY=\"{x:Bind ViewTranslateY}\"", xaml, StringComparison.Ordinal);

        // Sized to the content and pinned to the corner. Left stretching, it
        // would be re-measured to the card's turned shape and squash the page.
        Assert.Contains("<Grid Width=\"{x:Bind ContentWidth}\" Height=\"{x:Bind ContentHeight}\"",
                        xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void the_thumbnails_turn_with_the_page()
    {
        Assert.Contains("<RotateTransform Angle=\"{x:Bind ViewRotation, Mode=OneWay}\" />",
                        PageXaml(), StringComparison.Ordinal);

        Assert.Contains("thumbnail.ViewRotation = next",
                        MethodBody(ViewModel(), "private void SetViewRotation"),
                        StringComparison.Ordinal);
    }

    [Fact]
    public void the_rulers_measure_the_page_at_the_size_it_is_drawn()
    {
        // A turned page is scaled down to fit its card. Left out, every
        // distance the ruler reports is out by that factor, which is worse than
        // no ruler because it looks authoritative.
        Assert.Contains(
            "ViewModel.CurrentViewScale",
            MethodBody(PageCode(), "private void RedrawRulers"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void the_menu_and_the_keyboard_reach_the_same_commands()
    {
        string xaml = PageXaml();
        Assert.Contains("Click=\"RotateViewCw_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"RotateViewCcw_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"ResetViewRotation_Click\"", xaml, StringComparison.Ordinal);

        string run = MethodBody(PageCode(), "private void Run(EditorCommand command)");
        Assert.Contains("EditorCommand.RotateViewClockwise", run, StringComparison.Ordinal);
        Assert.Contains("EditorCommand.RotateViewCounterClockwise", run, StringComparison.Ordinal);
    }

    [Fact]
    public void the_menu_says_view_so_it_is_not_mistaken_for_the_page_command()
    {
        // The app has both, and they do very different things. An entry reading
        // "Rotate 90" in the View menu beside "Rotate page 90" under Pages
        // would be a coin toss over whether the file gets modified.
        string xaml = PageXaml();

        Assert.Contains("Text=\"Rotate view clockwise\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Rotate view anticlockwise\"", xaml, StringComparison.Ordinal);
    }

    // ---------------- The chords ----------------

    [Theory]
    [InlineData(KeyboardCommands.KeyOemPlus, EditorCommand.RotateViewClockwise)]
    [InlineData(KeyboardCommands.KeyAdd, EditorCommand.RotateViewClockwise)]
    [InlineData(KeyboardCommands.KeyOemMinus, EditorCommand.RotateViewCounterClockwise)]
    [InlineData(KeyboardCommands.KeySubtract, EditorCommand.RotateViewCounterClockwise)]
    public void shift_ctrl_plus_and_minus_turn_the_view(int key, EditorCommand expected)
    {
        Assert.Equal(expected, KeyboardCommands.Resolve(key, ctrl: true, shift: true, textFocused: false));
    }

    [Theory]
    [InlineData(KeyboardCommands.KeyOemPlus)]
    [InlineData(KeyboardCommands.KeyAdd)]
    [InlineData(KeyboardCommands.KeyOemMinus)]
    [InlineData(KeyboardCommands.KeySubtract)]
    public void without_shift_plus_and_minus_are_left_alone(int key)
    {
        // Ctrl+plus and Ctrl+minus are zoom in every reader there is. Claiming
        // them would turn the page when the reader meant to make it bigger.
        Assert.Equal(
            EditorCommand.None,
            KeyboardCommands.Resolve(key, ctrl: true, shift: false, textFocused: false));
    }

    [Theory]
    [InlineData(KeyboardCommands.KeyOemPlus)]
    [InlineData(KeyboardCommands.KeyOemMinus)]
    public void the_chords_stay_out_of_text_fields(int key)
    {
        Assert.Equal(
            EditorCommand.None,
            KeyboardCommands.Resolve(key, ctrl: true, shift: true, textFocused: true));

        Assert.Equal(
            EditorCommand.None,
            KeyboardCommands.Resolve(key, ctrl: false, shift: true, textFocused: false));
    }
}
