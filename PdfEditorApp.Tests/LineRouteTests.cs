using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Which writer a line is routed to, and therefore whether the app paints it
/// editable.
///
/// ⚠️ THIS IS A SAFETY TABLE, NOT A PREFERENCE. Two of its rows are the reason
/// it exists at all. A justified line must reach the block writer, because the
/// app used to paint it refused while Path A, which exists precisely to re-solve
/// justified spacing, sat behind the gate untried. And a complex-script line
/// must never reach any writer, because PDFium reads those back in reading
/// order while the stream draws them in visual order, so a length that agrees
/// can still mean something else entirely. A row moving between those two
/// halves is a correctness change, and this fails when one does.
/// </summary>
public class LineRouteTests
{
    private static LineSnapshot Line(LineRefusal refusal) =>
        new(FirstObject: 0, LastObject: 3, PrefixChars: 0, Words: 5,
            Left: 0.1, Top: 0.1, Right: 0.9, Bottom: 0.12, Baseline: 0.118,
            FontSizePts: 11, ColorRgb: 0, Refusal: refusal,
            Text: "a line of ordinary text", FontName: "Arial");

    [Theory]
    [InlineData(LineRefusal.None, LineWriter.ObjectWriter)]
    [InlineData(LineRefusal.Justified, LineWriter.BlockWriter)]
    [InlineData(LineRefusal.MixedStyle, LineWriter.BlockWriter)]
    [InlineData(LineRefusal.PartialSpan, LineWriter.BlockWriter)]
    [InlineData(LineRefusal.NoFontName, LineWriter.BlockWriter)]
    [InlineData(LineRefusal.ComplexScript, LineWriter.None)]
    [InlineData(LineRefusal.OutOfOrder, LineWriter.None)]
    [InlineData(LineRefusal.Gapped, LineWriter.None)]
    [InlineData(LineRefusal.ForeignObject, LineWriter.None)]
    [InlineData(LineRefusal.NotUpright, LineWriter.None)]
    [InlineData(LineRefusal.NoObjects, LineWriter.None)]
    public void every_refusal_routes_where_it_was_measured_to_belong(
        LineRefusal refusal, LineWriter expected)
    {
        Assert.Equal(expected, Line(refusal).Route);
    }

    /// <summary>
    /// The one row that can never move. Editing shaped text through either
    /// writer was measured reordering it.
    /// </summary>
    [Fact]
    public void complex_script_reaches_no_writer_at_all()
    {
        var line = Line(LineRefusal.ComplexScript);

        Assert.Equal(LineWriter.None, line.Route);
        Assert.False(line.CanEdit);
        Assert.NotEmpty(line.RefusalReason);
    }

    /// <summary>
    /// A justified line is the case this whole routing layer was built for: the
    /// app used to paint it refused and never ask.
    /// </summary>
    [Fact]
    public void a_justified_line_is_editable_and_says_so()
    {
        var line = Line(LineRefusal.Justified);

        Assert.Equal(LineWriter.BlockWriter, line.Route);
        Assert.True(line.CanEdit);
    }

    /// <summary>
    /// ⚠️ THE FRAME COLOUR AND THE WRITE MUST NOT BE ABLE TO DISAGREE. The app
    /// paints the frame from CanEdit and chooses the writer from Route; if
    /// CanEdit were still its own rule, a line could be painted refused and
    /// written anyway, or the reverse. Deriving one from the other is what makes
    /// that impossible, and this says so for every value.
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
    [InlineData(LineRefusal.ComplexScript)]
    public void what_the_frame_says_is_what_the_writer_does(LineRefusal refusal)
    {
        var line = Line(refusal);

        Assert.Equal(line.Route != LineWriter.None, line.CanEdit);
    }
}
