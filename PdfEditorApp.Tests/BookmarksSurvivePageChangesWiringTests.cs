using System;
using System.IO;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Bookmarks survive reordering, duplicating and deleting pages, and bookmark
/// edits survive a plain Save.
/// </summary>
/// <remarks>
/// ⚠️ TWO LOSSES, BOTH FOUND BY READING THE CODE AFTER THE USER ASKED.
/// A rebuild copies pages into a new document, which has no outline, and the
/// re-read afterwards found none. And a plain Save reopens the file before it
/// writes the outline, and reopening cleared the edits it was about to write.
/// render_core's a_rebuilt_document_has_no_outline_so_the_app_carries_it pins
/// the first; these pin the app's side of both.
/// </remarks>
public class BookmarksSurvivePageChangesWiringTests
{
    private static string ViewModelCode()
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

    private static int IndexIn(string body, string text)
    {
        int at = body.IndexOf(text, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{text}' was not found");
        return at;
    }

    [Fact]
    public void a_rebuild_takes_the_outline_first_and_carries_it_to_the_new_pages()
    {
        string rebuild = MethodBody(ViewModelCode(), "public bool RebuildPages(");

        int taken = IndexIn(rebuild, "var outline = Bookmarks.Select(b => b.Mark).ToList()");
        Assert.True(taken < IndexIn(rebuild, "RenderCoreNative.rebuild_page_order("));
        Assert.True(IndexIn(rebuild, "ReloadAfterPageStructureChange()")
            < IndexIn(rebuild, "CarryOutline(outline, p => PageReorder.NewIndexOf(order, p), oldPageCount)"));

        string carry = MethodBody(ViewModelCode(), "private void CarryOutline(");
        Assert.Contains("OutlineEdits.Remap(", carry, StringComparison.Ordinal);
        Assert.Contains("_pendingOutline = ", carry, StringComparison.Ordinal);
    }

    [Fact]
    public void a_save_writes_the_outline_it_had_before_reopening_the_file()
    {
        string save = MethodBody(ViewModelCode(), "public bool SaveDocumentAs(string path, bool flatten)");

        Assert.True(IndexIn(save, "var outlineToWrite = _pendingOutline;") < IndexIn(save, "RenderCoreNative.save_document("));
        Assert.True(IndexIn(save, "OpenDocument(path, preserveAnnotations: false);") < IndexIn(save, "FlushPendingOutline(outlineToWrite);"));

        string flush = MethodBody(ViewModelCode(), "private void FlushPendingOutline(");
        Assert.DoesNotContain("_pendingOutline is not { } outline", flush, StringComparison.Ordinal);
    }
}
