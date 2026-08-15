using System;

namespace PdfEditorApp.Viewport;

/// <summary>
/// Turns a rendered page dark for night reading.
///
/// Applied to the PIXELS rather than to the document: nothing is written back,
/// and turning it off re-renders the page exactly as it was. It also has to be
/// the pixels rather than a XAML effect, because annotations are separate
/// layers drawn on top and inverting the whole card would turn a yellow
/// highlight blue.
///
/// Not a plain 255-minus-c. That maps a white page to pure black, which loses
/// the page's edge against the canvas entirely and is harsh on an OLED panel.
/// The result is lifted off black and pulled down off white, so a page still
/// reads as a sheet lying on a surface.
/// </summary>
public static class NightMode
{
    /// <summary>
    /// Darkest a pixel becomes. A white page lands here rather than at 0, so
    /// the sheet is still distinguishable from the canvas behind it.
    /// </summary>
    public const byte Floor = 0x12;

    /// <summary>
    /// Brightest a pixel becomes. Black text lands here rather than at 255,
    /// because pure white text on near-black is the combination that smears
    /// for most readers.
    /// </summary>
    public const byte Ceiling = 0xEB;

    /// <summary>
    /// Inverts one channel into the reading range.
    ///
    /// Monotonically decreasing, so ordering is preserved: anything darker
    /// than its neighbour before is lighter than it after, which is what makes
    /// the result read as the same page rather than a different one.
    /// </summary>
    public static byte Channel(byte value) =>
        (byte)(Floor + (((255 - value) * (Ceiling - Floor)) / 255));

    /// <summary>
    /// Applies the transform to a BGRA buffer, in place.
    /// </summary>
    /// <remarks>
    /// Alpha is left exactly as it was. A page render is opaque, but a tile at
    /// the edge of a page is not, and rewriting alpha would put a dark rectangle
    /// where the page's corner should be transparent.
    /// </remarks>
    public static void Apply(byte[] bgra)
    {
        ArgumentNullException.ThrowIfNull(bgra);

        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            bgra[i] = Channel(bgra[i]);
            bgra[i + 1] = Channel(bgra[i + 1]);
            bgra[i + 2] = Channel(bgra[i + 2]);
        }
    }
}
