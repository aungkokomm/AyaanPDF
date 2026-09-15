using System;
using System.Linq;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The gray page image every recogniser reads: taken from PDFium's BGRA render,
/// cropped to lines, split into ink and paper, and resized for the line model.
/// </summary>
public class GrayImageTests
{
    private static GrayImage Blank(int width, int height, byte value = 255) =>
        new(width, height, Enumerable.Repeat(value, width * height).ToArray());

    [Fact]
    public void bgra_becomes_gray_weighted_the_way_the_eye_sees_colour()
    {
        // White, black, pure red, pure green: in BGRA order.
        byte[] bgra = { 255, 255, 255, 255, 0, 0, 0, 255, 0, 0, 255, 255, 0, 255, 0, 255 };
        var image = GrayImage.FromBgra(bgra, 4, 1);

        Assert.Equal(new byte[] { 255, 0, 76, 149 }, image.Pixels);
    }

    [Fact]
    public void a_crop_is_the_rectangle_asked_for_and_stays_inside_the_image()
    {
        var image = new GrayImage(4, 3, Enumerable.Range(0, 12).Select(i => (byte)i).ToArray());

        var middle = image.Crop(1, 1, 2, 2);
        Assert.Equal(new byte[] { 5, 6, 9, 10 }, middle.Pixels);

        var overhanging = image.Crop(3, 2, 10, 10);
        Assert.Equal((1, 1), (overhanging.Width, overhanging.Height));
        Assert.Equal(new byte[] { 11 }, overhanging.Pixels);
    }

    [Fact]
    public void otsu_separates_ink_from_paper_and_finds_where_the_ink_is()
    {
        var image = Blank(20, 10, 230);
        for (int x = 4; x < 15; x++) { image.Pixels[3 * 20 + x] = 20; image.Pixels[4 * 20 + x] = 35; }

        int threshold = image.OtsuThreshold();
        Assert.InRange(threshold, 35, 229);

        var rows = image.RowsWithInk(threshold);
        Assert.Equal(new[] { 3, 4 }, Enumerable.Range(0, 10).Where(y => rows[y]).ToArray());
        Assert.Equal((4, 14), image.InkColumns(0, 10, threshold));
        Assert.Null(image.InkColumns(5, 10, threshold));
    }

    [Fact]
    public void resizing_keeps_a_flat_image_flat_and_a_dark_stripe_where_it_was()
    {
        var flat = Blank(90, 45, 200).Resize(64, 32);
        Assert.Equal((64, 32), (flat.Width, flat.Height));
        Assert.All(flat.Pixels, p => Assert.InRange(p, (byte)199, (byte)201));

        // A dark column a third of the way across stays a third of the way across.
        var striped = Blank(60, 20);
        for (int y = 0; y < 20; y++) { for (int x = 18; x < 22; x++) { striped.Pixels[y * 60 + x] = 0; } }
        var doubled = striped.Resize(120, 20);
        int darkest = Enumerable.Range(0, 120).OrderBy(x => doubled.Pixels[10 * 120 + x]).First();
        Assert.InRange(darkest, 36, 44);
    }
}
