using System;
using System.Collections.Generic;
using System.IO;
using PdfEditorApp.Rendering.Skia;
using PdfEditorApp.Viewport;
using SkiaSharp;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The one committed shape Skia draws, and everything it must not draw.
///
/// A gradient reaches a shape's appearance stream only when the file is saved,
/// because PDFium cannot make a shading. This is what stands in between: the
/// selected shape, only while it has a gradient, painted over PDFium's own
/// rendering of it.
///
/// The measurement that approved this architecture is in
/// GradientOverlapMeasurement. These are the rules the feed has to keep: what
/// it emits, what it refuses to emit, and that the stored gradient is never
/// what moves.
/// </summary>
public class SelectedShapeOverlayTests
{
    private const double PageWidthPts = 600;
    private const double Scale = 800;

    private const double L = 0.1, T = 0.1, R = 0.4, B = 0.3;

    private const string Gradient =
        "f(c=FFFF0000,c2=FF0000FF,x0=0.0000,y0=0.5000,x1=1.0000,y1=0.5000)";

    private const string Shadow = "s(a=135.00,d=6.0000,b=0.0000,p=0.0000,c=FF000000)";
    private const string Glow = "g(a=0.00,d=0.0000,b=4.0000,p=0.0000,c=FFFFD400)";

    private static ShapeTag Tag(
        ShapeKind kind = ShapeKind.Rectangle,
        string? fillHex = null,
        string effects = Gradient,
        double rotationDeg = 0,
        double cornerRadiusPts = 0) =>
        new(kind, "#FF000000", 2, false, false, rotationDeg, fillHex, cornerRadiusPts,
            EffectsText: effects);

    private static IReadOnlyList<ShapeRenderItem> Items(ShapeTag tag) =>
        SelectedShapeOverlay.ItemsFor(tag, 0, L, T, R, B, PageWidthPts);

    // ---------------- what it emits ----------------

    [Theory]
    [InlineData(ShapeKind.Rectangle)]
    [InlineData(ShapeKind.Ellipse)]
    [InlineData(ShapeKind.RoundedRectangle)]
    public void a_selected_gradient_shape_is_emitted_with_its_gradient_and_its_stroke(ShapeKind kind)
    {
        var items = Items(Tag(kind, cornerRadiusPts: kind == ShapeKind.RoundedRectangle ? 20 : 0));

        var item = Assert.Single(items);

        Assert.NotNull(item.Fill.Gradient);
        Assert.Equal(new RenderColor(0xFF, 0, 0, 0), item.Color);
        Assert.Equal(2 / PageWidthPts, item.StrokeWidth, 9);
    }

    [Fact]
    public void the_geometry_is_the_shapes_own_and_not_a_box_around_it()
    {
        // Every kind draws its own outline: an ellipse is a polygon of its own
        // arc and a rounded rectangle has its corners cut. If the feed built a
        // rectangle for all three, the gradient would spill past the shape.
        int rectangle = Items(Tag(ShapeKind.Rectangle))[0].Points.Count;
        int ellipse = Items(Tag(ShapeKind.Ellipse))[0].Points.Count;
        int rounded = Items(Tag(ShapeKind.RoundedRectangle, cornerRadiusPts: 20))[0].Points.Count;

        Assert.Equal(5, rectangle);
        Assert.True(ellipse > 20, $"the ellipse came out as {ellipse} points");
        Assert.True(rounded > 20, $"the rounded rectangle came out as {rounded} points");
    }

    // ---------------- what it refuses ----------------

    [Fact]
    public void a_shape_with_no_gradient_is_not_fed_at_all()
    {
        // A solid fill is already in the appearance stream. Painting it again
        // would double a translucent one, and would put this path in the way of
        // every shape on the page rather than the one that needs it.
        Assert.Empty(Items(Tag(fillHex: "#FF3B82F6", effects: "")));
        Assert.Empty(Items(Tag(effects: "")));
        Assert.Empty(Items(Tag(effects: Shadow + ":" + Glow)));
    }

    [Fact]
    public void switching_a_gradient_to_a_solid_takes_the_overlay_away()
    {
        // The tag after the switch: no gradient field, a positional fill back
        // in its place. Nothing is left on screen standing in for paint that is
        // no longer there.
        Assert.NotEmpty(Items(Tag()));
        Assert.Empty(Items(Tag(fillHex: "#FFFF0000", effects: "")));
    }

    [Theory]
    [InlineData(Shadow)]
    [InlineData(Glow)]
    [InlineData(Shadow + ":" + Glow)]
    public void the_effects_are_left_to_pdfium(string effects)
    {
        // THE DOUBLING THIS PREVENTS. A shadow and a glow are already in the
        // annotation, as paths or as a rasterised picture, and PDFium draws
        // them underneath. Sending them here as well paints a second shadow
        // over the first, and where two translucent ones overlap the result is
        // darker than either.
        var item = Assert.Single(Items(Tag(effects: Gradient + ":" + effects)));

        Assert.Null(item.Effects);
        Assert.NotNull(item.Fill.Gradient);
    }

    [Fact]
    public void a_page_with_no_width_and_a_box_with_no_size_are_refused()
    {
        Assert.Empty(SelectedShapeOverlay.ItemsFor(Tag(), 0, L, T, R, B, 0));
        Assert.Empty(SelectedShapeOverlay.ItemsFor(Tag(), 0, L, T, L, B, PageWidthPts));
    }

    // ---------------- and where it puts things ----------------

