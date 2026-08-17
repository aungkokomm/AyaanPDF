using System;
using System.Collections.Generic;
using System.Linq;
using PdfEditorApp.Rendering.Skia;
using PdfEditorApp.Viewport;
using SkiaSharp;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// How far a mark's ink reaches past the box that says where it is.
///
/// Dirty-region painting clears and repaints a rectangle instead of the whole
/// surface, and the rectangle it uses is the cheap one:
/// <see cref="OverlayProjection.SlotBoundsOf"/>, the points' box opened out by
/// half the stroke. Real ink lands OUTSIDE that box for two separate reasons,
/// and clearing a box that is even a pixel too small leaves a rind of the old
/// frame behind on every drag.
///
///   antialiasing  spreads an edge over about a pixel, so the outermost lit
///                 pixel sits past the geometric edge whatever the geometry is.
///   mitred joins  overshoot their vertex. Skia's miter limit is 4, so a sharp
///                 enough corner reaches well past the half-stroke the cheap
///                 box allows for, and the freehand guide is full of sharp
///                 corners.
///
/// So the pad is DERIVED here rather than guessed. Every subject the preview
/// layer can draw is painted at every rotation, zoom and display scale in the
/// Stage 4 matrix, the outermost lit pixel is found on each side, and the
/// largest overshoot any of them produces is the number. A guessed 2px would
/// be a claim about the miter limit nobody had checked.
///
/// The measurement is deliberately paranoid about what counts as ink: the
/// surface is cleared to white and ANY pixel that is not pure white counts,
/// down to one part in 255. A coverage threshold here would under-report the
/// faint outer fringe, which is exactly the part that gets left behind.
/// </summary>
public class DirtyRegionBleedTests
{
    private const double Scale = 800;
    private const double ContentH = 1000;
    private const double Stroke = 0.006;
    /// <summary>
    /// Big enough that the largest cell in the matrix fits with room to spare.
    /// At zoom 2 and scale 2 a mark is four times its slot size, and a mark
    /// clipped by the edge reports a fringe that is missing rather than absent,
    /// which reads as a SMALL bleed and would quietly understate the pad. The
    /// first version of this file was 900 and did exactly that.
    /// </summary>
    private const int Surface = 1280;

    /// <summary>Where the mark is parked, so its bleed has room to be seen.</summary>
    private const double MarginPx = 60;

    private static readonly int[] Rotations = [0, 90, 180, 270];
    /// <summary>
    /// Zoomed OUT as well as in. The miter term of the pad is proportional to
    /// the stroke, so it shrinks towards nothing as the view zooms out while
    /// the antialiasing fringe stays about a pixel wide whatever the scale.
    /// Without a low zoom here the fixed term of the pad is never the one
    /// holding the line, and removing it would look safe.
    /// </summary>
    private static readonly double[] Zooms = [0.25, 0.5, 1.0, 2.0];
    private static readonly double[] Dpis = [1.0, 1.5, 2.0];

    /// <summary>
    /// The five things the preview layer draws. An arrow is two items, a shaft
    /// and a filled head, and both are measured because they bleed differently:
    /// the head is a fill plus a hairline with a very sharp tip.
    /// </summary>
    public static IEnumerable<object[]> Matrix() =>
        from subject in new[] { "rect", "ellipse", "line", "arrow", "ink", "hairpin" }
        from rotation in Rotations
        from zoom in Zooms
        from dpi in Dpis
        select new object[] { subject, rotation, zoom, dpi };

    /// <summary>A stroke with sharp reversals, so mitred joins are exercised.</summary>
    private static readonly (double X, double Y)[] InkPoints =
    [
        (0.06, 0.08), (0.112, 0.136), (0.164, 0.112), (0.22, 0.188),
        (0.248, 0.284), (0.296, 0.264), (0.32, 0.34),
    ];

    /// <summary>
    /// A freehand stroke doubling back on itself as sharply as a hand can move.
    ///
    /// The case the gentler fixture above gets lucky on. Skia mitres a join out
    /// to a limit of four, so a near-reversal throws the join's tip a long way
    /// past the vertex, and a vertex at the edge of the mark's box throws it
    /// past the box. Freehand is the one subject whose angles the USER picks,
    /// so this is the one that has to be bounded by argument rather than by
    /// whatever the sample stroke happened to do.
    /// </summary>
    private static readonly (double X, double Y)[] HairpinPoints =
    [
        (0.10, 0.30), (0.30, 0.10), (0.3008, 0.1016), (0.11, 0.31),
    ];

