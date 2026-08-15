using System;
using System.IO;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// How the built-in stamps reach the page.
///
/// The rule the whole feature rests on: a built-in must become the SAME image
/// annotation a user's PNG becomes. If it ever grows its own placement path,
/// every one of move, resize, rotate, copy, delete, undo and z-order has to be
/// made to work twice, and the second copy is the one nobody tests.
///
/// View code, which this assembly cannot load, so these read the source. Same
/// technique and same reason as <c>AppWiringTests</c>.
/// </summary>
public class StampWiringTests
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

    private static string PageCode() => Read("PdfEditorApp", "MainPage.xaml.cs");
    private static string PageXaml() => Read("PdfEditorApp", "MainPage.xaml");
    private static string Renderer() => Read("PdfEditorApp", "StampRenderer.cs");

    private static string Section(string source, string anchor, int length)
    {
        int at = source.IndexOf(anchor, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{anchor}' is gone; this test needs rewriting to match");
        return source[at..Math.Min(source.Length, at + length)];
    }

    /// <summary>A whole method, bounded by the next declaration rather than by
    /// a character count that silently stops covering it.</summary>
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

    // ---------------- One placement path ----------------

    [Fact]
    public void a_built_in_is_placed_by_the_same_call_a_png_is()
    {
        // The whole design in one assertion. Two kinds of stamp, one
        // PlaceStamp, so everything downstream treats them identically.
        string body = MethodBody(PageCode(), "private async Task PlaceSelectedStampAsync");

        Assert.Contains("ViewModel.PlaceStamp(", body, StringComparison.Ordinal);
        Assert.Equal(
            1,
            body.Split("ViewModel.PlaceStamp(").Length - 1);
    }

    [Fact]
    public void a_built_in_is_drawn_at_placement_rather_than_read_from_a_file()
    {
        // This is what makes the date stamps possible and what keeps them sharp
        // at 8x. Reading a built-in from disk would mean shipping PNGs after
        // all.
        string body = MethodBody(PageCode(), "private async Task PlaceSelectedStampAsync");

        Assert.Contains("StampRenderer.Render(", body, StringComparison.Ordinal);
        Assert.Contains("CultureInfo.CurrentCulture", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_pixels_handed_to_pdfium_have_straight_alpha()
    {
        // Win2D draws premultiplied and PDFium builds its soft mask from these
        // bytes. Without the conversion every antialiased letter edge is
        // darkened, and a stamp is nothing but letter edges.
        Assert.Contains(
            "StampAlpha.Unpremultiply",
            MethodBody(Renderer(), "public static StampPixels? Render"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void the_picker_tile_is_drawn_by_the_code_that_draws_the_stamp()
    {
        // A separate preview drifts from the thing it previews. This one cannot,
        // because it is the same Rasterise call at a smaller width.
        string thumb = MethodBody(Renderer(), "public static ImageSource? Thumbnail");

        Assert.Contains("Rasterise(", thumb, StringComparison.Ordinal);

        // And it keeps the premultiplied pixels, which is what WriteableBitmap
        // wants and the opposite of what PDFium wants.
        Assert.DoesNotContain("Unpremultiply", thumb, StringComparison.Ordinal);
    }

    // ---------------- The two rows ----------------

    [Fact]
    public void the_picker_has_a_labelled_row_for_each_kind()
    {
        string xaml = PageXaml();

        int builtIn = xaml.IndexOf("Text=\"Built-in\"", StringComparison.Ordinal);
        int mine = xaml.IndexOf("Text=\"My stamps\"", StringComparison.Ordinal);

        Assert.True(builtIn >= 0, "the built-in row has lost its label");
        Assert.True(mine >= 0, "the user's row has lost its label");
        Assert.True(builtIn < mine, "the built-ins are meant to be the row on top");
    }

    [Fact]
    public void the_built_in_row_shows_the_whole_library()
    {
        // Bound to the registry itself, so adding Tier 2 later is a row in the
        // table and nothing here.
        Assert.Contains(
            "BuiltInStamps.All",
            Section(PageCode(), "BuiltInStampChoices.ItemsSource", 200),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("private void StampChoice_SelectionChanged", "_selectedBuiltIn = null")]
    [InlineData("private void BuiltInStampChoice_SelectionChanged", "_selectedStamp = null")]
    public void choosing_in_one_row_unchooses_the_other(string handler, string clears)
    {
        // Two armed stamps would make the next click's result depend on which
        // branch the placement code happened to read first.
        string body = MethodBody(PageCode(), handler);

        Assert.Contains(clears, body, StringComparison.Ordinal);
        Assert.Contains("ClearOtherStampRow(", body, StringComparison.Ordinal);
    }

    [Fact]
    public void clearing_the_other_row_cannot_re_enter_the_handlers()
    {
        // Setting SelectedItem raises SelectionChanged. Without the guard,
        // choosing a built-in would immediately run the PNG handler with a null
        // selection: the same re-entry trap RefreshStamps already documents,
        // which cost a stack overflow last time.
        string body = MethodBody(PageCode(), "private void ClearOtherStampRow");

        Assert.Contains("_suppressStampSelection = true", body, StringComparison.Ordinal);
        Assert.Contains("finally", body, StringComparison.Ordinal);
        Assert.Contains("_suppressStampSelection = false", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_remembered_stamp_can_be_either_kind()
    {
        // One .last-used file holds both, which is only safe because a built-in
        // id carries a colon and a Windows file name cannot.
        string body = MethodBody(PageCode(), "private void RefreshStamps");

        Assert.Contains("BuiltInStamps.FromEntryId(", body, StringComparison.Ordinal);
        Assert.Contains("StampLibrary.LastUsedId()", body, StringComparison.Ordinal);
    }

    // ---------------- The bundled assets ----------------

    [Fact]
    public void the_font_is_loaded_from_beside_the_exe()
    {
        // An ABSOLUTE PATH, and both halves of it matter.
        //
        // ms-appx throws outright here: Win2D rejects it in an unpackaged app,
        // which is what this is. A bare family name is worse, because it does
        // NOT throw: DirectWrite substitutes another face and every stamp comes
        // out in the wrong typeface with nothing to show for it.
        string body = MethodBody(Renderer(), "private static string FontUri");

        Assert.DoesNotContain("ms-appx", body, StringComparison.Ordinal);
        Assert.Contains("AppContext.BaseDirectory", body, StringComparison.Ordinal);
        Assert.Contains("theme.FontFile", body, StringComparison.Ordinal);

        // The # is what turns a path into a font reference rather than a family
        // lookup, so losing it is the silent-substitution failure.
        Assert.Contains("\"#\" + theme.FontFamily", body, StringComparison.Ordinal);
    }

    [Fact]
    public void text_is_never_measured_in_a_zero_sized_box()
    {
        // Zero reads as "unconstrained" and is not. The same string measured
        // 28.4 wide at zero and 212.3 at 4096, and the small number went
        // straight into the shrink-to-fit, so every long label would have kept
        // its full size and overflowed the frame.
        string body = MethodBody(Renderer(), "private static void DrawLine");

        Assert.Contains("MeasureBound", body, StringComparison.Ordinal);
        Assert.DoesNotContain("probe, 0, 0", body, StringComparison.Ordinal);
        Assert.DoesNotContain("format, 0, 0", body, StringComparison.Ordinal);
    }

    [Fact]
    public void text_is_centred_on_its_ink_rather_than_its_line_box()
    {
        // LayoutBounds is the line box, which for Oswald has far more space
        // above the capitals than below. Centring on it is what made every word
        // sit low in its frame. DrawBounds is the ink.
        string body = MethodBody(Renderer(), "private static void DrawLine");

        Assert.Contains("DrawBounds", body, StringComparison.Ordinal);
        Assert.DoesNotContain("LayoutBounds", body, StringComparison.Ordinal);
        Assert.Contains("BuiltInStamps.CenterOffset(", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_stamp_strip_is_sized_from_the_viewport()
    {
        // Not a constant in the XAML. A fixed cap showed four of seventeen
        // tiles and stayed that way however big the window was.
        Assert.DoesNotContain("MaxWidth=\"320\"", PageXaml(), StringComparison.Ordinal);

        string body = MethodBody(PageCode(), "private void ResizeStampStrip");

        Assert.Contains("BuiltInStamps.StripMaxWidth(", body, StringComparison.Ordinal);
        Assert.Contains("PageScroller.ViewportWidth", body, StringComparison.Ordinal);
        Assert.Contains("BuiltInStampChoices.MaxWidth", body, StringComparison.Ordinal);
        Assert.Contains("StampChoices.MaxWidth", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_strip_is_resized_before_it_is_ever_shown()
    {
        // SizeChanged alone is not enough: the tool can be armed without the
        // viewport having resized since launch, and the strip would sit at
        // XAML's unbounded default until the window was touched.
        Assert.Contains(
            "ResizeStampStrip()",
            MethodBody(PageCode(), "private void RefreshStamps"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void the_font_and_its_licences_are_in_the_install_payload()
    {
        // The .iss recurses the publish folder, so these ride along on their
        // own and nothing would reveal their absence by looking at the app. A
        // missing font is a silent substitution; a missing licence is a breach.
        string script = Read("tools", "build_installer.ps1");

        foreach (string asset in new[] { "Oswald-Bold.ttf", "OFL.txt", "THIRD-PARTY-NOTICES.txt" })
        {
            Assert.Contains(asset, script, StringComparison.Ordinal);
        }

        Assert.Contains("missing from publish output", script, StringComparison.Ordinal);
    }

    [Fact]
    public void the_fonts_folder_is_copied_to_the_output()
    {
        Assert.Contains(
            "Assets\\Fonts\\**\\*",
            Read("PdfEditorApp", "PdfEditorApp.csproj"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void the_notices_carry_what_the_licences_require()
    {
        // MIT wants Tabler's copyright line to ship. The OFL wants its own text
        // to accompany the font and the Reserved Font Name to be respected.
        string notices = Read("PdfEditorApp", "Assets", "Fonts", "THIRD-PARTY-NOTICES.txt");

        Assert.Contains("Pawel Kuna", notices, StringComparison.Ordinal);
        Assert.Contains("MIT License", notices, StringComparison.Ordinal);
        Assert.Contains("SIL Open Font License", notices, StringComparison.Ordinal);
        Assert.Contains("Reserved Font Name", notices, StringComparison.Ordinal);
    }

    [Fact]
    public void the_bundled_licence_is_the_real_one()
    {
        string ofl = Read("PdfEditorApp", "Assets", "Fonts", "OFL.txt");

        Assert.Contains("SIL OPEN FONT LICENSE", ofl, StringComparison.Ordinal);
        Assert.Contains("Oswald Project Authors", ofl, StringComparison.Ordinal);
    }
}
