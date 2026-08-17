using System;

namespace PdfEditorApp.Viewport;

/// <summary>
/// How a page's content is turned to sit inside its card, for view rotation.
///
/// View rotation turns what is on screen and nothing else. It never touches
/// the document, so it costs no page parsing, sets no dirty flag, records no
/// undo step, and survives being applied to a file that cannot be written to.
/// The app already has document rotation, which is the other thing: it edits
/// /Rotate, must be saved, and on a three-thousand page book would have to
/// parse every page to do it.
///
/// The trick that makes this cheap is leaving the CONTENT BOX alone. Every
/// overlay in the app, and every rect the view model computes, is in slot DIPs
/// against a fixed 800-wide content box: OverlayScale is that constant, and
/// Norm divides by it. Rotating the content box would have invalidated all of
/// it. So the content box keeps its size and its coordinates, and is rotated
/// and scaled as a whole into a card that is still exactly the layout width.
/// Nothing downstream of a page's coordinates has to know this happened; only
/// the three places that map between card space and content space do.
///
/// Scaling as well as rotating is what keeps the card at the layout width. A
/// portrait page turned on its side is taller than the layout is wide, so
/// without the scale the stack would have pages of two different widths and
/// fit-width, which is a pure function of the one fixed layout width, would
/// stop being able to answer.
/// </summary>
public readonly record struct PageTransform(
    double CardWidth,
    double CardHeight,
    double ContentWidth,
    double ContentHeight,
    double Scale,
    int Rotation,
    double TranslateX,
    double TranslateY)
{
    /// <summary>The four rotations a view can be in, in degrees.</summary>
    public const int Quarter = 90;

    /// <summary>
    /// Brings any rotation back into 0, 90, 180 or 270.
    ///
    /// Takes negatives, because turning anticlockwise from 0 is the obvious way
    /// to reach 270 and C# leaves -90 % 360 as -90.
    /// </summary>
    public static int Normalize(int degrees)
    {
        int r = degrees % 360;
        if (r < 0)
        {
            r += 360;
        }

        // Anything that is not a quarter turn is treated as no turn: the value
        // reaches here from settings and from arithmetic, and a page shown at
        // 37 degrees is never what was meant.
        return r is Quarter or 180 or 270 ? r : 0;
    }

    /// <summary>Whether this rotation exchanges width for height.</summary>
    public static bool Swaps(int rotation) => Normalize(rotation) is Quarter or 270;

    /// <summary>
    /// Works out the card and the transform that puts a content box in it.
    ///
    /// The transform is expressed the way CompositeTransform applies it, scale
    /// then rotate then translate, so the four numbers can be bound straight
    /// onto one and no TransformGroup ordering has to be reasoned about in
    /// markup.
    /// </summary>
    public static PageTransform For(double contentWidth, double contentHeight, int rotation, double cardWidth)
    {
        int rot = Normalize(rotation);
        double w = Math.Max(1.0, contentWidth);
        double h = Math.Max(1.0, contentHeight);
        double card = Math.Max(1.0, cardWidth);

        // The content's extent along the card's x axis once turned, which is
        // what has to be brought to the card's width.
        double acrossX = Swaps(rot) ? h : w;
        double scale = card / acrossX;
        double cardHeight = scale * (Swaps(rot) ? w : h);

        // Rotating about the origin takes the box off into negative space. The
        // translation is exactly the amount that brings its corner back.
        var (tx, ty) = rot switch
        {
            Quarter => (scale * h, 0.0),
            180 => (scale * w, scale * h),
            270 => (0.0, scale * w),
            _ => (0.0, 0.0),
        };

        return new PageTransform(card, cardHeight, w, h, scale, rot, tx, ty);
    }

    /// <summary>
    /// Turns a point on the page into a point on the card.
    ///
    /// What the markup does to the page's own content, expressed as arithmetic
    /// so that something drawn OVER a page can be put in the same place the
    /// page put it. <see cref="ToContent"/> has always been here because hit
    /// testing needs to undo the turn; this is the direction needed to draw
    /// with it.
    ///
    /// Scale is included, so a mark comes back in card units. A length has to
    /// be multiplied by <see cref="Scale"/> separately: a stroke on a page
    /// turned sideways is not only somewhere else, it is a different weight,
    /// because the content box is scaled to bring its other axis to the card's
    /// width.
    /// </summary>
    public (double X, double Y) ToCard(double contentX, double contentY)
    {
        double s = Scale > 0 ? Scale : 1.0;

        return Rotation switch
        {
            Quarter => (s * (ContentHeight - contentY), s * contentX),
            180 => (s * (ContentWidth - contentX), s * (ContentHeight - contentY)),
            270 => (s * contentY, s * (ContentWidth - contentX)),
            _ => (s * contentX, s * contentY),
        };
    }

    /// <summary>
    /// Turns a point on the card back into a point on the page.
    ///
    /// The inverse of what the markup does, and the only reason hit testing
    /// keeps working while the view is turned. Everything above the one call
    /// site goes on receiving page coordinates and cannot tell the difference.
    /// </summary>
    public (double X, double Y) ToContent(double cardX, double cardY)
    {
        double s = Scale > 0 ? Scale : 1.0;
        double x = cardX / s;
        double y = cardY / s;

        return Rotation switch
        {
            Quarter => (y, ContentHeight - x),
            180 => (ContentWidth - x, ContentHeight - y),
            270 => (ContentWidth - y, x),
            _ => (x, y),
        };
    }

    /// <summary>
    /// The region of the page covered by a rectangle of the card.
    ///
    /// Used to ask the tile pyramid for the tiles actually on screen. Rotations
    /// are quarter turns, so mapping the corners and taking their bounds is
    /// exact rather than the usual over-estimate.
    /// </summary>
    public (double Left, double Top, double Right, double Bottom) ContentBounds(
        double cardLeft, double cardTop, double cardRight, double cardBottom)
    {
        var a = ToContent(cardLeft, cardTop);
        var b = ToContent(cardRight, cardBottom);

        return (Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
    }

    /// <summary>
    /// How big the content is drawn compared with its own coordinates, which is
    /// what decides how many pixels to render.
    ///
    /// A page turned on its side is scaled down to fit the card, so asking for
    /// the resolution its unturned width would need produces a render sharper
    /// than the screen can show, and on the way back up, blurrier. The render
    /// budget takes a zoom, so the scale folds into the zoom it is given.
    /// </summary>
    public double EffectiveZoom(double zoomFactor) => zoomFactor * Scale;
}
