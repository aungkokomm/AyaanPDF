using System;
using System.IO;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// That the gradient writer is actually on the save path.
///
/// The Rust suite proves the writer turns a stored gradient into a real PDF
/// shading and that PDFium renders it back. It cannot prove anybody CALLS it,
/// and a writer nothing calls is the exact shape of a feature that passes every
/// test and does nothing in the app. The view model cannot be loaded here, so
/// these read the source, the technique the effect rows' tests already use and
/// for the same reason.
/// </summary>
public class GradientSaveWiringTests
{
    private static string FileFromRepo(params string[] relative)
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
        FileFromRepo("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");

    private static string Interop() =>
        FileFromRepo("PdfEditorApp", "Interop", "RenderCoreNative.cs");

    [Fact]
    public void the_writer_is_declared_across_the_boundary()
    {
        Assert.Contains("int write_gradients(", Interop(), StringComparison.Ordinal);
    }

    [Fact]
    public void every_save_runs_the_writer_over_what_pdfium_wrote()
    {
        // On the file PDFium has just produced, not on the open document: the
        // gradient goes into the appearance stream, and PDFium generates that
        // itself and cannot put a shading in it.
        string code = ViewModel();

        int save = code.IndexOf(
            "bool saved = RenderCoreNative.save_document(", StringComparison.Ordinal);
        Assert.True(save > 0, "the save call has moved; this test needs updating");

        int call = code.IndexOf("WriteGradientFills(writePath);", save, StringComparison.Ordinal);
        Assert.True(call > save, "the gradient writer is not called after the save");

        // BEFORE the swap. An in-place save writes to a temporary copy and only
        // then replaces the original, and a writer that ran after that would be
        // rewriting the user's own file.
        int swap = code.IndexOf("if (inPlace && saved)", save, StringComparison.Ordinal);
        Assert.True(swap > call, "the writer runs after the file has been swapped in");
    }

    [Fact]
    public void a_document_with_no_gradient_is_left_exactly_as_it_was()
    {
        // The writer reports how many it wrote and creates nothing when that is
        // zero. Moving the file regardless would replace every saved document
        // with one lopdf had rewritten, for a feature it is not using.
        string body = MethodBody("private static void WriteGradientFills(");

        Assert.Contains("if (written == 0)", body, StringComparison.Ordinal);

        int guard = body.IndexOf("if (written == 0)", StringComparison.Ordinal);
        int move = body.IndexOf("File.Move(temp, path", StringComparison.Ordinal);

        Assert.True(move > guard, "the file is swapped before the count is checked");
    }

    [Fact]
    public void a_failed_swap_leaves_both_files_rather_than_deleting_one()
    {
        // The same rule the outline writer follows: the saved file is intact
        // and the rewritten copy stays where it is. A failed replace must never
        // be able to lose both.
        string body = MethodBody("private static void WriteGradientFills(");

        Assert.Contains("catch (Exception", body, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Delete(", body, StringComparison.Ordinal);
    }

    private static string MethodBody(string signature)
    {
        string code = ViewModel();
        int at = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at > 0, signature + " is missing");

        int end = code.IndexOf("\n    public ", at + 1, StringComparison.Ordinal);

        return end > at ? code[at..end] : code[at..];
    }
}
