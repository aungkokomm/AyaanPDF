using System;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Svg;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PdfEditorApp.Viewport;
using Windows.Foundation;
using Windows.UI;

namespace PdfEditorApp;

/// <summary>
/// Draws a built-in stamp into the pixels render_core already accepts.
///
/// Rasterised HERE, at placement time and at the size being placed, rather than
/// shipped as images: a PNG in the installer is soft by 8x zoom, is one fixed
/// colour, and can never print today's date. Everything that decides WHAT to
/// draw is in BuiltInStamps, where it is tested; this file is only the paint.
///
/// The output is an ordinary BGRA buffer, so a built-in stamp becomes exactly
/// the same image annotation a user's own PNG becomes, and move, resize,
/// rotate, copy, delete, undo and z-order all work on it without knowing.
/// </summary>
internal static class StampRenderer
{
    /// <summary>
    /// How wide a stamp is drawn, in pixels.
    ///
    /// Generous on purpose. The placed size is a quarter of the page, and a
    /// page is rendered far larger than its own points at high zoom, so this
    /// buys headroom for the zoom rather than for the page. Above this the
    /// buffer costs more than the sharpness is worth.
    /// </summary>
    private const int RenderWidth = 1200;

    /// <summary>
    /// Where the bundled font lives: an ABSOLUTE PATH, then #FamilyName.
    ///
    /// Not ms-appx. The title bar's icon loads that way, so it looked like the
    /// obvious choice, but Win2D throws ArgumentException on an ms-appx font
    /// URI in an unpackaged app, which is what this app is. Measured.
    ///
    /// Not a bare family name either, which is the trap: "Oswald" on its own
    /// does not throw, it silently resolves to whatever DirectWrite substitutes
    /// and every stamp comes out in the wrong typeface. The probe that found
    /// this measured "APPROVED" at 235.7 for a bare family and for Segoe UI,
    /// against 212.3 for the real file. Identical numbers were the tell.
    /// </summary>
    private static string FontUri(StampTheme theme) =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", theme.FontFile)
        + "#" + theme.FontFamily;

    /// <summary>
    /// The box text is measured in.
    ///
    /// Deliberately large rather than zero. Zero looks like "no constraint" and
    /// is not: the same string measured 28.4 wide at zero and 212.3 at 4096, so
    /// a zero here fed nonsense into the shrink-to-fit and every long label
    /// would have come out at full size and overflowed its frame.
    /// </summary>
    private const float MeasureBound = 4096;

    /// <summary>
    /// Draws a stamp, or returns null if anything goes wrong.
    ///
    /// Null rather than throwing, matching StampLibrary.DecodeAsync: a stamp
    /// that cannot be drawn is a status message, never a crash in the middle of
    /// someone's document.
    /// </summary>
    public static StampPixels? Render(BuiltInStamp stamp, StampTheme theme, CultureInfo culture)
    {
        var raw = Rasterise(stamp, theme, culture, RenderWidth);
        if (raw is null)
        {
            return null;
        }

        // Win2D hands back premultiplied alpha; PDFium's soft mask wants it
        // straight. Skipping this fringes every antialiased letter edge.
        StampAlpha.Unpremultiply(raw.Bgra);
        return raw;
    }

