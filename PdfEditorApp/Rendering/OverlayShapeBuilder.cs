using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using PdfEditorApp.Viewport;
using Windows.Foundation;
using Windows.UI;

namespace PdfEditorApp.Rendering;

/// <summary>
/// How the reference renderer turns a mark into a XAML element.
///
/// Lifted out of MainPage unchanged, for one reason: the parity harness has to
/// measure the ACTUAL reference renderer. It used to build its own polyline
/// "the way the overlay builds one", which is a claim, not a measurement, and a
/// harness that reimplements the thing it is checking can agree with the
/// candidate while both differ from what is on screen. That is precisely the
/// failure stage 4 exists to catch, so there is now one construction and two
/// callers.
///
/// The view model is NOT read here. A page's scale, its top in the stack and
/// its transform arrive as parameters, because MainPage resolves them from the
/// view model and the harness supplies its own. That parameter list is the only
/// structural difference from the private methods this replaces; every line of
/// arithmetic is the same one, in the same order.
///
/// It builds PREVIEW geometry. Committed marks are real PDF annotations that
/// PDFium draws into the page, and nothing here is a second representation of
/// one.
/// </summary>
internal static class OverlayShapeBuilder
{
    /// <summary>
    /// Expands a stroke's normalized points into slot space and offsets them
    /// by its page's position in the stack, so the ink lands on the right page
    /// of the continuous view.
    /// </summary>
    public static Polyline Stroke(
        InkStrokeAnnotation stroke, double scale, double pageTop, PageTransform view)
    {
        var polyline = new Polyline
        {
            Stroke = new SolidColorBrush(ColorFromHex(stroke.ColorHex)),
            // view.Scale as well as the overlay scale: a page turned sideways is
            // scaled to bring its other axis to the card's width, so a mark on
            // it is a different weight as well as in a different place.
            StrokeThickness = stroke.StrokeWidth * scale * view.Scale,
        };

        foreach (var (x, y) in stroke.Points)
        {
            var (cx, cy) = view.ToCard(x * scale, y * scale);
            polyline.Points.Add(new Point(cx, cy + pageTop));
        }

        return polyline;
    }

    /// <summary>
    /// An arrow's head, as a filled triangle. A stroked outline is not the same
    /// shape and would not match what goes into the file.
    /// </summary>
    public static Polygon FilledHead(
        IReadOnlyList<(double X, double Y)> points, string colorHex,
        double scale, double pageTop, PageTransform view)
    {
        var brush = new SolidColorBrush(ColorFromHex(colorHex));
        var polygon = new Polygon { Fill = brush, Stroke = brush, StrokeThickness = 0.5 };

        foreach (var (x, y) in points)
        {
            var (cx, cy) = view.ToCard(x * scale, y * scale);
            polygon.Points.Add(new Point(cx, cy + pageTop));
        }

        return polygon;
    }

    public static Color ColorFromHex(string hex)
    {
        hex = hex.TrimStart('#');
        byte a = Convert.ToByte(hex.Substring(0, 2), 16);
        byte r = Convert.ToByte(hex.Substring(2, 2), 16);
        byte g = Convert.ToByte(hex.Substring(4, 2), 16);
        byte b = Convert.ToByte(hex.Substring(6, 2), 16);
        return Color.FromArgb(a, r, g, b);
    }
}
