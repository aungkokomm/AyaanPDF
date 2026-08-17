using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using PdfEditorApp.Viewport;
using IOPath = System.IO.Path;
using Windows.UI;

namespace PdfEditorApp.Rendering;

/// <summary>
/// Stage 4, tier 2: the whole preview surface drawn through BOTH renderers and
/// measured, cell by cell, into a CSV.
///
/// A diagnostic, not a feature. It runs only when AYAAN_SKIA_CAPTURE names an
/// output directory, so it cannot fire for a user, and it is the only way to
/// get the XAML renderer's actual pixels: a test assembly cannot load WinUI, so
/// every claim about what the overlay draws is otherwise an argument from its
/// source rather than a measurement of its output.
///
/// It is also the runtime gate for the whole stage. SkiaSharp.Views.WinUI has a
/// documented history of failing in UNPACKAGED apps, and this app is
/// unpackaged: a build that succeeds proves nothing, because the failure is a
/// COM cast at paint time. If Skia cannot paint here, this is where that is
/// found.
///
/// THE REFERENCE IS THE REFERENCE. The XAML side is built by calling
/// OverlayShapeBuilder, the same code the live preview runs, not by
/// reconstructing it here. This harness used to reimplement the overlay's
/// polyline "the way the overlay builds one", which can agree with the
/// candidate while both differ from what is on screen.
///
/// PREVIEWS ONLY, on purpose. Committed marks are real PDF annotations drawn by
/// PDFium into the page bitmap; neither renderer here draws them, and nothing
/// in this file is a second representation of one.
///
/// WHAT THE SINGLE-RECTANGLE VERSION MEASURED, 17 Aug 2026, device scale 1.5:
///
///   position     first ink at 78.00 DIPs for BOTH. Exact.
///   stroke width XAML 4.800 device px (3.200 DIPs) -- exactly what was asked
///                Skia 5.004 device px (3.336 DIPs) -- rounded up to 5.0
///
/// So Direct2D does not quantise stroke width and Skia does, to the nearest
/// half device pixel. Skia is 4.2% thicker there. The difference is REAL and it
/// is Skia's, not the overlay's. Left uncompensated by instruction: this stage
/// MEASURES it and records it, and the width column can never fail a cell.
/// </summary>
internal static class SkiaParityCapture
{
    public const string DirectoryVariable = "AYAAN_SKIA_CAPTURE";

    /// <summary>A portrait content box, so a quarter turn visibly changes the card.</summary>
    private const double ContentWidth = 400;

    private const double ContentHeight = 500;

    /// <summary>Normalized divides both axes by the page WIDTH, as everywhere else.</summary>
    private const double Scale = ContentWidth;

    private const double StrokeWidthNorm = 0.01;
    private const string InkColor = "#FF000000";

    /// <summary>Big enough for every rotation of every subject to land inside it.</summary>
    private const int Surface = 620;

    private static readonly int[] Rotations = [0, 90, 180, 270];

    /// <summary>
    /// The preview surface as it actually runs: the four shape tools and the
    /// freehand guide. An arrow contributes two marks, its shaft and its filled
    /// head, which are drawn by different code on both sides.
    /// </summary>
    private static IEnumerable<(string Name, ShapeKind? Kind)> Subjects()
    {
        yield return ("rect", ShapeKind.Rectangle);
        yield return ("ellipse", ShapeKind.Ellipse);
        yield return ("line", ShapeKind.Line);
        yield return ("arrow", ShapeKind.Arrow);
        yield return ("ink", null);           // the freehand guide
    }

    private static ShapeAnnotation ShapeFor(ShapeKind kind) =>
        new(0, new ShapeDraft(kind, 0.12, 0.12, 0.72, 0.68), InkColor, StrokeWidthNorm);

    /// <summary>A fixed, hand-written stroke. No randomness anywhere in here.</summary>
    private static readonly (double X, double Y)[] InkPoints =
    [
        (0.15, 0.20), (0.28, 0.34), (0.41, 0.28), (0.55, 0.47),
        (0.62, 0.71), (0.74, 0.66), (0.80, 0.85),
    ];

    public static string? RequestedDirectory =>
        Environment.GetEnvironmentVariable(DirectoryVariable) is { Length: > 0 } dir ? dir : null;

