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
///
/// A KNOWN LIMIT OF THIS HARNESS, which the placement suite runs into and which
/// must not be read as a parity finding. The touching, half and corner cells
/// report MISMATCH because the REFERENCE comes out translated: it is the right
/// SIZE, to the pixel (152 against 152, 62 against 62), and in the wrong place.
/// A whole picture moved is a positioning artefact of the capture, not a
/// renderer drawing something different.
///
/// The cause is that a scrolled-off-edge reference cannot be reproduced here.
/// RenderTargetBitmap renders an element's own subtree, so content pushed past
/// the frame's origin by a RenderTransform is not composed the way a real
/// ScrollView composes it. In the app this situation does not arise: the ink
/// layer spans the whole document at positive slot coordinates and the scroller
/// clips its viewport.
///
/// Those verdicts are LEFT AS THEY FELL rather than tuned away, and the
/// question they were meant to answer is settled deterministically in tier 1
/// instead, by PagePlacementParityTests, which asserts that a clipped mark is
/// cut by the surface and not moved by it, at all four rotations, for all five
/// placements.
///
/// A THREE PIXEL ARTEFACT, recorded so nobody re-investigates it. Since the
/// effects foundation landed, two of the 144 PNGs differ from the run captured
/// before it: arrow-000-z100-ref and arrow-180-z100-ref. Both are REFERENCE
/// images, and no line of the XAML renderer changed.
///
/// The whole difference is three pixels, each off by exactly one level in 255:
/// (382,338) 248 to 249, (361,361) 183 to 184, and (76,52) 183 to 184. All 72
/// verdicts are identical, and the metrics move by 0.0002%.
///
/// It was chased to the end rather than waved through. The arrow's geometry is
/// BIT-IDENTICAL before and after, dumped at R17 across a stash, so nothing the
/// renderer is handed changed. The values are stable across runs of one binary
/// and shift between binaries. So this is a last-ULP rounding flip landing on a
/// coverage boundary, from JIT codegen changing when ShapeRenderItem grew a
/// field: every assembly here links PdfEditorApp.Viewport. It is the arrow
/// because it is the only subject whose head needs trigonometry, and 0 and 180
/// because ToCard takes a different arithmetic path there than at 90 and 270.
///
/// Not to be chased by touching the XAML renderer. A one-level change on three
/// of 1.7 million pixels is not a defect to fix; it is a reason to compare
/// verdicts rather than file hashes when the Viewport assembly changes shape.
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
    /// The scroller's zoom, applied to BOTH renderers by the same number.
    ///
    /// Skia takes it in the ViewportProjection, and the reference takes it as
    /// the ScaleTransform its Canvas gets, which is what the ScrollView's
    /// compositor zoom does to the ink layer in the app. Any other arrangement
    /// would be comparing a zoomed picture with an unzoomed one.
    ///
    /// The frame does NOT grow with it. Growing it was tried and does not work:
    /// the capture is bounded by the app's own window, so a 1240-point frame
    /// was clipped at about 613 points and every mark at 180 degrees fell
    /// outside it entirely and measured "none". The frame stays at a size the
    /// window can hold, and the mark is brought into it by an origin shift,
    /// which is what Framing does below and what tier 1 already does.
    /// </summary>
    private static readonly double[] Zooms = [1.0, 2.0];

    /// <summary>Where the mark is parked, in logical points from the corner.</summary>
    private const double MarginDips = 24;

    /// <summary>
    /// The origin that brings a mark's top-left corner to the margin, so a
    /// zoomed capture measures the whole mark instead of the part that happened
    /// to stay on the surface.
    ///
    /// Applied to BOTH renderers by the same numbers: Skia takes it in the
    /// ViewportProjection, the reference takes it as a TranslateTransform after
    /// its ScaleTransform, which composes to the same (slot * zoom) + origin.
    /// </summary>
    private static (double X, double Y) OriginFor(
        double zoom, (double L, double T, double R, double B) slotDips) =>
        (MarginDips - (slotDips.L * zoom), MarginDips - (slotDips.T * zoom));

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
            "suite,subject,rotation,zoom,deviceScale," +
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
                foreach (double zoom in Zooms)
                {
                    var view = PageTransform.For(ContentWidth, ContentHeight, rotation, ContentWidth);
                    int side = Surface;
                    string stem = $"{name}-{rotation:D3}-z{zoom * 100:F0}";

                    Shot reference, candidate;
                    try
                    {
                        reference = await CaptureAsync(
                            host, BuildReference(kind, view, zoom, side), kind, side,
                            IOPath.Combine(directory, $"{stem}-ref.png"));

                        candidate = await CaptureAsync(
                            host, BuildCandidate(kind, view, device, zoom, side), kind, side,
                            IOPath.Combine(directory, $"{stem}-skia.png"));
                    }
                    catch (Exception ex)
                    {
                        csv.AppendLine(
                            $"subject,{name},{rotation},{zoom},{device}," +
                            $"CAPTURE FAILED: {ex.GetType().Name}: {ex.Message}");
                        continue;
                    }

                    csv.AppendLine(Compare(
                        "subject", name, rotation, zoom, device, reference, candidate,
                        ExpectedBounds(kind, view, device, zoom)));
                    cells++;
                }
            }
        }

        cells += await PlacementsAsync(host, directory, csv, device);

        string path = IOPath.Combine(directory, "stage4-parity.csv");
        File.WriteAllText(path, csv.ToString());
        return $"{cells} cells, {cells * 2} PNGs, stage4-parity.csv";
    }

    // ---------------- placements: page edges, off-surface, multi-page ----------------

    /// <summary>
    /// Where a mark sits relative to the surface, in CARD points, so "touching
    /// the left edge" means that at every rotation rather than only at zero.
    ///
    /// The last two are the ones with something to say. "off" must produce no
    /// ink from EITHER renderer, and two renderers agreeing on nothing is a
    /// real result: it says the culler and the surface bound agree. "crossPage"
    /// runs past the page's own right edge while staying on the surface, which
    /// the ink layer does not clip, so both should draw the overhang.
    /// </summary>
    /// <summary>
    /// Where the mark sits relative to the surface, expressed as the SCROLL
    /// that puts it there, not as negative geometry.
    ///
    /// This distinction cost a whole run. Placing the mark at negative card
    /// coordinates made the reference come out SHIFTED rather than clipped, by
    /// exactly the amount it hung off the edge: a WinUI Polyline whose points
    /// go negative is laid out at its own geometry's origin, so the overhang is
    /// pulled back into view. That is a situation the app never has. The ink
    /// layer spans the whole document in positive slot coordinates and the
    /// SCROLLER moves it, which is what these offsets now are.
    /// </summary>
    private static IEnumerable<(string Name, double ScrollX, double ScrollY)> Placements() =>
    [
        ("inside", 0, 0),
        ("touching", -140, 0),
        ("half", -240, 0),
        ("corner", -300, -260),
        ("crossPage", 0, 0),          // the mark itself runs past the PAGE's edge
        ("off", -520, -520),
    ];

    /// <summary>The mark, in card points. One box; the scroll does the rest.</summary>
    private static (double L, double T, double R, double B) PlacedBox(string placement) =>
        placement == "crossPage" ? (300, 140, 560, 300) : (140, 140, 340, 300);

    /// <summary>
    /// The placement suite, and the multi-page suite after it.
    ///
    /// One subject, a rectangle, because these cells are about WHERE a mark is
    /// rather than what shape it is: the subject matrix above already covers
    /// the shapes, and repeating five of them here would quadruple the run for
    /// nothing.
    /// </summary>
    private static async Task<int> PlacementsAsync(
        Panel host, string directory, StringBuilder csv, double device)
    {
        int cells = 0;

        foreach (var (name, scrollX, scrollY) in Placements())
        {
            foreach (int rotation in Rotations)
            {
                var view = PageTransform.For(ContentWidth, ContentHeight, rotation, ContentWidth);
                var (l, t, r, b) = PlacedBox(name);
                var draft = DraftInCard(view, l, t, r, b);

                cells += await OneAsync(
                    csv, host, directory, "placement", name, rotation, device, view, draft,
                    pageTop: 0, scrollX: scrollX, scrollY: scrollY,
                    stem: $"place-{name}-{rotation:D3}");
            }
        }

        // Multi-page: the SAME mark on page 0 and on page 1, with the viewport
        // scrolled onto page 1 for the second. Page 1's mark must land exactly
        // where page 0's did, and neither may show its neighbour's.
        foreach (int rotation in Rotations)
        {
            var view = PageTransform.For(ContentWidth, ContentHeight, rotation, ContentWidth);
            var draft = DraftInCard(view, 140, 140, 340, 300);

            for (int page = 0; page < 2; page++)
            {
                cells += await OneAsync(
                    csv, host, directory, "multipage", $"page{page}", rotation, device, view,
                    draft, pageTop: page * view.CardHeight, scrollX: 0, scrollY: 0,
                    stem: $"page{page}-{rotation:D3}");
            }
        }

        return cells;
    }

    /// <summary>
    /// A draft whose CARD-space box is the one asked for, found by inverting
    /// the page's own turn. Reusing ToContent rather than working out four
    /// rotations of arithmetic by hand is the point: the placement is stated
    /// once and the shared transform decides what it means.
    /// </summary>
    private static ShapeDraft DraftInCard(
        PageTransform view, double l, double t, double r, double b)
    {
        var a = view.ToContent(l, t);
        var c = view.ToContent(r, b);

        return new ShapeDraft(
            ShapeKind.Rectangle,
            Math.Min(a.X, c.X) / Scale, Math.Min(a.Y, c.Y) / Scale,
            Math.Max(a.X, c.X) / Scale, Math.Max(a.Y, c.Y) / Scale);
    }

    /// <summary>
    /// One cell: both renderers, both PNGs, one CSV row.
    ///
    /// No origin framing here, deliberately. The subject suite re-frames each
    /// mark so it can be measured whole; these cells are ABOUT the frame, and
    /// shifting them into view would delete the thing under test. The scroll
    /// offset is the page's own top and nothing else.
    /// </summary>
    private static async Task<int> OneAsync(
        StringBuilder csv, Panel host, string directory, string suite, string name,
        int rotation, double device, PageTransform view, ShapeDraft draft,
        double pageTop, double scrollX, double scrollY, string stem)
    {
        var shape = new ShapeAnnotation(0, draft, InkColor, StrokeWidthNorm);
        var items = ShapeRenderList.From([], [shape]);

        // The scroll carries the page's top AND the placement offset, which is
        // the same number the app's viewport origin carries.
        var projection = new ViewportProjection(1, device, scrollX, scrollY - pageTop);

        Shot reference, candidate;
        try
        {
            reference = await CaptureAsync(
                host, PlacedReference(shape, view, pageTop, scrollX, scrollY),
                ShapeKind.Rectangle, Surface,
                IOPath.Combine(directory, $"{stem}-ref.png"));

            candidate = await CaptureAsync(
                host, PlacedCandidate(items, view, projection, pageTop),
                ShapeKind.Rectangle, Surface,
                IOPath.Combine(directory, $"{stem}-skia.png"));
        }
        catch (Exception ex)
        {
            csv.AppendLine($"{suite},{name},{rotation},1,{device},CAPTURE FAILED: {ex.Message}");
            return 0;
        }

        csv.AppendLine(Compare(
            suite, name, rotation, 1, device, reference, candidate,
            PlacedExpectation(items, view, pageTop, scrollX, scrollY, device)));

        return 1;
    }

    private static UIElement PlacedReference(
        ShapeAnnotation shape, PageTransform view, double pageTop,
        double scrollX, double scrollY)
    {
        var canvas = new Canvas
        {
            Width = Surface,
            Height = Surface,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };

        var shaft = new Polyline
        {
            Stroke = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                OverlayShapeBuilder.ColorFromHex(shape.ColorHex)),
            StrokeThickness = OverlayProjection.ToSlotThickness(shape.StrokeWidth, Scale, view),
        };

        // The page's top goes in as the offset the ink layer applies, and the
        // scroll takes it back out, exactly as the app does.
        OverlayShapeBuilder.ProjectInto(shaft, shape.Outline, Scale, pageTop, view);
        canvas.Children.Add(shaft);

        // The scroll moves the LAYER, which keeps every child at the positive
        // slot coordinates the app gives them and is what the scroller does.
        canvas.RenderTransform = new Microsoft.UI.Xaml.Media.TranslateTransform
        {
            X = scrollX,
            Y = scrollY - pageTop,
        };

        return canvas;
    }

    private static UIElement PlacedCandidate(
        IReadOnlyList<ShapeRenderItem> items, PageTransform view,
        ViewportProjection projection, double pageTop)
    {
        var layer = new SkiaShapeLayer { Width = Surface, Height = Surface };
        layer.Show(items, Scale, _ => pageTop, _ => view, projection);

        return layer;
    }

    /// <summary>
    /// The arithmetic's answer for a placed mark, clamped to the surface.
    ///
    /// Clamped because these cells are about the frame: a mark half off the
    /// edge SHOULD measure as the part that is on it, and comparing against the
    /// unclipped box would report correct clipping as a mismatch.
    /// </summary>
    private static (double L, double T, double R, double B) PlacedExpectation(
        IReadOnlyList<ShapeRenderItem> items, PageTransform view, double pageTop,
        double scrollX, double scrollY, double device)
    {
        double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;

        foreach (var item in items)
        {
            double reach = OverlayProjection.WidthOf(item, Scale, view) / 2;
            foreach (var p in item.Points)
            {
                var (x, y) = OverlayProjection.ToSlot(p, Scale, pageTop, view);
                l = Math.Min(l, x - reach); t = Math.Min(t, y - reach);
                r = Math.Max(r, x + reach); b = Math.Max(b, y + reach);
            }
        }

        // The scroll takes the page's top back out, and only from Y. X is
        // measured from the same left edge on every page in the stack.
        double edge = Surface - (1.0 / device);
        l += scrollX; r += scrollX;
        t += scrollY - pageTop; b += scrollY - pageTop;

        // Nothing of it reaches the surface at all: the expectation is EMPTY,
        // and both renderers drawing nothing is the correct answer rather than
        // a missing measurement.
        if (r < 0 || b < 0 || l > edge || t > edge)
        {
            return (double.NaN, double.NaN, double.NaN, double.NaN);
        }

        return (Math.Clamp(l, 0, edge) * device, Math.Clamp(t, 0, edge) * device,
                Math.Clamp(r, 0, edge) * device, Math.Clamp(b, 0, edge) * device);
    }

    // ---------------- the two renderers ----------------

    /// <summary>
    /// The reference: real XAML elements from OverlayShapeBuilder, laid on a
    /// Canvas the way the ink layer lays them out.
    /// </summary>
    private static UIElement BuildReference(
        ShapeKind? kind, PageTransform view, double zoom, int side)
    {
        // Zoom as a RenderTransform on the layer, which is what the scroller's
        // compositor zoom is: the ink overlay's children keep their slot
        // coordinates and the whole layer is scaled. Sized at 1:1 and scaled,
        // NOT laid out at the zoomed size, or the scale would be applied twice.
        //
        // Scale THEN translate, in that order, which composes to the same
        // (slot * zoom) + origin that ViewportProjection gives Skia.
        //
        // Left and Top alignment stated explicitly. A Canvas defaults to
        // Stretch, and when the frame was briefly larger than the canvas that
        // centred it and put the whole reference 465 device pixels off.
        var (originX, originY) = OriginFor(zoom, SlotBounds(kind, view));

        var moved = new Microsoft.UI.Xaml.Media.TransformGroup();
        moved.Children.Add(new Microsoft.UI.Xaml.Media.ScaleTransform { ScaleX = zoom, ScaleY = zoom });
        moved.Children.Add(new Microsoft.UI.Xaml.Media.TranslateTransform { X = originX, Y = originY });

        var canvas = new Canvas
        {
            Width = Surface,
            Height = Surface,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            RenderTransform = moved,
        };

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
        ShapeKind? kind, PageTransform view, double device, double zoom, int side)
    {
        // The Skia surface really is the zoomed size, because it rasterises
        // once at final resolution rather than being scaled by the compositor.
        // That IS the architectural difference between the two layers, and it
        // is why zoom reaches this one as a number.
        var layer = new SkiaShapeLayer { Width = side, Height = side };

        var items = kind is null
            ? ShapeRenderList.From([], [], inkPreview: ShapeRenderList.InkGuide(0, InkPoints))
            : ShapeRenderList.From([], [ShapeFor(kind.Value)]);

        // The same origin the reference is translated by, so the two pictures
        // are of the same thing in the same place. The device scale is passed
        // in rather than read off this layer, which has no XamlRoot until it is
        // in the tree.
        var (originX, originY) = OriginFor(zoom, SlotBounds(kind, view));

        layer.Show(
            items, Scale, _ => 0, _ => view,
            new ViewportProjection(
                Zoom: zoom,
                DeviceScale: device,
                OriginXDips: originX,
                OriginYDips: originY));

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
    /// about its centreline, and then taken to DEVICE pixels through the zoom
    /// and the display scale, so it can be compared with what came off the two
    /// surfaces.
    /// </summary>
    private static (double L, double T, double R, double B) ExpectedBounds(
        ShapeKind? kind, PageTransform view, double device, double zoom)
    {
        var slot = SlotBounds(kind, view);
        var (ox, oy) = OriginFor(zoom, slot);

        return (((slot.L * zoom) + ox) * device, ((slot.T * zoom) + oy) * device,
                ((slot.R * zoom) + ox) * device, ((slot.B * zoom) + oy) * device);
    }

    /// <summary>The mark's box in slot DIPs, before zoom, origin or display.</summary>
    private static (double L, double T, double R, double B) SlotBounds(
        ShapeKind? kind, PageTransform view)
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

        return (l - reach, t - reach, r + reach, b + reach);
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
        string suite, string name, int rotation, double zoom, double device,
        Shot reference, Shot candidate,
        (double L, double T, double R, double B) expected)
    {
        string boundsVerdict = BoundsVerdict(reference.Bounds, candidate.Bounds);
        string centroidVerdict = Near(
            Math.Max(Math.Abs(reference.CentroidX - candidate.CentroidX),
                     Math.Abs(reference.CentroidY - candidate.CentroidY)), 0.5, 1.0);

        // Same rule for mass: no ink on either side is agreement.
        string massVerdict = reference.Mass <= 0 && candidate.Mass <= 0
            ? "MATCH"
            : Near(
                reference.Mass <= 0 ? 1 : Math.Abs(candidate.Mass - reference.Mass) / reference.Mass,
                0.02, 0.08);

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
            suite, name, rotation, F(zoom), F(device),
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
        // BOTH empty is agreement, not a missing measurement. A mark scrolled
        // entirely off the surface must produce no ink from either renderer,
        // and reporting that as a MISMATCH turned the correctly culled cells
        // into the loudest failures in the report.
        if (a is null && b is null)
        {
            return "MATCH";
        }

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
        // An EMPTY expectation, written as NaN: the arithmetic says nothing of
        // this mark reaches the surface, so no ink is the right answer and ink
        // would be the wrong one.
        if (double.IsNaN(want.L))
        {
            return got is null ? "YES" : "NO (drawn off-surface)";
        }

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
        double.IsNaN(b.L) ? "empty" : $"{b.L:F1} {b.T:F1} {b.R:F1} {b.B:F1}";

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
    private static Border Frame(UIElement content, int side) => new()
    {
        Width = side,
        Height = side,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Color.FromArgb(255, 255, 255, 255)),
        Child = content,
    };

    private static async Task<Shot> CaptureAsync(
        Panel host, UIElement content, ShapeKind? kind, int side, string pngPath)
    {
        var frame = Frame(content, side);
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
