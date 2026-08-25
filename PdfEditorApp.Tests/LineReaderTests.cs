using System;
using System.Collections.Generic;
using System.Text;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The line wire format, decoded away from the app.
///
/// A format checked only by running the app is a format nobody checks, and the
/// two ends of this one are written in different languages: the core lays the
/// bytes out and this reads them, and nothing but a test holds the two
/// together.
/// </summary>
public class LineReaderTests
{
    private sealed class Builder
    {
        private readonly List<byte> _bytes = new();
        private int _count;

        public Builder Line(
            int first, int last, int prefix, int words,
            float left, float top, float right, float bottom, float baseline,
            float size, uint color, uint refusal, string text, string font)
        {
            _count++;
            U32((uint)first);
            U32((uint)last);
            U32((uint)prefix);
            U32((uint)words);
            F32(left); F32(top); F32(right); F32(bottom); F32(baseline);
            F32(size);
            U32(color);
            U32(refusal);
            Str(text);
            Str(font);
            return this;
        }

        public byte[] Build()
        {
            var all = new List<byte>();
            all.AddRange(BitConverter.GetBytes((uint)_count));
            all.AddRange(_bytes);
            return all.ToArray();
        }

        /// <summary>A buffer that promises more lines than it carries.</summary>
        public byte[] BuildClaiming(uint count)
        {
            var all = new List<byte>();
            all.AddRange(BitConverter.GetBytes(count));
            all.AddRange(_bytes);
            return all.ToArray();
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

    [Fact]
    public void a_line_arrives_with_every_field_the_core_sent()
    {
        byte[] bytes = new Builder()
            .Line(77, 77, 0, 14, 0.1059f, 0.30f, 0.6448f, 0.32f, 0.318f,
                  12f, 0x112233, 0, "breaks where the line runs out", "BAAAAA+TimesNewRomanPSMT")
            .Build();

        var line = Assert.Single(LineReader.Parse(bytes));

        Assert.Equal(77, line.FirstObject);
        Assert.Equal(77, line.LastObject);
        Assert.Equal(0, line.PrefixChars);
        Assert.Equal(14, line.Words);
        Assert.Equal(0.1059, line.Left, 4);
        Assert.Equal(0.6448, line.Right, 4);
        Assert.Equal(0.318, line.Baseline, 4);
        Assert.Equal(12.0, line.FontSizePts, 4);
        Assert.Equal(0x112233u, line.ColorRgb);
        Assert.Equal(LineRefusal.None, line.Refusal);
        Assert.Equal("breaks where the line runs out", line.Text);
        Assert.Equal("BAAAAA+TimesNewRomanPSMT", line.FontName);
        Assert.True(line.CanEdit);
    }

    [Fact]
    public void several_lines_decode_in_order()
    {
        byte[] bytes = new Builder()
            .Line(0, 27, 0, 4, 0.1f, 0.1f, 0.5f, 0.12f, 0.11f, 16f, 0, 0, "Chapter One", "Arial")
            .Line(28, 40, 0, 13, 0.1f, 0.2f, 0.6f, 0.22f, 0.21f, 12f, 0, 7, "The quick brown", "Times")
            .Build();

        var lines = LineReader.Parse(bytes);

        Assert.Equal(2, lines.Count);
        Assert.Equal("Chapter One", lines[0].Text);
        Assert.Equal(LineRefusal.Justified, lines[1].Refusal);
        Assert.False(lines[1].CanEdit);
    }

    [Fact]
    public void an_empty_page_decodes_as_no_lines()
    {
        Assert.Empty(LineReader.Parse(new Builder().Build()));
        Assert.Empty(LineReader.Parse(null));
        Assert.Empty(LineReader.Parse(Array.Empty<byte>()));
    }

    [Fact]
    public void a_buffer_that_promises_more_than_it_carries_stops_rather_than_throwing()
    {
        // A short buffer means the core and this build disagree about the
        // layout, which is a bug, but not one worth taking the app down for.
        byte[] bytes = new Builder()
            .Line(0, 3, 0, 2, 0.1f, 0.1f, 0.5f, 0.12f, 0.11f, 12f, 0, 0, "only one", "Times")
            .BuildClaiming(9);

        var lines = LineReader.Parse(bytes);

        Assert.Single(lines);
        Assert.Equal("only one", lines[0].Text);
    }

    [Fact]
    public void a_truncated_string_stops_rather_than_over_reading()
    {
        byte[] whole = new Builder()
            .Line(0, 3, 0, 2, 0.1f, 0.1f, 0.5f, 0.12f, 0.11f, 12f, 0, 0, "some text", "Times")
            .Build();

        for (int cut = 5; cut < whole.Length; cut += 3)
        {
            byte[] chopped = new byte[cut];
            Array.Copy(whole, chopped, cut);
            LineReader.Parse(chopped);   // must not throw
        }
    }

    [Fact]
    public void a_refusal_this_build_has_not_heard_of_is_still_a_refusal()
    {
        // ⚠️ THE ONE THAT MATTERS. A newer core adding a reason an older app
        // does not know must never read as "editable": that would let through
        // exactly the write the core had just decided to forbid.
        byte[] bytes = new Builder()
            .Line(0, 3, 0, 2, 0.1f, 0.1f, 0.5f, 0.12f, 0.11f, 12f, 0, 999,
                  "from the future", "Times")
            .Build();

        var line = Assert.Single(LineReader.Parse(bytes));

        Assert.False(line.CanEdit);
        Assert.NotEqual(string.Empty, line.RefusalReason);
    }

    [Theory]
    [InlineData(1, LineRefusal.NotUpright)]
    [InlineData(2, LineRefusal.MixedStyle)]
    [InlineData(3, LineRefusal.OutOfOrder)]
    [InlineData(4, LineRefusal.NoFontName)]
    [InlineData(5, LineRefusal.NoObjects)]
    [InlineData(6, LineRefusal.PartialSpan)]
    [InlineData(7, LineRefusal.Justified)]
    [InlineData(8, LineRefusal.Gapped)]
    [InlineData(9, LineRefusal.ForeignObject)]
    [InlineData(11, LineRefusal.ComplexScript)]
    public void every_refusal_the_core_can_send_arrives_as_itself_and_says_why(
        uint code, LineRefusal expected)
    {
        byte[] bytes = new Builder()
            .Line(0, 3, 0, 2, 0.1f, 0.1f, 0.5f, 0.12f, 0.11f, 12f, 0, code, "text", "Times")
            .Build();

        var line = Assert.Single(LineReader.Parse(bytes));

        Assert.Equal(expected, line.Refusal);
        Assert.False(line.CanEdit);
        Assert.NotEqual(string.Empty, line.RefusalReason);
    }

    [Fact]
    public void non_ascii_text_survives_the_wire()
    {
        byte[] bytes = new Builder()
            .Line(0, 3, 0, 2, 0.1f, 0.1f, 0.5f, 0.12f, 0.11f, 12f, 0, 0,
                  "café naïve résumé Zürich", "Georgia")
            .Build();

        Assert.Equal("café naïve résumé Zürich", Assert.Single(LineReader.Parse(bytes)).Text);
    }
}
