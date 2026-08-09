using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PdfEditorApp.Viewport;

/// <summary>
/// A freehand stroke, described well enough to rebuild it.
///
/// Every other object this app writes carries a tag that fully describes it,
/// which is what lets undo delete a mark and redo rebuild it without copying
/// the whole PDF, and what lets a resize re-draw the object at a new size
/// instead of stretching a bitmap. Ink had no such description: the points
/// went into the PDF's own /InkList and nothing else, so a drawing could not
/// be recreated, and therefore could not be undone cheaply or resized at all.
///
/// Format: <c>AyaanInk:RRGGBBAA:width:x,y;x,y;...</c>
///
/// The points stored are the THINNED control points, not the fitted curve.
/// The fit is deterministic, so running <see cref="StrokeSmoothing.Fit"/> over
/// these reproduces exactly the curve that was drawn, and the tag stays a few
/// hundred bytes instead of several kilobytes.
///
/// All values are normalized page units, so a stroke survives a page being
/// re-rendered at any size. See [[the shape tag]] in ShapeTagReader for the
/// same idea applied to rectangles and arrows.
/// </summary>
public static class InkTag
{
    public const string Prefix = "AyaanInk:";

    /// <summary>Four decimals is a tenth of a pixel on a 900px-wide render.</summary>
    private const string Format = "0.####";

    public static string Write(string colorRgba, double strokeWidth, IReadOnlyList<(double X, double Y)> control)
    {
        var sb = new StringBuilder(Prefix);
        sb.Append(colorRgba).Append(':');
        sb.Append(strokeWidth.ToString(Format, CultureInfo.InvariantCulture)).Append(':');
        for (int i = 0; i < control.Count; i++)
        {
            if (i > 0) { sb.Append(';'); }
            sb.Append(control[i].X.ToString(Format, CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append(control[i].Y.ToString(Format, CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Parses a stroke tag. Returns false for anything that is not one, which
    /// includes a hand-edited PDF and any annotation this app did not write.
    /// </summary>
    public static bool TryParse(
        string? tag,
        out string colorRgba,
        out double strokeWidth,
        out List<(double X, double Y)> control)
    {
        colorRgba = "000000FF";
        strokeWidth = 0;
        control = new List<(double X, double Y)>();

        // The ID prefix is stripped first, exactly as every other tag parser
        // does. Forgetting that is what silently broke every shape for two
        // weeks once identity was added.
        string body = ShapeTagReader.StripIdPrefix(tag ?? string.Empty);
        if (!body.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        // Split into exactly three fields: the point list contains no colons,
        // so a plain split is safe and stays safe as points are added.
        string[] parts = body.Substring(Prefix.Length).Split(':');
        if (parts.Length < 3)
        {
            return false;
        }

        colorRgba = parts[0];
        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out strokeWidth))
        {
            return false;
        }

        foreach (string pair in parts[2].Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int comma = pair.IndexOf(',');
            if (comma <= 0) { return false; }
            if (!double.TryParse(pair.AsSpan(0, comma), NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                || !double.TryParse(pair.AsSpan(comma + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
            {
                return false;
            }
            control.Add((x, y));
        }

        return control.Count >= 2;
    }

    /// <summary>
    /// The control points mapped from their own bounding box onto
    /// <paramref name="target"/>. This is what makes a drawing resizable: the
    /// stroke is re-drawn at the new size from its description, rather than a
    /// rendered bitmap being stretched.
    ///
    /// A stroke drawn as a straight horizontal or vertical line has a
    /// zero-height or zero-width box; scaling by it would divide by zero, so
    /// that axis is centred in the target instead.
    /// </summary>
    public static List<(double X, double Y)> ScaleTo(
        IReadOnlyList<(double X, double Y)> control,
        double left, double top, double right, double bottom)
    {
        var scaled = new List<(double X, double Y)>(control.Count);
        if (control.Count == 0)
        {
            return scaled;
        }

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (x, y) in control)
        {
            if (x < minX) { minX = x; }
            if (y < minY) { minY = y; }
            if (x > maxX) { maxX = x; }
            if (y > maxY) { maxY = y; }
        }

        double srcW = maxX - minX;
        double srcH = maxY - minY;
        double dstW = right - left;
        double dstH = bottom - top;

        foreach (var (x, y) in control)
        {
            double nx = srcW > 1e-9 ? left + (x - minX) / srcW * dstW : left + dstW / 2;
            double ny = srcH > 1e-9 ? top + (y - minY) / srcH * dstH : top + dstH / 2;
            scaled.Add((nx, ny));
        }
        return scaled;
    }

    /// <summary>The bounding box of a point list, in the same units.</summary>
    public static (double Left, double Top, double Right, double Bottom) Bounds(
        IReadOnlyList<(double X, double Y)> pts)
    {
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (x, y) in pts)
        {
            if (x < minX) { minX = x; }
            if (y < minY) { minY = y; }
            if (x > maxX) { maxX = x; }
            if (y > maxY) { maxY = y; }
        }
        return pts.Count == 0 ? (0, 0, 0, 0) : (minX, minY, maxX, maxY);
    }
}
