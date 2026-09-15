using System;
using System.IO;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// What reaches the notice bar. `Status` was written in 148 places and shown in
/// none; these are real messages the app writes, taken from the source.
/// </summary>
public class StatusNoticeTests
{
    private static string Source(params string[] relative)
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

    [Theory]
    [InlineData("Could not add that stamp.")]
    [InlineData("That link could not be removed.")]
    [InlineData("A line cannot be made empty. Delete is a different edit.")]
    [InlineData("That text has changed since, so this step could not be undone.")]
    [InlineData("Still saving the last change.")]
    [InlineData("That mark isn't in a group.")]
    [InlineData("report.pdf is password protected")]
    [InlineData("Failed to open report.pdf")]
    [InlineData("Windows could not open that link.")]
    [InlineData("This word is set in more than one font or size.")]
    [InlineData("This link belongs to the document's own navigation and is not removed here.")]
    [InlineData("This link has no address.")]
    [InlineData("Ayaan will not open a \"ftp\" link. Only web and mail addresses can be opened.")]
    [InlineData("This link's address is not one Ayaan can read.")]
    public void a_refusal_is_said_as_a_warning(string message)
    {
        Assert.Equal(NoticeKind.Warning, StatusNotices.Classify(message));
    }

    [Theory]
    [InlineData("Click where My signature should go.")]
    [InlineData("Drag over the area you want to make clickable.")]
    [InlineData("Draw your signature with the pen, select it, then save it.")]
    [InlineData("Choose a stamp first.")]
    [InlineData("Select two or more marks (shift-click) before grouping.")]
    [InlineData("Nothing selected to group.")]
    [InlineData("Full screen. Press Esc or F11 to leave.")]
    [InlineData("Recovered report.pdf")]
    public void an_instruction_is_said(string message)
    {
        Assert.Equal(NoticeKind.Information, StatusNotices.Classify(message));
    }

    /// <summary>
    /// ⚠️ A CONFIRMATION IS NOT A NOTICE. The page already shows a retyped line
    /// or a moved block, and a banner after every commit is one more thing to
    /// dismiss while typing Burmese or Hindi.
    /// </summary>
    [Theory]
    [InlineData("Line retyped.")]
    [InlineData("Text moved.")]
    [InlineData("Saved report.pdf.")]
    [InlineData("Grouped 3 marks.")]
    [InlineData("Changed “a” to “b”.")]
    [InlineData("Resumed at page 4.")]
    [InlineData("Opening document...")]
    [InlineData("")]
    [InlineData(null)]
    public void a_confirmation_is_kept_quiet(string? message)
    {
        Assert.Equal(NoticeKind.None, StatusNotices.Classify(message));
    }

    /// <summary>
    /// ⚠️ A SELECTION'S DESCRIPTION QUOTES THE DOCUMENT, and the document can
    /// say anything, "cannot" included.
    /// </summary>
    [Theory]
    [InlineData("Word: “cannot”  •  click again where you want to type")]
    [InlineData("Line: “धर्मसंकट”  •  This script is not read back faithfully enough to retype.")]
    [InlineData("“You could not know”  •  Calibri 12pt")]
    public void what_was_clicked_is_not_a_notice(string message)
    {
        Assert.Equal(NoticeKind.None, StatusNotices.Classify(message));
    }

    [Fact]
    public void every_reason_a_line_is_refused_is_said_as_a_warning()
    {
        foreach (LineRefusal refusal in Enum.GetValues<LineRefusal>())
        {
            if (refusal == LineRefusal.None) { continue; }
            var line = new LineSnapshot(
                FirstObject: 0, LastObject: 0, PrefixChars: 0, Words: 1,
                Left: 0.1, Top: 0.1, Right: 0.2, Bottom: 0.12, Baseline: 0.12,
                FontSizePts: 11, ColorRgb: 0, Refusal: refusal, Text: "x", FontName: "F");

            Assert.True(StatusNotices.Classify(line.RefusalReason) == NoticeKind.Warning,
                $"{refusal}: \"{line.RefusalReason}\" would be kept quiet");
        }
    }

    [Fact]
    public void a_refusal_the_label_already_explains_is_not_said_twice()
    {
        var notices = new StatusNotices();
        notices.Status("This text could not be moved.");
        notices.Label("This text could not be moved.");
        Assert.Null(notices.Flush());

        // And in the other order.
        notices.Label("That text could not be deleted.");
        notices.Status("Could not add that stamp.");
        Assert.Null(notices.Flush());
    }

    [Fact]
    public void the_next_turn_starts_clean()
    {
        var notices = new StatusNotices();
        notices.Label("That text could not be deleted.");
        Assert.Null(notices.Flush());

        notices.Status("Could not add that stamp.");
        Assert.Equal(("Could not add that stamp.", NoticeKind.Warning), notices.Flush());
        Assert.Null(notices.Flush());
    }

    [Fact]
    public void the_last_message_of_a_turn_is_the_one_said()
    {
        var notices = new StatusNotices();
        notices.Status("Choose a stamp first.");
        notices.Status("Could not add that stamp.");
        Assert.Equal(("Could not add that stamp.", NoticeKind.Warning), notices.Flush());
    }

    [Fact]
    public void a_label_taken_away_hides_nothing()
    {
        var notices = new StatusNotices();
        notices.Label(null);
        notices.Status("Could not add that stamp.");
        Assert.Equal(("Could not add that stamp.", NoticeKind.Warning), notices.Flush());
    }

    // ---------------- where it is wired ----------------

    [Fact]
    public void the_notice_bar_is_in_the_canvas_column_and_bound_to_the_view_model()
    {
        string xaml = Source("PdfEditorApp", "MainPage.xaml");

        int at = xaml.IndexOf("x:Name=\"NoticeBar\"", StringComparison.Ordinal);
        Assert.True(at > 0, "there is no notice bar");
        int tag = xaml.LastIndexOf("<InfoBar", at, StringComparison.Ordinal);
        string opening = xaml[tag..xaml.IndexOf("/>", tag, StringComparison.Ordinal)];

        Assert.Contains("Grid.Column=\"2\"", opening, StringComparison.Ordinal);
        Assert.Contains("ViewModel.IsNoticeOpen", opening, StringComparison.Ordinal);
        Assert.Contains("ViewModel.NoticeMessage", opening, StringComparison.Ordinal);
        Assert.Contains("ViewModel.NoticeIsWarning", opening, StringComparison.Ordinal);
    }

    [Fact]
    public void the_status_and_the_label_both_feed_the_notices()
    {
        string vm = Source("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");

        int hook = vm.IndexOf("partial void OnStatusChanged(string value)", StringComparison.Ordinal);
        Assert.True(hook > 0, "writing the status shows nothing");
        Assert.Contains("_notices.Status(value);", vm[hook..(hook + 300)], StringComparison.Ordinal);

        int label = vm.IndexOf("public void ShowUnitNotice(string? message)", StringComparison.Ordinal);
        Assert.True(label > 0, "the label is gone");
        Assert.Contains("_notices.Label(message);", vm[label..(label + 400)], StringComparison.Ordinal);
    }
}
