using System.Globalization;

namespace PdfEditorApp.Viewport;

/// <summary>
/// Effects across the boundary between the object model and the file.
///
/// Two conversions, kept together because they are one contract read from both
/// ends: what goes into a shape's tag, and what comes back out of it. Split
/// across the view model and the reader they would drift, and a shadow that
/// saves as one colour and loads as another is the kind of thing nobody notices
/// until a file has been through several rounds.
///
/// THE TWO SPACES. The object model measures in NORMALIZED page-local units,
/// where a length is a fraction of the page's width, because that is what keeps
/// a mark the same size on any page at any zoom. The tag measures in POINTS,
/// because that is what a PDF is written in and what every other length in the
/// tag already uses. The page width is what converts between them, and it is
/// the caller's to supply: a tag does not carry one.
///
/// ZERO MEANS NO SHADOW, decided by the COLOUR and not by the offsets. A shadow
/// directly under its shape, offset by nothing, is a legitimate thing to ask
/// for; a shadow in no colour is not. The same rule the fill already uses.
/// </summary>
public static class ShapeEffectsTag
{
    /// <summary>
    /// The shadow's colour as the tag and the core carry it, 0xAARRGGBB, or 0
    /// when there is no shadow to draw.
    /// </summary>
    public static uint RgbaOf(ShapeEffects? effects)
    {
        if (effects?.Shadow is not { } shadow)
        {
            return 0;
        }

        var c = shadow.Color;

        // Fully transparent is not a shadow. Returning its channels would set
        // the "there is a shadow" flag for something that paints nothing, and
        // the shape would then take the longer tag rung for no reason.
        if (c.A == 0)
        {
            return 0;
        }

        return ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
    }

    /// <summary>
    /// The effects a loaded shape has, or null when its tag records none.
    ///
    /// Null rather than an empty <see cref="ShapeEffects"/>, so a shape read
    /// from a file that predates effects is indistinguishable from one that has
    /// none, which it is.
    /// </summary>
    /// <param name="pageWidthPts">
    /// The width of the page the shape is on, in points. What converts the
    /// tag's lengths back into the normalized units the object model works in.
    /// </param>
    public static ShapeEffects? From(ShapeTag tag, double pageWidthPts)
    {
        if (tag.ShadowHex is null || pageWidthPts <= 0)
        {
            return null;
        }

        var (a, r, g, b) = InkPresets.ParseHex(tag.ShadowHex);

        return new ShapeEffects(new DropShadow(
            tag.ShadowDxPts / pageWidthPts,
            tag.ShadowDyPts / pageWidthPts,
            new RenderColor(a, r, g, b)));
    }

    /// <summary>
    /// The colour as the tag spells it, "#AARRGGBB", or null for no shadow.
    /// The inverse of the hex half of <see cref="From"/>, for the tests that
    /// have to state a round trip without going through the core.
    /// </summary>
    public static string? HexOf(ShapeEffects? effects)
    {
        uint rgba = RgbaOf(effects);

        return rgba == 0
            ? null
            : "#" + rgba.ToString("X8", CultureInfo.InvariantCulture);
    }
}