    public static async Task<string> RunAsync(Panel host, string directory)
    {
        Directory.CreateDirectory(directory);

        var csv = new StringBuilder();
        csv.AppendLine(
            "subject,rotation," +
            "refBounds,skiaBounds,expectedBounds," +
            "refCentroid,skiaCentroid,refMass,skiaMass," +
            "refWidthPx,skiaWidthPx,widthDeltaPct," +
            "boundsVerdict,centroidVerdict,massVerdict,widthNote," +
            "refAgreesWithArithmetic,skiaAgreesWithArithmetic,verdict");

        // EVERYTHING IS COMPARED IN DEVICE PIXELS, and the scale has to come
        // from the HOST. Reading it off the Skia layer is what the first run of
        // this harness did, and the layer has no XamlRoot until it is in the
        // tree, so it silently got 1.0: Skia drew at DIP coordinates into a
        // surface the capture then read as device pixels, and came out exactly
        // 1/1.5 the size of the reference. Both renderers and the arithmetic
        // now use this one number.
        double device = host.XamlRoot?.RasterizationScale ?? 1.0;

        int cells = 0;
        foreach (var (name, kind) in Subjects())
        {
            foreach (int rotation in Rotations)
            {
                var view = PageTransform.For(ContentWidth, ContentHeight, rotation, ContentWidth);

                Shot reference, candidate;
                try
                {
                    reference = await CaptureAsync(
                        host, BuildReference(name, kind, view), kind,
                        IOPath.Combine(directory, $"{name}-{rotation:D3}-ref.png"));

                    candidate = await CaptureAsync(
                        host, BuildCandidate(name, kind, view, device), kind,
                        IOPath.Combine(directory, $"{name}-{rotation:D3}-skia.png"));
                }
                catch (Exception ex)
                {
                    csv.AppendLine($"{name},{rotation},CAPTURE FAILED: {ex.GetType().Name}: {ex.Message}");
                    continue;
                }

                csv.AppendLine(Compare(
                    name, rotation, reference, candidate,
                    ExpectedBounds(name, kind, view, device)));
                cells++;
            }
        }

        string path = IOPath.Combine(directory, "stage4-parity.csv");
        File.WriteAllText(path, csv.ToString());
        return $"{cells} cells, {cells * 2} PNGs, stage4-parity.csv";
    }

    // ---------------- the two renderers ----------------

    /// <summary>
    /// The reference: real XAML elements from OverlayShapeBuilder, laid on a
    /// Canvas the way the ink layer lays them out.
    /// </summary>
    private static UIElement BuildReference(string name, ShapeKind? kind, PageTransform view)
    {
        var canvas = new Canvas { Width = Surface, Height = Surface, IsHitTestVisible = false };

        if (kind is null)
        {
            // The freehand guide: red, and a flat weight nothing scales.
            var guide = new Polyline
            {
                Stroke = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Microsoft.UI.Colors.Red),
                StrokeThickness = OverlayProjection.InkGuideWidthDips,
            };
            OverlayShapeBuilder.ProjectInto(guide, InkPoints, Scale, 0, view);
            canvas.Children.Add(guide);
            return canvas;
        }

        var shape = ShapeFor(kind.Value);

