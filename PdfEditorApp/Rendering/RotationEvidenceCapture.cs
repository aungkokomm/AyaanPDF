using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using PdfEditorApp.Viewport;
using Windows.Foundation;
using Windows.UI;
using IOPath = System.IO.Path;

namespace PdfEditorApp.Rendering;

/// <summary>
/// Evidence, not inspection: does a shape drawn on the ink layer follow the
/// page when the view is rotated?
///
/// It draws THE SAME shape twice at each of the four rotations.
///
///   BLUE  as a per-page overlay, inside the rotated content grid, positioned
///         in content coordinates. This is the pattern HighlightRects,
///         SearchMatchRects and the selection chrome already use.
///   RED   as an ink-layer overlay, outside the card, positioned in slot
///         coordinates by exactly the arithmetic BuildStrokePolyline performs.
///
/// If the two coincide at 0 and separate at 90, 180 and 270, that is the
/// mismatch, shown rather than argued. Comparing the ink layer against itself
/// would prove nothing, which is why the per-page path is the control.
///
/// A development diagnostic. It runs only when AYAAN_ROTATION_EVIDENCE names an
/// output directory, so it cannot fire for a user, and it writes both the PNGs
/// and a measured report so the answer does not depend on anyone squinting at
/// an image.
/// </summary>
internal static class RotationEvidenceCapture
{
    public const string DirectoryVariable = "AYAAN_ROTATION_EVIDENCE";

    /// <summary>
    /// A landscape content box, so a quarter turn visibly changes the card and
    /// a square fixture cannot hide a transposed axis.
    /// </summary>
    private const double ContentWidth = 400;

    private const double ContentHeight = 300;

    /// <summary>Normalized divides both axes by the page WIDTH, as everywhere else.</summary>
    private const double Scale = ContentWidth;

    private const double StrokeWidthNorm = 0.01;

    private const int SurfaceWidth = 620;
    private const int SurfaceHeight = 700;

    private static readonly int[] Rotations = [0, 90, 180, 270];

    private static readonly Color PerPage = Color.FromArgb(255, 0, 0, 255);
    private static readonly Color InkLayer = Color.FromArgb(255, 255, 0, 0);

    public static string? RequestedDirectory =>
        Environment.GetEnvironmentVariable(DirectoryVariable) is { Length: > 0 } dir ? dir : null;

    /// <summary>The two shapes asked for: a rectangle and an arrow.</summary>
    private static IEnumerable<(string Name, ShapeAnnotation Shape)> Subjects()
    {
        yield return ("rect", new ShapeAnnotation(
            0, new ShapeDraft(ShapeKind.Rectangle, 0.12, 0.12, 0.55, 0.42), "#FF000000", StrokeWidthNorm));

        yield return ("arrow", new ShapeAnnotation(
            0, new ShapeDraft(ShapeKind.Arrow, 0.15, 0.20, 0.70, 0.55), "#FF000000", StrokeWidthNorm));
    }

    public static async Task<string> RunAsync(Panel host, string directory)
    {
        Directory.CreateDirectory(directory);
        var report = new StringBuilder();
        report.AppendLine("shape,rotation,perPageBounds,inkLayerBounds,coincide");

        foreach (var (name, shape) in Subjects())
        {
            foreach (int rotation in Rotations)
            {
                var view = PageTransform.For(ContentWidth, ContentHeight, rotation, ContentWidth);

                // Measured in SEPARATE captures, one layer each. Drawn together
                // they overlap exactly at 0, and whichever is on top hides the
                // other, so a combined capture reports the lower one as absent
                // and turns the very case that should read "identical" into a
                // null. The combined image is still written, for the eye.
                var blue = await MeasureAsync(host, shape, view, perPageOnly: true);
                var red = await MeasureAsync(host, shape, view, perPageOnly: false);

                await WriteCombinedAsync(
                    host, shape, view, IOPath.Combine(directory, $"{name}-{rotation:D3}.png"));

                report.AppendLine(
                    $"{name},{rotation},{Describe(blue)},{Describe(red)},{Coincide(blue, red)}");
            }
        }

        string path = IOPath.Combine(directory, "rotation-evidence.csv");
        File.WriteAllText(path, report.ToString());
        return $"wrote {Rotations.Length * 2} PNGs and rotation-evidence.csv";
    }

