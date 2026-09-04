using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The recovery wire format, decoded away from the app, and what the app makes
/// of a page once it has been read.
///
/// A format checked only by running the app is a format nobody checks, and the
/// two ends of this one are written in different languages: the core lays the
/// bytes out and this reads them.
/// </summary>
public class RecoveredLineTests
{
    /// <summary>"မြန်မာ": six characters, eighteen UTF-8 bytes, three clusters.</summary>
    private const string Burmese = "မြန်မာ";

    private sealed class Builder
    {
        private readonly List<byte> _bytes = new();
        private int _count;

        public Builder Line(
            float pdfBaseline, float left, float top, float right, float bottom,
            float baseline, float size, string text, string font,
            params (int From, int To, float Left, float Right)[] clusters)
        {
            _count++;
            F32(pdfBaseline);
            F32(left); F32(top); F32(right); F32(bottom); F32(baseline);
            F32(size);
            Str(text);
            Str(font);
            U32((uint)clusters.Length);
            foreach (var c in clusters)
            {
                U32((uint)c.From);
                U32((uint)c.To);
                F32(c.Left);
                F32(c.Right);
            }
            return this;
        }

        public byte[] Build()
        {
            var head = new List<byte>();
            head.AddRange(BitConverter.GetBytes((uint)_count));
            head.AddRange(_bytes);
            return head.ToArray();
        }

        private void U32(uint v) => _bytes.AddRange(BitConverter.GetBytes(v));

        private void F32(float v) => _bytes.AddRange(BitConverter.GetBytes(v));

