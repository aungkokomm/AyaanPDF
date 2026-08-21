using System.Diagnostics;
using PdfEditorApp.Viewport;
using SkiaSharp;

namespace ShadowLab;

/// <summary>
/// Writes the sheets. Everything here is layout and labelling; the shadow work
/// is in Casters and the geometry is the app's own.
/// </summary>
public static class Program
{
    private const int Cell = 420;
    private const int Header = 34;
    private const int Gutter = 10;

    private static string OutDir = "";

    public static void Main(string[] args)
    {
        OutDir = args.Length > 0 ? args[0] : "out";
        Directory.CreateDirectory(OutDir);

        ApproachGrid();
        BlurSweep();
        AlphaAndDistance();
        WhatTheSavedPageShows();
        BlurInThePdf();
        ShadowIntoThePdf();
        Benchmark();
        Studies.RunAll(Fixture(), OutDir);

        Console.WriteLine($"\nSheets written to {Path.GetFullPath(OutDir)}");
    }

    // ---------------- sheet 1: the three approaches, side by side ----------------

    private static void ApproachGrid()
    {
        Approach[] columns = [Approach.None, Approach.Current, Approach.ImageFilter, Approach.ImageFilterOnly];
        string[] titles = ["no shadow", "CURRENT (hand-built)", "CreateDropShadow", "CreateDropShadowOnly"];

        var scenes = Scenes.Grid(Scenes.Shadow());
        var sheet = NewSheet(columns.Length, scenes.Count, out var canvas);

        for (int c = 0; c < columns.Length; c++)
        {
            Label(canvas, titles[c], (c * (Cell + Gutter)) + Gutter, 24, 16, bold: true);
        }

        for (int r = 0; r < scenes.Count; r++)
        {
            for (int c = 0; c < columns.Length; c++)
            {
                DrawCell(canvas, c, r, scenes[r].Items, columns[c],
                    c == 0 ? scenes[r].Name : null);
            }
        }

        Save(sheet, "01-approaches.png");
    }

    // ---------------- sheet 2: does the blur value do anything ----------------

    private static void BlurSweep()
    {
        double[] blurs = [0, 4, 8, 16, 24, 32];
        Approach[] rows = [Approach.Current, Approach.ImageFilter, Approach.ImageFilterOnly];
        string[] names = ["CURRENT", "CreateDropShadow", "CreateDropShadowOnly"];

        var sheet = NewSheet(blurs.Length, rows.Length, out var canvas);

        for (int c = 0; c < blurs.Length; c++)
        {
            Label(canvas, $"blur {blurs[c]:F0}pt", (c * (Cell + Gutter)) + Gutter, 24, 15, bold: true);
        }

        for (int r = 0; r < rows.Length; r++)
        {
            for (int c = 0; c < blurs.Length; c++)
            {
                var scene = Scenes.Grid(Scenes.Shadow(distancePts: 30, blurPts: blurs[c]))[1];
                DrawCell(canvas, c, r, scene.Items, rows[r], c == 0 ? names[r] : null);
            }
        }

        Save(sheet, "02-blur-sweep.png");
    }

    // ---------------- sheet 3: alpha and distance ----------------

    private static void AlphaAndDistance()
    {
        byte[] alphas = [0x20, 0x40, 0x80, 0xC0, 0xFF];
        double[] distances = [0, 4, 10, 20, 30];

        var sheet = NewSheet(alphas.Length, 4, out var canvas);

        for (int c = 0; c < alphas.Length; c++)
        {
            Label(canvas, $"alpha {alphas[c]:X2}", (c * (Cell + Gutter)) + Gutter, 24, 15, bold: true);
        }

        for (int c = 0; c < alphas.Length; c++)
        {
            var scene = Scenes.Grid(Scenes.Shadow(alpha: alphas[c]))[0];
            DrawCell(canvas, c, 0, scene.Items, Approach.Current, c == 0 ? "CURRENT" : null);
            DrawCell(canvas, c, 1, scene.Items, Approach.ImageFilter, c == 0 ? "CreateDropShadow" : null);
        }

        for (int c = 0; c < distances.Length; c++)
        {
            var scene = Scenes.Grid(Scenes.Shadow(distancePts: distances[c]))[0];
            DrawCell(canvas, c, 2, scene.Items, Approach.Current,
                c == 0 ? "CURRENT" : null, $"distance {distances[c]:F0}pt");
            DrawCell(canvas, c, 3, scene.Items, Approach.ImageFilter,
                c == 0 ? "CreateDropShadow" : null, $"distance {distances[c]:F0}pt");
        }

        Save(sheet, "03-alpha-and-distance.png");
    }

