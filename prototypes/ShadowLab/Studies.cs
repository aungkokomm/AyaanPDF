using System.Diagnostics;
using PdfEditorApp.Viewport;
using SkiaSharp;

namespace ShadowLab;

/// <summary>
/// The measurements behind the two decisions.
/// </summary>
public static class Studies
{
    private const int Capture = 1000;

    /// <summary>The zoom the answer has to survive. The app stops at 8x, and a
    /// point is 96/72 device pixels at 100% on an ordinary display, so the worst
    /// case is about 10.7 device pixels to the point.</summary>
    private const double WorstCasePxPerPt = 8 * 96.0 / 72.0;

    private static string Fixture = "";

    public static void RunAll(string fixture, string outDir)
    {
        Fixture = fixture;
        Resolution();
        Cropping();
        Transforms();
        RasterCost();
        Sheet(outDir);
    }

    /// <summary>A stroke-only rectangle, which is the case with the most edge
    /// per unit area and so the hardest to under-sample.</summary>
    private static (IReadOnlyList<ShapeRenderItem> Items, DropShadow Shadow) Case(double blurPts) =>
        (Scenes.Grid(Scenes.Shadow(distancePts: 30, blurPts: blurPts))[1].Items,
         Scenes.Shadow(distancePts: 30, blurPts: blurPts));

    /// <summary>How many pixels the box needs at a given sampling rate. The
    /// fixture page is 200pt across and the scenes are laid out in a 1000-unit
    /// capture space, so a normalized 1.0 is the page width.</summary>
    private static (int W, int H) PixelsFor(
        (double L, double T, double R, double B) box, double pxPerPt, double pageWidthPts)
    {
        return ((int)Math.Ceiling((box.R - box.L) * pageWidthPts * pxPerPt),
                (int)Math.Ceiling((box.B - box.T) * pageWidthPts * pxPerPt));
    }

    private static double PageWidthPts => 200;

    // ---------------- 1. how finely ----------------

    private static void Resolution()
    {
        Console.WriteLine();
        Console.WriteLine("=== 1. RASTERISATION RESOLUTION ===");
        Console.WriteLine("Each rate is compared against a 32 px/pt reference of the same shadow,");
        Console.WriteLine("both embedded in a PDF and rendered at 8x zoom (10.7 device px/pt).");
        Console.WriteLine("A difference of 1 level is invisible; the eye needs about 3.");
        Console.WriteLine();

        int renderWidth = (int)Math.Round(PageWidthPts * WorstCasePxPerPt);

        foreach (double blur in new double[] { 0, 4, 8, 24 })
        {
            var (items, shadow) = Case(blur);
            var box = Decisions.ShadowBounds(items, shadow);

            var (rw, rh) = PixelsFor(box, 32, PageWidthPts);
            using var reference = Decisions.ThroughPdf(
                Fixture, Decisions.Rasterize(items, shadow, box, rw, rh), box, renderWidth);

            Console.WriteLine($"  blur {blur,2:F0}pt");
            Console.WriteLine($"    {"px/pt",6} {"dpi",6} {"bitmap",12} {"KB",7} {"mean",7} {"worst",6} {"px off",8}");

            foreach (double rate in new double[] { 1, 1.5, 2, 3, 4, 6, 8, 12 })
            {
                var (w, h) = PixelsFor(box, rate, PageWidthPts);
                using var bitmap = Decisions.Rasterize(items, shadow, box, w, h);
                using var page = Decisions.ThroughPdf(Fixture, bitmap, box, renderWidth);

                var (mean, max, off) = Decisions.Compare(reference, page);
                double kb = EncodedKb(bitmap);

                Console.WriteLine(
                    $"    {rate,6:F1} {rate * 72,6:F0} {w + "x" + h,12} {kb,7:F1} {mean,7:F2} {max,6} {off,8}");
            }

            Console.WriteLine();
        }
    }

    /// <summary>What the bitmap costs in a file, as PNG, which is what a PDF
    /// image object compresses to in the same ballpark.</summary>
    private static double EncodedKb(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return data.Size / 1024.0;
    }

    // ---------------- 2. what box ----------------

