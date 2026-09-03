using System;
using System.IO;

namespace PdfEditorApp.Viewport;

/// <summary>
/// The installed font that can stand in for a PDF's own.
///
/// WHY A STAND-IN IS EVER NEEDED. Real documents SUBSET their fonts: a page's
/// Arial contains only the glyphs that page already uses. Typing a genuinely new
/// word into one was measured to silently drop the letters it lacks, so
/// "Section" written into a heading's font came back "etin" with a success
/// return. Where the document's own font cannot spell a replacement, the core
/// rebuilds the word in the font this class finds.
///
/// WHY THAT IS ACCEPTABLE. It is not a substitute in the usual sense, where a
/// missing typeface is swapped for an approximation. The PDF names the typeface
/// it was set in, and this returns THAT typeface from the machine's own copy.
/// Measured by replacing words with themselves through the stand-in and diffing
/// the rendered page: identical for Times, Arial Bold, Times Italic, Arial Bold
/// Italic, Courier and Comic Sans, and within sub-pixel antialiasing for plain
/// Arial. A negative control registered 22 to 32 per cent changed pixels, so the
/// measurement could see a difference when there was one.
///
/// When there is no match, there is no edit: the caller refuses rather than
/// picking something that merely looks close.
/// </summary>
public static class SystemFontMatch
{
    /// <summary>Where Windows keeps its fonts.</summary>
    public static string FontsDirectory { get; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "Fonts");

    /// <summary>
    /// The font file for a PDF BaseFont name, or null when nothing matches.
    ///
    /// The name arrives as PDFium reports it, subset tag and all
    /// ("AAAAAA+Arial-BoldMT"), so the tag is dropped first. Weight and slope
    /// come from the rest of the name, which is where every producer puts them.
    /// </summary>
    public static string? FileNameFor(string? baseFont)
    {
        if (string.IsNullOrWhiteSpace(baseFont)) { return null; }

        // "AAAAAA+Arial-BoldMT" -> "arial-boldmt"
        int plus = baseFont.LastIndexOf('+');
        string name = (plus >= 0 ? baseFont[(plus + 1)..] : baseFont).ToLowerInvariant();

        bool bold = name.Contains("bold") || name.Contains("demi") || name.Contains("black");
        bool italic = name.Contains("italic") || name.Contains("oblique");

        // ORDER MATTERS. "arialnarrow" contains "arial", so the narrow cut has
        // to be recognised before the plain one claims it.
        if (name.Contains("arialnarrow") || name.Contains("arial-narrow"))
        {
            return Pick("ARIALN.TTF", "ARIALNB.TTF", "ARIALNI.TTF", "ARIALNBI.TTF", bold, italic);
        }
        if (name.Contains("arial") || name.Contains("helvetica"))
        {
            return Pick("arial.ttf", "arialbd.ttf", "ariali.ttf", "arialbi.ttf", bold, italic);
        }
        if (name.Contains("timesnewroman") || name.Contains("times"))
        {
            return Pick("times.ttf", "timesbd.ttf", "timesi.ttf", "timesbi.ttf", bold, italic);
        }
        if (name.Contains("couriernew") || name.Contains("courier"))
        {
            return Pick("cour.ttf", "courbd.ttf", "couri.ttf", "courbi.ttf", bold, italic);
        }
        if (name.Contains("georgia"))
        {
            return Pick("georgia.ttf", "georgiab.ttf", "georgiai.ttf", "georgiaz.ttf", bold, italic);
        }
        if (name.Contains("verdana"))
        {
            return Pick("verdana.ttf", "verdanab.ttf", "verdanai.ttf", "verdanaz.ttf", bold, italic);
        }
        if (name.Contains("tahoma"))
        {
            return Pick("tahoma.ttf", "tahomabd.ttf", "tahoma.ttf", "tahomabd.ttf", bold, italic);
        }
        if (name.Contains("calibri"))
        {
            return name.Contains("light")
                ? Pick("calibril.ttf", "calibri.ttf", "calibrili.ttf", "calibrii.ttf", bold, italic)
                : Pick("calibri.ttf", "calibrib.ttf", "calibrii.ttf", "calibriz.ttf", bold, italic);
        }
        if (name.Contains("cambria"))
        {
            return Pick("cambria.ttc", "cambriab.ttf", "cambriai.ttf", "cambriaz.ttf", bold, italic);
        }
        if (name.Contains("consol"))
        {
            return Pick("consola.ttf", "consolab.ttf", "consolai.ttf", "consolaz.ttf", bold, italic);
        }
        if (name.Contains("comicsans") || name.Contains("comic"))
        {
            return Pick("comic.ttf", "comicbd.ttf", "comici.ttf", "comicz.ttf", bold, italic);
        }
        if (name.Contains("segoeui") || name.Contains("segoe"))
        {
            return Pick("segoeui.ttf", "segoeuib.ttf", "segoeuii.ttf", "segoeuiz.ttf", bold, italic);
        }
        if (name.Contains("trebuchet"))
        {
            return Pick("trebuc.ttf", "trebucbd.ttf", "trebucit.ttf", "trebucbi.ttf", bold, italic);
        }
        if (name.Contains("impact"))
        {
            return "impact.ttf";
        }

        // ⚠️ THE SAME TWO FAMILIES THE CORE WILL READ, AND NO MORE. Recovery
        // proves a page's Burmese by reshaping candidate text through the
        // INSTALLED font and demanding the page's own glyph ids back, so only a
        // font it can do that with is any use here. Its list is
        // recover::installed, and these two must not drift from it: offering a
        // font the core will not accept turns a clean refusal into a failed
        // write after the reader has finished typing.
        if (name.Contains("myanmartext"))
        {
            return Pick("mmrtext.ttf", "mmrtextb.ttf", "mmrtext.ttf", "mmrtextb.ttf", bold, italic);
        }
        if (name.Contains("pyidaungsu"))
        {
            return Pick("Pyidaungsu.ttf", "Pyidaungsu-Bold.ttf",
                "Pyidaungsu.ttf", "Pyidaungsu-Bold.ttf", bold, italic);
        }

        // A symbolic font is deliberately absent. Wingdings was measured to read
        // back empty through a rewrite: its glyphs are pictures, and the letters
        // of a replacement do not map onto them at all.
        return null;
    }

    /// <summary>
    /// The full path to the stand-in font, or null when nothing matches or the
    /// matched file is not actually installed.
    ///
    /// Existence is checked HERE rather than trusted: the tables above name the
    /// files a normal Windows carries, and a machine missing one should refuse
    /// the edit rather than hand the core a path it cannot read.
    /// </summary>
    public static string? PathFor(string? baseFont)
    {
        string? file = FileNameFor(baseFont);
        if (file is null) { return null; }

        string full = Path.Combine(FontsDirectory, file);
        return File.Exists(full) ? full : null;
    }

    private static string Pick(
        string regular, string boldFile, string italicFile, string boldItalicFile,
        bool bold, bool italic) =>
        (bold, italic) switch
        {
            (true, true) => boldItalicFile,
            (true, false) => boldFile,
            (false, true) => italicFile,
            (false, false) => regular,
        };
}
