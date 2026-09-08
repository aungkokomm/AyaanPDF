using System;
using System.IO;
using System.Linq;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The region boxes reach the screen, and stop reaching it when they should.
///
/// Source-scanned like the other wiring tests here, because the failures this
/// catches are wiring failures: a model that is built correctly and never
/// drawn, or drawn from a page that has since changed, both leave a green
/// suite and a wrong screen.
/// </summary>
public class TextRegionWiringTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null
            && !File.Exists(Path.Combine(dir.FullName, "PdfEditorApp", "MainPage.xaml")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>The source with comment lines dropped, so commenting a call out
    /// fails the test rather than passing it.</summary>
    private static string Source(params string[] parts)
    {
        string text = File.ReadAllText(Path.Combine(Root(), Path.Combine(parts)));
        return string.Join("\n", text.Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
    }

    private static string Xaml() => File.ReadAllText(
        Path.Combine(Root(), "PdfEditorApp", "MainPage.xaml"));

    [Fact]
    public void the_slot_carries_the_region_boxes()
    {
        Assert.Contains(
            "public ObservableCollection<ScaledRect> TextRegionOutlines { get; } = new();",
            Source("PdfEditorApp", "ViewModels", "PageSlot.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void the_page_draws_them()
    {
        string xaml = Xaml();
        Assert.Contains("ItemsSource=\"{x:Bind TextRegionOutlines}\"", xaml, StringComparison.Ordinal);

        // ⚠️ UNDER the selection frame, so a selected line's solid rule is not
        // hidden by the faint box that merely said text was there.
        int regions = xaml.IndexOf("{x:Bind TextRegionOutlines}", StringComparison.Ordinal);
        int selected = xaml.IndexOf("{x:Bind PageTextOutline}", StringComparison.Ordinal);
        Assert.True(regions > 0 && selected > 0);
        Assert.True(regions < selected, "the region boxes are drawn over the selection frame");
    }

    [Fact]
    public void they_cannot_eat_the_click_they_are_advertising()
    {
        string xaml = Xaml();
        int at = xaml.IndexOf("{x:Bind TextRegionOutlines}", StringComparison.Ordinal);
        Assert.True(at > 0);
        // The attribute sits on the same element, just after the binding.
        string element = xaml[at..Math.Min(xaml.Length, at + 120)];
        Assert.Contains("IsHitTestVisible=\"False\"", element, StringComparison.Ordinal);
    }

    [Fact]
    public void the_model_is_built_from_the_document_as_it_is_now()
    {
        string vm = Source("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");

        // ⚠️ NOT THE FILE ON DISK. The open document carries edits nobody has
        // saved, and a model built from the file would describe a page the
        // reader is not looking at.
        string build = BuildBody(vm);
        Assert.Contains("bytes ?? SnapshotDocumentBytes(handle)", build, StringComparison.Ordinal);
        Assert.Contains("TextRegionReader.Build(b, at, context.Lines)", build, StringComparison.Ordinal);
        Assert.DoesNotContain("File.ReadAllBytes", build);
    }

    // ---------------- the caret, and nothing beside it ----------------

    /// <summary>
    /// ⚠️ A CLICK IS RESOLVED AGAINST THE REAL CHARACTERS. Editing happens
    /// where the text already is, so the position a caret is put at has to come
    /// from the drawn glyphs, on the same click the reader made.
    /// </summary>
    [Fact]
    public void a_click_is_resolved_against_the_real_characters()
    {
        string vm = Source("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");

        int method = vm.IndexOf("public bool BeginInPlaceEdit(", StringComparison.Ordinal);
        Assert.True(method > 0);
        string body = vm[method..Math.Min(vm.Length, method + 2600)];

        Assert.Contains("TextRegionHitTest.LineAt(EnsureTextRegions(pageIndex), normX, normY)",
            body, StringComparison.Ordinal);
        Assert.Contains("CaretOffsetIn(glyphs, unit.Text, normX)", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠️ AN EDIT MUST NOT MAKE THE SAME TEXT UNEDITABLE. Committing throws
    /// the region model away, and the very next thing the reader does is click
    /// the line they just changed. When only the clearing existed, that click
    /// found an empty model and the app refused to edit text it had edited a
    /// second earlier.
    /// </summary>
    [Fact]
    public void the_model_is_asked_for_again_when_it_is_thrown_away()
    {
        string vm = Source("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");

        int body = vm.IndexOf("private void InvalidateTextRegions()", StringComparison.Ordinal);
        Assert.True(body > 0);
        string method = vm[body..Math.Min(vm.Length, body + 1400)];

        Assert.Contains("_textRegions.Clear();", method, StringComparison.Ordinal);
        Assert.Contains("RefreshTextRegions();", method, StringComparison.Ordinal);

        // Queued, not run inline: this is called several times over for a
        // single edit, from inside command batches.
        Assert.Contains("_dispatcherQueue.TryEnqueue(", method, StringComparison.Ordinal);

        // And a click that beats the queued pass builds rather than refuses.
        Assert.Contains("private IReadOnlyList<TextRegion> EnsureTextRegions(int page)",
            vm, StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠️ ONE MARK IN THE TEXT, AND IT IS THE CARET. A box was drawn on the
    /// glyph a click resolved to, to show that clicks landed correctly. It
    /// outlived the click that made it: while the reader typed, it sat on the
    /// original letter in the middle of the word they were changing. Two marks
    /// in the same text, one of them stale, is what stops a page feeling like
    /// the page.
    /// </summary>
    [Fact]
    public void nothing_is_marked_in_the_text_except_the_caret()
    {
        Assert.DoesNotContain("TextHitOutline", Xaml());
        Assert.DoesNotContain("TextHitOutline",
            Source("PdfEditorApp", "ViewModels", "PageSlot.cs"));
        Assert.DoesNotContain("TextHitOutline",
            Source("PdfEditorApp", "ViewModels", "ViewportViewModel.cs"));
    }

    /// <summary>
    /// ⚠️ NO SCRIM, AND NOTHING THAT DIMS THE PAGE. The page has to stay
    /// visually stable while text is being worked with. The modal editor this
    /// replaced dimmed it, and nothing built on the region model may copy that.
    /// </summary>
    [Fact]
    public void nothing_in_the_region_overlay_dims_the_page()
    {
        string xaml = Xaml();

        int at = xaml.IndexOf("{x:Bind TextRegionOutlines}", StringComparison.Ordinal);
        Assert.True(at > 0, "the region boxes are not drawn");

        int end = xaml.IndexOf("</ItemsControl>", at, StringComparison.Ordinal);
        Assert.True(end > at);
        string block = xaml[at..end];

        Assert.DoesNotContain("Fill=", block);
        Assert.DoesNotContain("Scrim", block);

        // And the layer the caret and the redrawn tail live on carries neither.
        int layer = xaml.IndexOf("InPlaceLayer", StringComparison.Ordinal);
        Assert.True(layer > 0);
        Assert.DoesNotContain("Scrim", xaml[layer..Math.Min(xaml.Length, layer + 200)]);
    }

    // ---------------- selecting ----------------
    //
    // ⚠️ WITHOUT THIS THE EDITOR IS NOT ONE. A caret and backspace alone means
    // the only way to change a word is to delete it letter by letter and retype
    // it, which is what the reader hit the first time they used it.

    [Fact]
    public void holding_shift_extends_the_selection_instead_of_moving()
    {
        string page = Source("PdfEditorApp", "MainPage.xaml.cs");

        int at = page.IndexOf("ViewModel.IsEditingInPlace && !IsTextInputFocused",
                              StringComparison.Ordinal);
        Assert.True(at > 0, "nothing routes keys to an in-place edit");
        string keys = page[at..Math.Min(page.Length, at + 2200)];

        Assert.Contains("bool extend = IsShiftDown();", keys, StringComparison.Ordinal);

        // ⚠️ LEFT AND RIGHT GO THROUGH THE CROSSING WRAPPERS NOW, which pass
        // extend straight down to the plain move unless the caret is at an edge
        // of the line with nothing selected. Held shift never crosses.
        Assert.Contains("InPlaceArrowLeft(extend)", keys, StringComparison.Ordinal);
        Assert.Contains("InPlaceArrowRight(extend)", keys, StringComparison.Ordinal);
        Assert.Contains("InPlaceMoveHome(extend)", keys, StringComparison.Ordinal);
        Assert.Contains("InPlaceMoveEnd(extend)", keys, StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠️ AND SELECT ALL IS THE ONLY CHORD IT CLAIMS. Ctrl+A while typing into
    /// text has to mean that text, not every annotation on the page. Every
    /// other Ctrl combination must fall through, so Ctrl+S still saves while a
    /// line is open.
    /// </summary>
    [Fact]
    public void control_a_selects_the_line_and_other_chords_fall_through()
    {
        string page = Source("PdfEditorApp", "MainPage.xaml.cs");

        int at = page.IndexOf("ViewModel.IsEditingInPlace && !IsTextInputFocused",
                              StringComparison.Ordinal);
        Assert.True(at > 0);
        string keys = page[at..Math.Min(page.Length, at + 1200)];

        Assert.Contains("if (_isCtrlDown)", keys, StringComparison.Ordinal);
        Assert.Contains("InPlaceSelectAll();", keys, StringComparison.Ordinal);

        // The chord branch returns rather than falling into the key handling
        // below it, which is what leaves every other chord alone.
        int ctrl = keys.IndexOf("if (_isCtrlDown)", StringComparison.Ordinal);
        int selectAll = keys.IndexOf("InPlaceSelectAll();", ctrl, StringComparison.Ordinal);
        int giveUp = keys.IndexOf("return;", selectAll, StringComparison.Ordinal);
        Assert.True(giveUp > selectAll, "the chord branch does not give the rest back");
    }

    /// <summary>
    /// ⚠️ NO CANVAS SHORTCUT MAY FIRE WHILE THE READER IS TYPING INTO TEXT.
    /// Found by typing a "u" and watching the highlighter switch on: single-key
    /// tool switching sits at the bottom of the key handler and claims whatever
    /// nothing else took. It was never only U, and never only the tools: the
    /// canvas cases above it read space, the digits and several plain letters.
    ///
    /// The gate returns rather than marking the event handled, because the
    /// keystroke still has to become a character for CharacterReceived to type.
    /// </summary>
    [Fact]
    public void no_shortcut_fires_while_typing_into_the_page()
    {
        string page = Source("PdfEditorApp", "MainPage.xaml.cs");

        int at = page.IndexOf("ViewModel.IsEditingInPlace && !IsTextInputFocused",
                              StringComparison.Ordinal);
        Assert.True(at > 0, "nothing routes keys to an in-place edit");

        // The gate is inside the in-place block, after the keys it acts on.
        int act = page.IndexOf("if (act is not null)", at, StringComparison.Ordinal);
        Assert.True(act > at);
        string tail = page[act..Math.Min(page.Length, act + 1400)];

        int give = tail.IndexOf("return;", StringComparison.Ordinal);
        Assert.True(give > 0, "unclaimed keys still fall through to the canvas");

        // ⚠️ AND THE TOOL SWITCH IS BELOW IT, which is what makes the gate work.
        int shortcut = page.IndexOf("ViewModel.ToolForShortcut((char)e.Key)", StringComparison.Ordinal);
        Assert.True(shortcut > at,
            "single-key tool switching now runs before the in-place gate");
    }

    /// <summary>
    /// ⚠️ A LINE BEING EDITED HAS TWO HALVES AND THEY ANSWER DIFFERENTLY. Up to
    /// the first changed character the page is still drawing its own type, so
    /// the position is read off the page's glyphs. After it, what is on screen
    /// was drawn by this app in a font it chose, and only the view can say how
    /// wide it came out. Asking the glyphs about that half was why clicking
    /// into text you had just typed did nothing at all.
    /// </summary>
    [Fact]
    public void a_click_reaches_text_the_reader_has_already_typed()
    {
        string page = Source("PdfEditorApp", "MainPage.xaml.cs");

        // One place decides which half answers, and both the press and the drag
        // go through it.
        Assert.Contains("private void PlaceInPlaceCaret(double normX, bool extend)",
            page, StringComparison.Ordinal);
        Assert.Contains("PlaceInPlaceCaret(nx, IsShiftDown());", page, StringComparison.Ordinal);
        Assert.Contains("PlaceInPlaceCaret(content.X / ViewModel.OverlayScale, extend: true);",
            page, StringComparison.Ordinal);

        int at = page.IndexOf("private void PlaceInPlaceCaret(", StringComparison.Ordinal);
        string body = page[at..Math.Min(page.Length, at + 700)];
        Assert.Contains("CaretOffsetInTail(normX)", body, StringComparison.Ordinal);
        Assert.Contains("InPlacePlaceCaret(inTail, extend)", body, StringComparison.Ordinal);
        Assert.Contains("InPlaceClickCaret(normX, extend)", body, StringComparison.Ordinal);

        // ⚠️ AND THE TAIL IS MEASURED, NOT ESTIMATED, in the very font it was
        // drawn in, by the same midpoint rule the glyph path uses.
        int tail = page.IndexOf("private int? CaretOffsetInTail(", StringComparison.Ordinal);
        Assert.True(tail > 0);
        string measure = page[tail..Math.Min(page.Length, tail + 1600)];
        Assert.Contains("RunWidth(tail.Text[..i], tail, fontDip, ink)", measure, StringComparison.Ordinal);
        Assert.Contains("(previous + edge) / 2", measure, StringComparison.Ordinal);

        // The view model still refuses to answer for the half it cannot see.
        string vm = Source("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");
        int click = vm.IndexOf("public void InPlaceClickCaret(", StringComparison.Ordinal);
        Assert.Contains("if (at > prefix) { return; }",
            vm[click..Math.Min(vm.Length, click + 1200)], StringComparison.Ordinal);
    }

    [Fact]
    public void dragging_through_text_selects_it()
    {
        string page = Source("PdfEditorApp", "MainPage.xaml.cs");

        // Armed by the press,
        int press = page.IndexOf("_inPlaceDragging = true;", StringComparison.Ordinal);
        Assert.True(press > 0, "a press in text does not arm a drag");

        // carried by the move,
        int move = page.IndexOf("if (_inPlaceDragging && ViewModel.IsEditingInPlace)",
                                StringComparison.Ordinal);
        Assert.True(move > 0, "moving the pointer does not extend the selection");
        Assert.Contains("extend: true",
            page[move..Math.Min(page.Length, move + 400)], StringComparison.Ordinal);

        // and let go by the release.
        Assert.Contains("_inPlaceDragging = false;", page, StringComparison.Ordinal);
    }

    [Fact]
    public void a_double_click_takes_the_word()
    {
        string page = Source("PdfEditorApp", "MainPage.xaml.cs");

        int at = page.IndexOf("private void ViewportHost_DoubleTapped(", StringComparison.Ordinal);
        Assert.True(at > 0);
        Assert.Contains("InPlaceSelectWordAt(nx);",
            page[at..Math.Min(page.Length, at + 2200)], StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠️ THE WASH GOES UNDER THE TEXT, NOT OVER IT. It is drawn across the
    /// page's own letterforms, and painting it opaque would hide the very type
    /// it is saying is selected.
    /// </summary>
    [Fact]
    public void the_selection_is_a_wash_the_type_reads_through()
    {
        string page = Source("PdfEditorApp", "MainPage.xaml.cs");

        // Translucent: an alpha well below FF.
        int hex = page.IndexOf("SelectionWashHex = \"#", StringComparison.Ordinal);
        Assert.True(hex > 0, "there is no selection wash");
        string value = page.Substring(hex + "SelectionWashHex = \"#".Length, 2);
        Assert.True(Convert.ToInt32(value, 16) < 0xC0, "the selection wash is not translucent");

        // Laid down before the text that has to read through it.
        int render = page.IndexOf("private void RenderInPlaceEdit()", StringComparison.Ordinal);
        string body = page[render..Math.Min(page.Length, render + 2600)];
        int wash = body.IndexOf("InPlaceSelectionOnPage()", StringComparison.Ordinal);
        int tail = body.IndexOf("DrawInPlaceTail(", StringComparison.Ordinal);
        Assert.True(wash > 0 && tail > wash, "the wash is drawn over the text");
    }

    /// <summary>The paint method, which must stay cheap.</summary>
    private static string PaintBody(string vm)
    {
        int at = vm.IndexOf("public void RefreshTextRegions()", StringComparison.Ordinal);
        Assert.True(at > 0);
        int end = vm.IndexOf("private int _textRegionEpoch;", at, StringComparison.Ordinal);
        Assert.True(end > at, "RefreshTextRegions no longer ends where this test thinks");
        return vm[at..end];
    }

    /// <summary>The background build.</summary>
    private static string BuildBody(string vm)
    {
        int at = vm.IndexOf("private async Task BuildTextRegionsAsync(", StringComparison.Ordinal);
        Assert.True(at > 0);
        return vm[at..Math.Min(vm.Length, at + 3200)];
    }

    /// <summary>
    /// ⚠️ PAINTING MUST NOT READ THE DOCUMENT. Measured warm, reading one
    /// page's lines out of the core is 1311 ms on the Harari book and 4630 ms
    /// on the Myanmar file. On the UI thread that is a frozen window every time
    /// the reader scrolls into a page in Edit mode, and a caret sitting in the
    /// text will make that latency far more obvious than a box does.
    /// </summary>
    [Fact]
    public void painting_never_touches_the_core()
    {
        string paint = PaintBody(Source("PdfEditorApp", "ViewModels", "ViewportViewModel.cs"));

        Assert.DoesNotContain("LineGateway.Load", paint);
        Assert.DoesNotContain("LinesFor(", paint);
        Assert.DoesNotContain("SnapshotDocumentBytes", paint);
        Assert.DoesNotContain("TextRegionReader.Build", paint);

        // What it does instead: hand the missing pages to the background build.
        Assert.Contains("BuildTextRegionsAsync(first, last)", paint, StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠️ THE LINE CACHE STAYS SINGLE-THREADED. LinesFor writes into a
    /// dictionary the UI thread owns, so the pool may not call it. The read
    /// happens on the pool and the lines are handed back to that cache on the
    /// UI thread, which is what keeps the next visit to the page free.
    /// </summary>
    [Fact]
    public void the_background_build_reads_lines_without_writing_the_ui_thread_cache()
    {
        string build = BuildBody(Source("PdfEditorApp", "ViewModels", "ViewportViewModel.cs"));

        // Consulted before the read.
        Assert.Contains("_contextByPage.TryGetValue(page, out var have)", build, StringComparison.Ordinal);
        // Read on the pool, not through LinesFor.
        Assert.Contains("Task.Run(", build, StringComparison.Ordinal);
        // ⚠️ AND TOLD WHETHER THE ANSWER IS FINAL. A page of shaped text is
        // read on another thread again, and until that finishes the gateway can
        // only report what PDFium made of it; caching that would mean the
        // reading finished and this page never noticed. The context carries
        // that in Settled, which is what the write-back below consults.
        Assert.Contains("Interop.LineGateway.Context(handle, at)", build, StringComparison.Ordinal);
        Assert.DoesNotContain("LinesFor(", build);
        // And given back afterwards.
        Assert.Contains("cached is null && result.Context.Settled", build, StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠️ A BUILD IN FLIGHT MAY NOT WRITE BACK INTO A DOCUMENT THAT MOVED ON.
    /// Clearing the model does not stop a read that already started, so the
    /// epoch is what makes its answer be dropped rather than painted over a
    /// page it no longer describes.
    /// </summary>
    [Fact]
    public void a_stale_build_is_thrown_away_rather_than_painted()
    {
        string vm = Source("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");
        string build = BuildBody(vm);

        Assert.Contains("int epoch = _textRegionEpoch;", build, StringComparison.Ordinal);
        Assert.Contains(
            "if (handle != _documentHandle || epoch != _textRegionEpoch || !IsEditMode)",
            build, StringComparison.Ordinal);

        // And the epoch actually moves when the model is dropped.
        int body = vm.IndexOf("private void InvalidateTextRegions()", StringComparison.Ordinal);
        Assert.True(body > 0);
        Assert.Contains("_textRegionEpoch++;",
            vm[body..Math.Min(vm.Length, body + 700)], StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠️ THE POOL MAY NOT READ _documentHandle. It would read whatever it has
    /// become since, so a build in flight would snapshot a document the reader
    /// has already closed or replaced.
    /// </summary>
    [Fact]
    public void the_background_build_uses_the_handle_it_captured()
    {
        string vm = Source("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");
        string build = BuildBody(vm);

        Assert.Contains("ulong handle = _documentHandle;", build, StringComparison.Ordinal);
        Assert.Contains("SnapshotDocumentBytes(handle)", build, StringComparison.Ordinal);

        // The overload it calls takes the handle rather than reading the field,
        // and being static is what stops that from being reintroduced.
        Assert.Contains("private static byte[]? SnapshotDocumentBytes(ulong handle)",
            vm, StringComparison.Ordinal);
    }

    /// <summary>
    /// A page that can offer nothing must still be recorded, or it is rebuilt
    /// on every paint and the expensive read never stops happening.
    /// </summary>
    [Fact]
    public void a_page_with_nothing_on_it_is_still_recorded()
    {
        string build = BuildBody(Source("PdfEditorApp", "ViewModels", "ViewportViewModel.cs"));
        Assert.Contains("_textRegions[page] = result.Regions;", build, StringComparison.Ordinal);
    }

    [Fact]
    public void the_model_dies_with_the_page_it_describes()
    {
        string vm = Source("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");

        int invalidate = vm.IndexOf("private void InvalidateLoadedPage(", StringComparison.Ordinal);
        Assert.True(invalidate > 0);
        int call = vm.IndexOf("InvalidateTextRegions();", invalidate, StringComparison.Ordinal);
        Assert.True(call > invalidate && call < invalidate + 600,
            "a page can be repainted without the region model being dropped");

        // And dropping it drops the bytes too, or the next build describes the
        // document as it was before the edit.
        int body = vm.IndexOf("private void InvalidateTextRegions()", StringComparison.Ordinal);
        Assert.True(body > 0);
        string method = vm[body..Math.Min(vm.Length, body + 300)];
        Assert.Contains("_textRegions.Clear();", method, StringComparison.Ordinal);
        Assert.Contains("_textRegionBytes = null;", method, StringComparison.Ordinal);
    }

    [Fact]
    public void the_boxes_appear_and_disappear_with_edit_mode()
    {
        string vm = Source("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");

        // The mode switch rebuilds them.
        int mode = vm.IndexOf("public AppMode Mode", StringComparison.Ordinal);
        Assert.True(mode > 0);
        int refresh = vm.IndexOf("RefreshTextRegions();", mode, StringComparison.Ordinal);
        Assert.True(refresh > mode && refresh < mode + 2500,
            "changing mode does not rebuild the region boxes");

        // And View mode draws none: the guard is the first thing after the clear.
        int method = vm.IndexOf("public void RefreshTextRegions()", StringComparison.Ordinal);
        Assert.True(method > 0);
        string body = vm[method..Math.Min(vm.Length, method + 700)];
        Assert.Contains("slot.TextRegionOutlines.Clear();", body, StringComparison.Ordinal);
        Assert.Contains("if (!IsEditMode", body, StringComparison.Ordinal);
    }

    [Fact]
    public void scrolling_rebuilds_only_on_the_debounce()
    {
        // ⚠️ NOT ON EVERY FRAME. Building a page's regions is tens of
        // milliseconds; doing it per scroll event would spend it continuously
        // for pages that are already off screen again.
        string vm = Source("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");
        // ⚠️ NORMALIZED FIRST. This repository is checked out with
        // core.autocrlf, so whether a file arrives with CRLF or LF depends on
        // the machine and on which tool last wrote it. An assertion that spans
        // a blank line and names one of the two passes or fails for a reason
        // having nothing to do with what it is checking.
        vm = vm.Replace("\r\n", "\n");
        int sharpen = vm.IndexOf("SharpenVisiblePages();\n\n", StringComparison.Ordinal);
        Assert.True(sharpen > 0, "the sharpen debounce is no longer where regions ride");
        Assert.Contains("RefreshTextRegions();",
            vm[sharpen..Math.Min(vm.Length, sharpen + 400)], StringComparison.Ordinal);
    }
}
