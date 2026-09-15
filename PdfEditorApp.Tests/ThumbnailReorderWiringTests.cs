using System;
using System.IO;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Dragging a thumbnail to a new place reorders the document itself, so the
/// order survives a save.
/// </summary>
/// <remarks>
/// ⚠️ A LISTVIEW REORDER IS A REMOVE THEN AN ADD, NEVER A MOVE. The handler
/// that waited for a Move never ran, from v1.43.0 until 3.45.32: the thumbnails
/// showed the new order, the document kept the old one, and saving wrote the
/// old one. Found by the user after inserting pages.
/// </remarks>
public class ThumbnailReorderWiringTests
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
        return File.ReadAllText(Path.Combine(dir!.FullName, path)).Replace("\r\n", "\n");
    }

    private static string PageCode() => Read("PdfEditorApp", "MainPage.xaml.cs");

    private static string Handler()
    {
        string source = PageCode();
        const string signature = "private void ThumbnailList_DragItemsCompleted(";
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{signature}' is gone; this test needs rewriting to match");
        int next = source.IndexOf("\n    private ", at + signature.Length, StringComparison.Ordinal);
        return next > at ? source[at..next] : source[at..];
    }

    private static int IndexIn(string body, string text)
    {
        int at = body.IndexOf(text, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{text}' was not found");
        return at;
    }

    [Fact]
    public void a_thumbnail_drag_rebuilds_the_document_when_the_drag_finishes()
    {
        Assert.Contains("DragItemsCompleted=\"ThumbnailList_DragItemsCompleted\"",
            Read("PdfEditorApp", "MainPage.xaml"), StringComparison.Ordinal);

        string handler = Handler();
        Assert.True(IndexIn(handler, "ViewModel.Thumbnails.Select(t => t.PageIndex)") < IndexIn(handler, "ViewModel.RebuildPages(order)"));
        Assert.Contains("DispatcherQueue.TryEnqueue(", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void nothing_waits_for_a_move_the_list_never_raises()
    {
        Assert.DoesNotContain("NotifyCollectionChangedAction.Move", PageCode(), StringComparison.Ordinal);
        Assert.DoesNotContain("Thumbnails.CollectionChanged +=", PageCode(), StringComparison.Ordinal);
    }

    [Fact]
    public void a_drag_dropped_where_it_started_changes_nothing()
    {
        string handler = Handler();

        Assert.True(IndexIn(handler, "order.SequenceEqual(Enumerable.Range(0, order.Count))")
            < IndexIn(handler, "ViewModel.RebuildPages(order)"));
    }
}