    private static void Cropping()
    {
        Console.WriteLine("=== 2. WHAT BOX THE BITMAP COVERS ===");
        Console.WriteLine("Same shadow, same 4 px/pt sampling, three different boxes.");
        Console.WriteLine();

        var (items, shadow) = Case(8);
        var tight = Decisions.ShadowBounds(items, shadow);

        double sl = double.MaxValue, st = double.MaxValue, sr = double.MinValue, sb = double.MinValue;
        foreach (var item in items)
        {
            foreach (var (x, y) in item.Points)
            {
                sl = Math.Min(sl, x); st = Math.Min(st, y);
                sr = Math.Max(sr, x); sb = Math.Max(sb, y);
            }
        }

        Console.WriteLine($"    {"box",-24} {"bitmap",12} {"KB",8} {"vs tight",10}");

        double tightKb = 0;
        foreach (var (name, box) in new (string, (double, double, double, double))[]
        {
            ("tight to the shadow", tight),
            ("the shape's own box", (sl, st, sr, sb)),
            ("the whole page", (0, 0, 1, 1)),
        })
        {
            var (w, h) = PixelsFor(box, 4, PageWidthPts);
            using var bitmap = Decisions.Rasterize(items, shadow, box, w, h);
            double kb = EncodedKb(bitmap);
            if (tightKb == 0) { tightKb = kb; }

            Console.WriteLine($"    {name,-24} {w + "x" + h,12} {kb,8:F1} {kb / tightKb,9:F1}x");
        }

        Console.WriteLine();
        Console.WriteLine("  Does the shape's own box clip the shadow?");
        int renderWidth = (int)Math.Round(PageWidthPts * WorstCasePxPerPt);
        var (tw, th) = PixelsFor(tight, 4, PageWidthPts);
        using (var full = Decisions.ThroughPdf(
            Fixture, Decisions.Rasterize(items, shadow, tight, tw, th), tight, renderWidth))
        {
            var shapeBox = (sl, st, sr, sb);
            var (cw, ch) = PixelsFor(shapeBox, 4, PageWidthPts);
            using var clipped = Decisions.ThroughPdf(
                Fixture, Decisions.Rasterize(items, shadow, shapeBox, cw, ch), shapeBox, renderWidth);

            var (mean, max, off) = Decisions.Compare(full, clipped);
            Console.WriteLine($"    mean {mean:F2}, worst {max}, {off} pixels off by more than one");
        }

        Console.WriteLine();
        Console.WriteLine("  How much reach the crop needs, as multiples of sigma:");
        var (rw2, rh2) = PixelsFor(tight, 4, PageWidthPts);
        using var reference = Decisions.ThroughPdf(
            Fixture, Decisions.Rasterize(items, shadow, tight, rw2, rh2), tight, renderWidth);

        foreach (double sigmas in new double[] { 1, 1.5, 2, 2.5, 3 })
        {
            var box = Reach(items, shadow, sigmas);
            var (w, h) = PixelsFor(box, 4, PageWidthPts);
            using var bitmap = Decisions.Rasterize(items, shadow, box, w, h);
            using var page = Decisions.ThroughPdf(Fixture, bitmap, box, renderWidth);

            var (mean, max, off) = Decisions.Compare(reference, page);
            Console.WriteLine($"    {sigmas,4:F1} sigma: mean {mean,6:F2}, worst {max,4}, {off,6} pixels off");
        }

        Console.WriteLine();
    }

    private static (double L, double T, double R, double B) Reach(
        IReadOnlyList<ShapeRenderItem> group, DropShadow shadow, double sigmas)
    {
        double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;
        double pad = 0;

        foreach (var item in group)
        {
            foreach (var (x, y) in item.Points)
            {
                l = Math.Min(l, x); t = Math.Min(t, y);
                r = Math.Max(r, x); b = Math.Max(b, y);
            }

            pad = Math.Max(pad, item.StrokeWidth / 2);
        }

        double reach = pad + (shadow.Softness / 2 * sigmas);
        return (l + shadow.OffsetX - reach, t + shadow.OffsetY - reach,
                r + shadow.OffsetX + reach, b + shadow.OffsetY + reach);
    }

    // ---------------- 3. what happens when the shape changes ----------------

