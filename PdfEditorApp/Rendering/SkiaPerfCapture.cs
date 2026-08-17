using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PdfEditorApp.Rendering.Skia;
using PdfEditorApp.Viewport;
using SkiaSharp;
using IOPath = System.IO.Path;

namespace PdfEditorApp.Rendering;

/// <summary>
/// Stage 4, the cost half: how long a preview frame takes in each renderer.
///
/// Parity says the two draw the same picture. It does not say either is fast
/// enough to be the one that runs, and the two are not merely different
/// implementations of one idea: XAML RETAINS a scene of vector elements and
/// redraws what changed, Skia REDRAWS the whole surface every frame. That
/// difference has opposite signs at opposite ends of the mark count, so a
/// single number would hide the answer rather than give it.
///
/// So three costs are measured, not one:
///
///   XAML rebuild   every element reconstructed, then laid out. What the app
///                  actually does today, because UpdateInkPreview drops the
///                  preview element and builds a new one on every change.
///   XAML retained  the scene built once, then ONE element's points refilled.
///                  XAML's best case and its real architectural advantage.
///   Skia full      cull plus paint of every item into a viewport-sized
///                  surface, clearing all of it first. What the layer used to
///                  do on every frame, kept so the improvement is measured
///                  against a baseline taken on the same machine in the same
///                  run rather than against a number from a previous commit.
///   Skia dirty     the same, through DirtyRegionTracker: clip to the union of
///                  where the mark was and where it is going, clear THAT, and
///                  paint. What the layer does now.
///
/// A fourth measurement, RenderTargetBitmap over each renderer's host, was
/// tried as a cross-check and REMOVED. RenderAsync completes only when the
/// compositor presents a frame for the subtree, and a tight await loop never
/// lets the UI thread idle long enough for that to happen: it hung the run at
/// the first call and took the three synchronous measurements down with it,
/// since they share the per-count loop. The parity harness gets away with the
/// same API only because it settles for 480ms before every single call, which
/// is affordable 144 times and not 360. A frame timing that needs a 480ms
/// pause around it is not a frame timing anyway.
///
/// WHAT THE REALISTIC MARK COUNT IS, because the stress numbers invite the
/// wrong reading. The overlay draws the LIVE PREVIEW and nothing else:
/// _allShapes and _allInkStrokes are never added to, and committed marks are
/// PDF annotations drawn by PDFium into the page bitmap. A drag therefore has
/// one item in flight, or two while an arrow's head is separate. The larger
/// counts bound the risk of a future that puts committed marks back on this
/// layer; they are not today's load, and the report says so.
///
/// A diagnostic, like its sibling: it runs only when AYAAN_SKIA_PERF names an
/// output directory, so it cannot fire for a user.
/// </summary>
internal static class SkiaPerfCapture
{
    public const string DirectoryVariable = "AYAAN_SKIA_PERF";

    public static string? RequestedDirectory =>
        Environment.GetEnvironmentVariable(DirectoryVariable) is { Length: > 0 } dir ? dir : null;

    private const double ContentWidth = 400;
    private const double ContentHeight = 500;
    private const double Scale = ContentWidth;
    private const double StrokeWidthNorm = 0.01;
    private const string InkColor = "#FF000000";

    /// <summary>
    /// Two viewports, in logical points, because the two renderers scale with
    /// DIFFERENT things and one size cannot show that.
    ///
    /// XAML's cost follows the MARK COUNT: a vector scene costs what its
    /// elements cost and the compositor rasterises whatever area it likes.
    /// Skia's follows the SURFACE AREA: every frame clears and repaints every
    /// pixel it covers, whether or not a mark is anywhere near it. So the
    /// interesting question for a default switch is not what Skia costs on this
    /// machine's window, it is what it costs on the biggest window a user has.
    ///
    /// 2560x1440 at a 1.5 scale is 3840x2160: a maximised window on a 4K
    /// display, and about four times the pixels of the first row. Measuring it
    /// turns an extrapolation into a number.
    /// </summary>
    private static readonly (int W, int H)[] Viewports = [(1200, 800), (2560, 1440)];

    /// <summary>
    /// One and two are the counts the app actually reaches; the rest bound the
    /// risk. 500 is far past anything the preview layer does today.
    /// </summary>
    private static readonly int[] Counts = [1, 2, 10, 50, 200, 500];

    /// <summary>
    /// Enough repetitions that a single GC pause cannot decide the answer, and
    /// a warmup so JIT and first-use allocation are not counted as frame cost.
    /// </summary>
    private const int Warmup = 20;

    private const int Reps = 200;

