using System;
using System.IO;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// What the app does before a document exists, and how you get out of it.
///
/// Launching used to conjure a blank one-page document nobody asked for. It
/// made the app look like it had opened something, put an untitled document in
/// front of someone who wanted to open a file, and meant the empty state could
/// never be seen at all: it was written, it was wrong (hardcoded white text on
/// a canvas that is white in half the themes), and nothing revealed either fact.
///
/// All of this is view code, which this assembly cannot load, so these read the
/// source. Same technique and same reason as <c>AppWiringTests</c> and
/// <c>KeyboardFocusWiringTests</c>.
/// </summary>
public class StartupAndEmptyStateWiringTests
{
    private static string Read(params string[] relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string path = Path.Combine(relative);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, path)))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, path));
    }

    private static string MainPageCode() => Read("PdfEditorApp", "MainPage.xaml.cs");
    private static string MainPageXaml() => Read("PdfEditorApp", "MainPage.xaml");
    private static string MainWindowCode() => Read("PdfEditorApp", "MainWindow.xaml.cs");
    private static string MainWindowXaml() => Read("PdfEditorApp", "MainWindow.xaml");

    private static string Section(string source, string anchor, int length)
    {
        int at = source.IndexOf(anchor, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{anchor}' is gone; this test needs rewriting to match");
        return source[at..Math.Min(source.Length, at + length)];
    }

    /// <summary>
    /// A whole method, bounded by the next declaration rather than by a fixed
    /// number of characters.
    ///
    /// A fixed window silently stops covering a method once it grows past it,
    /// and the test then passes because it never looked at the part that
    /// changed. RootGrid_KeyDown is several hundred lines and caught this test
    /// out exactly that way.
    /// </summary>
    private static string MethodBody(string source, string signature)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{signature}' is gone; this test needs rewriting to match");

        int next = source.IndexOf("\n    private ", at + signature.Length, StringComparison.Ordinal);
        return next > at ? source[at..next] : source[at..];
    }

    // ---------------- No document is invented at launch ----------------

    [Fact]
    public void starting_the_app_does_not_open_a_document()
    {
        // The blank page is only made when something explicitly asks for one.
        // An unguarded OpenBlankDocument in this block is the regression.
        string startup = Section(MainPageCode(), "PDFEDITOR_AUTOOPEN", 2200);

        int blank = startup.IndexOf("ViewModel.OpenBlankDocument()", StringComparison.Ordinal);
        if (blank >= 0)
        {
            string guard = startup[..blank];
            Assert.True(
                guard.Contains("if (StartBlank)", StringComparison.Ordinal),
                "the startup path makes a blank document without being asked");
        }
    }

    [Fact]
    public void the_window_opens_its_first_tab_with_no_document()
    {
        // AddDocumentTab(null) and nothing else: passing startBlank here would
        // put the old behaviour back one layer down.
        string ctor = MethodBody(MainWindowCode(), "public MainWindow()");

        Assert.Contains("AddDocumentTab(null)", ctor, StringComparison.Ordinal);
        Assert.DoesNotContain("AddDocumentTab(null, startBlank", ctor, StringComparison.Ordinal);
    }

    [Fact]
    public void file_new_still_makes_a_document()
    {
        // The feature that had to survive removing the automatic one.
        string body = Section(MainPageCode(), "private void New_Click", 500);

        Assert.Contains("startBlank: true", body, StringComparison.Ordinal);
    }

    [Fact]
    public void closing_the_last_document_returns_to_the_empty_state()
    {
        // The window refuses to be tabless, and the tab it adds carries no
        // document, so closing lands where launching does.
        string body = Section(MainWindowCode(), "private async Task CloseTab", 900);

        Assert.Contains("AddDocumentTab(null)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("startBlank", body, StringComparison.Ordinal);
    }

    // ---------------- The empty state ----------------

    [Fact]
    public void the_empty_state_offers_to_open_a_pdf()
    {
        string block = Section(MainPageXaml(), "x:Name=\"EmptyState\"", 1800);

        Assert.Contains("Open a PDF", block, StringComparison.Ordinal);
        Assert.Contains("Click=\"EmptyStateOpen_Click\"", block, StringComparison.Ordinal);

        // And that button runs the same picker the menu does, rather than a
        // second copy of the open flow.
        Assert.Contains(
            "OpenFile_Click(",
            Section(MainPageCode(), "private void EmptyStateOpen_Click", 300),
            StringComparison.Ordinal);
    }

    [Fact]
    public void the_empty_state_says_a_pdf_can_be_dropped_on_it()
    {
        // Nothing about a blank canvas suggests it is a drop target, so the
        // affordance has to be words.
        Assert.Contains(
            "Drag and drop a PDF here to open",
            Section(MainPageXaml(), "x:Name=\"EmptyState\"", 1800),
            StringComparison.Ordinal);
    }

    [Fact]
    public void the_empty_state_actually_accepts_a_drop()
    {
        // Saying so and doing so are separate: without AllowDrop and a DragOver
        // that sets AcceptedOperation, the cursor shows "no" and Drop never
        // fires.
        string block = Section(MainPageXaml(), "x:Name=\"EmptyState\"", 1800);

        Assert.Contains("AllowDrop=\"True\"", block, StringComparison.Ordinal);
        Assert.Contains("DragOver=\"EmptyState_DragOver\"", block, StringComparison.Ordinal);
        Assert.Contains("Drop=\"EmptyState_Drop\"", block, StringComparison.Ordinal);

        Assert.Contains(
            "AcceptedOperation",
            Section(MainPageCode(), "private void EmptyState_DragOver", 700),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("private async void EmptyState_Drop")]
    [InlineData("private async void OpenFile_Click")]
    public void every_way_of_opening_a_file_goes_through_the_same_place(string entry)
    {
        // The picker, the empty state's button and a dropped file must land in
        // the same tab logic. Three copies of "new tab or this one?" is how
        // they drift.
        Assert.Contains("OpenPickedFile(", Section(MainPageCode(), entry, 1800), StringComparison.Ordinal);
    }

    [Fact]
    public void a_dropped_file_has_to_be_a_pdf()
    {
        // A folder, an image or a Word file can all be dropped here, and
        // handing any of them to PDFium is an error dialog rather than an
        // answer.
        Assert.Contains(
            "\".pdf\"",
            Section(MainPageCode(), "private async void EmptyState_Drop", 1400),
            StringComparison.Ordinal);
    }

    [Fact]
    public void the_empty_state_is_themed_rather_than_painted_white()
    {
        // It carried #8AFFFFFF and #C8FFFFFF, which is near-white text on a
        // canvas that is white in Light and Sepia. Invisible, and invisible for
        // months because a blank document always covered it.
        string block = Section(MainPageXaml(), "x:Name=\"EmptyState\"", 1800);

        Assert.DoesNotContain("#8AFFFFFF", block, StringComparison.Ordinal);
        Assert.DoesNotContain("#C8FFFFFF", block, StringComparison.Ordinal);
        Assert.Contains("ThemeResource TextFillColor", block, StringComparison.Ordinal);
    }

    // ---------------- The page fills the window ----------------

    [Fact]
    public void the_tab_content_is_stretched_to_the_bottom_of_the_window()
    {
        // WinUI's own TabView style sets VerticalAlignment to Top, so without a
        // local value the control takes only the height it needs: its tab strip
        // plus whatever the page inside asks for. Measured in a 680-tall window,
        // the TabView came out 523 and left a 123-tall band of the Shell's own
        // colour along the bottom.
        //
        // It went unseen until the empty state existed, because a page of
        // documents always wanted more height than the window had. Nothing about
        // an open document fixes it: a single short page does it too.
        string block = Section(MainWindowXaml(), "<TabView x:Name=\"Tabs\"", 400);

        Assert.Contains("VerticalAlignment=\"Stretch\"", block, StringComparison.Ordinal);
    }

    // ---------------- Ctrl+Q ----------------

    [Fact]
    public void quit_is_a_window_level_accelerator()
    {
        // On the window's root grid, so it fires wherever focus is. The page's
        // key handler ignores everything while a text field has focus, so a
        // case in that switch would die in the find box.
        string xaml = MainWindowXaml();
        int at = xaml.IndexOf("<Grid.KeyboardAccelerators>", StringComparison.Ordinal);
        Assert.True(at >= 0, "the window declares no accelerators, so Ctrl+Q cannot be one");

        string block = xaml[at..Math.Min(xaml.Length, at + 400)];

        Assert.Contains("Modifiers=\"Control\"", block, StringComparison.Ordinal);
        Assert.Contains("Key=\"Q\"", block, StringComparison.Ordinal);
        Assert.Contains("Invoked=\"Quit_Invoked\"", block, StringComparison.Ordinal);
    }

    [Fact]
    public void the_quit_accelerator_does_not_advertise_itself_over_the_window()
    {
        // WinUI floats a tooltip naming the chord over whatever element owns an
        // accelerator. This one is owned by the whole window, so it parked
        // "Ctrl+Q" over the app at launch and stayed. MainPage's RootGrid
        // already carried this attribute for the identical reason; adding an
        // accelerator one layer up brought the bug back.
        Assert.Contains(
            "KeyboardAcceleratorPlacementMode=\"Hidden\"",
            Section(MainWindowXaml(), "<Grid x:Name=\"Shell\"", 120),
            StringComparison.Ordinal);
    }

    [Fact]
    public void quit_is_not_implemented_as_a_canvas_shortcut()
    {
        // The failure this is guarding: someone "simplifying" it into the page
        // key handler, where it would stop working the moment you had typed in
        // the find box.
        string handler = MethodBody(MainPageCode(), "private void RootGrid_KeyDown");

        Assert.DoesNotContain("VirtualKey.Q", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void quitting_goes_through_the_same_close_every_other_route_uses()
    {
        // Close() raises OnClosing, which is what prompts about unsaved work.
        // Exiting the process directly would lose it silently.
        string body = Section(MainWindowCode(), "private void Quit_Invoked", 600);

        Assert.Contains("Close()", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_exit_menu_item_advertises_the_chord_without_declaring_it()
    {
        // A MenuFlyoutItem's accelerator is dead until its flyout has been
        // opened once. The text is what prints "Ctrl+Q" beside the entry; the
        // accelerator on the window is what runs it.
        string item = Section(MainPageXaml(), "Text=\"Exit\"", 300);

        Assert.Contains("KeyboardAcceleratorTextOverride=\"Ctrl+Q\"", item, StringComparison.Ordinal);
        Assert.DoesNotContain("<KeyboardAccelerator ", item, StringComparison.Ordinal);
    }
}
