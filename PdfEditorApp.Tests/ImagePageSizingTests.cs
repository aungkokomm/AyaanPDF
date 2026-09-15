using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>The page a picture goes on in a merged file.</summary>
public class ImagePageSizingTests
{
    [Fact]
    public void a_pictures_own_size_comes_from_its_pixels_and_resolution()
    {
        Assert.Equal((720f, 360f), ImagePageSizing.For(960, 480, 96, 96, ImagePageSize.OwnSize));
        Assert.Equal((72f, 144f), ImagePageSizing.For(300, 600, 300, 300, ImagePageSize.OwnSize));
    }

    [Fact]
    public void a_file_with_no_resolution_is_read_at_96_dots_an_inch()
    {
        Assert.Equal((720f, 360f), ImagePageSizing.For(960, 480, 0, 0, ImagePageSize.OwnSize));
    }

    [Fact]
    public void a_huge_picture_is_scaled_to_the_largest_page_a_pdf_can_hold()
    {
        var (width, height) = ImagePageSizing.For(40000, 20000, 96, 96, ImagePageSize.OwnSize);

        Assert.Equal(ImagePageSizing.LargestPage, width);
        Assert.Equal(ImagePageSizing.LargestPage / 2, height);
    }

    [Fact]
    public void paper_is_turned_to_match_the_picture()
    {
        Assert.Equal((595.28f, 841.89f), ImagePageSizing.For(1000, 2000, 96, 96, ImagePageSize.A4));
        Assert.Equal((841.89f, 595.28f), ImagePageSizing.For(2000, 1000, 96, 96, ImagePageSize.A4));
        Assert.Equal((792f, 612f), ImagePageSizing.For(2000, 1000, 96, 96, ImagePageSize.Letter));
    }
}
