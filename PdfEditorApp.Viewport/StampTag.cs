using System;
using System.Globalization;

namespace PdfEditorApp.Viewport;

/// <summary>
/// What one of our image stamps records in its <c>/Contents</c>, decoded.
///
/// <paramref name="RotationDeg"/> is clockwise on screen, the same convention
/// every other rotation in this app uses.
///
/// The rectangle is the stamp's own UPRIGHT box, NORMALIZED here even though the
/// tag stores it in capture-space pixels. Normalizing at the reader means one
/// conversion in one place; leaving it raw is how a factor of a thousand ends up
/// in a hit test.
///
/// The upright box exists for the same reason a rotated shape's does: a turned
/// stamp's /Rect is the axis-aligned box that CONTAINS the turned image, which
/// is larger than the stamp in both axes and cannot be inverted. The tag keeps
/// the original so a second rotation is measured from the box the stamp was
/// placed in rather than compounding on the enlarged one.
/// </summary>
public readonly record struct StampTag(
    double RotationDeg, double Left, double Top, double Right, double Bottom)
{
    public TextRect Bounds => new(Left, Top, Right, Bottom);
}

/// <summary>
/// Reads the tag an image stamp stores, the C# side of <c>parse_stamp_tag</c> in
/// render_core.
///
/// Format: <c>AyaanStamp:&lt;deg&gt;:&lt;l&gt;:&lt;t&gt;:&lt;r&gt;:&lt;b&gt;</c>,
/// with the rectangle in capture-space pixels.
///
/// Stamps carried no tag at all before rotation existed, because nothing needed
/// recovering from one: the image lives in the annotation. Such a stamp is
/// upright by definition and simply does not parse here, which is the same
/// answer the Rust reader gives.
/// </summary>
public static class StampTagReader
{
    public const string Prefix = "AyaanStamp:";

    /// <summary>
    /// The capture width the rectangle is stored in, matching every writer in
    /// the app. Dividing by it recovers the normalized units the annotation
    /// layer works in.
    /// </summary>
    public const double CaptureWidth = 1000.0;

    /// <summary>True if this /Contents is one of our stamps at all, with or
    /// without an identity prefix in front of it.</summary>
    public static bool IsStampTag(string? contents) =>
        ShapeTagReader.StripIdPrefix(contents) is string s
        && s.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// Decodes a stamp tag, or returns false for anything that is not one.
    ///
    /// Every field is required: unlike the shape tag, this one has never had a
    /// shorter form, so a truncated or unparseable tag is damage rather than an
    /// older version. Returning false leaves the caller on the annotation's own
    /// /Rect, which is right for an upright stamp and no worse than today for a
    /// turned one.
    /// </summary>
    public static bool TryParse(string? contents, out StampTag tag)
    {
        tag = default;

        // Stripped first, exactly as every other tag reader in this app does.
        // A reader that only works on pre-stripped input is a reader waiting to
        // be handed raw input.
        string? body = ShapeTagReader.StripIdPrefix(contents);
        if (body is null || !body.StartsWith(Prefix, StringComparison.Ordinal)) { return false; }

        string[] parts = body.Substring(Prefix.Length).Split(':');
        if (parts.Length < 5) { return false; }

        if (!Number(parts[0], out double deg)
            || !Number(parts[1], out double left)
            || !Number(parts[2], out double top)
            || !Number(parts[3], out double right)
            || !Number(parts[4], out double bottom))
        {
            return false;
        }

        tag = new StampTag(
            deg,
            left / CaptureWidth,
            top / CaptureWidth,
            right / CaptureWidth,
            bottom / CaptureWidth);
        return true;
    }

    private static bool Number(string s, out double value) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        && double.IsFinite(value);
}