    private static IReadOnlyList<ShapeRenderItem> ItemsFor(string subject)
    {
        if (subject == "ink")
        {
            return [ShapeRenderList.InkGuide(0, InkPoints)];
        }

        if (subject == "hairpin")
        {
            return [ShapeRenderList.InkGuide(0, HairpinPoints)];
        }

        var kind = subject switch
        {
            "rect" => ShapeKind.Rectangle,
            "ellipse" => ShapeKind.Ellipse,
            "line" => ShapeKind.Line,
            _ => ShapeKind.Arrow,
        };

        // A compact mark, so four-times magnification still fits the surface.
        // Shrinking the geometry does not weaken the measurement: the stroke
        // width is unchanged by it, and the angles the joins are made of are
        // preserved, so both terms of the bleed are exactly as they were.
        return ShapeRenderList.From(
            [], [new ShapeAnnotation(0, new ShapeDraft(kind, 0.10, 0.10, 0.40, 0.36), "#FF000000", Stroke)]);
    }

    /// <summary>
    /// The UNPADDED box, in device pixels. Only used to park the mark on the
    /// surface and to say how much of the padded rectangle is padding.
    /// </summary>
    private static (double L, double T, double R, double B) CheapDeviceBounds(
        IReadOnlyList<ShapeRenderItem> items, PageTransform view, ViewportProjection p)
    {
        double l = double.MaxValue, t = double.MaxValue;
        double r = double.MinValue, b = double.MinValue;

        foreach (var item in items)
        {
            var (sl, st, sr, sb) = OverlayProjection.SlotBoundsOf(item, Scale, 0, view);
            var (dl, dt) = p.SlotToDevice(sl, st);
            var (dr, db) = p.SlotToDevice(sr, sb);

            l = Math.Min(l, dl); t = Math.Min(t, dt);
            r = Math.Max(r, dr); b = Math.Max(b, db);
        }

        return (l, t, r, b);
    }

    /// <summary>
    /// The rectangle the layer would actually clear and repaint: the thing
    /// under test, called exactly as the layer will call it.
    /// </summary>
    private static (double L, double T, double R, double B) DirtyBounds(
        IReadOnlyList<ShapeRenderItem> items, PageTransform view, ViewportProjection p) =>
        DirtyRegion.DeviceBoundsOf(items, Scale, _ => 0, _ => view, p)!.Value;

    /// <summary>
    /// The overshoot on each side, in device pixels. Positive means ink landed
    /// outside the cheap box, which is what the pad has to cover.
    /// </summary>
    internal static (double L, double T, double R, double B) OvershootFor(
        string subject, int rotation, double zoom, double dpi)
    {
        var view = PageTransform.For(Scale, ContentH, rotation, Scale);
        var items = ItemsFor(subject);

        // Park the mark at a fixed margin so nothing is clipped by the surface
        // and the fringe on every side is inside the bitmap to be found.
        var probe = new ViewportProjection(zoom, dpi, 0, 0);
        var (pl, pt, _, _) = CheapDeviceBounds(items, view, probe);
        var projection = new ViewportProjection(
            zoom, dpi, ((MarginPx - pl) / dpi), ((MarginPx - pt) / dpi));

        using var bitmap = new SKBitmap(Surface, Surface, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            ShapeSkiaPainter.PaintViewport(canvas, items, Scale, _ => 0, _ => view, projection);
        }

        var (il, it, ir, ib) = InkedBounds(bitmap);

        Assert.True(il >= 0, $"{subject} {rotation} z{zoom} d{dpi}: nothing was drawn");

        // A mark touching the border was cut by it, and its missing fringe
        // would read as a small bleed. That is a broken measurement, not a
        // small number, so it fails here rather than lowering the pad.
        Assert.True(
            il > 0 && it > 0 && ir < Surface - 1 && ib < Surface - 1,
            $"{subject} {rotation} z{zoom} d{dpi}: mark reached the surface edge "
            + $"({il},{it})-({ir},{ib}) on {Surface}px, so its bleed was clipped");

        // Pixel index i covers [i, i+1), so the lit pixel at index il occupies
        // up to il+1. Comparing the far edges the same way on both sides keeps
        // this an overshoot and not an off-by-one.
        var (dl2, dt2, dr2, db2) = DirtyBounds(items, view, projection);

        // Positive means ink landed OUTSIDE the rectangle that would be
        // cleared, which is precisely a ghost.
        return (dl2 - il, dt2 - it, (ir + 1) - dr2, (ib + 1) - db2);
    }

