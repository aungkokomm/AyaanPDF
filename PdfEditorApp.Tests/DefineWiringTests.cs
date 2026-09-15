using System;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Define, as wired into the page, and the dictionary the app really ships.
/// </summary>
/// <remarks>
/// The popup itself cannot be loaded here, so what is checked is the order the
/// right-click does things in, the three ways the popup is put away, and that
/// the bundled file answers for ordinary English words and for nothing else.
/// </remarks>
public class DefineWiringTests
{
    private static string PathTo(params string[] relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string path = Path.Combine(relative);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, path)))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, path);
    }

    private static string PageCode() => File.ReadAllText(PathTo("PdfEditorApp", "MainPage.xaml.cs"));

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

    // ---------------- The right-click ----------------

    [Fact]
    public void the_word_is_read_before_the_right_click_re_picks_what_is_selected()
    {
        string handler = MethodBody(PageCode(), "private void ViewportHost_RightTapped(");

        int read = IndexIn(handler, "ViewModel.DefineCandidate()");
        Assert.True(read < IndexIn(handler, "TryShowLinkMenu("));
        Assert.True(read < IndexIn(handler, "ViewModel.SelectAnnotationAt("));
    }

    [Fact]
    public void only_a_selection_that_is_one_english_word_reaches_the_menu()
    {
        string handler = MethodBody(PageCode(), "private void ViewportHost_RightTapped(");

        Assert.True(IndexIn(handler, "EnglishWord.TryNormalize(") < IndexIn(handler, "DefineWord = _defineCandidate?.Word"));
        Assert.Contains("case ContextCommand.Define: ShowDefinition(); break;",
            MethodBody(PageCode(), "private void RunContextCommand("), StringComparison.Ordinal);
    }

    // ---------------- Putting it away ----------------

    [Fact]
    public void escape_closes_the_popup_before_it_can_reach_an_edit_or_a_selection()
    {
        string handler = MethodBody(PageCode(), "private void RootGrid_KeyDown(");

        int hide = IndexIn(handler, "HideDefinition();");
        Assert.True(hide < IndexIn(handler, "if (IsTextInputFocused)"));
        Assert.True(hide < IndexIn(handler, "ViewModel.IsEditingInPlace"));
        Assert.True(hide < IndexIn(handler, "case VirtualKey.Escape:"));
    }

    [Fact]
    public void any_press_on_the_page_closes_the_popup_whatever_the_button()
    {
        string handler = MethodBody(PageCode(), "private void ViewportHost_PointerPressed(");

        Assert.True(IndexIn(handler, "HideDefinition();") < IndexIn(handler, "IsLeftButtonPressed"));
    }

    [Fact]
    public void the_popup_follows_its_word_through_scroll_zoom_and_the_rulers()
    {
        Assert.Contains("PlaceDefinition();", MethodBody(PageCode(), "private void PageScroller_ViewChanged("), StringComparison.Ordinal);
        Assert.Contains("DefinitionPopup.Margin = PageScroller.Margin;", MethodBody(PageCode(), "private void SetRulersVisible("), StringComparison.Ordinal);
    }

    [Fact]
    public void the_popup_is_yellow_and_never_takes_a_click()
    {
        string xaml = File.ReadAllText(PathTo("PdfEditorApp", "MainPage.xaml"));
        var element = Regex.Match(xaml, @"<Border\s+x:Name=""DefinitionPopup""[^>]*?>", RegexOptions.Singleline);

        Assert.True(element.Success, "DefinitionPopup was not found in MainPage.xaml");
        Assert.Contains(@"IsHitTestVisible=""False""", element.Value, StringComparison.Ordinal);
        Assert.Contains(@"Background=""#FFFFF4CE""", element.Value, StringComparison.Ordinal);
    }

    // ---------------- The dictionary the app ships ----------------

    [Fact]
    public void the_dictionary_and_its_licence_are_copied_beside_the_exe()
    {
        string project = File.ReadAllText(PathTo("PdfEditorApp", "PdfEditorApp.csproj"));

        Assert.Contains(@"<Content Include=""Assets\Dictionary\**\*"">", project, StringComparison.Ordinal);
        Assert.True(File.Exists(PathTo("PdfEditorApp", "Assets", "Dictionary", "LICENSE-WordNet.txt")));
    }

    private static readonly Lazy<WordDefinitions> Shipped = new(() =>
    {
        using var file = File.OpenRead(PathTo("PdfEditorApp", "Assets", "Dictionary", "wordnet-en.tsv.gz"));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        return WordDefinitions.Load(reader);
    });

    [Theory]
    [InlineData("book", "book", "noun")]
    [InlineData("Reading.", "reading", "noun")]
    [InlineData("running", "running", "noun")]
    [InlineData("went", "go", "verb")]
    [InlineData("children", "child", "noun")]
    [InlineData("bigger", "big", "adjective")]
    [InlineData("stopped", "stop", "verb")]
    [InlineData("serendipity", "serendipity", "noun")]
    [InlineData("“meditation,”", "meditation", "noun")]
    public void ordinary_english_words_are_defined_by_the_shipped_dictionary(string selected, string headword, string pos)
    {
        Assert.True(EnglishWord.TryNormalize(selected, out string word));

        var found = Shipped.Value.Lookup(word);

        Assert.NotNull(found);
        Assert.Contains(found!.Senses, s => s.Headword == headword && s.PartOfSpeech == pos);
        Assert.All(found.Senses, s => Assert.NotEmpty(s.Definitions[0]));
    }

    [Theory]
    [InlineData("नमस्ते")]
    [InlineData("पुस्तक")]
    [InlineData("ध्यान")]
    [InlineData("မင်္ဂလာပါ")]
    [InlineData("စာအုပ်")]
    [InlineData("book पुस्तक")]
    public void hindi_and_myanmar_selections_are_ignored_and_never_looked_up(string selected)
    {
        // Refused at the door, so no Define row is offered.
        Assert.False(EnglishWord.TryNormalize(selected, out _));

        // And had one slipped through, the dictionary has nothing to say.
        Assert.Null(Shipped.Value.Lookup(selected));
    }
}
