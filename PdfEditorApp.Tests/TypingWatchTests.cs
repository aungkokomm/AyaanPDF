using System;
using System.IO;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Text Services that answers keeps every keystroke; Text Services that stays
/// silent hands them back to the character route instead of losing them.
/// </summary>
public class TypingWatchTests
{
    [Fact]
    public void text_services_that_answers_first_keeps_every_keystroke()
    {
        var watch = new TypingWatch();
        watch.EditStarted();
        watch.Delivered();

        foreach (char c in "and keep typing")
        {
            Assert.Null(watch.Received(c));
        }
        Assert.False(watch.Silent);
    }

    [Fact]
    public void text_services_that_answers_after_the_first_character_still_keeps_it()
    {
        // The character can reach the window before Text Services' update for
        // the same keystroke. Holding it and then dropping it is what keeps a
        // working machine from typing that letter twice.
        var watch = new TypingWatch();
        watch.EditStarted();

        Assert.Null(watch.Received('a'));
        watch.Delivered();
        Assert.Null(watch.Received('b'));
        Assert.Null(watch.Received('c'));
        Assert.False(watch.Silent);
    }

    [Fact]
    public void silent_text_services_gives_back_every_held_character_at_once()
    {
        var watch = new TypingWatch();
        watch.EditStarted();

        Assert.Null(watch.Received('a'));
        Assert.Null(watch.Received('n'));
        Assert.Equal("and", watch.Received('d'));
        Assert.True(watch.Silent);
        Assert.Equal(3, TypingWatch.SilentAfter);
    }

    [Fact]
    public void a_new_edit_starts_counting_again_but_silence_is_remembered()
    {
        var watch = new TypingWatch();
        watch.EditStarted();
        watch.Received('x');
        watch.Received('y');

        // A line left before the third character: what it held is not carried
        // into the next line.
        watch.EditStarted();
        Assert.Null(watch.Received('a'));
        Assert.Null(watch.Received('b'));
        Assert.Equal("abc", watch.Received('c'));

        watch.EditStarted();
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
    public void the_page_asks_the_watch_before_dropping_a_character_and_inserts_what_comes_back()
    {
        string code = Read("PdfEditorApp", "MainPage.xaml.cs");
        int handler = IndexIn(code, "private void RootGrid_CharacterReceived(");
        int control = IndexIn(code, "if (char.IsControl(args.Character)) { return; }", handler);
        int ask = IndexIn(code, "input.Unclaimed(args.Character) is not { } held", handler);
        int insert = IndexIn(code, "ViewModel.InPlaceInsert(held);", handler);
        Assert.True(control < ask && ask < insert);
    }

    [Fact]
    public void text_services_is_heard_before_its_update_is_applied_and_not_offered_once_silent()
    {
        string code = Read("PdfEditorApp", "PageTextInput.cs");
        int updating = IndexIn(code, "private void OnTextUpdating(");
        Assert.True(IndexIn(code, "s_watch.Delivered();", updating) < IndexIn(code, "_model.InPlaceReplaceRange(", updating));

        int enter = IndexIn(code, "public void Enter()");
        Assert.True(IndexIn(code, "s_watch.Silent", enter) < IndexIn(code, "IsActive = true;", enter));
    }

    [Fact]
    public void the_window_also_takes_classic_file_drops_into_the_same_open_path()
    {
        string code = Read("PdfEditorApp", "MainWindow.xaml.cs");
        IndexIn(code, "Activated += AcceptFileDropsOnce;");
        IndexIn(code, "FileDrops.Accept(WinRT.Interop.WindowNative.GetWindowHandle(this), OpenLaunchedFiles);");
    }
}
