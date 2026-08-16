using System;
using System.Linq;
using System.Runtime.InteropServices;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Styles read out of a REAL document through the REAL core.
///
/// This is the half that matters. <see cref="StyleBookmarkerTests"/> proves the
/// matcher does the right thing with runs it is handed, and
/// <see cref="StyleSurveyTests"/> proves the survey groups them; neither says
/// the bytes crossing the FFI are the bytes the parser expects. A parser that
/// agrees with a hand-written array and disagrees with render_core would be
/// worse than none, and nothing short of a round trip catches that.
/// </summary>
public class StyledRunInteropTests
{
    private const string Lib = "render_core";
    private const int OkPdfium = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct ByteBuffer
    {
        public IntPtr Data;
        public nuint Len;
        public int Status;
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern ulong open_document(string path);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void close_document(ulong handle);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern ByteBuffer get_page_text_runs(ulong handle, int pageIndex);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void free_byte_buffer(ByteBuffer buffer);

    /// <summary>Reads a page the way the app does, and frees the buffer.</summary>
    private static StyledRunPage PageOf(ulong handle, int pageIndex)
    {
        var buffer = get_page_text_runs(handle, pageIndex);
        try
        {
            Assert.Equal(OkPdfium, buffer.Status);
            if (buffer.Data == IntPtr.Zero || buffer.Len == 0)
            {
                return StyledRunPage.Empty(pageIndex);
            }

            byte[] bytes = new byte[(int)buffer.Len];
            Marshal.Copy(buffer.Data, bytes, 0, bytes.Length);
            return StyledRunReader.Parse(bytes, pageIndex);
        }
        finally
        {
            free_byte_buffer(buffer);
        }
    }

    private static IReadOnlyList<StyledRun> RunsOn(ulong handle, int pageIndex) =>
        PageOf(handle, pageIndex).Runs;

    private static ulong OpenStyled()
    {
        ulong handle = open_document("sample_styled.pdf");
        Assert.True(handle != 0, "the styled fixture did not open");
        return handle;
    }

    [Fact]
    public void the_parser_reads_exactly_what_the_core_wrote()
    {
        // Every field, on a document whose styles are known because the test
        // suite that generated it chose them.
        ulong handle = OpenStyled();
        try
        {
            var runs = RunsOn(handle, 0);
            Assert.NotEmpty(runs);

            var heading = runs.Single(r => r.Text.Contains("Chapter One", StringComparison.Ordinal));

            Assert.Equal(18, heading.Style.SizePoints, precision: 1);
            Assert.Contains("Bold", heading.Style.FontName, StringComparison.Ordinal);
            Assert.Equal(0xFF0000, heading.Style.ColorRgb);
            Assert.Equal(heading.Text.Length, heading.CharCount);
        }
        finally
        {
            close_document(handle);
        }
    }

    [Fact]
    public void a_real_page_surveys_into_the_styles_it_is_set_in()
    {
        // What the dialog shows when the scan finishes, computed from a real
        // document rather than from an array written to suit the assertion.
        ulong handle = OpenStyled();
        try
        {
            var survey = StyleSurvey.Survey(RunsOn(handle, 0), StyleMatch.Default);

            Assert.NotEmpty(survey);

            // Biggest first, which is the order that makes the list useful.
            Assert.Equal(18, survey[0].Style.SizePoints, precision: 1);
            Assert.Contains("Chapter One", survey[0].Sample, StringComparison.Ordinal);
            Assert.All(survey.Zip(survey.Skip(1)),
                       pair => Assert.True(pair.First.Style.SizePoints >= pair.Second.Style.SizePoints));
        }
        finally
        {
            close_document(handle);
        }
    }

    [Fact]
    public void the_whole_pipeline_turns_a_real_page_into_a_real_bookmark()
    {
        // Core to bookmark, with nothing hand-written in between: this is the
        // feature, and every other test here is a piece of it.
        ulong handle = OpenStyled();
        try
        {
            var runs = RunsOn(handle, 0);
            var survey = StyleSurvey.Survey(runs, StyleMatch.Default);

            var found = StyleBookmarker.Detect(
                runs, [survey[0].Style], StyleMatch.Default, allowMultiline: false);

            var bookmark = Assert.Single(found);
            Assert.Equal("Chapter One", bookmark.Title);
            Assert.Equal(0, bookmark.PageIndex);
            Assert.Equal(1, bookmark.Level);
        }
        finally
        {
            close_document(handle);
        }
    }

    [Fact]
    public void the_body_text_is_not_mistaken_for_a_heading()
    {
        // The other half of the same claim. A style that matched everything
        // would produce the test above's result too.
        ulong handle = OpenStyled();
        try
        {
            var runs = RunsOn(handle, 0);
            var heading = runs.Single(r => r.Text.Contains("Chapter One", StringComparison.Ordinal));

            var found = StyleBookmarker.Detect(
                runs, [heading.Style], StyleMatch.Default, allowMultiline: false);

            Assert.DoesNotContain(found, h => h.Title.Contains("Ordinary body", StringComparison.Ordinal));
        }
        finally
        {
            close_document(handle);
        }
    }

    [Fact]
    public void a_real_page_reports_more_characters_than_its_runs_kept()
    {
        // The number the diagnosis rests on. It has to come from the page
        // itself, not from adding up the runs: the whole point is that it
        // counts characters no run kept, which is how "this page is a scan" is
        // told from "this page's text maps to nothing".
        ulong handle = OpenStyled();
        try
        {
            var page = PageOf(handle, 0);

            Assert.True(page.CharCount > 0, "a page with text reported no characters");
            Assert.True(
                page.CharCount >= page.Runs.Sum(r => r.CharCount),
                $"{page.CharCount} characters on the page but more than that in runs");
        }
        finally
        {
            close_document(handle);
        }
    }

    [Fact]
    public void a_page_with_no_text_layer_reports_no_characters()
    {
        // The signal that makes a scan recognisable. If an empty page claimed
        // characters, every scanned book would be diagnosed as unreadable text
        // and told the wrong thing.
        ulong handle = open_document("blank.pdf");
        Assert.True(handle != 0, "the blank fixture did not open");
        try
        {
            var page = PageOf(handle, 0);

            Assert.Equal(0, page.CharCount);
            Assert.Empty(page.Runs);
        }
        finally
        {
            close_document(handle);
        }
    }

    [Fact]
    public void a_selected_character_finds_the_style_it_sits_in()
    {
        // How "tick the style of the text I selected" is answered, against real
        // character indices rather than invented ones.
        ulong handle = OpenStyled();
        try
        {
            var runs = RunsOn(handle, 0);
            var heading = runs.Single(r => r.Text.Contains("Chapter One", StringComparison.Ordinal));

            var found = StyleSurvey.StyleAt(runs, 0, heading.CharStart + 2);

            Assert.Equal(heading.Style, found);
        }
        finally
        {
            close_document(handle);
        }
    }
}
