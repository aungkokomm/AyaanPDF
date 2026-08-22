using System;

namespace PdfEditorApp.Viewport;

/// <summary>
/// The room a shape's effects need around its own box: four one-sided amounts,
/// each at least zero, in normalized page units with y running DOWN.
/// </summary>
public readonly record struct EffectsRoom(double Left, double Top, double Right, double Bottom)
{
    public static EffectsRoom None => default;

    public bool IsEmpty => this == default;
}

/// <summary>
/// How much bigger than the shape its annotation's rectangle is, because of the
/// effects.
///
/// THE C# HALF OF <c>effects_room_pts</c>, and it exists because the two halves
/// were not both there. render_core grows /Rect by this room so PDFium does not
/// crop a shadow, and its restyle takes the same four numbers back off before
/// reading the shape's own extent. The model did not, so every reader of
/// <see cref="DocumentObject.UprightBounds"/> saw a shape as big as its shadow.
///
/// That was invisible until a gradient was put on a shadowed shape: the Skia
/// overlay draws the gradient at the model's upright box, so the painted fill
/// grew with every touch of a shadow slider, and being opaque it covered the
/// very shadow that had grown it. Two reported symptoms, one missing
/// subtraction.
///
/// THE WIDEST, NOT THE SUM, exactly as render_core has it: every effect is
/// drawn from the same silhouette into the same picture, so each side opens by
/// the furthest any one effect reaches on it. Adding them would grow the shape
/// on every edit.
/// </summary>
public static class ShapeEffectsRoom
{
    /// <summary>
    /// The room a tag's effects text asks for, in normalized page units.
    ///
    /// READ FROM THE TAG'S FIELDS rather than from a parsed
    /// <see cref="ShapeEffects"/>, so that a field written by a LATER build is
    /// counted. render_core reserves room for one it cannot name, and a model
    /// that skipped it would inset by less than the writer added and hand back
    /// a box bigger than the shape. The fill's own field asks for nothing,
    /// because it carries no blur and no distance, so it needs no special case
    /// here any more than it needs one there.
    /// </summary>
    public static EffectsRoom Of(string? effectsText, double pageWidthPts)
    {
        if (pageWidthPts <= 0 || string.IsNullOrEmpty(effectsText))
        {
            return EffectsRoom.None;
        }

        double left = 0, top = 0, right = 0, bottom = 0;

        foreach (var field in ShapeTagReader.EffectsIn(effectsText))
        {
            // A field with no colour paints nothing, so it is not an effect and
            // reserves nothing. The same bargain render_core's parse makes.
            if (field.Hex is null)
            {
                continue;
            }

            double blur = field.BlurPts / pageWidthPts;
            double distance = field.DistancePts / pageWidthPts;

            // Softness is the RADIUS and sigma is half of it, and a gaussian is
            // spent by three sigma. Through EffectSpec so there is one
            // statement of the reach and not a second copy of the arithmetic.
            var spec = new EffectSpec(
                EffectKind.DropShadow, default, Blur: blur, Distance: distance, AngleDeg: field.AngleDeg);

            double reach = spec.Reach;
            double dx = spec.OffsetX;

            // y DOWN here, y UP in render_core, which is why its top and bottom
            // read as the opposite sign. Both derive from the same angle table.
            double dy = spec.OffsetY;

            left = Math.Max(left, reach + Math.Max(-dx, 0));
            right = Math.Max(right, reach + Math.Max(dx, 0));
            top = Math.Max(top, reach + Math.Max(-dy, 0));
            bottom = Math.Max(bottom, reach + Math.Max(dy, 0));
        }

        return new EffectsRoom(left, top, right, bottom);
    }

    /// <summary>
    /// A rectangle with the room taken back off, which is the exact inverse of
    /// the growth render_core applied.
    ///
    /// NEVER PAST NOTHING. A tag can carry a blur far larger than the shape it
    /// is on, and insetting by more than the box has would turn it inside out;
    /// a rectangle whose right is left of its left is not a smaller shape, it
    /// is a broken one, and it would take hit-testing and the renderer with it.
    /// </summary>
    public static TextRect TakenOff(TextRect bounds, EffectsRoom room)
    {
        if (room.IsEmpty)
        {
            return bounds;
        }

        double left = bounds.Left + room.Left;
        double right = bounds.Right - room.Right;
        double top = bounds.Top + room.Top;
        double bottom = bounds.Bottom - room.Bottom;

        if (right <= left)
        {
            double middle = (bounds.Left + bounds.Right) / 2;
            (left, right) = (middle, middle);
        }

        if (bottom <= top)
        {
            double middle = (bounds.Top + bounds.Bottom) / 2;
            (top, bottom) = (middle, middle);
        }

        return new TextRect(left, top, right, bottom);
    }
}
