using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// That a rotated shape is framed at its own size.
///
/// The overlay lays the frame, the grips and their hit zones out upright and
/// then turns the lot about the centre. Laying that out in the annotation's
/// /Rect turned it twice, because the /Rect of a turned shape is the
/// axis-aligned box CONTAINING the rotation rather than the shape's own box.
///
/// The 90 degree case is the one a person actually reported: a rectangle
/// standing on its side, framed by a box lying flat.
/// </summary>
public class SelectionFrameTests
{
    // A 0.6 x 0.4 shape centred at (0.5, 0.35), in normalized page units.
    private const double BoxW = 0.6;
    private const double BoxH = 0.4;
    private const double CentreX = 0.5;
    private const double CentreY = 0.35;

    private static void AssertRect(
        (double Left, double Top, double Right, double Bottom) actual,
        double left, double top, double right, double bottom)
    {
        Assert.Equal(left, actual.Left, 9);
        Assert.Equal(top, actual.Top, 9);
        Assert.Equal(right, actual.Right, 9);
        Assert.Equal(bottom, actual.Bottom, 9);
    }

    // ---------------- the reported bug ----------------

    [Fact]
    public void a_shape_on_its_side_is_framed_standing_not_lying_flat()
    {
        // At 90 degrees the /Rect is the shape's box with its sides swapped:
        // 0.4 wide by 0.6 tall. Framing THAT and then turning it 90 degrees
        // put a 0.6 x 0.4 frame around a 0.4 x 0.6 shape, which is what the
        // screenshot showed.
        var frame = SelectionFrame.Upright(0.3, 0.05, 0.7, 0.65, BoxW, BoxH);

        AssertRect(frame, 0.2, 0.15, 0.8, 0.55);
        Assert.Equal(BoxW, frame.Right - frame.Left, 9);
        Assert.Equal(BoxH, frame.Bottom - frame.Top, 9);
    }

    [Theory]
    // 30 degrees: 0.6x0.4 turned has an axis-aligned box of about 0.72 x 0.65.
    [InlineData(0.14, 0.0225, 0.86, 0.6775)]
    // 45 degrees: the axis-aligned box is square, and a square cannot say
    // which way round the shape inside it is. This is why the size is recorded
    // rather than derived.
    [InlineData(0.14645, 0.00355, 0.85355, 0.69645)]
    // 90 degrees.
    [InlineData(0.3, 0.05, 0.7, 0.65)]
    public void the_frame_is_the_shapes_own_size_whatever_the_reported_box(
        double left, double top, double right, double bottom)
    {
        var frame = SelectionFrame.Upright(left, top, right, bottom, BoxW, BoxH);

        Assert.Equal(BoxW, frame.Right - frame.Left, 9);
        Assert.Equal(BoxH, frame.Bottom - frame.Top, 9);
    }

    [Theory]
    [InlineData(0.14, 0.0225, 0.86, 0.6775)]
    [InlineData(0.3, 0.05, 0.7, 0.65)]
    public void the_frame_stays_where_the_shape_is(double l, double t, double r, double b)
    {
        // A rotation about the centre cannot move the centre, so the reported
        // rectangle's centre is the shape's centre at every angle. If the frame
        // were re-centred the fix would trade a size error for a position one.
        var frame = SelectionFrame.Upright(l, t, r, b, BoxW, BoxH);

        Assert.Equal(CentreX, (frame.Left + frame.Right) / 2, 9);
        Assert.Equal(CentreY, (frame.Top + frame.Bottom) / 2, 9);
    }

    // ---------------- what must not change ----------------

    [Fact]
    public void an_unrotated_shape_is_framed_exactly_as_before()
    {
        // Its de-padded /Rect already IS its box, so the answer must be the
        // rectangle it was given, to the last digit. Everything unrotated is
        // the overwhelming majority of what people select.
        var frame = SelectionFrame.Upright(0.2, 0.15, 0.8, 0.55, BoxW, BoxH);

        AssertRect(frame, 0.2, 0.15, 0.8, 0.55);
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.0, 0.4)]
    [InlineData(0.6, 0.0)]
    [InlineData(-0.6, 0.4)]
    [InlineData(0.6, -0.4)]
    public void a_mark_with_no_recorded_size_keeps_the_bounds_it_was_given(
        double boxWidth, double boxHeight)
    {
        // Text boxes, stamps, drawings, and shapes written before the tag
        // recorded a size. None of them may be re-framed on a guess: the
        // bounds as given are what the overlay has always drawn.
        var frame = SelectionFrame.Upright(0.3, 0.05, 0.7, 0.65, boxWidth, boxHeight);

        AssertRect(frame, 0.3, 0.05, 0.7, 0.65);
    }
}
