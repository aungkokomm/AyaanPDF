using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace PdfEditorApp.Viewport;

/// <summary>
/// Builds the editable-text model for one page: where every character of the
/// document's own text sits, tied to the line the writer already knows how to
/// address.
/// </summary>
/// <remarks>
/// ⚠️ THE READER IS PDFPIG, THE WRITER IS UNCHANGED. PDFium still renders and
/// the Rust writer still writes; this only answers "what text is here, and
/// exactly where". PDFium's own answer to that costs 1756 ms of per-object FFI
/// on a 296-page book, against about 20 ms here, which is the whole reason this
/// exists.
///
/// ⚠️ AND THE TWO ARE BRIDGED BY GEOMETRY, NOT BY INDEX. Measured: a letter is
/// not an object. PdfPig reports 1111 letters on a page where PDFium reports 58
/// text objects, because ordinary producers put a whole line in one object. It
/// is not bridged by reading order either: the two disagree about the order of
/// a table's cells, and agreeing on that is not something a click requires. So
/// for each line the core published, the letters that PHYSICALLY sit on it are
/// gathered and aligned against its text. Over the corpus that placed 98.0% of
/// Latin characters, and the rest are marked rather than guessed at.
/// </remarks>
public static class TextRegionReader
{
    /// <summary>Where a character has to sit, vertically, to count as on a
    /// line: a fraction of the line's own size, with a floor for tiny type.</summary>
    private const double BaselineToleranceFactor = 0.45;
    private const double BaselineToleranceFloor = 1.5;

    /// <summary>How far outside a line's ink a glyph's box may start and still
    /// belong to it. The line's left is an ink edge and a glyph's box is not,
    /// so a hair of slack here is the difference between placing a line's first
    /// letter and dropping it.</summary>
    private const double EdgeSlack = 1.0;

    /// <summary>
    /// The regions on one page of <paramref name="pdfBytes"/>.
    /// </summary>
    /// <param name="pdfBytes">
    /// ⚠️ THE DOCUMENT AS IT IS NOW, not the file on disk. The open document
    /// carries edits that have not been saved, and a model built from the file
    /// would describe a page the reader is not looking at.
    /// </param>
    /// <param name="lines">
    /// The lines the core published for this page, which carry the object range
    /// the writer addresses by.
    /// </param>
    public static IReadOnlyList<TextRegion> Build(
        byte[] pdfBytes, int pageIndex, IReadOnlyList<LineSnapshot> lines)
    {
        if (pdfBytes is null || pdfBytes.Length == 0 || lines is null || lines.Count == 0)
        {
            return Array.Empty<TextRegion>();
        }

        try
        {
            using var doc = PdfDocument.Open(pdfBytes);
            if (pageIndex < 0 || pageIndex >= doc.NumberOfPages) { return Array.Empty<TextRegion>(); }

            var page = doc.GetPage(pageIndex + 1);
            return Build(page, lines);
        }
        catch (Exception)
        {
            // A document PdfPig will not read is not an error the reader should
            // ever see: the page still renders and still selects the way it did
            // before this existed. It simply offers no regions.
            return Array.Empty<TextRegion>();
        }
    }

    internal static IReadOnlyList<TextRegion> Build(Page page, IReadOnlyList<LineSnapshot> lines)
    {
        double pw = page.Width, ph = page.Height;
        if (pw <= 0 || ph <= 0) { return Array.Empty<TextRegion>(); }

        var letters = page.Letters;
        var used = new bool[letters.Count];
        var built = new List<TextLine>(lines.Count);

        foreach (var line in lines)
        {
            built.Add(BuildLine(line, letters, used, pw, ph));
        }

        return GroupIntoRegions(page, built, pw, ph);
    }

