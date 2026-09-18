using System;
using System.Collections.Generic;
using System.IO;
using PdfEditorApp.Rendering.Skia;
using PdfEditorApp.Viewport;
using SkiaSharp;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The gradient-filled shapes Skia stands in for, and everything it must not
/// stand in for.
///
/// PDFium cannot make a shading, so a gradient reaches a shape's appearance
/// stream only when the file is saved. This paints it over the top in the
/// meantime, for every gradient shape in view rather than only the selected
/// one, because a fill that exists only while you are looking at it is not a
/// fill.
///
/// The pixels that approved the architecture are in GradientOverlapMeasurement.
/// These are the rules the feed has to keep, driven through the real page model
/// so the tags are parsed by the code that parses them in the app.
/// </summary>
public class GradientOverlayTests
{
    private const double PageWidthPts = 600;
    private const double Scale = 800;

    private const double L = 0.1, T = 0.1, R = 0.4, B = 0.3;

    private const string Gradient =
        "f(c=FFFF0000,c2=FF0000FF,x0=0.0000,y0=0.5000,x1=1.0000,y1=0.5000)";

    private const string Shadow = "s(a=135.00,d=6.0000,b=0.0000,p=0.0000,c=FF000000)";
    private const string Glow = "g(a=0.00,d=0.0000,b=4.0000,p=0.0000,c=FFFFD400)";

    /// <summary>
    /// A shape tag as render_core writes one. Built as TEXT and parsed back by
    /// the real builder, so nothing here can agree with the model about a
    /// format the document does not use.
    /// </summary>
    private static string TagText(
        ShapeKind kind = ShapeKind.Rectangle,
        string fill = "00000000",
        string effects = Gradient,
        double rotationDeg = 0,
        double cornerRadiusPts = 0) =>
        $"AyaanShape:{(int)kind}:000000FF:2.0000:1:1:{rotationDeg:F2}:{fill}:{cornerRadiusPts:F4}"
        + (effects.Length > 0 ? ":0.0000:0.0000:" + effects : string.Empty);

    private static PageModel Page(params string[] tags)
    {
        var snapshots = new List<AnnotationSnapshot>();
        for (int at = 0; at < tags.Length; at++)
        {
            snapshots.Add(new AnnotationSnapshot(
                at, 2, L, T, R, B, 1.0, Guid.NewGuid(), tags[at]));
        }

        return DocumentModelBuilder.BuildPage(0, snapshots, PageWidthPts);
    }

    private static IReadOnlyList<ShapeRenderItem> Items(params string[] tags) =>
        GradientOverlay.ItemsFor(Page(tags));

    // ---------------- the model carries the gradient at all ----------------

    [Fact]
    public void the_page_model_reads_a_gradient_off_the_tag_it_already_parses()
    {
        // The whole feature rests on this costing nothing: the builder had the
        // tag in its hand for the stroke and the corners anyway.
        var shape = Assert.IsType<ShapeObject>(Page(TagText()).Objects[0]);

        Assert.NotNull(shape.Gradient);
        Assert.Equal(new RenderColor(0xFF, 0xFF, 0, 0), shape.Gradient!.Value.From);
        Assert.Equal(new RenderColor(0xFF, 0, 0, 0xFF), shape.Gradient.Value.To);
    }

    [Fact]
    public void a_shape_with_a_solid_fill_carries_no_gradient()
    {
        var shape = Assert.IsType<ShapeObject>(
            Page(TagText(fill: "FF3B82F6", effects: "")).Objects[0]);

        Assert.Null(shape.Gradient);
        Assert.Equal("#FF3B82F6", shape.FillHex);
    }

    // ---------------- what is fed ----------------

    [Theory]
    [InlineData(ShapeKind.Rectangle, 0.0)]
    [InlineData(ShapeKind.Ellipse, 0.0)]
    [InlineData(ShapeKind.RoundedRectangle, 20.0)]
    public void every_gradient_shape_in_view_is_fed(ShapeKind kind, double corner)
    {
        var item = Assert.Single(Items(TagText(kind, cornerRadiusPts: corner)));

        Assert.NotNull(item.Fill.Gradient);
        Assert.Equal(new RenderColor(0xFF, 0, 0, 0), item.Color);
        Assert.Equal(2 / PageWidthPts, item.StrokeWidth, 9);
    }

    [Fact]
    public void several_gradient_shapes_on_one_page_are_all_fed()
    {
        // The point of the whole stage: a shape keeps its paint when the person
        // clicks on the next one. Nothing here knows what is selected.
        var items = Items(TagText(), TagText(ShapeKind.Ellipse), TagText());

        Assert.Equal(3, items.Count);
    }