        var shaft = new Polyline
        {
            Stroke = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                OverlayShapeBuilder.ColorFromHex(shape.ColorHex)),
            StrokeThickness = OverlayProjection.ToSlotThickness(shape.StrokeWidth, Scale, view),
        };
        OverlayShapeBuilder.ProjectInto(shaft, shape.Outline, Scale, 0, view);
        canvas.Children.Add(shaft);

        if (shape.Head.Count == 3)
        {
            canvas.Children.Add(OverlayShapeBuilder.FilledHead(
                shape.Head, shape.ColorHex, scale: Scale, pageTop: 0, view: view));
        }

        return canvas;
    }

    /// <summary>The candidate: the real Skia layer, handed the real frame.</summary>
    private static UIElement BuildCandidate(
        string name, ShapeKind? kind, PageTransform view, double device)
    {
        var layer = new SkiaShapeLayer { Width = Surface, Height = Surface };

        var items = kind is null
            ? ShapeRenderList.From([], [], inkPreview: ShapeRenderList.InkGuide(0, InkPoints))
            : ShapeRenderList.From([], [ShapeFor(kind.Value)]);

        // No zoom and no scroll: zoom is its own commit, and this one measures
        // the renderers rather than the plumbing between them and the window.
        // The device scale is NOT optional though, and is passed in rather than
        // read off this layer, which has no XamlRoot until it is in the tree.
        layer.Show(
            items, Scale, _ => 0, _ => view,
            new ViewportProjection(
                Zoom: 1,
                DeviceScale: device,
                OriginXDips: 0,
                OriginYDips: 0));

        return layer;
    }

    // ---------------- the third, independent expectation ----------------

    /// <summary>
    /// Where the mark should be, from the shared projection alone, touching
    /// neither renderer.
    ///
    /// This is what separates "Skia is wrong" from "the reference is wrong",
    /// and it is not a formality: the reference renderer WAS the wrong one
    /// under rotation, and only a third opinion could have said so.
    ///
    /// Computed in slot DIPs, grown by half a stroke because a path is stroked
    /// about its centreline, and then taken to DEVICE pixels so it can be
    /// compared with what came off the two surfaces.
    /// </summary>
    private static (double L, double T, double R, double B) ExpectedBounds(
        string name, ShapeKind? kind, PageTransform view, double device)
    {
        var points = kind is null ? InkPoints : (IReadOnlyList<(double X, double Y)>)ShapeFor(kind.Value).Outline;
        double reach = (kind is null
            ? OverlayProjection.InkGuideWidthDips
            : OverlayProjection.ToSlotThickness(StrokeWidthNorm, Scale, view)) / 2;

        double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;

        void Take(IReadOnlyList<(double X, double Y)> ps)
        {
            foreach (var p in ps)
            {
                var (x, y) = OverlayProjection.ToSlot(p, Scale, 0, view);
                l = Math.Min(l, x); t = Math.Min(t, y);
                r = Math.Max(r, x); b = Math.Max(b, y);
            }
        }

        Take(points);
        if (kind is not null && ShapeFor(kind.Value).Head is { Count: 3 } head)
        {
            Take(head);
        }

        return ((l - reach) * device, (t - reach) * device,
                (r + reach) * device, (b + reach) * device);
    }

    // ---------------- measurement ----------------

    private readonly record struct Shot(
        (int L, int T, int R, int B)? Bounds, double CentroidX, double CentroidY,
        double Mass, double Width);

    /// <summary>
    /// How far one pixel is from white, on its DARKEST channel, after being
    /// composited over white.
    ///
    /// Two traps, both of which produced nonsense before being handled.
    ///
    /// Not the red channel alone: the freehand guide is pure red, so a fully
    /// covered pixel is (255, 0, 0) and reading red scores it blank.
    ///
    /// And the buffer is PREMULTIPLIED BGRA, so a fully transparent pixel is
    /// (0, 0, 0, 0), which on any darkest-channel rule is maximum ink. The
    /// first run of this harness reported every cell inked from 0 to 929 on a
    /// 620-point surface, which is the whole bitmap, because of exactly that.
    /// Compositing over white first sends transparent to white, where it
    /// belongs, and leaves opaque pixels untouched.
    /// </summary>
    private static double Coverage(byte b, byte g, byte r, byte a)
    {
        int over = 255 - a;
        int darkest = Math.Min(r + over, Math.Min(g + over, b + over));

        return (255 - Math.Min(255, darkest)) / 255.0;
    }

    /// <summary>
    /// Whether a subject has a flat top edge for the width probe to cross.
    ///
    /// A rectangle and an ellipse do. A diagonal line, an arrow and a freehand
    /// stroke do not, and scanning a column at the top of their inked box finds
    /// a corner or nothing at all: the first run reported a width of 0.000 for
    /// all three, which is not a measurement of anything. They report n/a
    /// instead of a number that would read as a real difference.
    /// </summary>
    private static bool HasFlatTopEdge(ShapeKind? kind) =>
        kind is ShapeKind.Rectangle or ShapeKind.Ellipse or ShapeKind.RoundedRectangle;

    private static Shot Measure(byte[] bgra, int w, int h, ShapeKind? kind)
    {
        int l = int.MaxValue, t = int.MaxValue, rr = int.MinValue, bb = int.MinValue;
        double mass = 0, cx = 0, cy = 0;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = ((y * w) + x) * 4;
                double c = Coverage(bgra[i], bgra[i + 1], bgra[i + 2], bgra[i + 3]);
                if (c <= 0)
                {
                    continue;
                }

                mass += c;
                cx += x * c;
                cy += y * c;
                l = Math.Min(l, x); t = Math.Min(t, y);
                rr = Math.Max(rr, x); bb = Math.Max(bb, y);
            }
        }

        if (mass <= 0)
        {
            return new Shot(null, 0, 0, 0, double.NaN);
        }

        double width = HasFlatTopEdge(kind)
            ? WidthAt(bgra, w, h, (l + rr) / 2, t)
            : double.NaN;

        return new Shot((l, t, rr, bb), cx / mass, cy / mass, mass, width);
    }

    /// <summary>
    /// The cross-section of the topmost edge, scanned DOWN a column through the
    /// inked box's middle. Coverage summed rather than dark pixels counted: a
    /// 3-wide antialiased stroke spreads over five rows at partial coverage, so
    /// counting measures nothing.
    /// </summary>
    private static double WidthAt(byte[] bgra, int w, int h, int x, int fromY)
    {
        double ink = 0;
        for (int y = Math.Max(0, fromY - 2); y < Math.Min(h, fromY + 14); y++)
        {
            int i = ((y * w) + x) * 4;
            double c = Coverage(bgra[i], bgra[i + 1], bgra[i + 2], bgra[i + 3]);
            if (c <= 0 && ink > 0)
            {
                break;
            }

            ink += c;
        }

        return ink;
    }

    // ---------------- verdicts ----------------

    private static string Compare(
        string name, int rotation, Shot reference, Shot candidate,
        (double L, double T, double R, double B) expected)
    {
        string boundsVerdict = BoundsVerdict(reference.Bounds, candidate.Bounds);
        string centroidVerdict = Near(
            Math.Max(Math.Abs(reference.CentroidX - candidate.CentroidX),
                     Math.Abs(reference.CentroidY - candidate.CentroidY)), 0.5, 1.0);

        double massDelta = reference.Mass <= 0 ? 1 : Math.Abs(candidate.Mass - reference.Mass) / reference.Mass;
        string massVerdict = Near(massDelta, 0.02, 0.08);

        // The width column can NEVER fail a cell. It is measured and recorded
        // by instruction, not compensated and not asserted. Subjects with no
        // flat edge to cross report n/a rather than a zero that would read as a
        // real difference.
        bool measurable = !double.IsNaN(reference.Width) && !double.IsNaN(candidate.Width)
                          && reference.Width > 0;

        double widthDelta = measurable ? (candidate.Width - reference.Width) / reference.Width : 0;

        string widthNote = !measurable
            ? "n/a: no flat edge"
            : Math.Abs(candidate.Width - reference.Width) <= 0.5
                ? "WITHIN HALF DEVICE PIXEL"
                : "KNOWN QUIRK: skia stroker snap";

        string verdict = Worst(boundsVerdict, centroidVerdict, massVerdict);

        return string.Join(',',
            name, rotation,
            Box(reference.Bounds), Box(candidate.Bounds), BoxOf(expected),
            $"{reference.CentroidX:F2} {reference.CentroidY:F2}",
            $"{candidate.CentroidX:F2} {candidate.CentroidY:F2}",
            F(reference.Mass), F(candidate.Mass),
            W(reference.Width), W(candidate.Width), F(widthDelta * 100),
            boundsVerdict, centroidVerdict, massVerdict, widthNote,
            Agrees(reference.Bounds, expected), Agrees(candidate.Bounds, expected),
            verdict);
    }

    private static string BoundsVerdict((int L, int T, int R, int B)? a, (int L, int T, int R, int B)? b)
    {
        if (a is not { } x || b is not { } y)
        {
            return "MISMATCH";
        }

        int worst = Math.Max(
            Math.Max(Math.Abs(x.L - y.L), Math.Abs(x.T - y.T)),
            Math.Max(Math.Abs(x.R - y.R), Math.Abs(x.B - y.B)));

        return Near(worst, 1, 2);
    }

    /// <summary>
    /// Whether a renderer's inked box matches the arithmetic, within the two
    /// pixels antialiasing and the stroker can move an edge.
    /// </summary>
    private static string Agrees((int L, int T, int R, int B)? got, (double L, double T, double R, double B) want)
    {
        if (got is not { } g)
        {
            return "NO INK";
        }

        double worst = Math.Max(
            Math.Max(Math.Abs(g.L - want.L), Math.Abs(g.T - want.T)),
            Math.Max(Math.Abs(g.R - want.R), Math.Abs(g.B - want.B)));

        return worst <= 2 ? "YES" : $"NO ({worst:F1}px)";
    }

    private static string Near(double delta, double match, double tolerate) =>
        delta <= match ? "MATCH" : delta <= tolerate ? "WITHIN TOLERANCE" : "MISMATCH";

    private static string Worst(params string[] verdicts)
    {
        if (Array.IndexOf(verdicts, "MISMATCH") >= 0) { return "MISMATCH"; }
        return Array.IndexOf(verdicts, "WITHIN TOLERANCE") >= 0 ? "WITHIN TOLERANCE" : "MATCH";
    }

    private static string Box((int L, int T, int R, int B)? b) =>
        b is { } v ? $"{v.L} {v.T} {v.R} {v.B}" : "none";

    private static string BoxOf((double L, double T, double R, double B) b) =>
        $"{b.L:F1} {b.T:F1} {b.R:F1} {b.B:F1}";

    private static string F(double v) => v.ToString("F3", CultureInfo.InvariantCulture);

    private static string W(double v) => double.IsNaN(v) ? "n/a" : F(v);

    // ---------------- plumbing ----------------

    /// <summary>
    /// An opaque, fixed-size frame around whatever is being captured.
    ///
    /// Both renderers must be captured through one of these or the comparison
    /// is meaningless. RenderTargetBitmap renders only the area an element
    /// actually PAINTS, so a bare Canvas came back cropped to its polyline's
    /// inked bounds while the Skia layer, which paints a full-size surface,
    /// came back at the requested size. Same size, same origin, same ground, or
    /// the two images cannot be laid over each other.
    /// </summary>
    private static Border Frame(UIElement content) => new()
    {
        Width = Surface,
        Height = Surface,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Color.FromArgb(255, 255, 255, 255)),
        Child = content,
    };

    private static async Task<Shot> CaptureAsync(
        Panel host, UIElement content, ShapeKind? kind, string pngPath)
    {
        var frame = Frame(content);
        host.Children.Add(frame);
        try
        {
            await SettleAsync(frame);

            var target = new RenderTargetBitmap();
            await target.RenderAsync(frame);
            var buffer = await target.GetPixelsAsync();

            byte[] bytes = new byte[buffer.Length];
            using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer))
            {
                reader.ReadBytes(bytes);
            }

            await WriteAsync(bytes, target.PixelWidth, target.PixelHeight, pngPath);
            return Measure(bytes, target.PixelWidth, target.PixelHeight, kind);
        }
        finally
        {
            host.Children.Remove(frame);
        }
    }

    /// <summary>Lets layout and a paint pass happen before the capture.</summary>
    private static async Task SettleAsync(FrameworkElement element)
    {
        element.UpdateLayout();
        for (int i = 0; i < 8; i++)
        {
            await Task.Delay(60);
        }
    }

    private static async Task WriteAsync(byte[] bgra, int width, int height, string path)
    {
        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
            Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, await OpenAsync(path));

        encoder.SetPixelData(
            Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
            Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
            (uint)width, (uint)height, 96, 96, bgra);

        await encoder.FlushAsync();
    }

    private static async Task<Windows.Storage.Streams.IRandomAccessStream> OpenAsync(string path)
    {
        var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(
            IOPath.GetDirectoryName(path)!);
        var file = await folder.CreateFileAsync(
            IOPath.GetFileName(path), Windows.Storage.CreationCollisionOption.ReplaceExisting);
        return await file.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite);
    }
}
