using System;
using System.IO;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The page stack is laid out by PageStackLayout, at exact positions.
///
/// Read out of the source, because the layout lives in the WinUI project and
/// a net10.0 test assembly cannot load it. The geometry it places cards by is
/// tested for real in <see cref="PageStackGeometryTests"/>.
/// </summary>
public class PageStackLayoutWiringTests
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
        return File.ReadAllText(Path.Combine(dir!.FullName, path)).Replace("\r\n", "\n");
    }

    private static string PageStack()
    {
        string xaml = Source("PdfEditorApp", "MainPage.xaml");
        int at = xaml.IndexOf("<ItemsRepeater ItemsSource=\"{x:Bind ViewModel.PageSlots}\"", StringComparison.Ordinal);
        Assert.True(at > 0, "the page stack is gone");
        return xaml[at..xaml.IndexOf("</ItemsRepeater.Layout>", at, StringComparison.Ordinal)];
    }

    [Fact]
    public void the_page_stack_is_placed_exactly_not_estimated()
    {
        // ⚠️ StackLayout estimated a jump to page 39175 into blank cards for
        // pages near 36000.
        string stack = PageStack();

        Assert.Contains("<controls:PageStackLayout x:Name=\"PageCardLayout\" Spacing=\"16\" />", stack, StringComparison.Ordinal);
        Assert.DoesNotContain("<StackLayout", stack, StringComparison.Ordinal);
    }

    [Fact]
    public void the_layout_gap_is_the_view_models_page_gap()
    {
        // The view model scrolls to tops computed with its own gap. A layout
        // gap one DIP different drifts a DIP per page: 39 thousand DIPs, dozens
        // of pages, by the end of a big book.
        Assert.Contains("private readonly ContinuousLayout _layout = new(pageGap: 16);",
            Source("PdfEditorApp", "ViewModels", "ViewportViewModel.cs"), StringComparison.Ordinal);
        Assert.Contains("Spacing=\"16\"", PageStack(), StringComparison.Ordinal);
    }

    [Fact]
    public void the_layout_realizes_by_exact_geometry_and_leaves_recycling_to_the_repeater()
    {
        string code = Source("PdfEditorApp", "Controls", "PageStackLayout.cs");

        Assert.Contains("geometry.Range(top, bottom)", code, StringComparison.Ordinal);
        Assert.Contains("context.GetOrCreateElementAt(i)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("SuppressAutoRecycle", code, StringComparison.Ordinal);

        // An unbounded window must not realize the whole book.
        Assert.Contains("double.IsInfinity(window.Height)", code, StringComparison.Ordinal);

        // A changed page list forgets the old positions.
        Assert.Contains("_geometry = null;", code, StringComparison.Ordinal);
    }

    [Fact]
    public void the_page_box_fits_a_five_digit_page_number()
    {
        // At 26 DIP it showed "3917" for page 39175.
        Assert.Contains("<TextBox x:Name=\"PageJumpBox\" Width=\"36\"",
            Source("PdfEditorApp", "MainPage.xaml"), StringComparison.Ordinal);
    }
}
