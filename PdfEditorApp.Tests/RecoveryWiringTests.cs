using System;
using System.Collections.Generic;
using System.IO;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The signal that says a page needs its text read back out of the glyphs, and
/// the one place that acts on it.
///
/// ⚠️ THE CORE CANNOT DECIDE THIS FOR ITSELF, which is why the app has to.
/// Reading a shaped page costs about seventeen seconds of work, and finding out
/// whether a document is worth it means either serialising the whole file or
/// asking PDFium for a page, and asking for a page parses it. Both were
/// measured and both cost more than they saved. The refusal the core already
/// sends back is free, so the decision lives on this side of the wire.
/// </summary>
public class RecoveryWiringTests
{
    private static LineSnapshot Line(LineRefusal refusal) => new(
        FirstObject: 0,
        LastObject: 1,
        PrefixChars: 0,
        Words: 2,
        Left: 0.1,
        Top: 0.1,
        Right: 0.5,
        Bottom: 0.12,
        Baseline: 0.11,
        FontSizePts: 12,
        ColorRgb: 0,
        Refusal: refusal,
        Text: "text",
        FontName: "Times");

    [Fact]
    public void a_page_with_a_shaped_line_asks_for_it_to_be_read()
    {
        Assert.True(LineReader.NeedsReshaping(new[] { Line(LineRefusal.ComplexScript) }));
    }

    /// <summary>
    /// ⚠️ ONE SHAPED LINE IS ENOUGH. A page is usually mostly ordinary text
    /// with the Burmese in it somewhere, and asking about the page is the
    /// question, not asking about every line on it.
    /// </summary>
    [Fact]
    public void one_shaped_line_among_many_is_enough_to_ask()
    {
        var lines = new[]
        {
            Line(LineRefusal.None),
            Line(LineRefusal.Justified),
            Line(LineRefusal.ComplexScript),
            Line(LineRefusal.None),
        };
        Assert.True(LineReader.NeedsReshaping(lines));
    }

    /// <summary>
    /// ⚠️ AND NO OTHER REFUSAL ASKS. Every one of these means a line cannot be
    /// retyped for some reason of its own, and none of them is a reason to
    /// spend seventeen seconds reading a font.
    /// </summary>
    [Theory]
    [InlineData(LineRefusal.None)]
    [InlineData(LineRefusal.NotUpright)]
    [InlineData(LineRefusal.MixedStyle)]
    [InlineData(LineRefusal.OutOfOrder)]
    [InlineData(LineRefusal.NoFontName)]
    [InlineData(LineRefusal.NoObjects)]
    [InlineData(LineRefusal.PartialSpan)]
    [InlineData(LineRefusal.Justified)]
    [InlineData(LineRefusal.Gapped)]
    [InlineData(LineRefusal.ForeignObject)]
    public void no_other_refusal_asks_for_seventeen_seconds_of_work(LineRefusal refusal)
    {
        Assert.False(LineReader.NeedsReshaping(new[] { Line(refusal) }));
    }

    [Fact]
    public void a_page_with_no_lines_at_all_asks_for_nothing()
    {
        Assert.False(LineReader.NeedsReshaping(Array.Empty<LineSnapshot>()));
        Assert.False(LineReader.NeedsReshaping(null));
    }

    /// <summary>
    /// The gateway is where the signal is acted on, because it is the one place
    /// a page's lines are read.
    ///
    /// ⚠️ CHECKED IN THE SOURCE because the call is a static extern into the
    /// native library, which a test assembly cannot stand in for. What can be
    /// checked is that the decision and the call are in the same place, and
    /// that the decision is the shared one rather than a second copy of it.
    /// </summary>
    [Fact]
    public void the_gateway_asks_when_the_lines_say_to()
    {
        string source = Source("PdfEditorApp", "Interop", "LineGateway.cs");

        Assert.Contains("LineReader.NeedsReshaping(lines)", source);
        Assert.Contains("RenderCoreNative.prepare_recovery(docHandle, pageIndex)", source);

        // The call sits INSIDE the question, not beside it.
        int asked = source.IndexOf("LineReader.NeedsReshaping(lines)", StringComparison.Ordinal);
        int called = source.IndexOf("RenderCoreNative.prepare_recovery", StringComparison.Ordinal);
        Assert.True(asked < called, "the page is prepared before anyone asks whether it needs it");
    }

    private static string Source(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, Path.Combine(parts))))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, Path.Combine(parts)));
    }
}
