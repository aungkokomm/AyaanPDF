using System;
using System.IO;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Where Text Services does not answer the page, a line is typed through a
/// hidden text box, and each change the box makes reaches the line as one
/// replaced range. These pin that arithmetic and the wiring around it.
/// </summary>
public class TextSinkTests
{
    private static string Apply(string before, (int Start, int End, string Inserted) edit) =>
        before[..edit.Start] + edit.Inserted + before[edit.End..];

    [Theory]
    // typed at the end, in the middle, at the start
    [InlineData("कैसा", "कैसा ह", 4)]
    [InlineData("क सा", "कैसा", 1)]
    [InlineData("सा", "कसा", 0)]
    // backspaced, and a selection typed over
    [InlineData("कैसा", "कैस", 4)]
    [InlineData("hello world", "hello there", 6)]
    // an input method revising a word it is composing, before the caret
    [InlineData("आप क", "आप कै", 4)]
    [InlineData("आप कै", "आप कैसे", 5)]
    public void the_range_reproduces_the_box(string before, string after, int caret)
    {
        var edit = TextSink.Edit(before, after, caret);
        Assert.Equal(after, Apply(before, edit));
    }

    [Fact]
    public void a_letter_typed_among_its_twins_lands_where_the_caret_was()
    {
        // Comparing the text alone reads this as an "a" added at the end.
        var edit = TextSink.Edit("aa", "aaa", 0);
        Assert.Equal((0, 0, "a"), edit);
    }

    [Fact]
    public void nothing_changed_is_an_empty_range()
    {
        Assert.Equal((3, 3, ""), TextSink.Edit("abc", "abc", 3));
    }

    [Fact]
    public void a_character_outside_the_basic_plane_is_never_cut_in_half()
    {
        // Two different emoji share their high surrogate.
        string before = "x\U0001F600y";
        string after = "x\U0001F601y";
        var edit = TextSink.Edit(before, after, 3);
        Assert.Equal(after, Apply(before, edit));
        Assert.False(char.IsLowSurrogate(edit.Inserted[0]), "the range began inside a pair");
        Assert.Equal(1, edit.Start);
    }

    [Theory]
    [InlineData(8)]   // Backspace
    [InlineData(9)]   // Tab
    [InlineData(13)]  // Enter
    [InlineData(27)]  // Escape
    [InlineData(33)]  // Page Up
    [InlineData(35)]  // End
    [InlineData(36)]  // Home
    [InlineData(37)]  // Left
    [InlineData(40)]  // Down
    [InlineData(46)]  // Delete
    [InlineData(114)] // F3
    public void keys_that_are_not_text_are_the_pages(int key)
    {
        Assert.True(TextSink.PageKey(key, ctrl: false, alt: false));
    }

    [Theory]
    [InlineData(65)]  // A
    [InlineData(75)]  // K
    [InlineData(32)]  // Space
    [InlineData(229)] // an input method's own key
    [InlineData(16)]  // Shift
    [InlineData(17)]  // Control, which the page still tracks as it bubbles
    public void keys_that_type_stay_with_the_box(int key)
    {
        Assert.False(TextSink.PageKey(key, ctrl: false, alt: false));
    }

    [Fact]
    public void every_ctrl_shortcut_is_the_pages_but_altgr_types()
    {
        Assert.True(TextSink.PageKey(86, ctrl: true, alt: false));   // Ctrl+V
        Assert.True(TextSink.PageKey(90, ctrl: true, alt: false));   // Ctrl+Z
        Assert.False(TextSink.PageKey(69, ctrl: true, alt: true));   // AltGr+E
    }

    [Fact]
    public void silence_found_in_an_earlier_run_is_believed_from_the_start()
    {
        var watch = new TypingWatch();
        watch.KnownSilent();
        Assert.True(watch.Silent);
    }

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

    private static int IndexIn(string text, string part, int from = 0)
    {
        int at = text.IndexOf(part, from, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{part}' was not found");
        return at;
    }

    [Fact]
    public void the_box_is_invisible_takes_no_clicks_and_is_outside_the_scrolling_page()
    {
        string xaml = Read("PdfEditorApp", "MainPage.xaml");
        int layer = IndexIn(xaml, "<Canvas x:Name=\"InPlaceInputLayer\"");
        int layerEnd = IndexIn(xaml, ">", layer);
        Assert.Contains("IsHitTestVisible=\"False\"", xaml[layer..layerEnd]);

        int box = IndexIn(xaml, "<TextBox x:Name=\"InPlaceTextSink\"", layer);
        int boxEnd = IndexIn(xaml, "/>", box);
        string tag = xaml[box..boxEnd];
        Assert.Contains("Opacity=\"0\"", tag);
        Assert.Contains("IsTabStop=\"False\"", tag);
        Assert.Contains("PreviewKeyDown=\"InPlaceTextSink_PreviewKeyDown\"", tag);

        // After the scrolling page has closed, so focusing it scrolls nothing.
        Assert.True(IndexIn(xaml, "</ScrollView>") < layer);
    }

    [Fact]
    public void a_silent_page_types_through_the_box_and_a_burmese_line_never_does()
    {
        string code = Read("PdfEditorApp", "MainPage.xaml.cs");
        int sync = IndexIn(code, "private void SyncTextInput()");
        int burmese = IndexIn(code, "TypingRoute.HostsTextServices(", sync);
        int silent = IndexIn(code, "if (_textInput.IsSilent)", sync);
        int enter = IndexIn(code, "_textSink.Enter();", sync);
        Assert.True(burmese < silent && silent < enter);
    }

    [Fact]
    public void the_box_is_not_a_field_and_its_characters_are_not_typed_twice()
    {
        string code = Read("PdfEditorApp", "MainPage.xaml.cs");
        int focused = IndexIn(code, "private bool IsTextInputFocused =>");
        IndexIn(code, "!ReferenceEquals(focused, InPlaceTextSink)", focused);

        int chars = IndexIn(code, "private void RootGrid_CharacterReceived(");
        int guard = IndexIn(code, "if (_textSink is { IsActive: true, HasFocus: true }) { return; }", chars);
        int insert = IndexIn(code, "ViewModel.InPlaceInsert(args.Character.ToString());", chars);
        Assert.True(guard < insert);

        int preview = IndexIn(code, "private void InPlaceTextSink_PreviewKeyDown(");
        int page = IndexIn(code, "TextSink.PageKey((int)e.Key, _isCtrlDown, IsAltDown())", preview);
        int handler = IndexIn(code, "RootGrid_KeyDown(RootGrid, e);", preview);
        Assert.True(page < handler);
    }

    [Fact]
    public void silence_is_remembered_between_runs()
    {
        string code = Read("PdfEditorApp", "PageTextInput.cs");
        IndexIn(code, "if (SettingsStore.Current.TextServicesSilent)");
        int unclaimed = IndexIn(code, "public string? Unclaimed(char c)");
        IndexIn(code, "SettingsStore.Update(s => s with { TextServicesSilent = true });", unclaimed);
    }
}