    private static void Transforms()
    {
        Console.WriteLine("=== 3. REUSING THE BITMAP WHEN THE SHAPE CHANGES ===");
        Console.WriteLine("Each edit, done by transforming the existing bitmap, compared against");
        Console.WriteLine("re-rasterising the shadow for the new state. Both through the PDF.");
        Console.WriteLine();

        var (items, shadow) = Case(8);
        var box = Decisions.ShadowBounds(items, shadow);
        int renderWidth = (int)Math.Round(PageWidthPts * WorstCasePxPerPt);
        var (w, h) = PixelsFor(box, 4, PageWidthPts);

        // MOVE. The same bitmap, the same size, a different place. The stamp's
        // rect is the only thing that changes.
        var moved = (box.L + 0.06, box.T + 0.04, box.R + 0.06, box.B + 0.04);
        using (var reused = Decisions.ThroughPdf(
            Fixture, Decisions.Rasterize(items, shadow, box, w, h), moved, renderWidth))
        {
            var shifted = items
                .Select(i => i with
                {
                    Points = i.Points.Select(p => (p.X + 0.06, p.Y + 0.04)).ToList(),
                })
                .ToList();

            var freshBox = Decisions.ShadowBounds(shifted, shadow);
            var (fw, fh) = PixelsFor(freshBox, 4, PageWidthPts);
            using var fresh = Decisions.ThroughPdf(
                Fixture, Decisions.Rasterize(shifted, shadow, freshBox, fw, fh), freshBox, renderWidth);

            Report("move, bitmap reused", Decisions.Compare(fresh, reused));
        }

        // RESIZE. The same bitmap stretched over a bigger rect, which stretches
        // the blur with it.
        var bigger = (box.L, box.T, box.L + ((box.R - box.L) * 1.6), box.T + ((box.B - box.T) * 1.6));
        using (var stretched = Decisions.ThroughPdf(
            Fixture, Decisions.Rasterize(items, shadow, box, w, h), bigger, renderWidth))
        {
            var grown = items
                .Select(i => i with
                {
                    Points = i.Points.Select(p => (
                        box.L + ((p.X - box.L) * 1.6),
                        box.T + ((p.Y - box.T) * 1.6))).ToList(),
                })
                .ToList();

            var freshBox = Decisions.ShadowBounds(grown, shadow);
            var (fw, fh) = PixelsFor(freshBox, 4, PageWidthPts);
            using var fresh = Decisions.ThroughPdf(
                Fixture, Decisions.Rasterize(grown, shadow, freshBox, fw, fh), freshBox, renderWidth);

            Report("resize 1.6x, bitmap stretched", Decisions.Compare(fresh, stretched));
        }

        // ROTATE. Re-rasterised at the angle, against the shadow turned with
        // the shape, to show whether the light stays where it was.
        var turned = Rotate(items, 30);
        var turnedBox = Decisions.ShadowBounds(turned, shadow);
        var (tw, th) = PixelsFor(turnedBox, 4, PageWidthPts);
        using (var fresh = Decisions.ThroughPdf(
            Fixture, Decisions.Rasterize(turned, shadow, turnedBox, tw, th), turnedBox, renderWidth))
        {
            // The same bitmap turned bodily, which turns the OFFSET too.
            using var spun = Decisions.ThroughPdf(
                Fixture, Decisions.Rasterize(items, shadow, turnedBox, tw, th, rotateDeg: 30),
                turnedBox, renderWidth);

            Report("rotate 30 deg, bitmap turned whole", Decisions.Compare(fresh, spun));
        }

        Console.WriteLine();
    }

    private static IReadOnlyList<ShapeRenderItem> Rotate(
        IReadOnlyList<ShapeRenderItem> items, double degrees)
    {
        double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;
        foreach (var item in items)
        {
            foreach (var (x, y) in item.Points)
            {
                l = Math.Min(l, x); t = Math.Min(t, y);
                r = Math.Max(r, x); b = Math.Max(b, y);
            }
        }

        double cx = (l + r) / 2, cy = (t + b) / 2;
        double rad = degrees * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);