    public static string Run(Panel host, string directory)
    {
        Directory.CreateDirectory(directory);

        double device = host.XamlRoot?.RasterizationScale ?? 1.0;
        var view = PageTransform.For(ContentWidth, ContentHeight, 0, ContentWidth);
        var projection = new ViewportProjection(Zoom: 1, DeviceScale: device, OriginXDips: 0, OriginYDips: 0);

        string path = IOPath.Combine(directory, "stage4-perf.csv");
        var csv = new StringBuilder();
        csv.AppendLine(
            "viewportW,viewportH,surfacePx,marks,deviceScale," +
            "xamlRebuildMedianMs,xamlRebuildP95Ms," +
            "xamlRetainedMedianMs,xamlRetainedP95Ms," +
            "skiaFullMedianMs,skiaFullP95Ms," +
            "skiaDirtyMedianMs,skiaDirtyP95Ms," +
            "dirtySpeedup,dirtyVsXamlRebuild,skiaDrawn");

        foreach (var (vw, vh) in Viewports)
        foreach (int count in Counts)
        {
            var shapes = SceneOf(count);
            var items = ShapeRenderList.From([], shapes);

            var rebuild = Time(() => BuildScene(shapes, view, vw, vh), Reps);
            var retained = TimeRetained(shapes, view, vw, vh);
            var (paint, drawn) = TimeSkiaPaint(items, view, projection, device, vw, vh);
            var dirty = TimeSkiaDirty(count, view, projection, device, vw, vh);

            csv.AppendLine(string.Join(",",
                vw.ToString(CultureInfo.InvariantCulture),
                vh.ToString(CultureInfo.InvariantCulture),
                ((long)Math.Round(vw * device) * (long)Math.Round(vh * device))
                    .ToString(CultureInfo.InvariantCulture),
                count.ToString(CultureInfo.InvariantCulture),
                F(device),
                F(rebuild.Median), F(rebuild.P95),
                F(retained.Median), F(retained.P95),
                F(paint.Median), F(paint.P95),
                F(dirty.Median), F(dirty.P95),
                Ratio(paint.Median, dirty.Median),
                Ratio(dirty.Median, rebuild.Median),
                drawn.ToString(CultureInfo.InvariantCulture)));

            // Written after every count, not once at the end. The first version
            // wrote the file last, so when it hung the whole run was lost and
            // there was nothing to say which count it had reached.
            File.WriteAllText(path, csv.ToString());
        }

        return $"{Counts.Length} counts, stage4-perf.csv";
    }

    private static string F(double v) => v.ToString("F4", CultureInfo.InvariantCulture);

    /// <summary>Skia's cost as a multiple of the XAML cost it is compared with.</summary>
    private static string Ratio(double skia, double xaml) =>
        xaml <= 0 ? "n/a" : (skia / xaml).ToString("F2", CultureInfo.InvariantCulture) + "x";

    // ---------------- the scene ----------------

    /// <summary>
    /// Marks spread over the page in a grid, cycling the four shape kinds so
    /// the number is not one degenerate case repeated. Deterministic: the
    /// position comes from the index, never from a random source.
    /// </summary>
    private static IReadOnlyList<ShapeAnnotation> SceneOf(int count) => SceneOf(count, 0);