    /// <summary>Renders one layer on its own and returns its bounding box.</summary>
    private static async Task<(int L, int T, int R, int B)?> MeasureAsync(
        Panel host, ShapeAnnotation shape, PageTransform view, bool perPageOnly)
    {
        var frame = BuildFrame(shape, view, perPage: perPageOnly, inkLayer: !perPageOnly);
        host.Children.Add(frame);
        try
        {
            await SettleAsync(frame);
            var (pixels, w, h) = await CaptureAsync(frame);
            return BoundsOf(pixels, w, h, wantBlue: perPageOnly);
        }
        finally
        {
            host.Children.Remove(frame);
        }
    }

    /// <summary>Both layers together, written out for visual inspection.</summary>
    private static async Task WriteCombinedAsync(
        Panel host, ShapeAnnotation shape, PageTransform view, string path)
    {
        var frame = BuildFrame(shape, view, perPage: true, inkLayer: true);
        host.Children.Add(frame);
        try
        {
            await SettleAsync(frame);
            var (pixels, w, h) = await CaptureAsync(frame);
            await WritePngAsync(pixels, w, h, path);
        }
        finally
        {
            host.Children.Remove(frame);
        }
    }

    private static async Task SettleAsync(FrameworkElement element)
    {
        element.UpdateLayout();
        for (int i = 0; i < 5; i++)
        {
            await Task.Delay(45);
        }
    }

    /// <summary>
    /// One capture: an opaque frame holding a page card with the rotated
    /// content grid inside it, and the ink-layer mark laid over the top.
    /// </summary>
    private static Border BuildFrame(
        ShapeAnnotation shape, PageTransform view, bool perPage, bool inkLayer)
    {
        // The page card and the one place view rotation happens, mirroring
        // MainPage.xaml exactly: a content-sized grid carrying a
        // CompositeTransform of scale, rotation and translate.
        var content = new Grid
        {
            Width = view.ContentWidth,
            Height = view.ContentHeight,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            RenderTransform = new CompositeTransform
            {
                ScaleX = view.Scale,
                ScaleY = view.Scale,
                Rotation = view.Rotation,
                TranslateX = view.TranslateX,
                TranslateY = view.TranslateY,
            },
        };

        // BLUE: the control. A per-page overlay in CONTENT coordinates, which
        // the grid's own transform then turns along with the page.
        if (perPage)
        {
            content.Children.Add(MarkIn(shape, PerPage, perPageSpace: true, view));
            if (HeadIn(shape, PerPage, perPageSpace: true, view) is { } head)
            {
                content.Children.Add(head);
            }
        }

        var card = new Grid
        {
            Width = view.CardWidth,
            Height = view.CardHeight,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromArgb(255, 245, 245, 245)),
        };
        card.Children.Add(content);

        var stack = new Grid
        {
            Width = SurfaceWidth,
            Height = SurfaceHeight,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        stack.Children.Add(card);

        // RED: the subject. The ink layer spans the stack and is never
        // transformed, so its mark is placed in slot coordinates over the top.
        if (inkLayer)
        {
            var layer = new Canvas { IsHitTestVisible = false };
            layer.Children.Add(MarkIn(shape, InkLayer, perPageSpace: false, view));
            if (HeadIn(shape, InkLayer, perPageSpace: false, view) is { } head)
            {
                layer.Children.Add(head);
            }
            stack.Children.Add(layer);
        }

        return new Border
        {
            Width = SurfaceWidth,
            Height = SurfaceHeight,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
            Child = stack,
        };
    }