        return items
            .Select(i => i with
            {
                Points = i.Points
                    .Select(p => (
                        cx + (((p.X - cx) * cos) - ((p.Y - cy) * sin)),
                        cy + (((p.X - cx) * sin) + ((p.Y - cy) * cos))))
                    .ToList(),
            })
            .ToList();
    }

    private static void Report(string what, (double Mean, int Max, int Off) diff) =>
        Console.WriteLine(
            $"    {what,-36} mean {diff.Mean,6:F2}  worst {diff.Max,4}  {diff.Off,7} px off");

    // ---------------- 4. can we just always re-rasterise ----------------

    private static void RasterCost()
    {
        Console.WriteLine("=== 4. COST OF RASTERISING ONE SHADOW ===");
        Console.WriteLine("At 4 px/pt, tight box. This runs once per COMMIT, not per frame.");
        Console.WriteLine();

        foreach (double blur in new double[] { 0, 8, 24 })
        {
            var (items, shadow) = Case(blur);
            var box = Decisions.ShadowBounds(items, shadow);
            var (w, h) = PixelsFor(box, 4, PageWidthPts);

            for (int i = 0; i < 5; i++)
            {
                Decisions.Rasterize(items, shadow, box, w, h).Dispose();
            }

            var watch = Stopwatch.StartNew();
            const int runs = 60;
            for (int i = 0; i < runs; i++)
            {
                Decisions.Rasterize(items, shadow, box, w, h).Dispose();
            }

            watch.Stop();
            Console.WriteLine(
                $"    blur {blur,2:F0}pt  {w + "x" + h,10}  {watch.Elapsed.TotalMilliseconds / runs,6:F2} ms");
        }

        Console.WriteLine();
    }

    // ---------------- the picture ----------------

    private static void Sheet(string outDir)
    {
        double[] rates = [1, 2, 4, 8];
        double[] blurs = [0, 8];

        const int cell = 420;
        const int header = 34;
        const int gutter = 10;

        var sheet = new SKBitmap(
            (rates.Length * (cell + gutter)) + gutter,
            header + (blurs.Length * (cell + gutter)) + gutter,
            SKColorType.Rgba8888, SKAlphaType.Premul);

        using var canvas = new SKCanvas(sheet);
        canvas.Clear(new SKColor(0xF3, 0xF4, 0xF6));

        using var font = new SKFont(
            SKTypeface.FromFamilyName("Segoe UI", SKFontStyleWeight.SemiBold,
                SKFontStyleWidth.Normal, SKFontStyleSlant.Upright), 15);
        using var text = new SKPaint { Color = new SKColor(0x11, 0x18, 0x27), IsAntialias = true };

        int renderWidth = (int)Math.Round(PageWidthPts * WorstCasePxPerPt);

        for (int c = 0; c < rates.Length; c++)
        {
            canvas.DrawText(
                $"{rates[c]:F0} px/pt ({rates[c] * 72:F0} dpi)",
                (c * (cell + gutter)) + gutter, 24, SKTextAlign.Left, font, text);
        }

        for (int r = 0; r < blurs.Length; r++)
        {
            var (items, shadow) = Case(blurs[r]);
            var box = Decisions.ShadowBounds(items, shadow);

            for (int c = 0; c < rates.Length; c++)
            {
                var (w, h) = PixelsFor(box, rates[c], PageWidthPts);
                using var bitmap = Decisions.Rasterize(items, shadow, box, w, h);
                using var page = Decisions.ThroughPdf(Fixture, bitmap, box, renderWidth);

                // Shown at 8x zoom, cropped to the shadow's corner, which is
                // where under-sampling shows first.
                int x = (c * (cell + gutter)) + gutter;
                int y = header + (r * (cell + gutter)) + gutter;

                var src = new SKRect(
                    (float)(box.L * renderWidth), (float)(box.T * renderWidth),
                    (float)(box.R * renderWidth), (float)(box.B * renderWidth));

                canvas.DrawBitmap(page, src, new SKRect(x, y, x + cell, y + cell));

                if (c == 0)
                {
                    canvas.DrawText(
                        $"blur {blurs[r]:F0}pt", x + 10, y + 24, SKTextAlign.Left, font, text);
                }
            }
        }

        string path = Path.Combine(outDir, "06-resolution.png");
        using (var image = SKImage.FromBitmap(sheet))
        using (var data = image.Encode(SKEncodedImageFormat.Png, 95))
        using (var file = File.OpenWrite(path))
        {
            data.SaveTo(file);
        }

        Console.WriteLine($"  wrote 06-resolution.png");
        sheet.Dispose();
    }
}
