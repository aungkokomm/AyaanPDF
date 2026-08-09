using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

public class RecentFilesTests
{
    [Fact]
    public void the_newest_file_goes_to_the_top()
    {
        var list = RecentFiles.Add(new[] { @"C:\a.pdf" }, @"C:\b.pdf");
        Assert.Equal(new[] { @"C:\b.pdf", @"C:\a.pdf" }, list);
    }

    [Fact]
    public void reopening_a_file_moves_it_rather_than_listing_it_twice()
    {
        var list = RecentFiles.Add(new[] { @"C:\a.pdf", @"C:\b.pdf" }, @"C:\b.pdf");
        Assert.Equal(new[] { @"C:\b.pdf", @"C:\a.pdf" }, list);
    }

    [Fact]
    public void the_same_file_spelled_differently_is_one_entry()
    {
        // Windows paths are case-insensitive, so the menu would otherwise offer
        // the same document twice under two spellings.
        var list = RecentFiles.Add(new[] { @"C:\Docs\A.pdf" }, @"c:\docs\a.pdf");
        Assert.Single(list);
    }

    [Fact]
    public void the_list_stops_growing_at_the_cap()
    {
        var list = new List<string>();
        for (int i = 0; i < 25; i++)
        {
            list = RecentFiles.Add(list, $@"C:\f{i}.pdf");
        }

        Assert.Equal(RecentFiles.Max, list.Count);
        Assert.Equal(@"C:\f24.pdf", list[0]);
    }

    [Fact]
    public void files_that_no_longer_exist_are_dropped()
    {
        var kept = RecentFiles.Prune(
            new[] { @"C:\gone.pdf", @"C:\here.pdf" },
            p => p.EndsWith("here.pdf", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(new[] { @"C:\here.pdf" }, kept);
    }
}