    private static TextLine BuildLine(
        LineSnapshot line, IReadOnlyList<Letter> letters, bool[] used, double pw, double ph)
    {
        // Shaped scripts keep the behaviour they already have. See
        // TextRegionStatus.ShapedScript for the measurement behind that.
        if (IsShaped(line.Text))
        {
            return Refused(line, TextRegionStatus.ShapedScript);
        }

        double left = line.Left * pw;
        double right = line.Right * pw;
        double baseY = ph - (line.Baseline * pw);
        double tol = Math.Max(BaselineToleranceFloor, line.FontSizePts * BaselineToleranceFactor);

        var onLine = new List<(int Index, Letter Letter)>();
        for (int i = 0; i < letters.Count; i++)
        {
            if (used[i]) { continue; }
            var l = letters[i];
            if (Math.Abs(l.StartBaseLine.Y - baseY) > tol) { continue; }
            if (l.BoundingBox.Right < left - EdgeSlack) { continue; }
            if (l.BoundingBox.Left > right + EdgeSlack) { continue; }
            onLine.Add((i, l));
        }
        // ⚠️ GLYPHS THAT SPELL NOTHING ARE NOT PART OF THE TEXT, and dropping
        // them is what keeps the two readers talking about the same line.
        //
        // Measured on the Harari book: editing a line rewrites it into a single
        // object and leaves the objects that used to draw it behind, emptied.
        // They still report as letters, with real boxes in the real font, and
        // their value is U+0000. After one edit of "Introduction" PdfPig saw 23
        // letters where the core saw 12: the twelve real ones interleaved with
        // eleven of these. PDFium does not count them, so neither may this, or
        // every line refuses to be edited a second time.
        onLine.RemoveAll(o => SpellsNothing(o.Letter.Value));

        if (onLine.Count == 0) { return Refused(line, TextRegionStatus.Unmapped); }

        onLine.Sort((a, b) => a.Letter.StartBaseLine.X.CompareTo(b.Letter.StartBaseLine.X));

        string got = string.Concat(onLine.Select(o => o.Letter.Value));
        int[]? map = Align(got, line.Text);
        if (map is null) { return Refused(line, TextRegionStatus.Unmapped); }

        foreach (var o in onLine) { used[o.Index] = true; }

        var characters = new List<TextCharacter>(onLine.Count);
        for (int i = 0; i < onLine.Count; i++)
        {
            var l = onLine[i].Letter;
            characters.Add(new TextCharacter(
                l.Value,
                map[i],
                l.BoundingBox.Left / pw,
                (ph - l.BoundingBox.Top) / pw,
                l.BoundingBox.Right / pw,
                (ph - l.BoundingBox.Bottom) / pw,
                l.FontName ?? string.Empty,
                l.PointSize,
                HexOf(l)));
        }

        return new TextLine(
            line.Text, line.FirstObject, line.LastObject,
            line.Left, line.Top, line.Right, line.Bottom, line.Baseline,
            TextRegionStatus.Ok, IntoRuns(characters));
    }

    /// <summary>
    /// Whether this glyph carries no text at all: nothing, or nothing but
    /// U+0000, which is what a glyph with no Unicode mapping reads back as.
    /// </summary>
    /// <remarks>
    /// ⚠️ NOT THE SAME AS BLANK. A space is text, is counted by both readers,
    /// and a caret can sit either side of it. This is a glyph the font cannot
    /// spell, which PDFium leaves out of the line entirely.
    /// </remarks>
    private static bool SpellsNothing(string value)
    {
        if (string.IsNullOrEmpty(value)) { return true; }

        foreach (char c in value)
        {
            if (c != '\0') { return false; }
        }
        return true;
    }

    /// <summary>
    /// The colour this glyph is drawn in, as #RRGGBB, falling back to black.
    /// </summary>
    /// <remarks>
    /// Wrapped because a page can define colour in spaces this does not have to
    /// understand, and a glyph whose colour cannot be read is still a glyph the
    /// reader can edit. Black is what nearly all body text is.
    /// </remarks>
    private static string HexOf(Letter letter)
    {
        try
        {
            var (r, g, b) = letter.Color.ToRGBValues();
            return "#" + Channel(r) + Channel(g) + Channel(b);
        }
        catch (Exception)
        {
            return "#000000";
        }

        static string Channel(double v) =>
            ((int)Math.Round(Math.Clamp(v, 0, 1) * 255)).ToString("X2");
    }

    private static TextLine Refused(LineSnapshot line, TextRegionStatus status) =>
        new(line.Text, line.FirstObject, line.LastObject,
            line.Left, line.Top, line.Right, line.Bottom, line.Baseline,
            status, Array.Empty<TextRun>());

    /// <summary>A run is a stretch set in one font at one size.</summary>
    private static IReadOnlyList<TextRun> IntoRuns(List<TextCharacter> characters)
    {
        var runs = new List<TextRun>();
        int start = 0;
        for (int i = 1; i <= characters.Count; i++)
        {
            bool boundary = i == characters.Count
                || characters[i].FontName != characters[start].FontName
                || Math.Abs(characters[i].PointSize - characters[start].PointSize) > 0.01;
            if (!boundary) { continue; }

            var span = characters.GetRange(start, i - start);
            runs.Add(new TextRun(
                string.Concat(span.Select(c => c.Text)),
                span.Min(c => c.Left), span.Min(c => c.Top),
                span.Max(c => c.Right), span.Max(c => c.Bottom),
                span[0].FontName, span[0].PointSize, span));
            start = i;
        }
        return runs;
    }

