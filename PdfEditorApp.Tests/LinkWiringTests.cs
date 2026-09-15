using System;
using System.IO;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The call sites that carry a link.
///
/// Read out of the source, because they live in the WinUI project and a net10.0
/// test assembly cannot load one; the same bargain every other wiring test here
/// makes. What they hold down is the handful of rules that are easy to break by
/// accident and expensive to notice.
/// </summary>
public class LinkWiringTests
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

    private static string Interop() =>
        Source("PdfEditorApp", "Interop", "RenderCoreNative.cs");

    private static string Core() => Source("render_core", "src", "lib.rs");

    private static string Body(string code, string signature)
    {
        int at = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at > 0, $"there is no {signature}");

        int next = code.IndexOf("\n    /// <summary>", at, StringComparison.Ordinal);
        return code[at..(next > at ? next : code.Length)];
    }

    private static int Count(string haystack, string needle)
    {
        int n = 0;
        for (int at = 0; (at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0; at += needle.Length)
        {
            n++;
        }
        return n;
    }

    [Fact]
    public void all_three_link_calls_are_declared()
    {
        string code = Interop();

        Assert.Contains("ByteBuffer get_page_links(", code, StringComparison.Ordinal);
        Assert.Contains("int add_uri_link(", code, StringComparison.Ordinal);
        Assert.Contains("int set_uri_link(", code, StringComparison.Ordinal);
    }

    [Fact]
    public void the_core_never_enumerates_links_through_pdfiums_own_collection()
    {
        // ⚠️ THE DEFECT THIS FEATURE IS BUILT AROUND. PdfPageLinks indexes the
        // /Annots ARRAY rather than a dense list of links: on a page holding a
        // stamp and two links it was measured to report three and to hand back
        // the first one twice. Walking the annotations is the only enumeration
        // that is right, and it is the one that yields the annotation index
        // every other call takes.
        string body = Body(Core(), "fn page_links(page: &pdfium_render::prelude::PdfPage)");

        Assert.Contains("page.annotations()", body, StringComparison.Ordinal);
        Assert.DoesNotContain("page.links()", body, StringComparison.Ordinal);
    }

    [Fact]
    public void reading_a_link_asks_both_questions()
    {
        // A browser writes an external link as an /A URI action and an internal
        // jump as a bare /Dest, and PDFium answers None from action() for the
        // second. Asking only the first makes every internal link look empty.
        string body = Body(Core(), "fn page_links(page: &pdfium_render::prelude::PdfPage)");

        Assert.Contains(".action()", body, StringComparison.Ordinal);
        Assert.Contains(".destination()", body, StringComparison.Ordinal);
    }

    [Fact]
    public void a_link_we_create_is_marked_printable()
    {
        // /F 4, which every real producer sets. Some viewers treat a link
        // without it as screen-only.
        Assert.Contains("set_is_printed(true)", Core(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("public bool AddLink(")]
    [InlineData("public bool EditLink(")]
    [InlineData("public bool DeleteLink(")]
    public void each_link_command_is_one_history_entry_and_not_several(string signature)
    {
        // The reader did one thing, so Ctrl+Z has to put it back in one step.
        string body = Body(ViewModel(), signature);

        Assert.Contains("BeginEdit(", body, StringComparison.Ordinal);
        Assert.Contains("RecordEdit(new LinkRecord(", body, StringComparison.Ordinal);
        Assert.Equal(1, Count(body, "CommitEdit()"));
    }

    [Theory]
    [InlineData("public bool AddLink(")]
    [InlineData("public bool EditLink(")]
    [InlineData("public bool DeleteLink(")]
    public void a_refusal_leaves_no_history_entry_behind(string signature)
    {
        // The core leaves the document as it found it when it refuses, so there
        // is nothing to undo. Committing anyway would leave a Ctrl+Z that does
        // nothing, which reads as the app having lost the edit.
        string body = Body(ViewModel(), signature);

        Assert.Contains("AbandonEdit()", body, StringComparison.Ordinal);
    }

    [Fact]
    public void undo_finds_its_link_by_rectangle_rather_than_by_index()
    {
        // ⚠️ Every write in this app renumbers a page's annotations, so an index
        // recorded at edit time names something else by the time undo runs. That
        // exact mistake destroyed annotation identities once already.
        string body = Body(ViewModel(), "private void ApplyLink(LinkRecord record, bool backwards)");

        Assert.Contains("LinkAtRect(", body, StringComparison.Ordinal);
    }

    [Fact]
    public void undo_checks_what_the_link_says_before_it_writes()
    {
        // Restoring is not enough: the record has to establish that the link at
        // that rectangle is still the one it was about, or a step could retarget
        // a link the reader made afterwards.
        string body = Body(ViewModel(), "private LinkSnapshot? LinkAtRect(");

        Assert.Contains("link.Uri != uri", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_history_dispatch_knows_about_links()
    {
        Assert.Contains("case LinkRecord l:", ViewModel(), StringComparison.Ordinal);
    }

    [Fact]
    public void a_links_cache_is_dropped_with_the_page_it_belongs_to()
    {
        // Links ARE annotations, so an edit that renumbers the page renumbers
        // them, and a cached link would hand a stale annotation index to a
        // delete.
        string body = Body(ViewModel(), "private void InvalidateAnnotationCache(int pageIndex)");

        Assert.Contains("_linksByPage.Remove(pageIndex)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void following_a_link_is_gated_on_a_confirmation_and_on_the_scheme()
    {
        // ⚠️ A LINK IS UNTRUSTED INPUT. Two things stand between a click and the
        // operating system: the scheme check, and the reader seeing the full
        // address first. Link text can say one thing and point at another.
        string body = Body(Page(), "private async System.Threading.Tasks.Task FollowLinkAsync(");

        Assert.Contains("LinkTarget.CanOpen(", body, StringComparison.Ordinal);
        Assert.Contains("ShowAsync()", body, StringComparison.Ordinal);
        Assert.Contains("ContentDialogResult.Primary", body, StringComparison.Ordinal);

        // The address itself is in the dialog, not just a "do you want to open
        // this link" with nothing to judge.
        Assert.Contains("Text = link.Uri", body, StringComparison.Ordinal);

        // Launcher, not a shell execute: it hands the URI to the user's browser
        // or mail client and will not run a program.
        Assert.Contains("Windows.System.Launcher.LaunchUriAsync", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_confirmation_does_not_default_to_opening()
    {
        // The safe button is the one a stray Enter presses.
        string body = Body(Page(), "private async System.Threading.Tasks.Task FollowLinkAsync(");

        Assert.Contains("DefaultButton = ContentDialogButton.Secondary", body, StringComparison.Ordinal);
    }

    [Fact]
    public void nothing_else_in_the_app_launches_a_uri_out_of_a_document()
    {
        // One door, so there is one place the scheme check and the confirmation
        // can be enforced.
        Assert.Equal(1, Count(Page(), "LaunchUriAsync(new Uri(link.Uri)"));
    }

    [Fact]
    public void the_overlay_is_drawn_and_is_not_hit_testable()
    {
        // The viewport host works out what was clicked from the coordinates.
        // Letting the outlines take the pointer would steal the press from the
        // code that decides what a click on a link means.
        string xaml = Source("PdfEditorApp", "MainPage.xaml");

        Assert.Contains("ItemsSource=\"{x:Bind LinkOutlines}\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "ItemsSource=\"{x:Bind LinkOutlines}\" IsHitTestVisible=\"False\"",
            xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ShowLinksToggle\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void only_the_pages_a_reader_can_see_are_asked_for_their_links()
    {
        // ⚠️ ASKING PDFIUM FOR A PAGE PARSES IT. Reading every page's links to
        // draw an overlay turned a 3352-page book into a 69-second wait once
        // already, in a different feature, for exactly this reason.
        string body = Body(ViewModel(), "public void RefreshLinkOutlines()");

        Assert.Contains("CurrentPageIndex - 1", body, StringComparison.Ordinal);
        Assert.Contains("CurrentPageIndex + 1", body, StringComparison.Ordinal);
    }

    [Fact]
    public void in_edit_mode_a_click_follows_a_link_only_while_links_are_shown()
    {
        // In Edit mode an ordinary click on a page still selects and edits text
        // the way it always has. In View mode a link is followed regardless:
        // gating it there left a real book's table of contents dead.
        string code = Page();

        Assert.Contains("bool followsLinks = ViewModel.ShowLinks || !ViewModel.IsEditMode;\n"
                        + "                if (followsLinks\n                    && ViewModel.LinkAt(",
                        code.Replace("\r\n", "\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void an_internal_link_is_never_offered_as_something_to_rewrite()
    {
        // PDFium has no setter for a destination, so writing a URI action over
        // one would leave the document holding both and disagreeing with itself
        // about where the link goes.
        string edit = Body(ViewModel(), "public bool EditLink(");
        string remove = Body(ViewModel(), "public bool DeleteLink(");

        Assert.Contains("link.CanEditUrl", edit, StringComparison.Ordinal);
        Assert.Contains("link.CanEditUrl", remove, StringComparison.Ordinal);
    }
}