    /// <summary>
    /// The shape's outline as a Polyline.
    ///
    /// The ink-layer branch reproduces BuildStrokePolyline: points at
    /// (x * scale, y * scale + pageTop) with a thickness of strokeWidth *
    /// scale, and NO reference to the page's transform. That absence is the
    /// thing under test, and a source guard in RotationEvidenceTests holds the
    /// real method to the same arithmetic.
    /// </summary>
    /// <summary>
    /// An arrow's filled head, the way BuildFilledHead now draws one, or null
    /// for a shape that has none.
    ///
    /// Included because the head is a SEPARATE polygon built by a separate
    /// method. A capture of only the shaft would have shown an arrow following
    /// the page perfectly while its tip stayed behind.
    /// </summary>
    private static Polygon? HeadIn(
        ShapeAnnotation shape, Color color, bool perPageSpace, PageTransform view)
    {
        var head = shape.Head;
        if (head.Count != 3)
        {
            return null;
        }

        var brush = new SolidColorBrush(color);
        var polygon = new Polygon { Fill = brush, Stroke = brush, StrokeThickness = 0.5 };

        foreach (var (nx, ny) in head)
        {
            var (x, y) = OverlayProjection.ToSlot((nx, ny), Scale, 0);
            var (cx, cy) = perPageSpace ? (x, y) : view.ToCard(x, y);
            polygon.Points.Add(new Point(cx, cy));
        }

        return polygon;
    }

    private static Polyline MarkIn(
        ShapeAnnotation shape, Color color, bool perPageSpace, PageTransform view)
    {
        // The per-page mark is authored in CONTENT coordinates and the grid it
        // sits in applies view.Scale for it. The ink-layer mark is outside that
        // grid, so it applies the same scale itself, exactly as the corrected
        // BuildStrokePolyline now does.
        var line = new Polyline
        {
            Stroke = new SolidColorBrush(color),
            StrokeThickness = perPageSpace
                ? StrokeWidthNorm * Scale
                : StrokeWidthNorm * Scale * view.Scale,
        };

        foreach (var (nx, ny) in shape.Outline)
        {
            var (x, y) = OverlayProjection.ToSlot((nx, ny), Scale, 0);

            // The correction under test. A per-page mark is turned by its
            // grid; an ink-layer mark has to turn itself.
            var (cx, cy) = perPageSpace ? (x, y) : view.ToCard(x, y);
            line.Points.Add(new Point(cx, cy));
        }

        return line;
    }

    private static async Task<(byte[] Pixels, int Width, int Height)> CaptureAsync(UIElement element)
    {
        var target = new RenderTargetBitmap();
        await target.RenderAsync(element);

        var buffer = await target.GetPixelsAsync();
        byte[] bytes = new byte[buffer.Length];
        using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer))
        {
            reader.ReadBytes(bytes);
        }

        return (bytes, target.PixelWidth, target.PixelHeight);
    }

    /// <summary>
    /// The bounding box of one colour, in device pixels, or null if it is
    /// absent. BGRA8, so blue is byte 0 and red is byte 2.
    /// </summary>
    private static (int L, int T, int R, int B)? BoundsOf(
        byte[] pixels, int width, int height, bool wantBlue)
    {
        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = ((y * width) + x) * 4;
                byte b = pixels[i], r = pixels[i + 2];

                bool hit = wantBlue ? b > 140 && r < 110 : r > 140 && b < 110;
                if (!hit)
                {
                    continue;
                }

                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }

        return left == int.MaxValue ? null : (left, top, right, bottom);
    }

    private static string Describe((int L, int T, int R, int B)? box) =>
        box is { } b ? $"{b.L} {b.T} {b.R} {b.B}" : "absent";

    /// <summary>Within two pixels on every edge, which antialiasing can move.</summary>
    private static bool Coincide((int L, int T, int R, int B)? a, (int L, int T, int R, int B)? b)
    {
        if (a is not { } x || b is not { } y)
        {
            return false;
        }

        return Math.Abs(x.L - y.L) <= 2 && Math.Abs(x.T - y.T) <= 2
            && Math.Abs(x.R - y.R) <= 2 && Math.Abs(x.B - y.B) <= 2;
    }

    private static async Task WritePngAsync(byte[] pixels, int width, int height, string path)
    {
        var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(
            IOPath.GetDirectoryName(path)!);
        var file = await folder.CreateFileAsync(
            IOPath.GetFileName(path), Windows.Storage.CreationCollisionOption.ReplaceExisting);

        using var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite);
        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
            Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);

        encoder.SetPixelData(
            Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
            Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
            (uint)width, (uint)height, 96, 96, pixels);

        await encoder.FlushAsync();
    }
}