    [Fact]
    public void the_shapes_without_a_gradient_are_left_entirely_to_pdfium()
    {
        var items = Items(
            TagText(fill: "FF3B82F6", effects: ""),
            TagText(),
            TagText(effects: Shadow + ":" + Glow),
            TagText(effects: ""));

        Assert.Single(items);
    }

    [Fact]
    public void a_document_with_no_gradient_adds_nothing_at_all()
    {
        // Byte for byte the frame the app drew before this existed.
        Assert.Empty(Items(TagText(fill: "FF3B82F6", effects: ""), TagText(effects: Glow)));
        Assert.Empty(Items());
    }

    [Theory]
    [InlineData(Shadow)]
    [InlineData(Glow)]
    [InlineData(Shadow + ":" + Glow)]
    public void the_effects_are_left_to_pdfium(string effects)
    {
        // A shadow and a glow are already in the annotation and PDFium draws
        // them underneath. A second copy paints a second shadow over the first,
        // and two translucent ones overlapping are darker than either.
        var item = Assert.Single(Items(TagText(effects: Gradient + ":" + effects)));

        Assert.Null(item.Effects);
        Assert.NotNull(item.Fill.Gradient);
    }

    // ---------------- and where it puts things ----------------

    [Fact]
    public void the_geometry_is_the_shapes_own_and_not_a_box_around_it()
    {
        Assert.Equal(5, Items(TagText())[0].Points.Count);
        Assert.True(Items(TagText(ShapeKind.Ellipse))[0].Points.Count > 20);

        // A rounded rectangle has to come out rounded BY THE RIGHT AMOUNT. The
        // page model's geometry drops the corner radius, because selection does
        // not need it, so the overlay puts it back from the tag. Counting the
        // points does not catch that: any radius at all produces an arc.
        const double RadiusPts = 20;
        var shape = Assert.IsType<ShapeObject>(
            Page(TagText(ShapeKind.RoundedRectangle, cornerRadiusPts: RadiusPts)).Objects[0]);
        var box = shape.UprightGeometry;

        var rounded = Items(TagText(ShapeKind.RoundedRectangle, cornerRadiusPts: RadiusPts))[0];

        // Along the top edge the outline runs from Left+r to Right-r, so the
        // leftmost point sitting AT the top says what the radius came out as.
        double leftmostOnTop = double.MaxValue;
        foreach (var (x, y) in rounded.Points)
        {
            if (Math.Abs(y - box.Top) < 1e-6)
            {
                leftmostOnTop = Math.Min(leftmostOnTop, x);
            }
        }

        Assert.True(leftmostOnTop < double.MaxValue, "the outline never reaches its own top edge");
        Assert.Equal(RadiusPts / PageWidthPts, leftmostOnTop - box.Left, 4);
    }

    [Fact]
    public void the_gradient_is_resolved_against_the_shape_as_drawn_not_its_rectangle()
    {
        // AN ANNOTATION'S /Rect IS NOT THE SHAPE. It carries the stroke's pad,
        // half the weight plus a point, and for a turned shape it is the box of
        // the turned content, bigger in both axes. A fraction of the shape's
        // box has to be measured against the box the person dragged out, or
        // every gradient sits a stroke's width off at each end.
        var shape = Assert.IsType<ShapeObject>(Page(TagText()).Objects[0]);
        var box = shape.UprightGeometry;

        Assert.True(box.Left > L, "the upright box is the padded rectangle, so this proves nothing");

        var g = Items(TagText())[0].Fill.Gradient!.Value;

        Assert.Equal(box.Left, g.X0, 6);
        Assert.Equal((box.Top + box.Bottom) / 2, g.Y0, 6);
        Assert.Equal(box.Right, g.X1, 6);
    }

    [Fact]
    public void the_gradient_turns_with_the_shape()
    {
        // Points and endpoints together, by one method. A turn that moved one
        // and not the other would leave the paint upright inside a turned
        // shape.
        var turned = Items(TagText(rotationDeg: 90))[0];
        var g = turned.Fill.Gradient!.Value;

        Assert.Equal(g.X0, g.X1, 6);
        Assert.NotEqual(g.Y0, g.Y1, 6);
    }

    [Fact]
    public void the_stored_gradient_is_never_what_moves()
    {
        // Whatever the renderer is handed, the tag stays fractions of the
        // shape's own box. That is what survives the save.
        foreach (double angle in new[] { 0.0, 30.0, 90.0 })
        {
            Assert.True(ShapeTagReader.TryParse(TagText(rotationDeg: angle), out var tag));
            Assert.Equal(Gradient, ShapeFillTag.FieldOf(ShapeFillTag.From(tag)));
        }
    }

