using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// A page's size comes from the cached list, never from a fresh read of every
/// page.
///
/// ⚠️ WRITTEN AFTER A 40-SECOND FREEZE. The rulers redraw on every scroll step
/// and asked for the current page's size, which was answered by calling
/// get_page_sizes: every page in the document, under the PDFium lock, to keep
/// one. On a 39881-page book that call measured 1546 ms, so each scroll step
/// took 100 to 300 ms on the UI thread (the UiStall log named ViewChanged)
/// and the page renders waited for the lock.
/// </summary>
public class PageSizeCacheWiringTests
{
    private static string ViewModel()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string path = Path.Combine("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, path)))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, path)).Replace("\r\n", "\n");
    }

    private static string Body(string code, string signature)
    {
        int at = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at > 0, $"{signature} is gone");
        int end = code.IndexOf("\n    }\n", at, StringComparison.Ordinal);
        return code[at..end];
    }

    [Fact]
    public void every_page_size_is_read_from_the_document_in_one_place()
    {
        string code = ViewModel();

        Assert.Single(Regex.Matches(code, @"RenderCoreNative\.get_page_sizes\("));
        Assert.Contains("RenderCoreNative.get_page_sizes(", Body(code, "private List<PageSizePoints> PageSizes()"),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("private (double W, double H) PagePointsFor(int pageIndex)")]
    [InlineData("private (double W, double H) CurrentPageSizePoints()")]
    public void one_page_s_size_comes_from_the_cache(string signature)
    {
        string body = Body(ViewModel(), signature);

        Assert.DoesNotContain("RenderCoreNative.get_page_sizes(", body, StringComparison.Ordinal);
    }
}