        private void Str(string s)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(s);
            U32((uint)utf8.Length);
            _bytes.AddRange(utf8);
        }
    }

    // ---------------- the wire format ----------------

    [Fact]
    public void a_line_comes_back_as_it_went_out()
    {
        byte[] bytes = new Builder()
            .Line(709.1f, 0.12f, 0.13f, 0.88f, 0.16f, 0.155f, 11f, Burmese, "BCDEEE+MyanmarText",
                (0, 6, 0.12f, 0.37f), (6, 12, 0.37f, 0.62f), (12, 18, 0.62f, 0.88f))
            .Build();

        var line = Assert.Single(RecoveredLineReader.Parse(bytes));

        Assert.Equal(709.1, line.PdfBaseline, 3);
        Assert.Equal(0.12, line.Left, 5);
        Assert.Equal(0.16, line.Bottom, 5);
        Assert.Equal(0.155, line.Baseline, 5);
        Assert.Equal(11, line.FontSizePts, 5);
        Assert.Equal(Burmese, line.Text);
        Assert.Equal("BCDEEE+MyanmarText", line.FontName);
        Assert.True(line.WasRead);
    }

    /// <summary>
    /// ⚠️ THE CORE COUNTS BYTES AND THIS APP COUNTS CHARS. Burmese is three
    /// bytes a letter, so a cluster the core calls 6..12 is 2..4 here. Reading
    /// the numbers straight through would put every caret at a third of the way
    /// along the line it belongs to, and the further along the worse.
    /// </summary>
    [Fact]
    public void the_cluster_offsets_arrive_counted_in_characters()
    {
        byte[] bytes = new Builder()
            .Line(700f, 0.1f, 0.1f, 0.9f, 0.13f, 0.128f, 11f, Burmese, "MyanmarText",
                (0, 6, 0.1f, 0.4f), (6, 12, 0.4f, 0.7f), (12, 18, 0.7f, 0.9f))
            .Build();

        var line = Assert.Single(RecoveredLineReader.Parse(bytes));

        Assert.Equal(new[] { 0, 2, 4 }, line.Clusters.Select(c => c.From));
        Assert.Equal(new[] { 2, 4, 6 }, line.Clusters.Select(c => c.To));
        Assert.Equal(line.Text.Length, line.Clusters[^1].To);
    }

    /// <summary>
    /// Every cluster names a real boundary in the string, so nothing above this
    /// can slice a character in half.
    /// </summary>
    [Fact]
    public void every_cluster_lands_on_a_character_boundary()
    {
        byte[] bytes = new Builder()
            .Line(700f, 0.1f, 0.1f, 0.9f, 0.13f, 0.128f, 11f, "abမြcd", "MyanmarText",
                (0, 1, 0.1f, 0.2f), (1, 2, 0.2f, 0.3f), (2, 8, 0.3f, 0.6f),
                (8, 9, 0.6f, 0.7f), (9, 10, 0.7f, 0.9f))
            .Build();

        var line = Assert.Single(RecoveredLineReader.Parse(bytes));

        Assert.Equal(5, line.Clusters.Count);
        Assert.Equal(new[] { 0, 1, 2, 4, 5 }, line.Clusters.Select(c => c.From));
        foreach (var c in line.Clusters)
        {
            Assert.InRange(c.From, 0, line.Text.Length);
            Assert.InRange(c.To, c.From, line.Text.Length);
        }
    }

    /// <summary>
    /// ⚠️ A LINE NOTHING COULD PROVE IS STILL REPORTED. The caller can then
    /// see that recovery declined a line rather than seeing nothing there at
    /// all, which is the difference between a limitation and a hole.
    /// </summary>
    [Fact]
    public void a_line_that_could_not_be_read_arrives_empty_and_says_so()
    {
        byte[] bytes = new Builder()
            .Line(575.5f, 0f, 0f, 0f, 0f, 0f, 0f, string.Empty, string.Empty)
            .Build();

        var line = Assert.Single(RecoveredLineReader.Parse(bytes));

        Assert.Equal(575.5, line.PdfBaseline, 3);
        Assert.Empty(line.Text);
        Assert.Empty(line.Clusters);
        Assert.False(line.WasRead);
    }

    [Fact]
    public void a_truncated_buffer_gives_back_what_it_could_read()
    {
        byte[] whole = new Builder()
            .Line(700f, 0.1f, 0.1f, 0.9f, 0.13f, 0.128f, 11f, Burmese, "MyanmarText",
                (0, 6, 0.1f, 0.4f))
            .Line(680f, 0.1f, 0.2f, 0.9f, 0.23f, 0.228f, 11f, Burmese, "MyanmarText",
                (0, 6, 0.1f, 0.4f))
            .Build();

        var cut = new byte[whole.Length - 9];
        Array.Copy(whole, cut, cut.Length);

        var found = RecoveredLineReader.Parse(cut);

        Assert.Single(found);
        Assert.Equal(700, found[0].PdfBaseline, 3);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 1, 0 })]
    public void nonsense_gives_back_nothing_rather_than_throwing(byte[]? bytes)
    {
        Assert.Empty(RecoveredLineReader.Parse(bytes));
    }

    // ---------------- what the page's lines become ----------------

    private static RecoveredLine Read(double baseline, string text, params double[] edges)
    {
        var clusters = new List<RecoveredCluster>();
        int per = Math.Max(1, text.Length / Math.Max(1, edges.Length - 1));
        for (int i = 0; i + 1 < edges.Length; i++)
        {
            int from = Math.Min(i * per, text.Length);
            int to = Math.Min((i + 1) * per, text.Length);
            clusters.Add(new RecoveredCluster(from, to, edges[i], edges[i + 1]));
        }
        return new RecoveredLine(
            PdfBaseline: 700 - (baseline * 1000), Left: edges[0], Top: baseline - 0.01,
            Right: edges[^1], Bottom: baseline + 0.004, Baseline: baseline,
            FontSizePts: 11, Text: text, FontName: "BCDEEE+MyanmarText",
            Clusters: clusters);
    }

    private static LineSnapshot Fragment(double baseline, LineRefusal refusal, string text) =>
        new(FirstObject: 0, LastObject: 3, PrefixChars: 0, Words: 1,
            Left: 0.1, Top: baseline - 0.01, Right: 0.2, Bottom: baseline + 0.004,
            Baseline: baseline, FontSizePts: 11, ColorRgb: 0, Refusal: refusal,
            Text: text, FontName: "BCDEEE+MyanmarText");

    /// <summary>
    /// ⚠️ THE FRAGMENTS GO, AND THAT IS THE WHOLE POINT. Measured on a
    /// Word-produced Burmese page, PDFium reports 150 lines where the page has
    /// 18: each one a placement, most a syllable or two, all of them carrying
    /// visual-order text. Leaving them beside the recovered lines would mean a
    /// click on one of the 18 real lines hitting one of the 150 fragments lying
    /// on top of it.
    /// </summary>
    [Fact]
    public void the_fragments_of_a_recovered_line_are_replaced_by_it()
    {
        var lines = new[]
        {
            Fragment(0.13, LineRefusal.ComplexScript, "်"),
            Fragment(0.13, LineRefusal.ComplexScript, "ဲ့"),
            Fragment(0.13, LineRefusal.ComplexScript, "ှ"),
        };
        var recovered = new[] { Read(0.13, Burmese, 0.12, 0.4, 0.7, 0.88) };

        var merged = RecoveredLines.Merge(lines, recovered);

        var one = Assert.Single(merged);
        Assert.Equal(Burmese, one.Text);
        Assert.Same(recovered[0], one.Recovered);
    }

    /// <summary>
    /// A page can hold a Burmese paragraph and an English heading, and PDFium
    /// reads the heading perfectly well.
    /// </summary>
    [Fact]
    public void a_line_pdfium_could_read_is_left_exactly_as_it_was()
    {
        var heading = new LineSnapshot(
            FirstObject: 0, LastObject: 2, PrefixChars: 0, Words: 2,
            Left: 0.1, Top: 0.05, Right: 0.5, Bottom: 0.08, Baseline: 0.078,
            FontSizePts: 18, ColorRgb: 0, Refusal: LineRefusal.None,
            Text: "A Heading", FontName: "Calibri-Bold");

        var lines = new[] { heading, Fragment(0.13, LineRefusal.ComplexScript, "်") };
        var merged = RecoveredLines.Merge(lines, new[] { Read(0.13, Burmese, 0.12, 0.5, 0.88) });

        Assert.Same(heading, merged.First(l => l.Text == "A Heading"));
        Assert.Equal(2, merged.Count);
    }

    /// <summary>
    /// ⚠️ AND A LINE RECOVERY COULD NOT READ KEEPS ITS FRAGMENTS. Dropping
    /// them would leave a strip of the page that answered no click at all, and
    /// a reader would have no way to tell it from blank paper.
    /// </summary>
    [Fact]
    public void a_line_recovery_declined_keeps_what_pdfium_had()
    {
        var stray = Fragment(0.30, LineRefusal.ComplexScript, "ှ");
        var lines = new[] { Fragment(0.13, LineRefusal.ComplexScript, "်"), stray };

        var merged = RecoveredLines.Merge(lines, new[] { Read(0.13, Burmese, 0.12, 0.5, 0.88) });

        Assert.Contains(stray, merged);
    }

    [Fact]
    public void nothing_read_changes_nothing()
    {
        var lines = new[] { Fragment(0.13, LineRefusal.ComplexScript, "်") };

        Assert.Same(lines, RecoveredLines.Merge(lines, Array.Empty<RecoveredLine>()));
        Assert.Same(lines, RecoveredLines.Merge(lines, null));
    }

    // ---------------- where it is routed ----------------

    /// <summary>
    /// ⚠️ THE ROW THAT OPENS THE GATE. A recovered line carries the
    /// complex-script refusal PDFium's own reading earned, and that refusal
    /// says nothing about this text: it was proven against the FONT by
    /// reshaping candidate letters and demanding the page's own glyph ids back
    /// identically. Routing on the refusal alone left the reader an amber box
    /// over text the core could read perfectly.
    /// </summary>
    [Fact]
    public void a_recovered_line_reaches_the_recovery_writer_and_no_other()
    {
        var merged = RecoveredLines.Merge(
            new[] { Fragment(0.13, LineRefusal.ComplexScript, "်") },
            new[] { Read(0.13, Burmese, 0.12, 0.5, 0.88) });

        var line = Assert.Single(merged);

        Assert.Equal(LineWriter.RecoveryWriter, line.Route);
        Assert.True(line.CanEdit);
        Assert.Empty(line.RefusalReason);
    }

    /// <summary>
    /// ⚠️ AND IT CARRIES NO OBJECT RANGE, so a writer addressed by objects
    /// cannot be handed one by mistake. -1 is refused by the core; object zero
    /// would have been written to.
    /// </summary>
    [Fact]
    public void a_recovered_line_has_no_object_range_to_hand_anyone()
    {
        var line = Assert.Single(RecoveredLines.Merge(
            new[] { Fragment(0.13, LineRefusal.ComplexScript, "်") },
            new[] { Read(0.13, Burmese, 0.12, 0.5, 0.88) }));

        Assert.Equal(-1, line.FirstObject);
        Assert.Equal(-1, line.LastObject);
    }

    [Fact]
    public void an_ordinary_line_is_routed_exactly_as_it_was()
    {
        Assert.Equal(LineWriter.ObjectWriter, Fragment(0.1, LineRefusal.None, "x").Route);
        Assert.Equal(LineWriter.BlockWriter, Fragment(0.1, LineRefusal.Justified, "x").Route);
        Assert.Equal(LineWriter.None, Fragment(0.1, LineRefusal.ComplexScript, "x").Route);
    }

    // ---------------- where the caret goes ----------------

    /// <summary>
    /// ⚠️ THE CARET CANNOT STAND INSIDE A CLUSTER, and that is the script's
    /// rule rather than a limitation. မြ is drawn as one mark with the medial
    /// before the consonant it follows, so the page has no position between the
    /// two of them; a caret offered one would be at an invented position, and
    /// the letter it appeared to be beside is not the letter an edit there
    /// would change.
    /// </summary>
    [Fact]
    public void a_click_anywhere_in_a_cluster_puts_the_caret_at_one_of_its_ends()
    {
        var line = Read(0.13, Burmese, 0.12, 0.4, 0.7, 0.88);
        var glyphs = EditGlyphs.Of(line, "#000000");

        Assert.Equal(3, glyphs.Count);
        Assert.Equal(new[] { 0, 2, 4 }, glyphs.Select(g => g.Offset));

        // Every point across the whole line, and never an offset inside a pair.
        for (double x = 0.10; x <= 0.92; x += 0.005)
        {
            int at = EditGlyphs.OffsetAt(glyphs, line.Text, x);
            Assert.Contains(at, new[] { 0, 2, 4, line.Text.Length });
        }
    }

    [Fact]
    public void a_click_on_the_left_half_of_a_mark_means_before_it()
    {
        var line = Read(0.13, Burmese, 0.12, 0.4, 0.7, 0.88);
        var glyphs = EditGlyphs.Of(line, "#000000");

        Assert.Equal(0, EditGlyphs.OffsetAt(glyphs, line.Text, 0.20));
        Assert.Equal(2, EditGlyphs.OffsetAt(glyphs, line.Text, 0.30));
        Assert.Equal(4, EditGlyphs.OffsetAt(glyphs, line.Text, 0.60));
        Assert.Equal(line.Text.Length, EditGlyphs.OffsetAt(glyphs, line.Text, 0.95));
    }

    /// <summary>
    /// Where the redrawn tail starts is where the first changed cluster is
    /// drawn, and the page's own glyphs before it are never covered.
    /// </summary>
    [Fact]
    public void the_tail_starts_where_the_first_changed_cluster_is_drawn()
    {
        var line = Read(0.13, Burmese, 0.12, 0.4, 0.7, 0.88);
        var glyphs = EditGlyphs.Of(line, "#000000");

        Assert.Equal(0.12, EditGlyphs.XOf(glyphs, 0), 5);
        Assert.Equal(0.40, EditGlyphs.XOf(glyphs, 2), 5);
        Assert.Equal(0.70, EditGlyphs.XOf(glyphs, 4), 5);
        Assert.Equal(0.88, EditGlyphs.XOf(glyphs, line.Text.Length), 5);
    }

    [Fact]
    public void a_line_with_no_clusters_offers_no_caret_at_all()
    {
        var line = new RecoveredLine(
            700, 0, 0, 0, 0, 0, 0, string.Empty, string.Empty,
            Array.Empty<RecoveredCluster>());

        Assert.Empty(EditGlyphs.Of(line, "#000000"));
    }

    // ---------------- the font it will be written in ----------------

    /// <summary>
    /// ⚠️ THE SAME TWO FAMILIES THE CORE WILL READ. recover::installed maps
    /// exactly these, and offering the reader a font the core will refuse turns
    /// a clean up-front refusal into a failed write after they have typed.
    /// </summary>
    [Theory]
    [InlineData("BCDEEE+MyanmarText", "mmrtext.ttf")]
    [InlineData("Pyidaungsu", "Pyidaungsu.ttf")]
    public void the_myanmar_families_resolve_to_the_files_the_core_reads(
        string baseFont, string expected)
    {
        Assert.Equal(expected, SystemFontMatch.FileNameFor(baseFont));
    }

    /// <summary>
    /// ⚠️ A COMMA IS AS GOOD AS A HYPHEN, and on a real file it is what the
    /// producer used: the page names its bold `ABCDEE+Pyidaungsu,Bold`. Both
    /// spellings have to reach the same file, or the app and the core disagree
    /// about a font one of them has already accepted.
    /// </summary>
    [Theory]
    [InlineData("ABCDEE+Pyidaungsu,Bold", "Pyidaungsu.ttf", "Pyidaungsu-Bold.ttf")]
    [InlineData("ABCDEF+Pyidaungsu-Bold", "Pyidaungsu.ttf", "Pyidaungsu-Bold.ttf")]
    [InlineData("BCDGEE+MyanmarText-Bold", "mmrtext.ttf", "mmrtextb.ttf")]
    public void a_bold_burmese_name_lands_on_the_bold_file_or_the_family_it_belongs_to(
        string baseFont, string regular, string boldFile)
    {
        // ⚠️ NOT A FIXED ANSWER, because it depends on what is installed. A
        // bold name resolves to the bold FILE when there is one and to the
        // family's regular file when there is not: measured, there is no
        // Pyidaungsu-Bold.ttf on the machine this was written on, and the
        // regular file proves 9 of the 10 bold lines of a real page.
        //
        // ⚠️ AND FALLING BACK CANNOT MISREAD ANYTHING. A line is proven by
        // shaping candidate text through the font and demanding the page's own
        // glyph ids back, so a font that does not match yields a refusal.
        string expected =
            System.IO.File.Exists(System.IO.Path.Combine(
                SystemFontMatch.FontsDirectory, boldFile))
                ? boldFile
                : regular;

        Assert.Equal(expected, SystemFontMatch.FileNameFor(baseFont));
    }
}