    private static IReadOnlyList<ShapeAnnotation> SceneOf(int count, double nudge)
    {
        var kinds = new[] { ShapeKind.Rectangle, ShapeKind.Ellipse, ShapeKind.Line, ShapeKind.Arrow };
        var shapes = new List<ShapeAnnotation>(count);

        int perRow = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(count)));

        for (int i = 0; i < count; i++)
        {
            double col = i % perRow;
            double row = i / perRow;
            double cell = 1.0 / perRow;

            double l = (col * cell) + (cell * 0.1) + nudge;
            double t = (row * cell) + (cell * 0.1) + nudge;

            shapes.Add(new ShapeAnnotation(
                0,
                new ShapeDraft(kinds[i % kinds.Length], l, t, l + (cell * 0.8), t + (cell * 0.8)),
                InkColor,
                StrokeWidthNorm));
        }

        return shapes;
    }

    // ---------------- the three costs ----------------

    /// <summary>
    /// A full XAML scene: every mark reconstructed as elements, then laid out.
    /// UpdateLayout is included on purpose. Constructing elements and never
    /// measuring them defers most of the cost past the clock, which would
    /// flatter this side.
    /// </summary>
    private static Canvas BuildScene(
        IReadOnlyList<ShapeAnnotation> shapes, PageTransform view, int vw, int vh)
    {
        var canvas = new Canvas
        {
            Width = vw,
            Height = vh,
            IsHitTestVisible = false,
        };

        foreach (var shape in shapes)
        {
            var stroke = new InkStrokeAnnotation(0, shape.Outline, shape.ColorHex, shape.StrokeWidth);
            canvas.Children.Add(OverlayShapeBuilder.Stroke(stroke, Scale, 0, view));

            if (shape.Head.Count > 0)
            {
                canvas.Children.Add(
                    OverlayShapeBuilder.FilledHead(shape.Head, shape.ColorHex, Scale, 0, view));
            }
        }

        canvas.UpdateLayout();
        return canvas;
    }

    /// <summary>
    /// XAML's best case: the scene exists, and a frame refills ONE element's
    /// points, which is what a retained-mode overlay would do while a mark is
    /// being dragged over a page of settled ones.
    /// </summary>
    private static Stats TimeRetained(
        IReadOnlyList<ShapeAnnotation> shapes, PageTransform view, int vw, int vh)
    {
        var canvas = BuildScene(shapes, view, vw, vh);
        var line = (Microsoft.UI.Xaml.Shapes.Polyline)canvas.Children[0];
        var points = shapes[0].Outline;

        return Time(() =>
        {
            OverlayShapeBuilder.ProjectInto(line, points, Scale, 0, view);
            canvas.UpdateLayout();
            return canvas;
        }, Reps);
    }

    /// <summary>
    /// Skia's real frame: cull, then paint into a surface the size of the
    /// viewport at device resolution. The count that survived culling is
    /// returned so a suspiciously fast result cannot pass as a fast one.
    /// </summary>
    private static (Stats Stats, int Drawn) TimeSkiaPaint(
        IReadOnlyList<ShapeRenderItem> items, PageTransform view,
        ViewportProjection projection, double device, int vw, int vh)
    {
        int pixelW = (int)Math.Round(vw * device);
        int pixelH = (int)Math.Round(vh * device);

        using var surface = SKSurface.Create(
            new SKImageInfo(pixelW, pixelH, SKColorType.Bgra8888, SKAlphaType.Premul));

        var canvas = surface.Canvas;
        var bounds = projection.VisibleSlotBounds(pixelW, pixelH, 64);
        int drawn = ShapeCulling.Visible(items, bounds, Scale, _ => 0, _ => view).Count;

        var stats = Time(() =>
        {
            canvas.Clear(SKColors.Transparent);
            var visible = ShapeCulling.Visible(items, bounds, Scale, _ => 0, _ => view);
            ShapeSkiaPainter.PaintViewport(canvas, visible, Scale, _ => 0, _ => view, projection);
            surface.Flush();
            return visible;
        }, Reps);

        return (stats, drawn);
    }

    /// <summary>How many distinct positions a simulated drag cycles through.</summary>
    private const int DragSteps = 16;

    /// <summary>
    /// A frame of a DRAG, which is the gesture the whole exercise is about.
    ///
    /// The mark moves between repetitions, so every timed frame is a real
    /// partial paint with a genuine union of two different positions. Measuring
    /// a still mark would clear a rectangle it had just cleared and flatter the
    /// result.
    ///
    /// The positions are built BEFORE the clock starts. Rebuilding the render
    /// list is real per-frame work in the app, but it is not what this is
    /// comparing, and leaving it in would put the same cost on both sides of a
    /// ratio meant to isolate the clear.
    /// </summary>
    private static Stats TimeSkiaDirty(
        int count, PageTransform view, ViewportProjection projection,
        double device, int vw, int vh)
    {
        int pixelW = (int)Math.Round(vw * device);
        int pixelH = (int)Math.Round(vh * device);

        var steps = new List<IReadOnlyList<ShapeRenderItem>>(DragSteps);
        for (int i = 0; i < DragSteps; i++)
        {
            steps.Add(ShapeRenderList.From([], SceneOf(count, i * 0.004)));
        }

        using var surface = SKSurface.Create(
            new SKImageInfo(pixelW, pixelH, SKColorType.Bgra8888, SKAlphaType.Premul));

        var canvas = surface.Canvas;
        var tracker = new DirtyRegionTracker();
        var bounds = projection.VisibleSlotBounds(pixelW, pixelH, 64);
        int at = 0;

        return Time(() =>
        {
            var items = steps[at++ % DragSteps];

            var plan = tracker.Plan(
                items, Scale, _ => 0, _ => view, projection, pixelW, pixelH);

            if (plan.Scope != PaintScope.Nothing)
            {
                int saved = canvas.Save();
                canvas.ClipRect(SKRect.Create(
                    plan.Rect.L, plan.Rect.T,
                    plan.Rect.R - plan.Rect.L, plan.Rect.B - plan.Rect.T));
                canvas.Clear(SKColors.Transparent);

                var visible = ShapeCulling.Visible(items, bounds, Scale, _ => 0, _ => view);
                if (visible.Count > 0)
                {
                    ShapeSkiaPainter.PaintViewport(
                        canvas, visible, Scale, _ => 0, _ => view, projection);
                }

                canvas.RestoreToCount(saved);
            }

            tracker.Painted(plan);
            surface.Flush();
            return plan;
        }, Reps);
    }

    // ---------------- timing ----------------

    private readonly record struct Stats(double Median, double P95);

    /// <summary>
    /// Median and 95th percentile, not mean: one GC pause in two hundred
    /// repetitions moves a mean and does not move a median, and the tail is
    /// reported separately rather than averaged into the middle.
    ///
    /// The delegate returns its result and the result is kept, so nothing being
    /// measured can be optimised away as dead.
    /// </summary>
    private static Stats Time<T>(Func<T> work, int reps)
    {
        object? sink = null;

        for (int i = 0; i < Warmup; i++)
        {
            sink = work();
        }

        var samples = new List<double>(reps);

        for (int i = 0; i < reps; i++)
        {
            long start = Stopwatch.GetTimestamp();
            sink = work();
            long end = Stopwatch.GetTimestamp();
            samples.Add((end - start) * 1000.0 / Stopwatch.Frequency);
        }

        GC.KeepAlive(sink);
        samples.Sort();

        return new Stats(samples[samples.Count / 2], samples[(int)(samples.Count * 0.95)]);
    }
}
