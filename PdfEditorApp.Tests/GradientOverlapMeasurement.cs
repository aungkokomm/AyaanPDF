using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using PdfEditorApp.Rendering.Skia;
using PdfEditorApp.Viewport;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace PdfEditorApp.Tests;

/// <summary>
/// MEASUREMENT, not a feature. What happens when Skia draws a committed shape
/// on top of the one PDFium already drew.
///
/// The question this settles: a gradient cannot reach a shape's appearance
/// stream until the file is saved, because PDFium cannot make a shading, so the
/// only way to SEE one while editing is for Skia to draw the shape over
/// PDFium's rendering of it. That means two rasterisers drawing the same stroke
/// in the same place, and nobody knows what that looks like until it is
/// measured.
///
/// THREE BITMAPS, all at the same size and geometry:
///
/// <list type="number">
/// <item>REFERENCE: the file as it will be after saving, with the gradient
/// written into the appearance stream by the stage 4 writer. Ground truth.</item>
/// <item>LIVE: the same document WITHOUT that pass, which is what the app shows
/// today: the stroke, and nothing inside it.</item>
/// <item>CANDIDATE: LIVE with Skia painting the shape over it, either fill and
/// stroke together or fill alone.</item>
/// </list>
///
/// Then CANDIDATE is diffed against REFERENCE, pixel by pixel, classified by
/// what the reference says that pixel is: paper, fill, stroke, or the
/// antialiased boundary between them.
///
/// Nothing here is production code and nothing here is called by anything. It
/// exists to produce numbers.
/// </summary>
public class GradientOverlapMeasurement
{
    private const string Lib = "render_core";
    private const int OkPdfium = 0;
    private const int Cap = 1000;

    /// <summary>The page is rendered this wide, and the Skia overlay uses the
    /// same number as its scale, so one normalized unit is one bitmap width.</summary>
    private const int Width = 600;

    /// <summary>The shape, in normalized page units.</summary>
    private const double L = 0.20, T = 0.20, R = 0.50, B = 0.40;

    /// <summary>Eight capture pixels, which is a stroke wide enough that its
    /// interior and its two antialiased edges are separately measurable.</summary>
    private const float StrokePx = 8f;

    private static readonly RenderColor Red = new(0xFF, 0xFF, 0x00, 0x00);
    private static readonly RenderColor Blue = new(0xFF, 0x00, 0x00, 0xFF);

    /// <summary>Left to right across the shape's own box.</summary>
    private const string GradientField =
        "f(c=FFFF0000,c2=FF0000FF,x0=0.0000,y0=0.5000,x1=1.0000,y1=0.5000)";

    private static readonly GradientFill Gradient = new(Red, Blue, 0, 0.5, 1, 0.5);

    private readonly ITestOutputHelper _out;

    public GradientOverlapMeasurement(ITestOutputHelper output) => _out = output;