    // ---------------- sheet 4: what a COMMITTED shape actually looks like ----------------

    private static void WhatTheSavedPageShows()
    {
        string fixture = Fixture();
        if (fixture is "")
        {
            Console.WriteLine("! blank.pdf not found; skipping the saved-page sheets");
            return;
        }

        // The page is 200pt wide and the capture space is 1000, so five capture
        // pixels to the point. The shape and its shadow are given in that space.
        var spec = ShapeSpec(shadowSoftnessPx: 40);

        var sheet = NewSheet(3, 1, out var canvas);
        Label(canvas, "SAVED PAGE (PDFium)", Gutter, 24, 16, bold: true);
        Label(canvas, "PREVIEW (Skia, current)", Cell + (2 * Gutter), 24, 16, bold: true);
        Label(canvas, "PREVIEW (CreateDropShadow)", (2 * Cell) + (3 * Gutter), 24, 16, bold: true);

        using (var page = Native.RenderPage(fixture, [spec], 1000, Cell))
        {
            canvas.DrawBitmap(page, Gutter, Header + Gutter);
        }

        var scene = SceneMatchingTheSpec();
        DrawCell(canvas, 1, 0, scene, Approach.Current, null);
        DrawCell(canvas, 2, 0, scene, Approach.ImageFilter, null);

        Save(sheet, "04-saved-page-vs-preview.png");
    }

