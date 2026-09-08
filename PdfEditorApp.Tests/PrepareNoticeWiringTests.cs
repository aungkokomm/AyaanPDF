using System;
using System.IO;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Saying that the Burmese is being prepared, instead of saying nothing.
///
/// ⚠️ THE DEFECT THIS EXISTS FOR. A Burmese page cannot be read until its font
/// has been reshaped into an index, which takes about twenty seconds, and for
/// the whole of that wait the app said nothing whatever. The reader saw
/// scrambled text that refused to be clicked, with no reason given, which can
/// only be read as the app having hung.
///
/// Checked in the source, because these live in a WinUI project a test assembly
/// cannot load.
/// </summary>
public class PrepareNoticeWiringTests
{
    private static string Vm() => Source("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");

    private static string Xaml() => Source("PdfEditorApp", "MainPage.xaml");

    /// <summary>
    /// ⚠️ STARTED WHERE THE WAIT IS DISCOVERED. The gateway is what kicks the
    /// preparation off and what finds out it has not finished, so the bar goes
    /// up from the branch that already knows, rather than from somewhere else
    /// having to guess at it.
    /// </summary>
    [Fact]
    public void the_bar_goes_up_when_a_page_comes_back_unsettled()
    {
        string body = Method(Vm(), "private PageTextContext ContextFor(", 1600);

        int settled = body.IndexOf("if (context.Settled)", StringComparison.Ordinal);
        int watch = body.IndexOf("WatchPreparation();", StringComparison.Ordinal);

        Assert.True(settled > 0, "the page is no longer cached on settling");
        Assert.True(watch > settled, "nothing starts watching when the answer is not final");
    }

    /// <summary>
    /// ⚠️ ASKED ON A TIMER, NOT WHEN PAGES HAPPEN TO BE DRAWN. The lines are
    /// read on whatever schedule scrolling produces, so a bar driven by that
    /// would sit frozen exactly when the reader is keeping still to watch it.
    /// </summary>
    [Fact]
    public void the_bar_is_driven_by_a_clock_and_stops_when_there_is_nothing_to_say()
    {
        string body = Method(Vm(), "private DispatcherQueueTimer? CreatePrepareTimer()", 1800);

        Assert.Contains("timer.IsRepeating = true;", body, StringComparison.Ordinal);
        Assert.Contains("RenderCoreNative.recovery_progress(_documentHandle)",
            body, StringComparison.Ordinal);

        // ⚠️ AND IT STOPS ITSELF. A repeating timer left running is a wake-up
        // four times a second for the life of the document.
        int stops = body.IndexOf("if (now < 0) { t.Stop(); }", StringComparison.Ordinal);
        Assert.True(stops > 0, "the timer never stops");
    }

    /// <summary>
    /// The percentage is the core's, and -1 means nothing is being prepared, so
    /// that is what decides whether anything shows at all.
    /// </summary>
    [Fact]
    public void nothing_shows_when_nothing_is_being_prepared()
    {
        string vm = Vm();
        Assert.Contains("public bool IsPreparingText => _preparePercent >= 0;",
            vm, StringComparison.Ordinal);
        Assert.Contains("public int PreparePercent => Math.Max(0, _preparePercent);",
            vm, StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠️ IT EXPLAINS A WAIT, IT DOES NOT ASK ANYTHING. A click has to reach
    /// the document underneath as though the notice were not there, or the app
    /// answers a silent freeze with a panel that swallows presses.
    /// </summary>
    [Fact]
    public void the_notice_floats_over_the_page_without_taking_its_clicks()
    {
        string xaml = Xaml();

        int at = xaml.IndexOf("ViewModel.IsPreparingText", StringComparison.Ordinal);
        Assert.True(at > 0, "nothing on screen says the text is being prepared");

        string block = xaml[at..Math.Min(xaml.Length, at + 900)];
        Assert.Contains("IsHitTestVisible=\"False\"", block, StringComparison.Ordinal);
        Assert.Contains("ViewModel.PrepareMessage", block, StringComparison.Ordinal);
        Assert.Contains("ViewModel.PreparePercent", block, StringComparison.Ordinal);

        // ⚠️ IN THE CANVAS COLUMN, AND WITHOUT THIS IT IS NOT A FLOATING NOTICE
        // AT ALL. The chrome here occupies real COLUMNS and the canvas is the
        // third, so an element that does not say which column it is in lands in
        // the FIRST: this shipped that way once and appeared squashed into the
        // bottom of the tool rail, on top of the buttons there, nowhere near
        // the document it was talking about.
        //
        // ⚠️ READ OFF THE TAG, NOT OFF THE TEXT AROUND IT. The comment above
        // this element explains the rule and so contains the very string being
        // looked for, and a window wide enough to catch a careless edit is wide
        // enough to catch that comment and pass on it.
        int tag = xaml.LastIndexOf("<Border", at, StringComparison.Ordinal);
        Assert.True(tag > 0, "the notice is not an element");
        string opening = xaml[tag..xaml.IndexOf('>', tag)];
        Assert.Contains("Grid.Column=\"2\"", opening, StringComparison.Ordinal);

        // ⚠️ LAST IN THE GRID, so it floats over the page rather than moving
        // it. Anywhere earlier and the document would jump the moment a wait
        // began and jump back when it ended.
        int grid = xaml.LastIndexOf("</Grid>", StringComparison.Ordinal);
        Assert.True(at < grid, "the notice is outside the grid it floats in");
        Assert.True(
            xaml.IndexOf("ViewportHost", StringComparison.Ordinal) < at,
            "the notice is declared before the page it floats over");

        // ⚠️ AND NOT AT THE EDGE THE STATUS PILL ALREADY OWNS. That is centred
        // at the BOTTOM of the same column, and two centred things at one edge
        // sit on top of each other.
        int pill = xaml.IndexOf("x:Name=\"StatusBar\"", StringComparison.Ordinal);
        Assert.True(pill > 0);
        string pillBlock = xaml[pill..Math.Min(xaml.Length, pill + 300)];
        Assert.Contains("VerticalAlignment=\"Bottom\"", pillBlock, StringComparison.Ordinal);
        Assert.Contains("VerticalAlignment=\"Top\"", block, StringComparison.Ordinal);
    }

    /// <summary>
    /// It says what the wait is FOR. "Building a reshaping index" is true and
    /// tells a reader nothing they can act on; what they want to know is that
    /// the text becomes editable and that waiting is how to get there.
    /// </summary>
    [Fact]
    public void the_message_says_what_the_wait_buys_and_not_what_the_code_is_doing()
    {
        string message = Method(Vm(), "public string PrepareMessage =>", 200);

        Assert.Contains("editing", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("index", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reshap", message, StringComparison.OrdinalIgnoreCase);
    }

    private static string Method(string source, string signature, int length)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at > 0, $"{signature} is gone");
        return source[at..Math.Min(source.Length, at + length)];
    }

    private static string Source(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string path = Path.Combine(parts);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, path)))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, path));
    }
}