    [Fact]
    public void the_gradient_is_resolved_against_the_shapes_current_box()
    {
        // Fractions of the shape's own upright box become points beside the
        // mark's own points. Move and resize are then not features: the box is
        // what changed, and the endpoints followed it.
        var g = Items(Tag())[0].Fill.Gradient!.Value;

        Assert.Equal(L, g.X0, 9);
        Assert.Equal((T + B) / 2, g.Y0, 9);
        Assert.Equal(R, g.X1, 9);
    }

    [Fact]
    public void moving_the_shape_moves_the_gradient_with_it()
    {
        var moved = SelectedShapeOverlay.ItemsFor(
            Tag(), 0, L + 0.2, T + 0.1, R + 0.2, B + 0.1, PageWidthPts);

        var g = moved[0].Fill.Gradient!.Value;

        Assert.Equal(L + 0.2, g.X0, 9);
        Assert.Equal(R + 0.2, g.X1, 9);
        Assert.Equal(((T + B) / 2) + 0.1, g.Y0, 9);
    }

    [Fact]
    public void resizing_the_shape_stretches_the_gradient()
    {
        var wider = SelectedShapeOverlay.ItemsFor(Tag(), 0, L, T, R + 0.3, B, PageWidthPts);

        var g = wider[0].Fill.Gradient!.Value;

        Assert.Equal(L, g.X0, 9);
        Assert.Equal(R + 0.3, g.X1, 9);
    }

    [Fact]
    public void the_gradient_turns_with_the_shape()
    {
        // THE TRAP THIS CLOSES. A shape's own angle is carried by turning its
        // points; the gradient's endpoints live in the same space beside them,
        // so a turn that moved one and not the other would leave the paint
        // upright inside a turned shape.
        var turned = Items(Tag(rotationDeg: 90))[0];
        var g = turned.Fill.Gradient!.Value;

        // A quarter turn about the box's centre takes a gradient that ran
        // across the shape and makes it run down it.
        Assert.Equal(g.X0, g.X1, 9);
        Assert.True(g.Y1 > g.Y0, "the gradient did not turn with the shape");

        // And the points turned by the same amount: the shape is now taller
        // than it is wide.
        double left = double.MaxValue, right = double.MinValue;
        double top = double.MaxValue, bottom = double.MinValue;
        foreach (var (x, y) in turned.Points)
        {
            left = Math.Min(left, x); right = Math.Max(right, x);
            top = Math.Min(top, y); bottom = Math.Max(bottom, y);
        }

        Assert.True(bottom - top > right - left, "the shape did not turn");
    }

    [Fact]
    public void the_stored_gradient_is_never_what_moves()
    {
        // The tag stays fractions of the box through every one of these. Only
        // the resolved copy the renderer is handed is moved, stretched or
        // turned.
        foreach (var tag in new[] { Tag(), Tag(rotationDeg: 30), Tag(rotationDeg: 90) })
        {
            Assert.Equal(Gradient, ShapeFillTag.FieldOf(ShapeFillTag.From(tag)));
        }
    }

    // ---------------- and what it actually paints ----------------

    [Fact]
    public void the_overlay_paints_a_gradient_clipped_to_the_shape()
    {
        // Through the real painter, on pixels, because everything above is
        // about the description rather than the paint.
        using var bitmap = Paint(Items(Tag(ShapeKind.Ellipse)));

        int cx = (int)(((L + R) / 2) * Scale), cy = (int)(((T + B) / 2) * Scale);

        var middle = bitmap.GetPixel(cx, cy);
        Assert.InRange(middle.Blue, 100, 155);
        Assert.InRange(middle.Red, 100, 155);

        // Inside the ellipse's box, outside the ellipse.
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

    [Fact]
    public void the_view_model_feeds_it_and_the_page_paints_it()
    {
        string page = FileFromRepo("PdfEditorApp", "MainPage.xaml.cs");

        Assert.Contains(
            "frame.AddRange(ViewModel.SelectedShapeOverlayItems);", page, StringComparison.Ordinal);

        // Deselecting has to take it away, and the tool rail's update is what
        // runs on every selection change.
        int at = page.IndexOf("private void UpdateToolRail()", StringComparison.Ordinal);
        Assert.True(at > 0, "UpdateToolRail is missing");

        int end = page.IndexOf("\n    private ", at + 1, StringComparison.Ordinal);
        Assert.Contains("RefreshSkiaShapeLayer();", page[at..end], StringComparison.Ordinal);
    }

    [Fact]
    public void the_old_overlay_shape_list_is_still_dead()
    {
        // The feed is one selected shape read from its own tag. If anything
        // ever starts adding to the list that v1.72 emptied, this is where that
        // shows up, because that list coming back is the architecture this was
        // built to avoid.
        string source = FileFromRepo("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");

        Assert.DoesNotContain("_allShapes.Add(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_allShapes.Insert(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void the_live_overlay_is_nowhere_near_the_save_path()
    {
        // The saved file's gradient comes from the tag through the lopdf
        // writer, and it must keep coming from there whatever the live surface
        // is doing. A save that consulted the overlay would make the file
        // depend on what happened to be selected.
        string source = FileFromRepo("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");

        int at = source.IndexOf(
            "public bool SaveDocumentAs(string path, bool flatten)", StringComparison.Ordinal);
        Assert.True(at > 0, "SaveDocumentAs is missing");

        int end = source.IndexOf("\n    public ", at + 1, StringComparison.Ordinal);

        Assert.DoesNotContain("SelectedShapeOverlay", source[at..end], StringComparison.Ordinal);
        Assert.Contains("WriteGradientFills(writePath);", source[at..end], StringComparison.Ordinal);
    }
}
