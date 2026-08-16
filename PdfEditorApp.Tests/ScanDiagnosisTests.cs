using System.Collections.Generic;
using System.Linq;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Telling the reader whether their DOCUMENT is the problem.
///
/// The question this answers has no good answer otherwise: when bookmarking by
/// style finds nothing, is the feature weak or is the file unreadable? The scan
/// has already been over every page and knows. Saying so turns a dead end into
/// an answer; silence lets a scanned book look like a broken feature.
/// </summary>
public class ScanDiagnosisTests
{
    private static readonly TextStyle Any = new("F1", 12, 0);

    private static StyledRunPage Page(int index, int chars, int runs) =>
        new(index, chars,
            Enumerable.Range(0, runs)
                .Select(i => new StyledRun(index, 0, i * 10, 5, Any, "text"))
                .ToList());

    private static IReadOnlyList<StyledRunPage> Pages(int count, int chars, int runs) =>
        Enumerable.Range(0, count).Select(i => Page(i, chars, runs)).ToList();

    [Fact]
    public void a_document_with_no_text_anywhere_is_named_as_a_scan()
    {
        // And told what would fix it, which is the part that makes this worth
        // showing at all.
        var diagnosis = ScanDiagnosis.Of(Pages(900, chars: 0, runs: 0), styles: 0);

        Assert.True(diagnosis.LooksScanned);
        Assert.Contains("scan", diagnosis.Describe(), System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OCR", diagnosis.Describe(), System.StringComparison.Ordinal);
    }

    [Fact]
    public void a_mostly_scanned_document_says_how_much_of_it_is_readable()
    {
        // A book scanned except for its typeset front matter. Naming the number
        // is what tells the reader the result is partial rather than wrong.
        var pages = Pages(80, chars: 0, runs: 0).Concat(Pages(20, chars: 500, runs: 4)).ToList();

        var diagnosis = ScanDiagnosis.Of(pages, styles: 3);

        Assert.True(diagnosis.LooksScanned);
        Assert.Contains("80 of 100", diagnosis.Describe(), System.StringComparison.Ordinal);
    }

    [Fact]
    public void a_few_picture_pages_do_not_make_a_book_a_scan()
    {
        // A book with forty plates in the middle is not a scanned book, and
        // telling its owner to run OCR would be wrong and unhelpful.
        var pages = Pages(360, chars: 900, runs: 6).Concat(Pages(40, chars: 0, runs: 0)).ToList();

        var diagnosis = ScanDiagnosis.Of(pages, styles: 5);

        Assert.False(diagnosis.LooksScanned);
        Assert.Contains("5 styles", diagnosis.Describe(), System.StringComparison.Ordinal);
    }

    [Fact]
    public void text_that_maps_to_nothing_is_told_apart_from_no_text_at_all()
    {
        // Both come back as zero runs and they need OPPOSITE advice: OCR fixes
        // a scan, and nothing fixes a font with no Unicode map, which defeats
        // every reader's search and not only this one.
        var diagnosis = ScanDiagnosis.Of(Pages(50, chars: 2000, runs: 0), styles: 0);

        Assert.False(diagnosis.LooksScanned);
        Assert.True(diagnosis.LooksUnreadable);

        string described = diagnosis.Describe();
        Assert.DoesNotContain("OCR", described, System.StringComparison.Ordinal);
        Assert.Contains("any reader", described, System.StringComparison.Ordinal);
    }

    [Fact]
    public void an_ordinary_document_gets_the_ordinary_instruction()
    {
        // The common case still has to read as an instruction, not as a report.
        var diagnosis = ScanDiagnosis.Of(Pages(320, chars: 1800, runs: 12), styles: 14);

        Assert.False(diagnosis.LooksScanned);
        Assert.False(diagnosis.LooksUnreadable);

        string described = diagnosis.Describe();
        Assert.Contains("14 styles", described, System.StringComparison.Ordinal);
        Assert.Contains("all 320 pages", described, System.StringComparison.Ordinal);
        Assert.Contains("Tick", described, System.StringComparison.Ordinal);
    }

    [Fact]
    public void one_style_is_not_called_styles()
    {
        Assert.Contains("1 style ", ScanDiagnosis.Of(Pages(3, 100, 2), styles: 1).Describe(),
                        System.StringComparison.Ordinal);
    }

    [Fact]
    public void text_that_is_all_paragraphs_says_so_rather_than_nothing()
    {
        // Runs longer than a heading are dropped before they reach here, so a
        // document of unbroken prose gives text, no runs on some pages, and no
        // styles. That is its own answer.
        var pages = Pages(10, chars: 4000, runs: 1).Concat([Page(11, 4000, 0)]).ToList();

        Assert.Contains("no styles came back",
                        ScanDiagnosis.Of(pages, styles: 0).Describe(),
                        System.StringComparison.Ordinal);
    }

    [Fact]
    public void an_empty_document_says_nothing_to_scan()
    {
        Assert.Equal("Nothing to scan.", ScanDiagnosis.Of([], styles: 0).Describe());
    }
}
