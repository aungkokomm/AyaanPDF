using System;
using System.Collections.Generic;
using System.Text;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// A page's LINKS, decoded from the buffer the core writes.
///
/// The decode is checked here rather than by opening a document because the
/// wire format is the one thing the two halves have to agree about exactly, and
/// a layout only exercised by running the app is a layout nobody checks.
/// </summary>
public class LinkReaderTests
{
    /// <summary>Builds a buffer the way render_core lays one out.</summary>
    private static byte[] Buffer(
        params (int Index, uint Kind, float L, float T, float R, float B, int Target, string Uri)[] links)
    {
        var bytes = new List<byte>();
        bytes.AddRange(BitConverter.GetBytes((uint)links.Length));

        foreach (var (index, kind, l, t, r, b, target, uri) in links)
        {
            bytes.AddRange(BitConverter.GetBytes((uint)index));
            bytes.AddRange(BitConverter.GetBytes(kind));
            bytes.AddRange(BitConverter.GetBytes(l));
            bytes.AddRange(BitConverter.GetBytes(t));
            bytes.AddRange(BitConverter.GetBytes(r));
            bytes.AddRange(BitConverter.GetBytes(b));
            bytes.AddRange(BitConverter.GetBytes(target));

            byte[] u = Encoding.UTF8.GetBytes(uri);
            bytes.AddRange(BitConverter.GetBytes((uint)u.Length));
            bytes.AddRange(u);
        }

        return bytes.ToArray();
    }

    [Fact]
    public void a_uri_link_decodes_field_for_field()
    {
        var found = LinkReader.Parse(
            Buffer((3, 0, 0.10f, 0.20f, 0.50f, 0.24f, -1, "https://example.com/path?q=1")));

        var link = Assert.Single(found);
        Assert.Equal(3, link.AnnotationIndex);
        Assert.Equal(LinkKind.Uri, link.Kind);
        Assert.Equal(0.10, link.Left, 5);
        Assert.Equal(0.20, link.Top, 5);
        Assert.Equal(0.50, link.Right, 5);
        Assert.Equal(0.24, link.Bottom, 5);
        Assert.Equal(-1, link.TargetPage);
        Assert.Equal("https://example.com/path?q=1", link.Uri);
        Assert.True(link.CanEditUrl);
    }

    [Fact]
    public void an_internal_link_arrives_with_its_page_and_no_url()
    {
        // ⚠️ A browser writes an internal jump as /Dest with NO action at all,
        // so the core has to ask two questions and this is the answer to the
        // second. Reading only the first makes every internal link look empty.
        var link = Assert.Single(LinkReader.Parse(
            Buffer((0, 1, 0.1f, 0.1f, 0.4f, 0.14f, 4, string.Empty))));

        Assert.Equal(LinkKind.Internal, link.Kind);
        Assert.Equal(4, link.TargetPage);
        Assert.Equal(string.Empty, link.Uri);
        Assert.False(link.CanEditUrl);
        Assert.Equal("Goes to page 5", link.Describe);
    }

    [Fact]
    public void the_annotation_index_is_what_survives_the_trip()
    {
        // ⚠️ NOT a position in a list of links. Every call that edits or deletes
        // one takes the ANNOTATION index, and on a page mixing links with our own
        // marks the two numbers are different: a stamp then two links makes the
        // first link annotation 1, not annotation 0.
        var found = LinkReader.Parse(
            Buffer((1, 0, 0.1f, 0.1f, 0.4f, 0.14f, -1, "https://one.example"),
                   (2, 0, 0.1f, 0.2f, 0.4f, 0.24f, -1, "https://two.example")));

        Assert.Equal(new[] { 1, 2 }, new[] { found[0].AnnotationIndex, found[1].AnnotationIndex });
    }

    [Fact]
    public void a_kind_this_build_has_never_heard_of_is_not_editable()
    {
        // THE LINE THAT MATTERS ON AN UPGRADE. A newer core adding a kind an
        // older app does not know must fall to the read-only side, or the app
        // would offer to retarget something the core will refuse to write.
        var link = Assert.Single(LinkReader.Parse(
            Buffer((0, 99u, 0.1f, 0.1f, 0.4f, 0.14f, -1, string.Empty))));

        Assert.Equal(LinkKind.Other, link.Kind);
        Assert.False(link.CanEditUrl);
    }

    [Fact]
    public void a_url_outside_ascii_survives_the_trip()
    {
        var found = LinkReader.Parse(
            Buffer((0, 0, 0.1f, 0.1f, 0.4f, 0.14f, -1, "https://example.com/café")));

        Assert.Equal("https://example.com/café", found[0].Uri);
    }

    [Fact]
    public void an_empty_buffer_is_no_links_and_not_a_crash()
    {
        Assert.Empty(LinkReader.Parse(Buffer()));
        Assert.Empty(LinkReader.Parse([]));
        Assert.Empty(LinkReader.Parse(null));
    }

    [Fact]
    public void a_truncated_buffer_keeps_what_it_could_read()
    {
        byte[] whole = Buffer(
            (0, 0, 0.1f, 0.1f, 0.4f, 0.14f, -1, "https://first.example"),
            (1, 0, 0.1f, 0.2f, 0.4f, 0.24f, -1, "https://second.example"));

        var found = LinkReader.Parse(whole[..(whole.Length - 3)]);

        Assert.Single(found);
        Assert.Equal("https://first.example", found[0].Uri);
    }

    [Fact]
    public void a_buffer_claiming_more_links_than_it_holds_does_not_run_off_the_end()
    {
        byte[] lying = Buffer((0, 0, 0.1f, 0.1f, 0.4f, 0.14f, -1, "https://first.example"));
        BitConverter.GetBytes(500u).CopyTo(lying, 0);

        Assert.Single(LinkReader.Parse(lying));
    }

    [Fact]
    public void a_url_longer_than_the_buffer_is_refused_rather_than_read()
    {
        byte[] lying = Buffer((0, 0, 0.1f, 0.1f, 0.4f, 0.14f, -1, "https://first.example"));
        BitConverter.GetBytes(9999u).CopyTo(lying, 4 + 28); // the URI length

        Assert.Empty(LinkReader.Parse(lying));
    }

    [Fact]
    public void a_point_inside_the_rectangle_is_on_the_link_and_one_outside_is_not()
    {
        var link = Assert.Single(LinkReader.Parse(
            Buffer((0, 0, 0.10f, 0.20f, 0.50f, 0.24f, -1, "https://x.example"))));

        Assert.True(link.Contains(0.30, 0.22));

        // Just inside each edge, not exactly on it: the bounds arrive as
        // floats and widen to doubles a hair away from the literal, so an
        // exact-corner assertion would be measuring the float, not the rule.
        Assert.True(link.Contains(link.Left + 1e-6, link.Top + 1e-6));
        Assert.True(link.Contains(link.Right - 1e-6, link.Bottom - 1e-6));

        Assert.False(link.Contains(0.09, 0.22));
        Assert.False(link.Contains(0.51, 0.22));
        Assert.False(link.Contains(0.30, 0.19));
        Assert.False(link.Contains(0.30, 0.26));
    }
}
