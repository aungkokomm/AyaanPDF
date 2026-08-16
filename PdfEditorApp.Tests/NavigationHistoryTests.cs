using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Getting back to where you were.
///
/// The browser model, because it is the one people already know. Most of the
/// value is in the two rules that are easy to get subtly wrong: going
/// somewhere new discards the forward trail, and Back returns to where the
/// reader actually WAS rather than where they first landed.
/// </summary>
public class NavigationHistoryTests
{
    private static NavigationPoint At(int page, double fraction = 0) => new(page, fraction);

    private static NavigationHistory Started(int page = 0)
    {
        var history = new NavigationHistory();
        history.Reset(At(page));
        return history;
    }

    [Fact]
    public void a_fresh_history_goes_nowhere()
    {
        var history = Started();

        Assert.False(history.CanGoBack);
        Assert.False(history.CanGoForward);
        Assert.Null(history.Back());
        Assert.Null(history.Forward());
    }

    [Fact]
    public void back_returns_to_where_the_jump_started()
    {
        // Page 1500, click a bookmark to page 20, want page 1500 again. The
        // whole feature in one case.
        var history = Started();
        history.Record(At(1500), At(20));

        Assert.True(history.CanGoBack);
        Assert.Equal(At(1500), history.Back());
    }

    [Fact]
    public void forward_undoes_a_back()
    {
        var history = Started();
        history.Record(At(1500), At(20));

        Assert.Equal(At(1500), history.Back());
        Assert.True(history.CanGoForward);
        Assert.Equal(At(20), history.Forward());
        Assert.False(history.CanGoForward);
    }

    [Fact]
    public void going_somewhere_new_discards_the_forward_trail()
    {
        // What makes forward mean "undo my back" rather than "somewhere I went
        // once". Without it, Forward would walk into a branch the reader
        // abandoned and has no memory of choosing.
        var history = Started();
        history.Record(At(100), At(200));
        history.Record(At(200), At(300));

        history.Back();                       // at 200
        Assert.True(history.CanGoForward);

        history.Record(At(200), At(900));     // a new jump from here
        Assert.False(history.CanGoForward);
        Assert.Equal(At(200), history.Back());
    }

    [Fact]
    public void back_returns_to_where_the_reader_actually_was()
    {
        // They arrived at page 20, then read on to page 24 before jumping
        // away. Back has to give them 24, not the 20 they landed on.
        var history = Started();
        history.Record(At(1500), At(20));
        history.Record(At(24), At(80));

        Assert.Equal(At(24), history.Back());
    }

    [Fact]
    public void scrolling_after_arriving_is_not_lost()
    {
        // The same thing without a second jump: NoteCurrent keeps the cursor
        // entry honest as the reader moves around.
        var history = Started();
        history.Record(At(1500), At(20));
        history.NoteCurrent(At(20, 0.75));

        Assert.Equal(At(20, 0.75), history.Forward() ?? At(20, 0.75));

        history.Record(At(20, 0.75), At(400));
        Assert.Equal(At(20, 0.75), history.Back());
    }

    [Fact]
    public void a_long_trail_walks_all_the_way_back()
    {
        var history = Started();
        for (int i = 1; i <= 5; i++)
        {
            history.Record(At(i * 100), At((i + 1) * 100));
        }

        for (int i = 5; i >= 1; i--)
        {
            Assert.Equal(At(i * 100), history.Back());
        }

        Assert.False(history.CanGoBack);
    }

    [Fact]
    public void the_oldest_places_are_dropped_and_the_cursor_keeps_up()
    {
        // The cursor is an index into a list that loses entries from the
        // front. Not moving it with them is how Back starts landing on the
        // wrong place after fifty jumps, which nobody would ever reproduce
        // deliberately.
        var history = Started();
        for (int i = 1; i <= NavigationHistory.Max + 20; i++)
        {
            history.Record(At(i), At(i + 1));
        }

        Assert.Equal(NavigationHistory.Max, history.Count);

        // Still coherent: the immediately previous jump is still there.
        Assert.True(history.CanGoBack);
        Assert.Equal(At(NavigationHistory.Max + 20), history.Back());
    }

    [Fact]
    public void a_new_document_starts_over()
    {
        // History belongs to a document. Offering to go "back" into the one
        // before it would be nonsense.
        var history = Started();
        history.Record(At(100), At(200));
        Assert.True(history.CanGoBack);

        history.Reset(At(0));

        Assert.False(history.CanGoBack);
        Assert.False(history.CanGoForward);
        Assert.Equal(1, history.Count);
    }

    // ---------------- What counts as a jump ----------------

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(10, 11, false)]
    [InlineData(10, 12, false)]
    [InlineData(10, 13, true)]
    [InlineData(10, 7, true)]
    [InlineData(1500, 20, true)]
    public void only_a_real_jump_is_recorded(int from, int to, bool expected)
    {
        // Reading forwards must not fill the history. If every page turn were
        // recorded, Back would step back one page, which is what PageUp does,
        // and the jump across the document would be buried under a hundred
        // entries.
        Assert.Equal(expected, NavigationHistory.IsWorthRecording(from, to));
    }

    [Fact]
    public void the_threshold_is_symmetric()
    {
        Assert.Equal(
            NavigationHistory.IsWorthRecording(10, 40),
            NavigationHistory.IsWorthRecording(40, 10));
    }
}
