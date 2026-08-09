using System;
using System.Collections.Generic;
using System.Linq;

namespace PdfEditorApp.Viewport;

/// <summary>One pen stroke of a signature, in the signature's own 0..1 box.</summary>
/// <param name="ColorHex">AARRGGBB, the order ParseHex reads eight characters in.</param>
/// <param name="WidthNorm">Stroke width, as a fraction of the signature's WIDTH.</param>
/// <param name="Points">Thinned control points; the curve is re-fitted on use.</param>
public sealed record SignatureStroke(string ColorHex, double WidthNorm, IReadOnlyList<(double X, double Y)> Points);

/// <summary>
/// A saved signature: several strokes, stored independently of any page.
///
/// Vector, not a picture. A signature is drawn once and then placed at whatever
/// size a form needs, so rasterising it would mean either a blurry stamp or a
/// huge one. Storing control points means every placement is re-drawn at full
/// quality, and the placed strokes are ordinary ink annotations afterwards:
/// selectable, movable, resizable, and removable like anything else.
///
/// Points are normalised into a 0..1 box so the same signature can be dropped
/// into a small initials field or across a whole page.
/// </summary>
public sealed record SignatureShape(string Name, IReadOnlyList<SignatureStroke> Strokes)
{
    /// <summary>
    /// Height relative to width, taken from the drawn strokes.
    ///
    /// Kept so a placement can preserve the shape. A signature squeezed to a
    /// square is not the user's signature any more, which is the one thing this
    /// feature cannot get wrong.
    /// </summary>
    public double AspectRatio { get; init; } = 1;

    /// <summary>
    /// Fits raw strokes into a 0..1 box, preserving their proportions, and
    /// records the aspect ratio that produced them.
    ///
    /// The longer side becomes 0..1 and the shorter one is centred, so a wide
    /// signature does not get stretched tall when it is placed.
    /// </summary>
    public static SignatureShape FromDrawn(
        string name, IReadOnlyList<(string ColorHex, double WidthNorm, IReadOnlyList<(double X, double Y)> Points)> strokes)
    {
        var all = strokes.SelectMany(s => s.Points).ToList();
        if (all.Count == 0)
        {
            return new SignatureShape(name, Array.Empty<SignatureStroke>());
        }

        double minX = all.Min(p => p.X), maxX = all.Max(p => p.X);
        double minY = all.Min(p => p.Y), maxY = all.Max(p => p.Y);
        double w = Math.Max(maxX - minX, 1e-9);
        double h = Math.Max(maxY - minY, 1e-9);

        // Divide BOTH axes by the same number, or the proportions change.
        double scale = 1.0 / Math.Max(w, h);
        double offsetX = (1.0 - w * scale) / 2;
        double offsetY = (1.0 - h * scale) / 2;

        var norm = strokes.Select(s => new SignatureStroke(
            s.ColorHex,
            s.WidthNorm * scale,
            s.Points.Select(p => (
                X: (p.X - minX) * scale + offsetX,
                Y: (p.Y - minY) * scale + offsetY)).ToList())).ToList();

        return new SignatureShape(name, norm) { AspectRatio = h / w };
    }

    /// <summary>
    /// The strokes placed into a page rectangle, in normalized page units.
    ///
    /// The rectangle is treated as a bounding box to fit inside, not to fill:
    /// the signature keeps its proportions and is centred in whatever is left
    /// over.
    /// </summary>
    public List<SignatureStroke> PlaceInto(double left, double top, double right, double bottom)
    {
        double boxW = Math.Max(right - left, 1e-9);
        double boxH = Math.Max(bottom - top, 1e-9);
        double scale = Math.Min(boxW, boxH);
        double padX = (boxW - scale) / 2;
        double padY = (boxH - scale) / 2;

        return Strokes.Select(s => new SignatureStroke(
            s.ColorHex,
            // Width follows the same scale, so a signature placed small has a
            // proportionally fine line rather than a marker-thick one.
            s.WidthNorm * scale,
            s.Points.Select(p => (
                X: left + padX + p.X * scale,
                Y: top + padY + p.Y * scale)).ToList())).ToList();
    }

    /// <summary>
    /// The box to place this signature in, given a click point and a width.
    /// Height follows the aspect ratio, and the click is the CENTRE, which is
    /// where a pointer feels like it is placing something.
    /// </summary>
    public (double Left, double Top, double Right, double Bottom) BoxAt(
        double centreX, double centreY, double width)
    {
        double height = width * AspectRatio;
        return (centreX - width / 2, centreY - height / 2,
                centreX + width / 2, centreY + height / 2);
    }
}
