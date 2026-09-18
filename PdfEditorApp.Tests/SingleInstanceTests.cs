using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Opening PDFs from Explorer: the files a launch was asked to open, and one
/// window per user that later launches hand their files to.
/// </summary>
public class SingleInstanceTests
{
    // ---------------- The launch's files ----------------

    [Fact]
    public void the_pdfs_on_the_command_line_become_full_paths_once_each()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ayaan-launch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string a = Path.Combine(folder, "a.pdf");
            string b = Path.Combine(folder, "My Book.PDF");
            string notPdf = Path.Combine(folder, "notes.txt");
            File.WriteAllText(a, "x");
            File.WriteAllText(b, "x");
            File.WriteAllText(notPdf, "x");

            var files = LaunchFiles.From(
                ["a.pdf", '"' + b + '"', a, notPdf, Path.Combine(folder, "missing.pdf"), "", "--flag"],
                folder);

            // Relative to the folder it was typed in, quotes dropped, the
            // repeat of a.pdf ignored, and nothing that is not an existing PDF.
            Assert.Equal([a, b], files);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void a_launch_with_no_files_asks_for_none()
    {
        Assert.Empty(LaunchFiles.From([], Path.GetTempPath()));
    }

    // ---------------- One window ----------------

    private static string UniqueName() => "AyaanPDF-test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public void the_first_launch_claims_the_window_and_a_second_does_not()
    {
        string name = UniqueName();
        using var first = SingleInstance.TryClaim(name, _ => { });
        Assert.NotNull(first);
        Assert.Null(SingleInstance.TryClaim(name, _ => { }));
    }

    [Fact]
    public void a_second_launch_hands_its_files_to_the_first()
    {
        string name = UniqueName();
        var received = new BlockingCollection<IReadOnlyList<string>>();
        using var first = SingleInstance.TryClaim(name, received.Add);
        Assert.NotNull(first);

        string[] files = [@"C:\Books\one.pdf", @"D:\Two words\two.pdf"];
        Assert.True(SingleInstance.TrySend(name, files, TimeSpan.FromSeconds(5)));
        Assert.True(received.TryTake(out var got, TimeSpan.FromSeconds(5)));
        Assert.Equal(files, got);

        // A launch with nothing to open still reaches it, to bring it forward.
        Assert.True(SingleInstance.TrySend(name, [], TimeSpan.FromSeconds(5)));
        Assert.True(received.TryTake(out var none, TimeSpan.FromSeconds(5)));
        Assert.Empty(none);
    }

    [Fact]
    public void several_launches_at_once_all_arrive()
    {
        // Explorer opens a multi-file selection as one launch per file, all at once.
        string name = UniqueName();
        var received = new ConcurrentBag<string>();
        using var first = SingleInstance.TryClaim(name, files => { foreach (var f in files) { received.Add(f); } });
        Assert.NotNull(first);

        var sent = Enumerable.Range(0, 6).Select(i => $@"C:\Books\book{i}.pdf").ToList();
        var threads = sent.Select(f => new Thread(() => Assert.True(SingleInstance.TrySend(name, [f], TimeSpan.FromSeconds(10))))).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        SpinWait.SpinUntil(() => received.Count == sent.Count, TimeSpan.FromSeconds(5));
        Assert.Equal(sent.OrderBy(f => f), received.OrderBy(f => f));
    }

    [Fact]
    public void with_no_window_to_answer_a_launch_gives_up_and_opens_its_own()
    {
        Assert.False(SingleInstance.TrySend(UniqueName(), [@"C:\a.pdf"], TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public void once_the_window_closes_the_next_launch_claims_it()
    {
        string name = UniqueName();
        var first = SingleInstance.TryClaim(name, _ => { });
        Assert.NotNull(first);
        first!.Dispose();

        using var next = SingleInstance.TryClaim(name, _ => { });
        Assert.NotNull(next);
    }

    [Fact]
    public void each_install_folder_has_its_own_window()
    {
        // A portable copy or a test build must not hand its files to the installed app.
        string installed = SingleInstance.NameFor("Ayaan PDF", @"C:\Users\Someone\AppData\Local\Programs\Ayaan PDF");
        string portable = SingleInstance.NameFor("Ayaan PDF", @"E:\Portable\Ayaan PDF");
        Assert.NotEqual(installed, portable);
        Assert.Equal(installed, SingleInstance.NameFor("Ayaan PDF", @"c:\users\someone\appdata\local\programs\ayaan pdf\"));
        Assert.DoesNotContain(' ', installed);
    }
}