    /// <summary>
    /// Groups lines into blocks using PdfPig's own page segmentation, and falls
    /// back to one region per line if it declines to answer.
    /// </summary>
    private static IReadOnlyList<TextRegion> GroupIntoRegions(
        Page page, List<TextLine> lines, double pw, double ph)
    {
        List<(double L, double T, double R, double B)> boxes;
        try
        {
            var words = page.GetWords(NearestNeighbourWordExtractor.Instance);
            boxes = DocstrumBoundingBoxes.Instance.GetBlocks(words)
                .Select(b => (
                    b.BoundingBox.Left / pw,
                    (ph - b.BoundingBox.Top) / pw,
                    b.BoundingBox.Right / pw,
                    (ph - b.BoundingBox.Bottom) / pw))
                .ToList();
        }
        catch (Exception)
        {
            boxes = new List<(double, double, double, double)>();
        }

        var regions = new List<TextRegion>();
        var taken = new bool[lines.Count];

        foreach (var box in boxes)
        {
            var mine = new List<TextLine>();
            for (int i = 0; i < lines.Count; i++)
            {
                if (taken[i]) { continue; }
                var l = lines[i];
                double midY = (l.Top + l.Bottom) / 2;
                double midX = (l.Left + l.Right) / 2;
                if (midY < box.T - 0.002 || midY > box.B + 0.002) { continue; }
                if (midX < box.L - 0.002 || midX > box.R + 0.002) { continue; }
                mine.Add(l);
                taken[i] = true;
            }
            if (mine.Count == 0) { continue; }
            regions.Add(new TextRegion(
                mine.Min(l => l.Left), mine.Min(l => l.Top),
                mine.Max(l => l.Right), mine.Max(l => l.Bottom), mine));
        }

        // Anything the segmenter did not claim is still a region of its own, so
        // a line is never lost just because it sits outside a block.
        for (int i = 0; i < lines.Count; i++)
        {
            if (taken[i]) { continue; }
            var l = lines[i];
            regions.Add(new TextRegion(l.Left, l.Top, l.Right, l.Bottom, new[] { l }));
        }

        return regions;
    }

    /// <summary>
    /// Whether this text is in a script that has to be shaped to be laid out:
    /// one with reordering, ligatures, contextual or stacked forms.
    /// </summary>
    /// <remarks>
    /// ⚠️ THIS IS NOT "ANYTHING ABOVE U+0300". That was the first rule here and
    /// it was wrong twice over. It is the PIECE WRITER's refusal threshold
    /// (pieces.rs locate_by_model), not a statement about scripts, and copying
    /// it here meant a curly apostrophe made a line a shaped script: measured
    /// over the first 40 pages of the Harari book, 155 of 1279 lines, 12.1%,
    /// every one of them ordinary English, with "Don't panic" the first of
    /// them. None of that book is a complex script at all.
    ///
    /// ⚠️ AND ONE WRITER'S REFUSAL IS NOT THE APP'S. That mistake has been made
    /// here before, hiding a working writer behind a refusal box. What the
    /// reader may claim is only "I could place these characters"; whether an
    /// edit is accepted belongs to whichever route the edit takes, and is not
    /// answerable from a character range.
    ///
    /// So this mirrors the core's own needs_shaping, which deliberately passes
    /// Cyrillic and CJK because they map one to one.
    /// </remarks>
    public static bool IsShaped(string text)
    {
        foreach (char c in text)
        {
            int u = c;
            if ((u >= 0x0590 && u <= 0x05FF)      // Hebrew
                || (u >= 0x0600 && u <= 0x06FF)   // Arabic
                || (u >= 0x0700 && u <= 0x074F)   // Syriac
                || (u >= 0x0750 && u <= 0x077F)   // Arabic Supplement
                || (u >= 0x0780 && u <= 0x07BF)   // Thaana
                || (u >= 0x0900 && u <= 0x0DFF)   // Devanagari .. Sinhala
                || (u >= 0x0E00 && u <= 0x0FFF)   // Thai, Lao, Tibetan
                || (u >= 0x1000 && u <= 0x109F)   // Myanmar
                || (u >= 0x1780 && u <= 0x17FF)   // Khmer
                || (u >= 0xFB1D && u <= 0xFDFF)   // Hebrew/Arabic presentation forms
                || (u >= 0xFE70 && u <= 0xFEFF))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Gives every character of <paramref name="drawn"/> its offset in
    /// <paramref name="lineText"/>, allowing the two readers to disagree about
    /// whitespace and nothing else.
    /// </summary>
    /// <remarks>
    /// ⚠️ WHITESPACE IS WHERE THEY LEGITIMATELY DIFFER. Measured on a real
    /// book: the word gaps decode as U+0009 through the font's own map while
    /// PDFium hands the app U+0020. Everything that is not whitespace has to
    /// match exactly, because that is what an edit is addressed by, and a
    /// looser rule would place a caret on the wrong glyph.
    /// </remarks>
    public static int[]? Align(string drawn, string lineText)
    {
        var map = new int[drawn.Length];
        int j = 0;
        for (int i = 0; i < drawn.Length; i++)
        {
            char a = drawn[i];
            if (char.IsWhiteSpace(a))
            {
                if (j < lineText.Length && char.IsWhiteSpace(lineText[j])) { map[i] = j; j++; }
                else { map[i] = j; }
                continue;
            }
            while (j < lineText.Length && char.IsWhiteSpace(lineText[j]) && lineText[j] != a) { j++; }
            if (j >= lineText.Length || lineText[j] != a) { return null; }
            map[i] = j;
            j++;
        }
        while (j < lineText.Length && char.IsWhiteSpace(lineText[j])) { j++; }
        return j == lineText.Length ? map : null;
    }
}
