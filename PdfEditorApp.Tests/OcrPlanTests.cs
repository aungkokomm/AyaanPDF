using System.Linq;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Recognize text: the languages chosen decide which recogniser reads the
/// pages, and choices that cannot work are refused in words.
/// </summary>
public class OcrPlanTests
{
    [Fact]
    public void stored_languages_read_back_in_offered_order_and_unknown_codes_are_dropped()
    {
        Assert.Equal(new[] { "eng", "hin" }, OcrPlan.ParseLanguages("hin+eng"));
        Assert.Equal(new[] { "mya" }, OcrPlan.ParseLanguages(" MYA + klingon "));
        Assert.Equal(new[] { "eng" }, OcrPlan.ParseLanguages(""));
        Assert.Equal(new[] { "eng" }, OcrPlan.ParseLanguages(null));
        Assert.Equal("eng+mya", OcrPlan.StoreLanguages(new[] { "mya", "eng" }));
    }

    [Fact]
    public void choices_that_cannot_work_are_refused_and_the_rest_are_not()
    {
        Assert.NotNull(OcrPlan.Problem(new string[0], fast: false));
        Assert.Contains("Myanmar and Hindi", OcrPlan.Problem(new[] { "hin", "mya" }, fast: false));
        Assert.Contains("English only", OcrPlan.Problem(new[] { "hin" }, fast: true));
        Assert.Contains("English only", OcrPlan.Problem(new[] { "eng", "hin" }, fast: true));

        Assert.Null(OcrPlan.Problem(new[] { "eng" }, fast: true));
        Assert.Null(OcrPlan.Problem(new[] { "eng", "hin" }, fast: false));
        Assert.Null(OcrPlan.Problem(new[] { "eng", "mya" }, fast: false));
    }

    [Fact]
    public void the_languages_pick_the_recogniser()
    {
        Assert.Equal(new OcrPlan(OcrEngineKind.Tesseract, "eng"), OcrPlan.For(new[] { "eng" }, fast: false));
        Assert.Equal(new OcrPlan(OcrEngineKind.Windows, "eng"), OcrPlan.For(new[] { "eng" }, fast: true));
        Assert.Equal(new OcrPlan(OcrEngineKind.Tesseract, "hin+eng"), OcrPlan.For(new[] { "eng", "hin" }, fast: false));
        Assert.Equal(new OcrPlan(OcrEngineKind.Tesseract, "hin"), OcrPlan.For(new[] { "hin" }, fast: false));
        // Myanmar's reader handles the English words on a Myanmar page itself.
        Assert.Equal(new OcrPlan(OcrEngineKind.Myanmar, "mya"), OcrPlan.For(new[] { "eng", "mya" }, fast: false));
    }

    [Fact]
    public void every_bundled_language_is_offered_by_name()
    {
        Assert.Equal(new[] { "English", "Hindi", "Myanmar" }, OcrPlan.Bundled.Select(b => b.Name));
    }
}