    private static (int L, int T, int R, int B) InkedBounds(SKBitmap bitmap)
    {
        int l = int.MaxValue, t = int.MaxValue, r = int.MinValue, b = int.MinValue;

        var pixels = bitmap.GetPixelSpan();
        int stride = bitmap.RowBytes;

        for (int y = 0; y < bitmap.Height; y++)
        {
            int row = y * stride;
            for (int x = 0; x < bitmap.Width; x++)
            {
                int at = row + (x * 4);

                // Anything at all that is not pure white. The faintest fringe
                // pixel is the one that gets left behind, so it counts.
                if (pixels[at] == 255 && pixels[at + 1] == 255 && pixels[at + 2] == 255)
                {
                    continue;
                }

                l = Math.Min(l, x); t = Math.Min(t, y);
                r = Math.Max(r, x); b = Math.Max(b, y);
            }
        }

        return (l, t, r, b);
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void every_lit_pixel_lies_inside_the_rectangle_that_would_be_cleared(
        string subject, int rotation, double zoom, double dpi)
    {
        // The contract, stated as the thing that actually goes wrong: any ink
        // outside the cleared rectangle is a pixel of the previous frame that
        // survives into the next one. A ghost.
        var (l, t, r, b) = OvershootFor(subject, rotation, zoom, dpi);

        Assert.True(l <= 0, $"ink reaches {l:F2}px past the LEFT of the dirty rect");
        Assert.True(t <= 0, $"ink reaches {t:F2}px past the TOP of the dirty rect");
        Assert.True(r <= 0, $"ink reaches {r:F2}px past the RIGHT of the dirty rect");
        Assert.True(b <= 0, $"ink reaches {b:F2}px past the BOTTOM of the dirty rect");
    }

    [Theory]
    [InlineData("rect", 1.0)]
    [InlineData("ellipse", 1.0)]
    [InlineData("line", 1.0)]
    [InlineData("arrow", 1.0)]
    [InlineData("ink", 1.0)]
    [InlineData("hairpin", 3.5)]
    public void the_measured_bleed_is_still_what_the_pad_was_derived_from(
        string subject, double documented)
    {
        // The evidence, kept in the suite rather than in a commit message.
        //
        // These are overshoots past the UNPADDED box, which is what the pad in
        // DirtyRegion was sized against: about a pixel of antialiasing for
        // every fixed shape, and three and a half for a deliberate freehand
        // hairpin whose mitred join throws its tip well past its own vertex.
        //
        // If a change to the painter makes ink spread further, this fails with
        // the new number and the pad gets re-derived deliberately. Without it
        // the pad would quietly lose its headroom and the first symptom would
        // be a ghost on somebody's screen.
        double worst = (
            from cell in Matrix()
            where (string)cell[0] == subject
            let view = PageTransform.For(Scale, ContentH, (int)cell[1], Scale)
            let items = ItemsFor(subject)
            let p = new ViewportProjection((double)cell[2], (double)cell[3], 0, 0)
            let cheap = CheapDeviceBounds(items, view, p)
            let dirty = DirtyBounds(items, view, p)
            let pad = new[]
            {
                cheap.L - dirty.L, cheap.T - dirty.T,
                dirty.R - cheap.R, dirty.B - cheap.B,
            }.Min()
            let over = OvershootFor(subject, (int)cell[1], (double)cell[2], (double)cell[3])
            select new[] { over.L, over.T, over.R, over.B }.Max() + pad).Max();

        Assert.InRange(worst, 0.0, documented);
    }
}
