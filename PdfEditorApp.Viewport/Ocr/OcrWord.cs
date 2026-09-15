namespace PdfEditorApp.Viewport;

/// <summary>
/// One word a recogniser found on a page, in the form the core writes it back.
/// </summary>
/// <remarks>
/// The box is a fraction of the page AS RENDERED: 0..1 across and down from the
/// top-left corner, in the orientation the page is displayed in. That is what a
/// recogniser sees, and render_core's add_ocr_words maps it back to page space
/// through PDFium, so rotation and the crop box need no handling here.
/// </remarks>
/// <param name="Confidence">0..100 when the recogniser gives one; NaN when it does not.</param>
public readonly record struct OcrWord(
    string Text, double Left, double Top, double Right, double Bottom, double Confidence = double.NaN)
{
    public double Height => Bottom - Top;
}
