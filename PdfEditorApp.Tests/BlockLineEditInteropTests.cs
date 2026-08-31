using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Editing a line of a document's OWN text, end to end, through the same two
/// calls the app makes: read the page's lines, then retype one of them.
///
/// ⚠️ THE LINE IS IDENTIFIED THE WAY THE APP IDENTIFIES IT. The object range
/// and the text come out of <see cref="LineReader"/>, exactly as the view model
/// gets them, and go straight back into the write. That is the half a Rust test
/// cannot cover: the core agreeing with itself proves nothing about whether the
/// triple the app is holding still names the same line by the time it writes.
///
/// The fixture is deliberately the awkward kind. Its headings are drawn one
/// glyph per operator, which is how real producers draw body text and is
/// exactly what the older object-replacing writer cannot take.
/// </summary>
public class BlockLineEditInteropTests
{
    private const string Lib = "render_core";
    private const int OkPdfium = 0;
    private const int StaleAnchor = 7;

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern ulong open_document(string path);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int close_document(ulong docHandle);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int save_document(ulong docHandle, string path);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern ByteBuffer get_page_lines(ulong docHandle, int pageIndex);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void free_byte_buffer(ByteBuffer buffer);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int set_block_line_text(
        ulong docHandle, int pageIndex, uint firstObject, uint lastObject,
        byte[] expectedUtf8, nuint expectedLen, byte[] newTextUtf8, nuint newTextLen);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByteBuffer
    {
        public IntPtr Data;
        public nuint Len;
        public int Status;
    }

    private static string Fixture(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, name);
        Assert.True(File.Exists(path), $"{name} was not copied next to the tests");
        return path;
    }

    /// <summary>The page's lines, read exactly as the view model reads them.</summary>
    private static LineSnapshot[] LinesOf(ulong handle, int page)
    {
        var buffer = get_page_lines(handle, page);
        try
        {
            Assert.Equal(OkPdfium, buffer.Status);
            byte[] bytes = new byte[(int)buffer.Len];
            Marshal.Copy(buffer.Data, bytes, 0, bytes.Length);
            return LineReader.Parse(bytes).ToArray();
        }
        finally
        {
            free_byte_buffer(buffer);
        }
    }

    /// <summary>The heading drawn one glyph at a time, whichever line it is.</summary>
    private static LineSnapshot Heading(ulong handle) =>
        Assert.Single(LinesOf(handle, 0).Where(l => l.Text.Trim() == "Kerning arial ordinary"));

    private static int Retype(ulong handle, LineSnapshot line, string newText)
    {
        byte[] want = Encoding.UTF8.GetBytes(line.Text);
        byte[] text = Encoding.UTF8.GetBytes(newText);
        return set_block_line_text(handle, 0,
            (uint)line.FirstObject, (uint)line.LastObject,
            want, (nuint)want.Length, text, (nuint)text.Length);
    }

    [Fact]
    public void a_line_the_app_read_can_be_shortened_by_the_block_writer()
    {
        ulong handle = open_document(Fixture("sample_font_cases.pdf"));
        Assert.NotEqual(0ul, handle);
        try
        {
            var line = Heading(handle);
            Assert.Equal(OkPdfium, Retype(handle, line, "Kerning arial ordinar"));

            Assert.Contains(LinesOf(handle, 0), l => l.Text.Trim() == "Kerning arial ordinar");
        }
        finally
        {
            close_document(handle);
        }
    }

    [Fact]
    public void the_same_line_can_be_lengthened()
    {
        ulong handle = open_document(Fixture("sample_font_cases.pdf"));
        Assert.NotEqual(0ul, handle);
        try
        {
            var line = Heading(handle);
            Assert.Equal(OkPdfium, Retype(handle, line, "Kerning arial ordinaryy"));

            Assert.Contains(LinesOf(handle, 0), l => l.Text.Trim() == "Kerning arial ordinaryy");
        }
        finally
        {
            close_document(handle);
        }
    }

    /// <summary>
    /// ⚠️ THE ONE THAT MATTERS FOR A DOCUMENT SOMEBODY KEEPS. An edit that only
    /// lives in memory is not an edit; this saves to a real file, closes the
    /// document, opens the saved file fresh, and asks it what it says.
    /// </summary>
    [Fact]
    public void the_edit_survives_saving_closing_and_opening_again()
    {
        string saved = Path.Combine(Path.GetTempPath(),
            $"block_line_edit_{Guid.NewGuid():N}.pdf");

        ulong handle = open_document(Fixture("sample_font_cases.pdf"));
        Assert.NotEqual(0ul, handle);
        try
        {
            Assert.Equal(OkPdfium, Retype(handle, Heading(handle), "Kerning arial ordinar"));
            Assert.Equal(OkPdfium, save_document(handle, saved));
        }
        finally
        {
            close_document(handle);
        }

        try
        {
            Assert.True(File.Exists(saved), "the document did not reach the disk");

            ulong reopened = open_document(saved);
            Assert.NotEqual(0ul, reopened);
            try
            {
                Assert.Contains(LinesOf(reopened, 0),
                    l => l.Text.Trim() == "Kerning arial ordinar");
                Assert.DoesNotContain(LinesOf(reopened, 0),
                    l => l.Text.Trim() == "Kerning arial ordinary");
            }
            finally
            {
                close_document(reopened);
            }
        }
        finally
        {
            if (File.Exists(saved)) { File.Delete(saved); }
        }
    }

    /// <summary>
    /// A selection that has gone stale refuses, and the page still says what it
    /// said. Never a wrong write.
    /// </summary>
    [Fact]
    public void a_stale_selection_refuses_and_leaves_the_page_saying_what_it_said()
    {
        ulong handle = open_document(Fixture("sample_font_cases.pdf"));
        Assert.NotEqual(0ul, handle);
        try
        {
            var line = Heading(handle);
            string[] before = LinesOf(handle, 0).Select(l => l.Text).ToArray();

            byte[] wrong = Encoding.UTF8.GetBytes("Kerning arial extraordinary");
            byte[] text = Encoding.UTF8.GetBytes("Kerning arial ordinar");
            Assert.Equal(StaleAnchor, set_block_line_text(handle, 0,
                (uint)line.FirstObject, (uint)line.LastObject,
                wrong, (nuint)wrong.Length, text, (nuint)text.Length));

            Assert.Equal(before, LinesOf(handle, 0).Select(l => l.Text).ToArray());
        }
        finally
        {
            close_document(handle);
        }
    }
}
