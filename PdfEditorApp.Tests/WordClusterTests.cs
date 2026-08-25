using System;
using System.Collections.Generic;
using System.Text;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// A page read as WORDS, decoded from the buffer the core writes.
///
/// The unit matters more than the decode. A producer decides for itself where
/// one PDF text object ends: measured on real files, Chromium emits ONE OBJECT
/// PER GLYPH, 565 of them for four short paragraphs, while other producers emit
/// one per run. Everything above this line works in words so that it never has
/// to know which kind of file it was given.
/// </summary>
public class WordClusterTests
{
    /// <summary>Builds a buffer the way render_core lays one out.</summary>
    private static byte[] Buffer(params (int[] Objects, string Text, string Font, uint Refusal)[] words)
    {
        var bytes = new List<byte>();
        bytes.AddRange(BitConverter.GetBytes((uint)words.Length));

        foreach (var (objects, text, font, refusal) in words)
        {
            bytes.AddRange(BitConverter.GetBytes((uint)(objects.Length > 0 ? objects[0] : 0)));
            bytes.AddRange(BitConverter.GetBytes((uint)objects.Length));
            foreach (int o in objects)
            {
                bytes.AddRange(BitConverter.GetBytes((uint)o));
            }

            bytes.AddRange(BitConverter.GetBytes(0.10f));  // left
            bytes.AddRange(BitConverter.GetBytes(0.20f));  // top
            bytes.AddRange(BitConverter.GetBytes(0.50f));  // right
            bytes.AddRange(BitConverter.GetBytes(0.30f));  // bottom
            bytes.AddRange(BitConverter.GetBytes(0.28f));  // baseline
            bytes.AddRange(BitConverter.GetBytes(12.5f));  // size
            bytes.AddRange(BitConverter.GetBytes(0x00AABBCCu));
            bytes.AddRange(BitConverter.GetBytes(refusal));

            byte[] t = Encoding.UTF8.GetBytes(text);
            bytes.AddRange(BitConverter.GetBytes((uint)t.Length));
            bytes.AddRange(t);

            byte[] f = Encoding.UTF8.GetBytes(font);
            bytes.AddRange(BitConverter.GetBytes((uint)f.Length));
            bytes.AddRange(f);
        }

        return bytes.ToArray();
    }

    [Fact]
    public void a_word_decodes_field_for_field()
    {
        var found = WordClusterReader.Parse(
            Buffer(([3, 4, 5, 6], "Chapter", "AAAAAA+Arial-BoldMT", 0)));

        var w = Assert.Single(found);
        Assert.Equal("Chapter", w.Text);
        Assert.Equal("AAAAAA+Arial-BoldMT", w.FontName);
        Assert.Equal(new[] { 3, 4, 5, 6 }, w.ObjectIndices);
        Assert.Equal(3, w.FirstObjectIndex);
        Assert.Equal(0.10, w.Left, 5);
        Assert.Equal(0.20, w.Top, 5);
        Assert.Equal(0.50, w.Right, 5);
        Assert.Equal(0.30, w.Bottom, 5);
        Assert.Equal(0.28, w.Baseline, 5);
        Assert.Equal(12.5, w.FontSizePts, 5);
        Assert.Equal(0x00AABBCCu, w.ColorRgb);
        Assert.True(w.CanEdit);
    }

    [Fact]
    public void a_word_of_one_object_and_a_word_of_many_read_the_same_way()
    {
        // THE WHOLE POINT. One producer writes a word as seven objects and
        // another writes it as one; the reader must not be able to tell.
        var perGlyph = WordClusterReader.Parse(
            Buffer(([0, 1, 2, 3, 4, 5, 6], "Chapter", "Arial", 0)));
        var perRun = WordClusterReader.Parse(
            Buffer(([0], "Chapter", "Arial", 0)));

        Assert.Equal(perGlyph[0].Text, perRun[0].Text);
        Assert.True(perGlyph[0].CanEdit);
        Assert.True(perRun[0].CanEdit);
    }

    [Theory]
    [InlineData(1u, ClusterRefusal.NotUpright)]
    [InlineData(2u, ClusterRefusal.MixedStyle)]
    [InlineData(3u, ClusterRefusal.SplitObjects)]
    [InlineData(4u, ClusterRefusal.NoFontName)]
    [InlineData(5u, ClusterRefusal.NoObjects)]
    public void every_refusal_the_core_can_give_arrives_intact(uint code, ClusterRefusal expected)
    {
        var w = Assert.Single(WordClusterReader.Parse(Buffer(([0], "x", "Arial", code))));

        Assert.Equal(expected, w.Refusal);
        Assert.False(w.CanEdit);
        Assert.NotEqual(string.Empty, w.RefusalReason);
    }

    [Fact]
    public void a_refusal_this_build_has_never_heard_of_still_refuses()
    {
        // THE LINE THAT MATTERS ON AN UPGRADE. A newer core adding a reason an
        // older app does not know must not read as "editable", or the app would
        // let through exactly the write the core had just decided to forbid.
        var w = Assert.Single(WordClusterReader.Parse(Buffer(([0], "x", "Arial", 99u))));

        Assert.False(w.CanEdit);
    }

    [Fact]
    public void text_outside_ascii_survives_the_trip()
    {
        var found = WordClusterReader.Parse(
            Buffer(([0], "café", "ABCDEF+NotoSans", 0)));

        Assert.Equal("café", found[0].Text);
    }

    [Fact]
    public void an_empty_buffer_is_no_words_and_not_a_crash()
    {
        Assert.Empty(WordClusterReader.Parse(Buffer()));
        Assert.Empty(WordClusterReader.Parse([]));
        Assert.Empty(WordClusterReader.Parse(null));
    }

    [Fact]
    public void a_truncated_buffer_keeps_what_it_could_read()
    {
        byte[] whole = Buffer(([0], "first", "Arial", 0), ([1], "second", "Arial", 0));

        var found = WordClusterReader.Parse(whole[..(whole.Length - 3)]);

        Assert.Single(found);
        Assert.Equal("first", found[0].Text);
    }

    [Fact]
    public void a_buffer_claiming_more_words_than_it_holds_does_not_run_off_the_end()
    {
        byte[] lying = Buffer(([0], "first", "Arial", 0));
        BitConverter.GetBytes(500u).CopyTo(lying, 0);

        Assert.Single(WordClusterReader.Parse(lying));
    }

    [Fact]
    public void a_word_claiming_more_objects_than_the_buffer_holds_is_refused()
    {
        // The object count is read before the objects, so a wrong one would walk
        // the cursor past the end of the buffer and take the rest with it.
        byte[] lying = Buffer(([0], "first", "Arial", 0));
        BitConverter.GetBytes(9999u).CopyTo(lying, 8); // the per-word object count

        Assert.Empty(WordClusterReader.Parse(lying));
    }
}
