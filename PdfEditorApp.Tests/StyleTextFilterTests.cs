using System;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The second opinion on a run that already matched a style.
///
/// The case it exists for: a book sets its running header in the same font and
/// size as its chapter titles, so style alone bookmarks every page with the
/// book's own name. Excluding that one phrase is the difference between a
/// usable outline and a useless one.
/// </summary>
public class StyleTextFilterTests
{
    [Fact]
    public void no_filter_lets_everything_through()
    {
        Assert.True(StyleTextFilter.Any.Allows("anything at all"));
        Assert.True(StyleTextFilter.Any.Allows(string.Empty));
    }

    [Fact]
    public void an_empty_box_is_no_filter_whatever_the_kind_says()
    {
        // Half-typed is the normal state of a text box. A filter that matched
        // nothing while it was empty would look like the scan had broken.
        foreach (var kind in new[]
        {
            TextFilterKind.Contains, TextFilterKind.StartsWith,
            TextFilterKind.DoesNotContain, TextFilterKind.Matches,
        })
        {
            Assert.True(StyleTextFilter.Create(kind, "").Allows("Chapter One"));
            Assert.True(StyleTextFilter.Create(kind, "   ").Allows("Chapter One"));
        }
    }

    [Fact]
    public void contains_and_its_opposite_are_the_running_header_case()
    {
        var keep = StyleTextFilter.Create(TextFilterKind.Contains, "chapter");
        var drop = StyleTextFilter.Create(TextFilterKind.DoesNotContain, "A HISTORY OF THE WORLD");

        Assert.True(keep.Allows("Chapter One"));
        Assert.False(keep.Allows("Introduction"));

        Assert.False(drop.Allows("A History of the World"));
        Assert.True(drop.Allows("Chapter One"));
    }

    [Fact]
    public void case_never_matters()
    {
        // A heading is capitalised, a running header is often set in capitals,
        // and nobody typing into that box is thinking about either.
        Assert.True(StyleTextFilter.Create(TextFilterKind.Contains, "CHAPTER").Allows("Chapter One"));
        Assert.True(StyleTextFilter.Create(TextFilterKind.StartsWith, "chapter").Allows("CHAPTER ONE"));
        Assert.True(StyleTextFilter.Create(TextFilterKind.Matches, "^chapter").Allows("Chapter One"));
    }

    [Fact]
    public void starts_with_ignores_the_space_a_pdf_left_in_front()
    {
        // Extracted text keeps whatever gap the layout put before the first
        // glyph, and "starts with" would otherwise almost never be true.
        Assert.True(StyleTextFilter.Create(TextFilterKind.StartsWith, "Chapter").Allows("  Chapter One"));
    }

    [Fact]
    public void a_pattern_that_will_not_compile_says_so_when_it_is_made()
    {
        // Rather than per run, silently, thousands of times. The dialog can
        // show this the way the pattern box already does.
        var bad = Assert.Throws<ArgumentException>(
            () => StyleTextFilter.Create(TextFilterKind.Matches, "([unclosed"));

        Assert.Contains("Not a valid pattern", bad.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void the_filter_is_applied_to_the_finished_title()
    {
        // A heading that wrapped is tested as the one thing it is. Testing the
        // halves would reject the second, which carries none of the words being
        // looked for, and break the join.
        var runs = new[]
        {
            new StyledRun(0, 0, 0, 11, new TextStyle("F1", 18, 0), "Chapter One"),
            new StyledRun(0, 1, 13, 9, new TextStyle("F1", 18, 0), "Continued"),
        };

        var found = StyleBookmarker.Detect(
            runs, [new TextStyle("F1", 18, 0)], StyleMatch.Default, allowMultiline: true,
            StyleTextFilter.Create(TextFilterKind.Contains, "Chapter"));

        Assert.Single(found);
        Assert.Equal("Chapter One Continued", found[0].Title);
    }
}
