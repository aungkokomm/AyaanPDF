using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The read side: where the document's own text is, so Edit mode can show it.
///
/// ⚠️ THE BRIDGE IS WHAT THESE ARE ABOUT. The geometry comes from PdfPig and
/// the object range comes from the core, and nothing but this joins them. It
/// cannot be joined by index: measured, a letter is NOT an object, because
/// ordinary producers put a whole line in one object (1111 letters against 58
/// objects on one page). It cannot be joined by reading order either: the two
/// disagree about the order of a table's cells. So it is joined by GEOMETRY,
/// per line, and that placed 98.0% of Latin characters over the corpus.
/// </summary>
public class TextRegionTests
{
    private const string Lib = "render_core";

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern ulong open_document(string path);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int close_document(ulong docHandle);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern ByteBuffer get_page_lines(ulong docHandle, int pageIndex);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void free_byte_buffer(ByteBuffer buffer);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern ByteBuffer snapshot_document(ulong docHandle);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int set_line_text(
        ulong docHandle, int pageIndex, uint firstObject, uint lastObject, uint prefixChars,
        byte[] newTextUtf8, nuint newTextLen, byte[]? fontPathUtf8, nuint fontPathLen);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByteBuffer { public IntPtr Data; public nuint Len; public int Status; }

