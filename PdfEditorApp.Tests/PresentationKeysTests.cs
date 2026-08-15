using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Entering and leaving full screen.
///
/// Almost all of this is about Escape. It already cancels a drag, dismisses the
/// text editor and clears the selection, so a full-screen exit that took it
/// unconditionally would quietly break three things that have nothing to do
/// with full screen.
/// </summary>
public class PresentationKeysTests
{
    private const int SomeOtherKey = 0x41;   // A

    [Fact]
    public void f11_enters_when_windowed()
    {
        Assert.Equal(
            PresentationAction.Enter,
            PresentationKeys.Resolve(PresentationKeys.KeyF11, isFullScreen: false, textFocused: false));
    }

    [Fact]
    public void f11_leaves_when_presenting()
    {
        // A toggle, which is what every browser and reader does.
        Assert.Equal(
            PresentationAction.Exit,
            PresentationKeys.Resolve(PresentationKeys.KeyF11, isFullScreen: true, textFocused: false));
    }

    [Fact]
    public void escape_leaves_when_presenting()
    {
        Assert.Equal(
            PresentationAction.Exit,
            PresentationKeys.Resolve(PresentationKeys.KeyEscape, isFullScreen: true, textFocused: false));
    }

    [Fact]
    public void escape_is_not_ours_when_windowed()
    {
        // THE test. Escape must fall straight through to the canvas, or
        // cancelling a drag and clearing a selection stop working.
        Assert.Equal(
            PresentationAction.None,
            PresentationKeys.Resolve(PresentationKeys.KeyEscape, isFullScreen: false, textFocused: false));
    }

    [Fact]
    public void escape_never_enters_full_screen()
    {
        // It is an exit key everywhere in Windows. Entering on it would be a
        // nasty surprise for someone dismissing something.
        foreach (bool full in new[] { true, false })
        {
            Assert.NotEqual(
                PresentationAction.Enter,
                PresentationKeys.Resolve(PresentationKeys.KeyEscape, full, textFocused: false));
        }
    }

    [Theory]
    [InlineData(PresentationKeys.KeyEscape)]
    [InlineData(PresentationKeys.KeyF11)]
    public void a_key_typed_into_a_text_field_belongs_to_the_field(int key)
    {
        // Escape cancels the edit; someone typing has not asked to change the
        // shape of the window.
        foreach (bool full in new[] { true, false })
        {
            Assert.Equal(
                PresentationAction.None,
                PresentationKeys.Resolve(key, full, textFocused: true));
        }
    }

    [Fact]
    public void other_keys_are_left_alone()
    {
        foreach (bool full in new[] { true, false })
        {
            Assert.Equal(
                PresentationAction.None,
                PresentationKeys.Resolve(SomeOtherKey, full, textFocused: false));
        }
    }

    // ---------------- Restoring chrome ----------------

    [Fact]
    public void presenting_shows_no_chrome() =>
        Assert.Equal(new ChromeState(false, false, false), ChromeState.Hidden);

    [Fact]
    public void what_was_captured_is_what_comes_back()
    {
        // Restoring a fixed set would turn rulers on for someone who had them
        // off, and reopen a panel they had closed. A mode that changes your
        // settings on the way out feels like it broke something.
        var before = new ChromeState(Rulers: true, Thumbnails: false, Bookmarks: true);

        Assert.Equal(before, before with { });
        Assert.NotEqual(before, ChromeState.Hidden);
    }

    [Fact]
    public void chrome_state_compares_by_value()
    {
        // It is stashed and compared on the way back out, so reference
        // equality would silently never match.
        Assert.Equal(new ChromeState(true, false, true), new ChromeState(true, false, true));
    }
}
