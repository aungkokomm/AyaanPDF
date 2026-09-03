using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Turning a point on the page into a place in the page's own text.
///
/// ⚠️ THIS IS WHAT IN-PLACE EDITING STANDS ON. A settled product requirement:
/// editing happens where the text already is, so a caret has to land at a real
/// position among real glyphs. If a click cannot be resolved to the right
/// character and the right side of it, no amount of drawing will make the
/// experience feel native to the page.
///
/// Measured against a real page rather than invented rectangles, because the
/// thing being tested is whether the geometry the reader supplies is good
/// enough to aim with.
/// </summary>
public class TextRegionHitTestTests
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

    [StructLayout(LayoutKind.Sequential)]
    private struct ByteBuffer { public IntPtr Data; public nuint Len; public int Status; }

    private static (byte[] Bytes, LineSnapshot[] Lines) Read(string name, int page)
    {
        string path = Path.Combine(AppContext.BaseDirectory, name);
        Assert.True(File.Exists(path), $"{name} was not copied next to the tests");

        ulong handle = open_document(path);
        Assert.NotEqual(0ul, handle);
        try
        {
            var buffer = get_page_lines(handle, page);
            try
            {
                if (buffer.Status != 0 || buffer.Data == IntPtr.Zero)
                {
                    return (File.ReadAllBytes(path), Array.Empty<LineSnapshot>());
                }
                byte[] bytes = new byte[(int)buffer.Len];
                Marshal.Copy(buffer.Data, bytes, 0, bytes.Length);
                return (File.ReadAllBytes(path), LineReader.Parse(bytes).ToArray());
            }
            finally { free_byte_buffer(buffer); }
        }
        finally { close_document(handle); }
    }

    private static (TextRegion[] Regions, TextLine Line) APage()
    {
        var (bytes, lines) = Read("sample_font_cases.pdf", 0);
        var regions = TextRegionReader.Build(bytes, 0, lines).ToArray();

        // A line with enough glyphs to aim between.
        var line = regions.SelectMany(r => r.Lines)
            .Where(l => l.CanOffer)
            .FirstOrDefault(l => l.Characters.Count(c => !char.IsWhiteSpace(c.Text[0])) >= 4);

        Assert.NotNull(line);
        return (regions, line!);
    }

    // ---------------- finding the line ----------------

    /// <summary>
    /// A point on a line's own glyphs finds that line, for every offerable line
    /// on the page. Aimed at the middle of the line's box, which is where a
    /// reader clicking on text actually aims.
    /// </summary>
    [Fact]
    public void a_point_on_the_text_finds_the_line_it_is_on()
    {
        var (regions, _) = APage();
        var offerable = regions.SelectMany(r => r.Lines).Where(l => l.CanOffer).ToList();
        Assert.NotEmpty(offerable);

        foreach (var line in offerable)
        {
            double y = (line.Top + line.Bottom) / 2;
            double x = (line.Left + line.Right) / 2;

            var found = TextRegionHitTest.LineAt(regions, x, y);

            Assert.NotNull(found);
            Assert.Equal(line.Text, found!.Text);
        }
    }

    /// <summary>
    /// ⚠️ AND A POINT WELL AWAY FROM THE TEXT FINDS NOTHING. A hit test that
    /// always answers is worse than one that refuses: it would put a caret in
    /// the margin and claim the reader meant it.
    /// </summary>
    [Fact]
    public void a_point_off_the_text_finds_nothing()
    {
        var (regions, _) = APage();

        Assert.Null(TextRegionHitTest.LineAt(regions, -0.5, -0.5));
        Assert.Null(TextRegionHitTest.LineAt(regions, 5.0, 5.0));
        Assert.Null(TextRegionHitTest.LineAt(null, 0.5, 0.5));
        Assert.Null(TextRegionHitTest.LineAt(Array.Empty<TextRegion>(), 0.5, 0.5));
    }

    /// <summary>
    /// Horizontally the line's own extent and no wider: the space beside a short
    /// line is not part of it, or a click in the margin of a ragged paragraph
    /// would land on whichever line happened to be nearest vertically.
    /// </summary>
    [Fact]
    public void the_space_beside_a_line_is_not_part_of_it()
    {
        var (regions, line) = APage();
        double y = (line.Top + line.Bottom) / 2;

        Assert.Null(TextRegionHitTest.LineAt(regions, line.Left - 0.05, y));
        Assert.Null(TextRegionHitTest.LineAt(regions, line.Right + 0.05, y));
    }

    /// <summary>
    /// A refused line is invisible to a caller placing a caret, and visible to
    /// one explaining the refusal.
    /// </summary>
    [Fact]
    public void a_refused_line_can_be_hidden_or_shown_on_request()
    {
        var (bytes, lines) = Read("sample_lines.pdf", 0);
        var regions = TextRegionReader.Build(bytes, 0, lines).ToArray();

        var refused = regions.SelectMany(r => r.Lines)
            .FirstOrDefault(l => !l.CanOffer && l.Right > l.Left && l.Bottom > l.Top);
        if (refused is null) { return; }   // the fixture would have to change

        double x = (refused.Left + refused.Right) / 2;
        double y = (refused.Top + refused.Bottom) / 2;

        Assert.Null(TextRegionHitTest.LineAt(regions, x, y, offerableOnly: true));

        var shown = TextRegionHitTest.LineAt(regions, x, y, offerableOnly: false);
        Assert.NotNull(shown);
    }

    // ---------------- finding the character ----------------

    /// <summary>
    /// ⚠️ EVERY GLYPH IS FOUND BY AIMING AT IT. This is the assertion that says
    /// a caret can be placed on the letter the reader pointed at, rather than
    /// somewhere on the right line.
    /// </summary>
    [Fact]
    public void aiming_at_a_glyph_finds_that_glyph()
    {
        var (_, line) = APage();

        foreach (var c in line.Characters.Where(c => !char.IsWhiteSpace(c.Text[0])))
        {
            double x = (c.Left + c.Right) / 2;
            var found = TextRegionHitTest.CharacterAt(line, x);

            Assert.NotNull(found);
            Assert.Equal(c.Offset, found!.Offset);
            Assert.Equal(c.Text, found.Text);
        }
    }

    [Fact]
    public void aiming_past_the_end_of_a_line_finds_no_glyph()
    {
        var (_, line) = APage();

        Assert.Null(TextRegionHitTest.CharacterAt(line, line.Right + 0.1));
        Assert.Null(TextRegionHitTest.CharacterAt(line, line.Left - 0.1));
        Assert.Null(TextRegionHitTest.CharacterAt(null, 0.5));
    }

    // ---------------- where the caret would go ----------------

    /// <summary>
    /// ⚠️ THE LEFT HALF OF A GLYPH MEANS BEFORE IT, THE RIGHT HALF MEANS AFTER.
    /// That one rule is the difference between typing before a letter and after
    /// it, and getting it wrong makes every click land one character off.
    /// </summary>
    [Fact]
    public void each_half_of_a_glyph_puts_the_caret_on_its_own_side()
    {
        var (_, line) = APage();

        foreach (var c in line.Characters.Where(c => !char.IsWhiteSpace(c.Text[0])))
        {
            double width = c.Right - c.Left;
            if (width <= 0) { continue; }

            int before = TextRegionHitTest.CaretOffsetAt(line, c.Left + (width * 0.2));
            Assert.Equal(c.Offset, before);

            int after = TextRegionHitTest.CaretOffsetAt(line, c.Right - (width * 0.2));
            Assert.True(after > c.Offset,
                $"clicking the right half of '{c.Text}' put the caret before it");
        }
    }

    /// <summary>
    /// The two ends, which are where a reader clicks most: before everything,
    /// and after everything.
    /// </summary>
    [Fact]
    public void the_ends_of_a_line_are_reachable()
    {
        var (_, line) = APage();

        Assert.Equal(0, TextRegionHitTest.CaretOffsetAt(line, line.Left - 1.0));
        Assert.Equal(line.Text.Length, TextRegionHitTest.CaretOffsetAt(line, line.Right + 1.0));
    }

    /// <summary>
    /// The caret offset is always somewhere a caret could actually be put in
    /// this line's own text, at every point across the whole line.
    /// </summary>
    [Fact]
    public void the_caret_offset_is_always_inside_the_line()
    {
        var (_, line) = APage();

        for (int step = 0; step <= 100; step++)
        {
            double x = line.Left + ((line.Right - line.Left) * step / 100.0);
            int offset = TextRegionHitTest.CaretOffsetAt(line, x);
            Assert.InRange(offset, 0, line.Text.Length);
        }
    }

    /// <summary>
    /// ⚠️ AND IT NEVER GOES BACKWARDS ACROSS THE LINE. Sweeping left to right
    /// must give offsets that only ever increase, or the caret would jump about
    /// as the reader moved the pointer steadily along a line.
    /// </summary>
    [Fact]
    public void sweeping_across_a_line_moves_the_caret_only_forwards()
    {
        var (_, line) = APage();

        int previous = -1;
        for (int step = 0; step <= 200; step++)
        {
            double x = line.Left + ((line.Right - line.Left) * step / 200.0);
            int offset = TextRegionHitTest.CaretOffsetAt(line, x);
            Assert.True(offset >= previous,
                $"the caret went backwards at x={x:F4}: {previous} then {offset}");
            previous = offset;
        }
    }

    // ---------------- where the caret is drawn ----------------

    /// <summary>
    /// ⚠️ A CLICK AND THE CARET IT PRODUCES MUST AGREE. The reader clicks a
    /// point, an offset comes back, and the caret is drawn from that offset. If
    /// those two disagree the caret appears somewhere other than where they
    /// pointed, which is the single most obvious way in-place editing can feel
    /// wrong.
    /// </summary>
    [Fact]
    public void the_caret_is_drawn_where_the_click_that_made_it_landed()
    {
        var (_, line) = APage();

        foreach (var c in line.Characters.Where(ch => !char.IsWhiteSpace(ch.Text[0])))
        {
            double width = c.Right - c.Left;
            if (width <= 0) { continue; }

            // Aim at the left half, which asks for the caret before this glyph.
            double x = c.Left + (width * 0.2);
            int offset = TextRegionHitTest.CaretOffsetAt(line, x);
            double caretX = TextRegionHitTest.CaretXFor(line, offset);

            // The caret lands on this glyph's own left edge, not somewhere in
            // the middle of it and not on its neighbour.
            Assert.True(Math.Abs(caretX - c.Left) < 0.002,
                $"clicking the left of '{c.Text}' drew the caret at {caretX:F4}, not {c.Left:F4}");
        }
    }

    [Fact]
    public void the_caret_at_the_ends_sits_at_the_ends()
    {
        var (_, line) = APage();

        Assert.Equal(line.Left, TextRegionHitTest.CaretXFor(line, 0), 4);
        Assert.Equal(line.Right, TextRegionHitTest.CaretXFor(line, line.Text.Length), 4);
        Assert.Equal(line.Right, TextRegionHitTest.CaretXFor(line, 9999), 4);
        Assert.Equal(0, TextRegionHitTest.CaretXFor(null, 3));
    }

    /// <summary>
    /// Walking the caret through the line moves it steadily rightwards, never
    /// back, which is what makes an arrow key feel like an arrow key.
    /// </summary>
    [Fact]
    public void stepping_the_caret_through_the_line_only_moves_it_forwards()
    {
        var (_, line) = APage();

        double previous = -1;
        for (int offset = 0; offset <= line.Text.Length; offset++)
        {
            double x = TextRegionHitTest.CaretXFor(line, offset);
            Assert.True(x >= previous - 0.0001,
                $"the caret went backwards at offset {offset}: {previous:F4} then {x:F4}");
            previous = x;
        }
    }

    // ---------------- the whole answer ----------------

    /// <summary>
    /// What a click gets: the line, the glyph if it landed on one, and where a
    /// caret would go. The offset stays meaningful even when the point fell in
    /// the gap between two glyphs, which is why the two are separate.
    /// </summary>
    [Fact]
    public void a_click_resolves_to_a_line_a_glyph_and_an_offset()
    {
        var (regions, line) = APage();

        var c = line.Characters.First(ch => !char.IsWhiteSpace(ch.Text[0]));
        double x = (c.Left + c.Right) / 2;
        double y = (line.Top + line.Bottom) / 2;

        var hit = TextRegionHitTest.Resolve(regions, x, y);

        Assert.NotNull(hit);
        Assert.Equal(line.Text, hit!.Line.Text);
        Assert.NotNull(hit.Character);
        Assert.Equal(c.Offset, hit.Character!.Offset);
        Assert.InRange(hit.CaretOffset, 0, line.Text.Length);

        // ⚠️ AND IT CARRIES THE WRITER'S HANDLE THROUGH UNTOUCHED. The object
        // range is what an edit is addressed by, and a hit test that lost it
        // would leave a later phase inventing an anchor.
        Assert.True(hit.Line.LastObject >= hit.Line.FirstObject);
    }

    [Fact]
    public void a_click_on_nothing_resolves_to_nothing()
    {
        var (regions, _) = APage();
        Assert.Null(TextRegionHitTest.Resolve(regions, -1.0, -1.0));
    }
}