    private static string Fixture(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, name);
        Assert.True(File.Exists(path), $"{name} was not copied next to the tests");
        return path;
    }

    private static LineSnapshot[] LinesOf(ulong handle, int page)
    {
        var buffer = get_page_lines(handle, page);
        try
        {
            if (buffer.Status != 0 || buffer.Data == IntPtr.Zero) { return Array.Empty<LineSnapshot>(); }
            byte[] bytes = new byte[(int)buffer.Len];
            Marshal.Copy(buffer.Data, bytes, 0, bytes.Length);
            return LineReader.Parse(bytes).ToArray();
        }
        finally { free_byte_buffer(buffer); }
    }

    private static (byte[] Bytes, LineSnapshot[] Lines) Read(string name, int page)
    {
        string path = Fixture(name);
        ulong handle = open_document(path);
        Assert.NotEqual(0ul, handle);
        try { return (File.ReadAllBytes(path), LinesOf(handle, page)); }
        finally { close_document(handle); }
    }

    // ---------------- the alignment rule, on its own ----------------

    /// <summary>
    /// ⚠️ WHITESPACE IS THE ONE THING THE TWO READERS MAY DISAGREE ABOUT.
    /// Measured on a real book: the word gaps decode as U+0009 through the
    /// font's own map while PDFium hands the app U+0020. Refusing that
    /// disagreement refused every line on the page that had a space in it.
    /// </summary>
    [Fact]
    public void a_tab_may_stand_for_the_space_the_reader_was_shown()
    {
        var map = TextRegionReader.Align("About\tthe\tBook", "About the Book");

        Assert.NotNull(map);
        // Every character keeps ITS OWN offset in the line, which is what an
        // edit is addressed by.
        Assert.Equal(0, map![0]);
        Assert.Equal(5, map[5]);
        Assert.Equal(13, map[13]);
    }

    /// <summary>The half that matters more: a real difference is still a
    /// refusal, because a caret placed on the wrong glyph edits the wrong
    /// text.</summary>
    [Theory]
    [InlineData("About\tthe\tBook", "About the Cook")]
    [InlineData("Title\tPage", "Title Rage")]
    [InlineData("Cover", "Cever")]
    [InlineData("About the Book", "About the Books")]
    [InlineData("Aboutthe Book", "About the Book x")]
    public void anything_other_than_whitespace_must_match(string drawn, string line)
    {
        Assert.Null(TextRegionReader.Align(drawn, line));
    }

    [Fact]
    public void a_line_with_no_whitespace_at_all_is_unaffected()
    {
        var map = TextRegionReader.Align("Introduction", "Introduction");
        Assert.NotNull(map);
        Assert.Equal(Enumerable.Range(0, 12), map!);
    }

    /// <summary>
    /// ⚠️ A CURLY APOSTROPHE IS NOT A SHAPED SCRIPT. The first rule here was
    /// "anything above U+0300", which is the piece writer's refusal threshold
    /// and not a statement about scripts. Measured over the first 40 pages of
    /// the Harari book it refused 155 of 1279 lines, 12.1%, every one of them
    /// ordinary English, none of that book being a complex script at all.
    /// </summary>
    [Theory]
    [InlineData("Don’t panic")]                          // right single quote
    [InlineData("“Quoted”")]                        // curly double quotes
    [InlineData("A dash — and an ellipsis…")]       // em dash, ellipsis
    [InlineData("› Part I, the platform")]               // single angle quote
    [InlineData("Привет")]      // Cyrillic maps 1:1
    [InlineData("日本語")]                        // CJK maps 1:1
    public void ordinary_typography_is_not_a_shaped_script(string text)
    {
        Assert.False(TextRegionReader.IsShaped(text));
    }

    /// <summary>And the half that must keep working: real complex scripts are
    /// still recognised, so they stay an explicit non-goal rather than being
    /// offered and then mis-edited.</summary>
    [Theory]
    [InlineData("ကောင်း")]      // Myanmar
    [InlineData("नमस्ते")]      // Devanagari
    [InlineData("مرحبا")]            // Arabic
    [InlineData("Mixed नमस्ते text")]
    public void complex_scripts_are_still_recognised(string text)
    {
        Assert.True(TextRegionReader.IsShaped(text));
    }

    // ---------------- a real page ----------------

    /// <summary>
    /// The fixture whose headings are drawn one glyph per operator, which is
    /// the shape that defeats index-based bridging.
    /// </summary>
    [Fact]
    public void ordinary_text_is_placed_character_by_character()
    {
        var (bytes, lines) = Read("sample_font_cases.pdf", 0);
        Assert.NotEmpty(lines);

        var regions = TextRegionReader.Build(bytes, 0, lines);
        Assert.NotEmpty(regions);

        var offered = regions.Where(r => r.CanOffer).ToList();
        Assert.NotEmpty(offered);

        foreach (var line in offered.SelectMany(r => r.Lines).Where(l => l.CanOffer))
        {
            var chars = line.Characters.ToList();
            Assert.NotEmpty(chars);

            // Every character points inside its own line's text, and says the
            // same thing the line says at that offset.
            foreach (var c in chars)
            {
                Assert.InRange(c.Offset, 0, line.Text.Length - 1);
                if (!char.IsWhiteSpace(c.Text[0]))
                {
                    Assert.Equal(line.Text[c.Offset], c.Text[0]);
                }
            }

            // And they are in reading order, which is what a caret walks.
            for (int i = 1; i < chars.Count; i++)
            {
                Assert.True(chars[i].Offset >= chars[i - 1].Offset,
                    "characters came back out of order");
            }

            // The object range is carried through untouched: it is the writer's
            // handle on this line and nothing here may invent it.
            Assert.True(line.LastObject >= line.FirstObject);
        }
    }

    /// <summary>
    /// ⚠️ A CARET MUST BE PLACEABLE FROM THIS MODEL ALONE, at the real position
    /// of the real glyph, because editing has to happen where the text already
    /// is. The overlay is the LOCATION of the page's own text, never a separate
    /// box to type into, so every character has to carry its own drawn bounds:
    /// a line box would only ever let a caret sit at the start or the end.
    /// </summary>
    [Fact]
    public void every_character_carries_the_geometry_a_caret_needs()
    {
        var (bytes, lines) = Read("sample_font_cases.pdf", 0);
        var offered = TextRegionReader.Build(bytes, 0, lines)
            .SelectMany(r => r.Lines).Where(l => l.CanOffer).ToList();
        Assert.NotEmpty(offered);

        foreach (var line in offered)
        {
            var chars = line.Characters.Where(c => !char.IsWhiteSpace(c.Text[0])).ToList();
            if (chars.Count < 2) { continue; }

            foreach (var c in chars)
            {
                // A real drawn glyph, not a placeholder: it has extent, and it
                // sits within the line it belongs to.
                Assert.True(c.Right > c.Left, $"'{c.Text}' has no width to put a caret beside");
                Assert.True(c.Bottom > c.Top, $"'{c.Text}' has no height");
                Assert.True(c.Left >= line.Left - 0.02 && c.Right <= line.Right + 0.02,
                    $"'{c.Text}' sits outside the line it belongs to");
                Assert.False(string.IsNullOrEmpty(c.FontName),
                    "a caret needs the font the glyph was actually drawn in");
                Assert.True(c.PointSize > 0);
            }

            // Left to right and non-overlapping enough to choose between: this
            // is what turns a click x into a caret index.
            for (int i = 1; i < chars.Count; i++)
            {
                Assert.True(chars[i].Left >= chars[i - 1].Left - 0.001,
                    "characters are not ordered across the line");
            }
        }
    }

    /// <summary>
    /// Every region drawn has a real box, and the boxes sit inside the page.
    /// </summary>
    [Fact]
    public void a_region_bounds_the_lines_it_holds()
    {
        var (bytes, lines) = Read("sample_font_cases.pdf", 0);
        var regions = TextRegionReader.Build(bytes, 0, lines);

        foreach (var region in regions.Where(r => r.CanOffer))
        {
            Assert.True(region.Right > region.Left, "a region has no width");
            Assert.True(region.Bottom > region.Top, "a region has no height");
            foreach (var line in region.Lines)
            {
                Assert.True(line.Left >= region.Left - 0.001);
                Assert.True(line.Right <= region.Right + 0.001);
            }
        }
    }

    /// <summary>
    /// ⚠️ SHAPED SCRIPTS ARE AN EXPLICIT NON-GOAL OF THIS PATH, NOT A DEFECT.
    /// Measured over the corpus: 1.2% of Myanmar characters could be placed
    /// against 98% of Latin, because the two readers disagree about both the
    /// order and the content. They are marked and not offered, and they keep
    /// whatever behaviour they already had.
    /// </summary>
    [Fact]
    public void shaped_script_is_marked_and_never_offered()
    {
        var (bytes, lines) = Read("sample_lines.pdf", 0);
        var shaped = lines.Where(l => TextRegionReader.IsShaped(l.Text)).ToList();
        if (shaped.Count == 0) { return; }   // the fixture would have to change

        var regions = TextRegionReader.Build(bytes, 0, lines);
        var built = regions.SelectMany(r => r.Lines).ToList();

        foreach (var line in shaped)
        {
            var found = built.FirstOrDefault(b => b.Text == line.Text);
            Assert.NotNull(found);
            Assert.Equal(TextRegionStatus.ShapedScript, found!.Status);
            Assert.False(found.CanOffer);
            Assert.Empty(found.Runs);
        }
    }

    /// <summary>
    /// ⚠️ A LINE STAYS EDITABLE AFTER IT HAS BEEN EDITED. This is the one the
    /// reader hit: the first edit worked, and clicking the same line again was
    /// refused with "this text cannot be edited in place yet".
    ///
    /// The cause, measured on the Harari book: writing a line rewrites it into
    /// a SINGLE object and leaves the objects that used to draw it behind,
    /// emptied. Those still report as letters, in the real font with real
    /// boxes, and their value is U+0000. PdfPig saw 23 letters where the core
    /// saw 12, the aligner refused the disagreement, and the line went
    /// Unmapped. PDFium does not count a glyph that spells nothing, so neither
    /// may the reader.
    /// </summary>
    /// <remarks>
    /// sample_lines.pdf and no other, because it is the only fixture here whose
    /// lines this writer will actually take. The others were tried and refused
    /// every line, which would have made this pass while testing nothing.
    /// </remarks>
    [Theory]
    [InlineData("sample_lines.pdf")]
    public void a_line_can_still_be_placed_after_it_has_been_rewritten(string fixture)
    {
        string path = Fixture(fixture);
        ulong handle = open_document(path);
        Assert.NotEqual(0ul, handle);

        try
        {
            var before = LinesOf(handle, 0);
            int rewrote = 0;

            foreach (var target in before)
            {
                // ⚠️ ONLY A LINE DRAWN IN SEVERAL OBJECTS IS THE CASE AT HAND.
                // The bug exists because the write collapses those into one and
                // abandons the rest; a line already in a single object would
                // leave this green while proving nothing.
                if (!target.CanEdit || target.Text.Length < 4) { continue; }
                if (target.LastObject <= target.FirstObject) { continue; }
                if (TextRegionReader.IsShaped(target.Text)) { continue; }

                // Same length, so nothing about reflow is in play.
                string rewritten = target.Text[..^1] + (target.Text[^1] == 'x' ? 'y' : 'x');

                byte[] text = System.Text.Encoding.UTF8.GetBytes(rewritten);
                int status = set_line_text(
                    handle, 0, (uint)target.FirstObject, (uint)target.LastObject, 0,
                    text, (nuint)text.Length, null, 0);
                if (status != 0) { continue; }   // this writer would not take it

                var after = LinesOf(handle, 0);
                var moved = after.FirstOrDefault(l => l.Text == rewritten);
                Assert.NotNull(moved);
                Assert.Equal(moved!.FirstObject, moved.LastObject);

                var line = TextRegionReader.Build(Snapshot(handle), 0, after)
                    .SelectMany(r => r.Lines)
                    .FirstOrDefault(l => l.Text == rewritten);

                Assert.NotNull(line);
                Assert.Equal(TextRegionStatus.Ok, line!.Status);
                Assert.True(line.CanOffer, "an edited line can no longer be edited again");

                // Every character still spells something a caret can sit beside.
                var chars = line.Characters.ToList();
                Assert.NotEmpty(chars);
                Assert.All(chars, c => Assert.NotEqual("\0", c.Text));

                rewrote++;
                break;
            }

            // ⚠️ AND THE TEST ACTUALLY RAN. An earlier version of this returned
            // quietly when the writer refused, so it passed while asserting
            // nothing at all.
            Assert.True(rewrote > 0, $"no line in {fixture} could be rewritten, so nothing was tested");
        }
        finally { close_document(handle); }
    }

    private static byte[] Snapshot(ulong handle)
    {
        var buffer = snapshot_document(handle);
        try
        {
            Assert.NotEqual(IntPtr.Zero, buffer.Data);
            byte[] bytes = new byte[(int)buffer.Len];
            Marshal.Copy(buffer.Data, bytes, 0, bytes.Length);
            return bytes;
        }
        finally { free_byte_buffer(buffer); }
    }

    /// <summary>
    /// A document PdfPig will not read must cost the reader nothing: the page
    /// still renders and still selects the way it did before this existed.
    /// </summary>
    [Fact]
    public void an_unreadable_document_offers_nothing_rather_than_throwing()
    {
        var (_, lines) = Read("sample_font_cases.pdf", 0);

        Assert.Empty(TextRegionReader.Build(new byte[] { 1, 2, 3 }, 0, lines));
        Assert.Empty(TextRegionReader.Build(Array.Empty<byte>(), 0, lines));
        Assert.Empty(TextRegionReader.Build(null!, 0, lines));
    }

    [Fact]
    public void a_page_the_document_does_not_have_offers_nothing()
    {
        var (bytes, lines) = Read("sample_font_cases.pdf", 0);
        Assert.Empty(TextRegionReader.Build(bytes, 9999, lines));
    }
}