    // ---------------- the boundary ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct ShapeSpec
    {
        public int PageIndex;
        public int Kind;
        public float X1;
        public float Y1;
        public float X2;
        public float Y2;
        public byte R;
        public byte G;
        public byte B;
        public byte A;
        public float WidthPx;
        public float RotationDeg;
        public uint FillRgba;
        public float CornerRadiusPx;
        public IntPtr EffectsUtf8;
        public nuint EffectsLen;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RenderResult
    {
        public int Width;
        public int Height;
        public IntPtr Buffer;
        public nuint Len;
        public int Status;
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong open_document([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void close_document(ulong docHandle);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int save_document(
        ulong docHandle, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int add_shape_annotations(
        ulong docHandle, int captureWidth, [In] ShapeSpec[]? specs, nuint specCount);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int rotate_shape_annotation(
        ulong docHandle, int pageIndex, int index, int captureWidth,
        float rotationDeg, out int newIndex);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int write_gradients(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string srcPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string dstPath,
        out int written);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern RenderResult render_uncached(
        ulong docHandle, int pageIndex, int targetWidth);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void free_render_result(RenderResult result);

    // ---------------- the harness ----------------

    private static string Scratch(string name) =>
        Path.Combine(Path.GetTempPath(), $"ayaan-overlap-{name}-{Environment.ProcessId}.pdf");

    /// <summary>
    /// A blank page carrying one gradient-tagged shape of the given kind, saved
    /// to a file. The positional fill is ZERO, which is what a gradient shape
    /// has: the gradient is its only paint.
    /// </summary>
    private static string ShapeFile(string name, int kind, float cornerPx, float rotationDeg)
    {
        string fixture = Path.Combine(AppContext.BaseDirectory, "blank.pdf");
        Assert.True(File.Exists(fixture), $"fixture missing at {fixture}");

        ulong handle = open_document(fixture);
        Assert.NotEqual(0UL, handle);

        byte[] tail = Encoding.UTF8.GetBytes(GradientField);
        IntPtr pinned = Marshal.AllocHGlobal(tail.Length);
        Marshal.Copy(tail, 0, pinned, tail.Length);

        try
        {
            var spec = new ShapeSpec
            {
                PageIndex = 0,
                Kind = kind,
                X1 = (float)(L * Cap),
                Y1 = (float)(T * Cap),
                X2 = (float)(R * Cap),
                Y2 = (float)(B * Cap),
                R = 0, G = 0, B = 0, A = 0xFF,
                WidthPx = StrokePx,
                FillRgba = 0,
                CornerRadiusPx = cornerPx,
                EffectsUtf8 = pinned,
                EffectsLen = (nuint)tail.Length,
            };

            Assert.Equal(OkPdfium, add_shape_annotations(handle, Cap, new[] { spec }, 1));

            if (rotationDeg != 0)
            {
                Assert.Equal(
                    OkPdfium,
                    rotate_shape_annotation(handle, 0, 0, Cap, rotationDeg, out _));
            }

            string path = Scratch(name);
            Assert.Equal(OkPdfium, save_document(handle, path));

            return path;
        }
        finally
        {
            close_document(handle);
            Marshal.FreeHGlobal(pinned);
        }
    }

    /// <summary>The same file with the stage 4 writer run over it.</summary>
    private static string WithGradient(string src, string name)
    {
        string dst = Scratch(name);
        Assert.Equal(OkPdfium, write_gradients(src, dst, out int written));
        Assert.Equal(1, written);

        return dst;
    }

    /// <summary>A page rendered by PDFium, as an SKBitmap in its own BGRA.</summary>
    private static SKBitmap RenderPdfium(string path)
    {
        ulong handle = open_document(path);
        Assert.NotEqual(0UL, handle);

        var result = render_uncached(handle, 0, Width);
        try
        {
            Assert.Equal(OkPdfium, result.Status);
            Assert.Equal(Width, result.Width);

            var info = new SKImageInfo(
                result.Width, result.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            var bitmap = new SKBitmap(info);

            byte[] bytes = new byte[(int)result.Len];
            Marshal.Copy(result.Buffer, bytes, 0, bytes.Length);
            Marshal.Copy(bytes, 0, bitmap.GetPixels(), bytes.Length);

            return bitmap;
        }
        finally
        {
            free_render_result(result);
            close_document(handle);
        }
    }

    /// <summary>
    /// The shape as the Skia painter would be handed it, with the gradient the
    /// tag carries and the same geometry the core was given.
    ///
    /// The rotation is applied to the POINTS and to the gradient's endpoints
    /// together, exactly as ShadowRasterizer.CasterItemsFor turns its own, which
    /// is the app's one convention for a shape's own angle.
    /// </summary>
    private static IReadOnlyList<ShapeRenderItem> SkiaItems(
        ShapeKind kind, double cornerFraction, double rotationDeg, bool stroke)
    {
        var shape = new ShapeAnnotation(
            0,
            new ShapeDraft(kind, L, T, R, B) { CornerFraction = cornerFraction },
            stroke ? "#FF000000" : "#00000000",
            StrokePx / Cap)
        {
            Fill = ShapeFill.Of(Gradient),
        };

        var items = ShapeRenderList.From([], [shape]);

        if (rotationDeg == 0)
        {
            return items;
        }

        double cx = (L + R) / 2, cy = (T + B) / 2;
        double rad = rotationDeg * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);

        (double X, double Y) About((double X, double Y) p) => (
            cx + (((p.X - cx) * cos) - ((p.Y - cy) * sin)),
            cy + (((p.X - cx) * sin) + ((p.Y - cy) * cos)));

        var turned = new List<ShapeRenderItem>(items.Count);
        foreach (var item in items)
        {
            var g = item.Fill.Gradient!.Value;
            var (x0, y0) = About((g.X0, g.Y0));
            var (x1, y1) = About((g.X1, g.Y1));

            var points = new List<(double X, double Y)>(item.Points.Count);
            foreach (var p in item.Points)
            {
                points.Add(About(p));
            }

            turned.Add(item with
            {
                Points = points,
                Fill = ShapeFill.Of(g with { X0 = x0, Y0 = y0, X1 = x1, Y1 = y1 }),
            });
        }

        return turned;
    }

    /// <summary>The live bitmap with Skia's version of the shape painted over it.</summary>
    private static SKBitmap Composited(
        SKBitmap live, ShapeKind kind, double cornerFraction, double rotationDeg, bool stroke)
    {
        var copy = live.Copy();
        using var canvas = new SKCanvas(copy);

        ShapeSkiaPainter.Paint(
            canvas,
            SkiaItems(kind, cornerFraction, rotationDeg, stroke),
            Width,
            _ => 0,
            _ => PageTransform.For(1, 1, 0, 1));

        return copy;
    }

    // ---------------- the measurement ----------------

    /// <summary>What the REFERENCE says a pixel is.</summary>
    private enum Region
    {
        Paper,
        Fill,
        Stroke,

        /// <summary>Neither cleanly: the antialiased edge of one against another.</summary>
        Boundary,
    }

    private static Region Classify(SKColor c)
    {
        if (c.Red > 248 && c.Green > 248 && c.Blue > 248)
        {
            return Region.Paper;
        }

        // Black stroke against a red-to-blue ramp: the stroke is the only thing
        // with no colour in it at all.
        if (c.Red < 40 && c.Green < 40 && c.Blue < 40)
        {
            return Region.Stroke;
        }

        // Solidly on the ramp: one of red or blue is strong, green is absent,
        // and the two add up to a full-strength colour.
        if (c.Green < 24 && c.Red + c.Blue > 232)
        {
            return Region.Fill;
        }

        return Region.Boundary;
    }

    private readonly record struct Tally(
        int Pixels, int Differing, int Delta5, int Worst, long SignedSum)
    {
        public Tally Add(int delta, int signed) => new(
            Pixels + 1,
            Differing + (delta > 2 ? 1 : 0),
            Delta5 + (delta > 5 ? 1 : 0),
            Math.Max(Worst, delta),
            SignedSum + signed);

        /// <summary>
        /// The average signed change in brightness, candidate minus reference.
        /// NEGATIVE means the candidate is darker, which is what a stroke drawn
        /// twice would be.
        /// </summary>
        public double MeanSigned => Pixels == 0 ? 0 : (double)SignedSum / Pixels;
    }

    private sealed class Diff
    {
        public readonly Dictionary<Region, Tally> ByRegion = new();
        public int CornerDiffering;
        public int WorstX = -1, WorstY = -1, Worst;

        public Tally Total()
        {
            var total = default(Tally);
            foreach (var t in ByRegion.Values)
            {
                total = new Tally(
                    total.Pixels + t.Pixels,
                    total.Differing + t.Differing,
                    total.Delta5 + t.Delta5,
                    Math.Max(total.Worst, t.Worst),
                    total.SignedSum + t.SignedSum);
            }

            return total;
        }
    }

    /// <summary>
    /// Every pixel in the window, compared. The window is the shape's box with
    /// room for the stroke and for a turn, so page content elsewhere cannot
    /// dilute the numbers.
    /// </summary>
    private static Diff Compare(SKBitmap reference, SKBitmap candidate)
    {
        var diff = new Diff();

        int lo = (int)(L * Width) - 60;
        int hi = (int)(R * Width) + 60;
        int top = (int)(T * Width) - 60;
        int bottom = (int)(B * Width) + 60;

        for (int y = Math.Max(0, top); y <= Math.Min(reference.Height - 1, bottom); y++)
        {
            for (int x = Math.Max(0, lo); x <= Math.Min(reference.Width - 1, hi); x++)
            {
                var a = reference.GetPixel(x, y);
                var b = candidate.GetPixel(x, y);

                int delta = Math.Max(
                    Math.Abs(a.Red - b.Red),
                    Math.Max(Math.Abs(a.Green - b.Green), Math.Abs(a.Blue - b.Blue)));

                int signed =
                    ((b.Red + b.Green + b.Blue) - (a.Red + a.Green + a.Blue)) / 3;

                var region = Classify(a);
                diff.ByRegion[region] =
                    diff.ByRegion.GetValueOrDefault(region).Add(delta, signed);

                if (delta > diff.Worst)
                {
                    diff.Worst = delta;
                    diff.WorstX = x;
                    diff.WorstY = y;
                }

                if (delta > 2 && NearACorner(x, y))
                {
                    diff.CornerDiffering++;
                }
            }
        }

        return diff;
    }

    /// <summary>Within six pixels of one of the upright box's four corners.</summary>
    private static bool NearACorner(int x, int y)
    {
        foreach (var (cx, cy) in new[]
        {
            (L * Width, T * Width), (R * Width, T * Width),
            (L * Width, B * Width), (R * Width, B * Width),
        })
        {
            if (Math.Abs(x - cx) <= 6 && Math.Abs(y - cy) <= 6)
            {
                return true;
            }
        }

        return false;
    }

    private void Report(string what, Diff diff)
    {
        int pixels = 0, differing = 0, bad = 0;
        var lines = new List<string>();

        foreach (var region in new[] { Region.Fill, Region.Stroke, Region.Boundary, Region.Paper })
        {
            var t = diff.ByRegion.GetValueOrDefault(region);
            pixels += t.Pixels;
            differing += t.Differing;
            bad += t.Delta5;

            lines.Add(
                $"    {region,-9} {t.Pixels,7} px   differ>2 {t.Differing,6}"
                + $" ({Pct(t.Differing, t.Pixels)})   differ>5 {t.Delta5,6}   worst {t.Worst,3}"
                + $"   mean signed {t.MeanSigned,7:F2}");
        }

        _out.WriteLine($"  {what}");
        foreach (string line in lines)
        {
            _out.WriteLine(line);
        }

        _out.WriteLine(
            $"    TOTAL     {pixels,7} px   differ>2 {differing,6} ({Pct(differing, pixels)})"
            + $"   differ>5 {bad,6}   worst {diff.Worst} at ({diff.WorstX},{diff.WorstY})");
        _out.WriteLine($"    corners   differ>2 {diff.CornerDiffering}");
        _out.WriteLine(string.Empty);
    }

    private static string Pct(int part, int whole) =>
        whole == 0 ? "n/a" : $"{100.0 * part / whole:F2}%";

    private void Measure(string name, ShapeKind kind, float cornerPx, float rotationDeg)
    {
        double cornerFraction = cornerPx > 0
            ? ShapeGeometry.CornerFractionFromRadius(
                cornerPx / Cap, R - L, B - T)
            : ShapeGeometry.DefaultCornerFraction;

        string live = ShapeFile($"{name}-live", (int)kind, cornerPx, rotationDeg);
        string reference = WithGradient(live, $"{name}-ref");

        using var referenceBitmap = RenderPdfium(reference);
        using var liveBitmap = RenderPdfium(live);

        _out.WriteLine($"=== {name} ===");

        using var both = Composited(liveBitmap, kind, cornerFraction, rotationDeg, stroke: true);
        var a = Compare(referenceBitmap, both);
        Report("A: Skia fill AND stroke over PDFium", a);

        using var fillOnly = Composited(liveBitmap, kind, cornerFraction, rotationDeg, stroke: false);
        var b = Compare(referenceBitmap, fillOnly);
        Report("B: Skia fill only, PDFium keeps the stroke", b);

        // The control. Without Skia at all the shape is unfilled, which is what
        // the app shows today and what makes the two numbers above meaningful.
        var c = Compare(referenceBitmap, liveBitmap);
        Report("C: no Skia at all, which is today", c);

        // AND THE ONE THAT SEPARATES THE TWO EXPLANATIONS. Skia painting the
        // whole shape onto a BLANK page, with no PDFium shape underneath at
        // all. If this matches A, the difference is two rasterisers disagreeing
        // about an antialiased edge and has nothing to do with drawing the
        // stroke twice, which means hiding the annotation would buy nothing.
        using var blank = RenderPdfium(Path.Combine(AppContext.BaseDirectory, "blank.pdf"));
        using var alone = Composited(blank, kind, cornerFraction, rotationDeg, stroke: true);
        var d = Compare(referenceBitmap, alone);
        Report("D: Skia alone on blank paper, no PDFium shape", d);

        File.Delete(live);
        File.Delete(reference);

        // ---------------- what the numbers have to keep saying ----------------
        //
        // The measurement is the point of this file, but a measurement nobody
        // asserts on is a measurement that quietly stops being true. These pin
        // the four findings the architecture decision rests on.

        Assert.True(
            a.ByRegion[Region.Paper].Delta5 <= 60,
            $"{name}: painting over PDFium put {a.ByRegion[Region.Paper].Delta5} marks on the paper");

        Assert.True(
            a.Total().Delta5 * 10 < c.Total().Delta5,
            $"{name}: A ({a.Total().Delta5}) is not an order better than doing nothing"
            + $" ({c.Total().Delta5})");

        Assert.True(
            Math.Abs(a.ByRegion[Region.Stroke].MeanSigned) < 2.0,
            $"{name}: the body of the stroke moved by {a.ByRegion[Region.Stroke].MeanSigned:F2}");

        Assert.True(
            b.ByRegion[Region.Stroke].Delta5 > a.ByRegion[Region.Stroke].Delta5 * 3,
            $"{name}: filling without stroking no longer eats the stroke"
            + $" (B {b.ByRegion[Region.Stroke].Delta5} vs A {a.ByRegion[Region.Stroke].Delta5})");

        Assert.True(
            d.Total().Delta5 * 2 > a.Total().Delta5,
            $"{name}: hiding the PDFium shape would now be a real improvement"
            + $" (D {d.Total().Delta5} vs A {a.Total().Delta5}), which changes the recommendation");
    }

    [Fact]
    public void measure_the_overlap()
    {
        Measure("rectangle", ShapeKind.Rectangle, 0, 0);
        Measure("ellipse", ShapeKind.Ellipse, 0, 0);
        Measure("rounded", ShapeKind.RoundedRectangle, 60, 0);
        Measure("rotated", ShapeKind.Rectangle, 0, 30);
    }
}
