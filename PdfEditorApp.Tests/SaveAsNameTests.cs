using System;
using System.Collections.Generic;
using System.IO;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Save As offers the original's own name with a number after it, rather
/// than "edited" for every file.
/// </summary>
public class SaveAsNameTests
{
    private static readonly string Folder = Path.Combine("D:", "Books");
    private static readonly string Original = Path.Combine(Folder, "Annual Report.pdf");

    private static Func<string, bool> Existing(params string[] names)
    {
        var there = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string n in names) { there.Add(Path.Combine(Folder, n)); }
        return there.Contains;
    }

    [Fact]
    public void a_copy_is_offered_the_originals_name_with_1()
    {
        Assert.Equal("Annual Report_1", SaveAsName.For(Original, flatten: false, Existing()));
    }

    [Fact]
    public void a_number_already_taken_beside_the_original_is_stepped_past()
    {
        Assert.Equal("Annual Report_3",
            SaveAsName.For(Original, flatten: false, Existing("Annual Report_1.pdf", "Annual Report_2.pdf")));
    }

    [Fact]
    public void a_flattened_copy_says_so_in_its_name()
    {
        Assert.Equal("Annual Report_flattened", SaveAsName.For(Original, flatten: true, Existing()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void a_document_never_saved_keeps_the_old_names(string? path)
    {
        Assert.Equal("edited", SaveAsName.For(path, flatten: false, Existing()));
        Assert.Equal("flattened", SaveAsName.For(path, flatten: true, Existing()));
    }

    [Fact]
    public void burmese_and_hindi_names_are_kept_as_they_are()
    {
        string myanmar = Path.Combine(Folder, "မြန်မာ စာ.pdf");
        string hindi = Path.Combine(Folder, "वार्षिक रिपोर्ट.pdf");
        Assert.Equal("မြန်မာ စာ_1", SaveAsName.For(myanmar, flatten: false, Existing()));
        Assert.Equal("वार्षिक रिपोर्ट_1", SaveAsName.For(hindi, flatten: false, Existing()));
    }

    [Fact]
    public void save_as_asks_for_this_name()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PdfEditorApp", "MainPage.xaml.cs")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        string code = File.ReadAllText(Path.Combine(dir!.FullName, "PdfEditorApp", "MainPage.xaml.cs"));
        Assert.Contains(
            "picker.SuggestedFileName = SaveAsName.For(ViewModel.DocumentPath, flatten, System.IO.File.Exists);",
            code, StringComparison.Ordinal);
    }
}