    /// <summary>
    /// A preview for the picker.
    ///
    /// Drawn by the SAME code that draws the real thing, so a tile cannot show
    /// something the page will not. It keeps the premultiplied pixels, because
    /// that is what WriteableBitmap wants, which is the opposite of what PDFium
    /// wants and the reason the two callers split here rather than earlier.
    /// </summary>
    public static ImageSource? Thumbnail(BuiltInStamp stamp, StampTheme theme, int width)
    {
        try
        {
            var raw = Rasterise(stamp, theme, CultureInfo.CurrentCulture, width);
            if (raw is null)
            {
                return null;
            }

            var bitmap = new WriteableBitmap(raw.Width, raw.Height);
            using (var stream = bitmap.PixelBuffer.AsStream())
            {
                stream.Write(raw.Bgra, 0, raw.Bgra.Length);
            }

            return bitmap;
        }
        catch (Exception ex)
        {
            // A tile that cannot be drawn must not take the picker down with
            // it, the same way a bad PNG does not.
            Diag.Log($"stamp thumbnail failed for {stamp.Id}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Draws every built-in onto one sheet and saves it, for PDFEDITOR_STAMPSHEET.
    ///
    /// A diagnostic in the same family as PDFEDITOR_AUTOSTAMP and
    /// PDFEDITOR_AUTOREORDER, and kept for the same reason: it is the only way
    /// to LOOK at what this file produces without driving the app by hand. A
    /// green build says nothing about whether the font resolved, and the way it
    /// fails is silent substitution rather than an error. This is what caught
    /// the ms-appx URI and the zero-width measurement.
    /// </summary>
    public static async System.Threading.Tasks.Task DumpContactSheet(
        string path, StampTheme theme, CultureInfo culture)
    {
        var device = CanvasDevice.GetSharedDevice();

        const int Cols = 2;
        const int CellW = 480;
        const int CellH = 150;
        int rows = (BuiltInStamps.All.Count + Cols - 1) / Cols;

        using var sheet = new CanvasRenderTarget(device, Cols * CellW, rows * CellH, 96);
        using (var ds = sheet.CreateDrawingSession())
        {
            ds.Clear(Microsoft.UI.Colors.White);

            for (int i = 0; i < BuiltInStamps.All.Count; i++)
            {
                var stamp = BuiltInStamps.All[i];

                // A mark is square, so at the word stamps' width it would be
                // three cells tall. Sized to the cell instead, which is the
                // sheet's problem rather than the stamp's.
                var metrics = BuiltInStamps.Measure(
                    stamp, stamp.Style == StampStyle.Mark ? 110 : 400, theme);

                double cx = (i % Cols) * CellW;
                double cy = (i / Cols) * CellH;
                var box = new Rect(
                    cx + ((CellW - metrics.Width) / 2),
                    cy + ((CellH - metrics.Height) / 2),
                    metrics.Width,
                    metrics.Height);

                ds.Transform = stamp.Diagonal
                    ? Matrix3x2.CreateRotation(
                        (float)(theme.DiagonalDegrees * Math.PI / 180.0),
                        new Vector2((float)(cx + (CellW / 2.0)), (float)(cy + (CellH / 2.0))))
                    : Matrix3x2.Identity;

                Draw(ds, device, stamp, theme, metrics, box, culture);
            }
        }

        await sheet.SaveAsync(path, CanvasBitmapFileFormat.Png);
        Diag.Log($"stamp contact sheet written to {path}");
    }

    /// <summary>Draws a stamp and returns its PREMULTIPLIED pixels.</summary>
    private static StampPixels? Rasterise(
        BuiltInStamp stamp, StampTheme theme, CultureInfo culture, int width)
    {
        try
        {
            var device = CanvasDevice.GetSharedDevice();
            var metrics = BuiltInStamps.Measure(stamp, width, theme);

            // A diagonal stamp needs a canvas big enough for its corners, or
            // rotating clips the first and last letters off.
            var (canvasWidth, canvasHeight) = stamp.Diagonal
                ? BuiltInStamps.RotatedBounds(metrics.Width, metrics.Height, theme.DiagonalDegrees)
                : (metrics.Width, metrics.Height);

            using var target = new CanvasRenderTarget(device, canvasWidth, canvasHeight, 96);
            using (var ds = target.CreateDrawingSession())
            {
                ds.Clear(Microsoft.UI.Colors.Transparent);

                if (stamp.Diagonal)
                {
                    ds.Transform = Matrix3x2.CreateRotation(
                        (float)(theme.DiagonalDegrees * Math.PI / 180.0),
                        new Vector2(canvasWidth / 2f, canvasHeight / 2f));
                }

                // The stamp itself is always drawn centred in the canvas, so
                // the rotation above pivots about its middle and the padding
                // falls evenly around it.
                var box = new Rect(
                    (canvasWidth - metrics.Width) / 2.0,
                    (canvasHeight - metrics.Height) / 2.0,
                    metrics.Width,
                    metrics.Height);

                Draw(ds, device, stamp, theme, metrics, box, culture);
            }

            return new StampPixels(target.GetPixelBytes(), canvasWidth, canvasHeight);
        }
        catch (Exception ex)
        {
            Diag.Log($"stamp render failed for {stamp.Id}: {ex}");
            return null;
        }
    }

    private static void Draw(
        CanvasDrawingSession ds,
        CanvasDevice device,
        BuiltInStamp stamp,
        StampTheme theme,
        StampMetrics metrics,
        Rect box,
        CultureInfo culture)
    {
        var ink = Color.FromArgb(255, stamp.Color.R, stamp.Color.G, stamp.Color.B);

        if (stamp.Style == StampStyle.Mark)
        {
            DrawMark(ds, device, stamp.MarkPath, ink, box);
            return;
        }

        // The border, inset by half its own thickness so the stroke lands
        // inside the box rather than straddling its edge.
        double half = metrics.Border / 2;
        var frame = new Rect(
            box.X + half, box.Y + half,
            box.Width - metrics.Border, box.Height - metrics.Border);

        ds.DrawRoundedRectangle(
            frame,
            (float)metrics.CornerRadius,
            (float)metrics.CornerRadius,
            ink,
            (float)metrics.Border);

        // What the letters have to fit inside: the frame, less the border and
        // the inset all round. SIGN HERE also gives up a square on the left for
        // its arrow.
        double markRoom = stamp.MarkPath.Length > 0 ? box.Height * 0.62 : 0;
        double textLeft = box.X + metrics.Border + metrics.Inset + markRoom;
        double textWidth = box.Right - metrics.Border - metrics.Inset - textLeft;
        double textTop = box.Y + metrics.Border;
        double textHeight = box.Height - (2 * metrics.Border);

        if (markRoom > 0)
        {
            double markSize = box.Height * 0.44;
            DrawMark(ds, device, stamp.MarkPath, ink, new Rect(
                box.X + metrics.Border + metrics.Inset,
                box.Y + ((box.Height - markSize) / 2),
                markSize,
                markSize));
        }

        string date = BuiltInStamps.DateLine(stamp, DateTime.Now, culture);

        if (date.Length == 0)
        {
            // One line, centred in the whole inner box.
            DrawLine(ds, device, stamp.Label, theme, metrics.LabelSize, ink,
                     new Rect(textLeft, textTop, textWidth, textHeight));
            return;
        }

        // Two lines. The inner box is split in the same proportion as the two
        // type sizes, and each line is then centred in its own share, so the
        // pair sits balanced rather than the date hanging off the bottom.
        double labelShare = textHeight * metrics.LabelSize / (metrics.LabelSize + metrics.DateSize);

        DrawLine(ds, device, stamp.Label, theme, metrics.LabelSize, ink,
                 new Rect(textLeft, textTop, textWidth, labelShare));

        DrawLine(ds, device, date, theme, metrics.DateSize, ink,
                 new Rect(textLeft, textTop + labelShare, textWidth, textHeight - labelShare));
    }

    /// <summary>
    /// One line of text, shrunk if it would not fit and centred on its INK.
    ///
    /// Measured before it is drawn because only DirectWrite knows how wide
    /// "NOT APPROVED" is in this font at this size. The rule for what to do
    /// about it is BuiltInStamps.FitSize, and where the result goes is
    /// BuiltInStamps.CenterOffset; both are tested. This measures and applies.
    ///
    /// DrawBounds throughout, not LayoutBounds. LayoutBounds is the line box,
    /// which for Oswald carries a lot of empty space above the capitals, so
    /// centring on it put every word low in its frame. DrawBounds is the ink.
    /// </summary>
    private static void DrawLine(
        CanvasDrawingSession ds,
        CanvasDevice device,
        string text,
        StampTheme theme,
        double size,
        Color ink,
        Rect band)
    {
        if (text.Length == 0 || band.Width <= 0 || band.Height <= 0)
        {
            return;
        }

        using var probe = TextFormat(theme, size);
        using var measured = new CanvasTextLayout(device, text, probe, MeasureBound, MeasureBound);

        double fitted = BuiltInStamps.FitSize(size, measured.DrawBounds.Width, band.Width);

        using var format = TextFormat(theme, fitted);
        using var layout = new CanvasTextLayout(device, text, format, MeasureBound, MeasureBound);
        var drawn = layout.DrawBounds;

        ds.DrawTextLayout(
            layout,
            (float)BuiltInStamps.CenterOffset(band.X, band.Width, drawn.X, drawn.Width),
            (float)BuiltInStamps.CenterOffset(band.Y, band.Height, drawn.Y, drawn.Height),
            ink);
    }

    private static CanvasTextFormat TextFormat(StampTheme theme, double size) => new()
    {
        FontFamily = FontUri(theme),
        FontSize = (float)size,
        FontWeight = Microsoft.UI.Text.FontWeights.Bold,

        // The vertical box is worked out from the metrics, so the layout must
        // not add its own leading on top or the text sits low in the frame.
        LineSpacingMode = CanvasLineSpacingMode.Proportional,
        LineSpacing = 1.0f,
        VerticalAlignment = CanvasVerticalAlignment.Top,
        HorizontalAlignment = CanvasHorizontalAlignment.Left,
        WordWrapping = CanvasWordWrapping.NoWrap,
    };

    /// <summary>
    /// Draws one of the Tabler marks.
    ///
    /// As SVG rather than a hand-built geometry, so the path data can stay
    /// exactly the string the icon set publishes: no transcription step, and
    /// updating an icon is a copy and paste. The stroke width, caps and joins
    /// are the set's own, which is what makes five different marks look like
    /// one family.
    /// </summary>
    private static void DrawMark(
        CanvasDrawingSession ds, CanvasDevice device, string path, Color ink, Rect into)
    {
        string colour = $"#{ink.R:X2}{ink.G:X2}{ink.B:X2}";
        string paint = BuiltInStamps.Marks.IsFilled(path)
            ? $"fill=\"{colour}\""
            : $"fill=\"none\" stroke=\"{colour}\" stroke-width=\"2\" "
              + "stroke-linecap=\"round\" stroke-linejoin=\"round\"";

        string svg =
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\" {paint}>"
            + $"<path d=\"{path}\" /></svg>";

        using var document = CanvasSvgDocument.LoadFromXml(device, svg);

        // DrawSvg fills the size it is given from the viewBox, so a square
        // destination keeps the icon's own proportions.
        double side = Math.Min(into.Width, into.Height);

        // Composed ONTO the existing transform, not replacing it: a diagonal
        // stamp already has a rotation in there, and overwriting it would leave
        // the mark upright inside a tilted frame.
        var previous = ds.Transform;
        ds.Transform = Matrix3x2.CreateTranslation(
            (float)(into.X + ((into.Width - side) / 2)),
            (float)(into.Y + ((into.Height - side) / 2))) * previous;

        ds.DrawSvg(document, new Size(side, side));
        ds.Transform = previous;
    }
}
