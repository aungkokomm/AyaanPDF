using System;
using System.IO;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// A retyped line is written the way the page will read it back.
/// </summary>
public class TypedLineTests
{
    [Fact]
    public void the_readers_double_space_is_written_as_one()
    {
        Assert.Equal("The reading room is open from and to nine until six on Saturday.",
            TypedLine.ForWriting("The reading room is open from and to  nine until six on Saturday."));
    }

    [Theory]
    [InlineData("a   b", "a b")]
    [InlineData("  edge spaces  ", "edge spaces")]
    [InlineData("one space stays", "one space stays")]
    [InlineData("मेरे  घर", "मेरे घर")]
    [InlineData("မြန်မာ  စာ", "မြန်မာ စာ")]
    public void runs_of_spaces_close_up_and_nothing_else_changes(string typed, string written)
    {
        Assert.Equal(written, TypedLine.ForWriting(typed));
    }

    [Fact]
    public void a_line_commit_writes_it_this_way()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string path = Path.Combine("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, path)))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        string code = File.ReadAllText(Path.Combine(dir!.FullName, path));

        int at = code.IndexOf("public bool EditSelectedLine(string newText)", StringComparison.Ordinal);
        Assert.True(at > 0, "EditSelectedLine is gone; this test needs rewriting to match");
        int written = code.IndexOf("newText = TypedLine.ForWriting(newText);", at, StringComparison.Ordinal);
        int empty = code.IndexOf("if (newText.Length == 0)", at, StringComparison.Ordinal);
        Assert.True(written > at && written < empty,
            "the line is not put into its written form before anything checks it");
    }
}