    /// <summary>
    /// Whether the blur value changes ANY pixel of the saved page.
    ///
    /// The report was that the blur control "changes transparency rather than
    /// producing a real blur". This asks the file directly.
    /// </summary>
    private static void BlurInThePdf()
    {
        string fixture = Fixture();
        if (fixture is "")
        {
            return;
        }

        using var none = Native.RenderPage(fixture, [ShapeSpec(shadowSoftnessPx: 0)], 1000, 600);
        using var lots = Native.RenderPage(fixture, [ShapeSpec(shadowSoftnessPx: 90)], 1000, 600);

        int differing = 0;
        var a = none.GetPixelSpan();
        var b = lots.GetPixelSpan();
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i]) { differing++; }
        }

        Console.WriteLine("\n=== does blur change the saved page? ===");
        Console.WriteLine($"  blur 0pt vs blur 18pt: {differing} differing bytes of {a.Length}");
        Console.WriteLine(differing == 0
            ? "  -> IDENTICAL. The blur value is stored and drawn by nothing."
            : "  -> the saved page changed.");
    }

    /// <summary>
    /// The proposal, end to end: a Skia-rasterised shadow embedded in a real
    /// PDF through the EXISTING stamp path, rendered back by PDFium.
    /// </summary>
    private static void ShadowIntoThePdf()
    {
        string fixture = Fixture();
        if (fixture is "")
        {
            return;
        }

        var shadow = Scenes.Shadow(distancePts: 30, blurPts: 24);
        var scenes = Scenes.Grid(shadow);

        var sheet = NewSheet(3, 2, out var canvas);
        Label(canvas, "SAVED PAGE today (hard)", Gutter, 24, 16, bold: true);
        Label(canvas, "PREVIEW (CreateDropShadow)", Cell + (2 * Gutter), 24, 16, bold: true);
        Label(canvas, "SAVED PAGE with a rasterised shadow", (2 * Cell) + (3 * Gutter), 24, 16, bold: true);

        for (int row = 0; row < 2; row++)
        {
            // Row 0 is the filled rectangle, row 1 the stroke-only one, which
            // is where the two approaches disagree most.
            var scene = scenes[row == 0 ? 0 : 1];
            var spec = SpecFor(row == 0);

            using (var today = Native.RenderPage(fixture, [spec], 1000, Cell))
            {
                canvas.DrawBitmap(today, Gutter, Header + Gutter + (row * (Cell + Gutter)));
            }

            DrawCell(canvas, 1, row, scene.Items, Approach.ImageFilter, null);

            using (var round = PdfRoundTrip.ThroughThePdf(fixture, scene.Items, shadow, spec, Cell))
            {
                canvas.DrawBitmap(
                    round, (2 * Cell) + (3 * Gutter), Header + Gutter + (row * (Cell + Gutter)));
            }
        }

        Save(sheet, "05-rasterised-shadow-in-the-pdf.png");
    }

    /// <summary>The scene's rectangle as the core draws one: same box, same
    /// colours, in capture space.</summary>
    private static NativeShapeSpec SpecFor(bool filled)
    {
        var spec = ShapeSpec(0);
        spec.X1 = 220;
        spec.Y1 = 260;
        spec.X2 = 680;
        spec.Y2 = 620;
        spec.WidthPx = 8;
        spec.FillRgba = filled ? 0xFF3B82F6 : 0;
        spec.ShadowAngleDeg = 135;
        spec.ShadowDistancePx = 150;
        spec.ShadowRgba = 0x80000000;
        return spec;
    }

    private static string Fixture()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null &&
               !File.Exists(Path.Combine(dir.FullName, "render_core", "tests", "fixtures", "blank.pdf")))
        {
            dir = dir.Parent;
        }

        return dir is null ? "" : Path.Combine(dir.FullName, "render_core", "tests", "fixtures", "blank.pdf");
    }

    /// <summary>A filled blue rectangle with a half-alpha black shadow, in the
    /// capture space add_shape_annotations expects.</summary>
    private static NativeShapeSpec ShapeSpec(float shadowSoftnessPx) => new()
    {
        PageIndex = 0,
        Kind = 0,
        X1 = 220,
        Y1 = 260,
        X2 = 680,
        Y2 = 620,
        R = 0x1F,
        G = 0x29,
        B = 0x37,
        A = 0xFF,
        WidthPx = 8,
        FillRgba = 0xFF3B82F6,
        ShadowAngleDeg = 135,
        ShadowDistancePx = 50,
        ShadowSoftnessPx = shadowSoftnessPx,
        ShadowRgba = 0x80000000,
    };

    /// <summary>The same shape as ShapeSpec, as render items, so the two
    /// renderers are drawing the same object.</summary>
    private static IReadOnlyList<ShapeRenderItem> SceneMatchingTheSpec() =>
        Scenes.Grid(Scenes.Shadow(angle: 135, distancePts: 10, blurPts: 8))[0].Items;

    // ---------------- performance ----------------

    private static void Benchmark()
    {
        const int frames = 40;
        var scenes = Scenes.Grid(Scenes.Shadow(blurPts: 8));

        Console.WriteLine("\n=== cost per frame, 8 objects, 1024 square, ms ===");
        Console.WriteLine($"  {"approach",-24} {"blur 0",8} {"blur 8pt",10} {"blur 18pt",10}");

        foreach (var (approach, name) in new[]
        {
            (Approach.None, "no shadow"),
            (Approach.Current, "CURRENT"),
            (Approach.ImageFilter, "CreateDropShadow"),
            (Approach.ImageFilterOnly, "CreateDropShadowOnly"),
            (Approach.Unbounded, "same, layer UNBOUNDED"),
        })
        {
            var cells = new List<string>();
            foreach (double blur in new double[] { 0, 8, 18 })
            {
                var items = Scenes.Grid(Scenes.Shadow(blurPts: blur))
                    .SelectMany(s => s.Items).ToList();
                cells.Add($"{TimeOne(items, approach, frames):F2}");
            }

            Console.WriteLine($"  {name,-24} {cells[0],8} {cells[1],10} {cells[2],10}");
        }

        // The preview draws ONE object: the shape being dragged. That is the
        // only frame rate a person can feel, so it is the one that decides
        // whether this is usable.
        Console.WriteLine();
        Console.WriteLine("=== cost per frame, ONE object, 1024 square, ms ===");
        Console.WriteLine($"  {"approach",-24} {"blur 0",8} {"blur 8pt",10} {"blur 18pt",10}");

        foreach (var (approach, name) in new[]
        {
            (Approach.None, "no shadow"),
            (Approach.Current, "CURRENT"),
            (Approach.ImageFilter, "CreateDropShadow"),
            (Approach.ImageFilterOnly, "CreateDropShadowOnly"),
        })
        {
            var cells = new List<string>();
            foreach (double blur in new double[] { 0, 8, 18 })
            {
                var items = Scenes.Grid(Scenes.Shadow(distancePts: 30, blurPts: blur))[0].Items;
                cells.Add($"{TimeOne(items, approach, 120):F2}");
            }

            Console.WriteLine($"  {name,-24} {cells[0],8} {cells[1],10} {cells[2],10}");
        }
    }

    private static double TimeOne(IReadOnlyList<ShapeRenderItem> items, Approach approach, int frames)
    {
        using var surface = SKSurface.Create(new SKImageInfo(1024, 1024, SKColorType.Rgba8888));
        var canvas = surface.Canvas;

        for (int i = 0; i < 20; i++)
        {
            canvas.Clear(SKColors.White);
            Casters.Paint(canvas, items, approach);
        }

        surface.Flush();
        var watch = Stopwatch.StartNew();

        for (int i = 0; i < frames; i++)
        {
            canvas.Clear(SKColors.White);
            Casters.Paint(canvas, items, approach);
        }

        surface.Flush();
        watch.Stop();

        return watch.Elapsed.TotalMilliseconds / frames;
    }

    // ---------------- sheet plumbing ----------------

    private static SKBitmap NewSheet(int cols, int rows, out SKCanvas canvas)
    {
        var bitmap = new SKBitmap(
            (cols * (Cell + Gutter)) + Gutter,
            Header + (rows * (Cell + Gutter)) + Gutter,
            SKColorType.Rgba8888, SKAlphaType.Premul);

        canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(0xF3, 0xF4, 0xF6));
        return bitmap;
    }

    private static void DrawCell(
        SKCanvas sheet, int col, int row, IReadOnlyList<ShapeRenderItem> items,
        Approach approach, string? rowLabel, string? cellLabel = null)
    {
        using var tile = new SKBitmap(Cell, Cell, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(tile))
        {
            canvas.Clear(SKColors.White);

            // The scenes are laid out in an 800-wide page; the tile shows it at
            // whatever size the sheet has room for.
            canvas.Scale(Cell / (float)Casters.Scale);
            Casters.Paint(canvas, items, approach);
        }

        int x = (col * (Cell + Gutter)) + Gutter;
        int y = Header + (row * (Cell + Gutter)) + Gutter;
        sheet.DrawBitmap(tile, x, y);

        using var border = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = new SKColor(0xD1, 0xD5, 0xDB),
            StrokeWidth = 1,
        };
        sheet.DrawRect(x, y, Cell, Cell, border);

        if (rowLabel is not null)
        {
            Label(sheet, rowLabel, x + 10, y + 24, 15, bold: true);
        }

        if (cellLabel is not null)
        {
            Label(sheet, cellLabel, x + 10, y + Cell - 12, 14, bold: false);
        }
    }

    private static void Label(SKCanvas canvas, string text, float x, float y, float size, bool bold)
    {
        using var font = new SKFont(
            SKTypeface.FromFamilyName(
                "Segoe UI", bold ? SKFontStyleWeight.SemiBold : SKFontStyleWeight.Normal,
                SKFontStyleWidth.Normal, SKFontStyleSlant.Upright),
            size);

        using var paint = new SKPaint { Color = new SKColor(0x11, 0x18, 0x27), IsAntialias = true };
        canvas.DrawText(text, x, y, SKTextAlign.Left, font, paint);
    }

    private static void Save(SKBitmap sheet, string name)
    {
        string path = Path.Combine(OutDir, name);
        using var image = SKImage.FromBitmap(sheet);
        using var data = image.Encode(SKEncodedImageFormat.Png, 95);
        using var file = File.OpenWrite(path);
        data.SaveTo(file);
        Console.WriteLine($"  wrote {name}");
        sheet.Dispose();
    }
}