    [Fact]
    public void the_overlay_paints_a_gradient_clipped_to_the_shape()
    {
        using var bitmap = Paint(Items(TagText(ShapeKind.Ellipse)));

        var middle = bitmap.GetPixel((int)(((L + R) / 2) * Scale), (int)(((T + B) / 2) * Scale));
        Assert.InRange(middle.Blue, 100, 155);
        Assert.InRange(middle.Red, 100, 155);

        var corner = bitmap.GetPixel((int)(L * Scale) + 4, (int)(T * Scale) + 4);
        Assert.True(
            corner is { Red: 255, Green: 255, Blue: 255 },
            $"the overlay spilled into the corner of the box: {corner}");
    }

    private static SKBitmap Paint(IReadOnlyList<ShapeRenderItem> items)
    {
        var bitmap = new SKBitmap(400, 400, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);

        ShapeSkiaPainter.Paint(
            canvas, items, Scale, _ => 0, _ => PageTransform.For(1, 1, 0, 1));

        return bitmap;
    }

    // ---------------- and the wiring around it ----------------

    private static string FileFromRepo(params string[] relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string path = Path.Combine(relative);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, path)))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);

        return File.ReadAllText(Path.Combine(dir!.FullName, path));
    }

    private static string ViewModel() =>
        FileFromRepo("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");

    [Fact]
    public void the_page_prepares_before_it_reads_and_paints_what_it_gets()
    {
        string page = FileFromRepo("PdfEditorApp", "MainPage.xaml.cs");

        int prepare = page.IndexOf("ViewModel.PrepareGradientOverlay();", StringComparison.Ordinal);
        int read = page.IndexOf("frame.AddRange(ViewModel.GradientOverlayItems);", StringComparison.Ordinal);

        Assert.True(prepare > 0, "the overlay is never prepared");
        Assert.True(read > prepare, "the overlay is read before it is prepared");
    }

    [Fact]
    public void only_the_pages_in_view_are_ever_loaded()
    {
        // THE BOUNDED RULE. Preparing a page means building its model, and that
        // means asking PDFium for its annotations and reading every tag. Doing
        // it for anything but the visible range is how a scroll starts to
        // stutter on a long document.
        string body = MethodBody("public void PrepareGradientOverlay()");

        Assert.Contains("_layout.VisibleRange(", body, StringComparison.Ordinal);
        Assert.Contains("for (int page = first; page <= last; page++)", body, StringComparison.Ordinal);

        // And a page already prepared is not prepared again.
        Assert.Contains("_gradientOverlayByPage.ContainsKey(page)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_overlay_dies_with_the_annotations_it_was_built_from()
    {
        // It is a projection of the page model, which is a projection of the
        // annotation cache. Given a lifetime of its own it would be a second
        // source of truth, and an edited gradient would keep painting its old
        // colours.
        string body = MethodBody("private void InvalidateAnnotationCache(int pageIndex)");

        Assert.Contains("_gradientOverlayByPage.Remove(pageIndex);", body, StringComparison.Ordinal);
        Assert.Contains("_pageModelByPage.Remove(pageIndex);", body, StringComparison.Ordinal);
    }

    [Fact]
    public void there_is_only_one_gradient_overlay_path()
    {
        // The selected-shape feed was replaced rather than joined. Two paths
        // for one thing is two places for it to be wrong.
        string source = ViewModel();

        Assert.DoesNotContain("SelectedShapeOverlay", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedShapeOverlayItems", source, StringComparison.Ordinal);
    }

    [Fact]
    public void the_old_overlay_shape_list_is_still_dead()
    {
        string source = ViewModel();

        Assert.DoesNotContain("_allShapes.Add(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_allShapes.Insert(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void the_live_overlay_is_nowhere_near_the_save_path()
    {
        // The saved file's gradient comes from the tag through the lopdf
        // writer, whatever the live surface is doing.
        string body = MethodBody("private SavePlan? BeginSave(")
            + MethodBody("private static void WriteSave(")
            + MethodBody("private bool FinishSave(");

        Assert.DoesNotContain("GradientOverlay", body, StringComparison.Ordinal);
        Assert.Contains("WriteGradientFills(plan.WritePath);", body, StringComparison.Ordinal);
    }

    private static string MethodBody(string signature)
    {
        string code = ViewModel();
        int at = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at > 0, signature + " is missing");

        int end = code.IndexOf("\n    }", at + 1, StringComparison.Ordinal);

        return end > at ? code[at..end] : code[at..];
    }
}
