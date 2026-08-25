using System;
using System.Collections.Generic;
using System.Linq;

namespace PdfEditorApp.Viewport;

/// <summary>
/// Whether the app is being read or being edited.
///
/// ⚠️ NOT another gesture layer, and it exists to remove one. A single
/// pointer chain was answering both questions at once: a press on the page had
/// to be a reader's text selection AND a possible object pick AND a possible
/// text-unit selection, and every new capability made that chain longer. The
/// mode splits it in two, so each half only has to decide among things that
/// belong together.
///
/// View is the default on every document open. A reader who never presses Edit
/// gets a viewer, and nothing they click can change the file.
/// </summary>
public enum AppMode
{
    /// <summary>Read, select text, follow links, fill forms. Ayaan's own marks
    /// are drawn but cannot be picked up.</summary>
    View,

    /// <summary>Everything View does, plus the tools and the selections that
    /// change the document.</summary>
    Edit,
}

/// <summary>What plain left-drag/click currently does in the viewport.</summary>
public enum ToolMode
{
    /// <summary>Drag selects text, or picks up and moves an annotation.</summary>
    Select,
    /// <summary>Drag selects text and immediately commits it as a permanent highlight.</summary>
    Highlight,
    /// <summary>Click places a sticky note.</summary>
    Note,
    /// <summary>Drag draws a freehand ink stroke.</summary>
    Draw,
    /// <summary>Plain left-drag pans, same as holding Space with any other tool active.</summary>
    Hand,
    /// <summary>Click places the chosen stamp image.</summary>
    Stamp,
    /// <summary>Drag draws a rectangle, ellipse, line or arrow.</summary>
    Shape,
    /// <summary>Click places an editable text box.</summary>
    Text,
    /// <summary>Drag draws the clickable area of a hyperlink.</summary>
    Link,
}

/// <summary>Which extra controls a tool needs in the property bar.</summary>
[Flags]
public enum ToolOptions
{
    None = 0,
    /// <summary>Offers a colour, from the pen or highlighter palette.</summary>
    Color = 1,
    /// <summary>Offers a stroke thickness.</summary>
    Width = 2,
    /// <summary>Offers the stamp picker.</summary>
    Stamp = 4,
    /// <summary>Offers the shape kind picker: rectangle, ellipse, line, arrow.</summary>
    Shape = 8,
    /// <summary>Offers the font-size picker.</summary>
    FontSize = 16,
}

/// <summary>
/// One entry in the tool rail: everything the UI needs to draw a tool and
/// decide what it offers.
///
/// A record rather than a hand-written button, because every new tool used to
/// mean another block of XAML, another named field, and another line in the
/// method that highlights the active one. Three places to edit per tool is
/// three places to forget, and the rail is about to grow shapes, text boxes
/// and callouts.
/// </summary>
/// <param name="Glyph">Segoe MDL2 Assets code point.</param>
/// <param name="Shortcut">Single key that selects this tool, uppercase.</param>
/// <param name="PathData">
/// Optional SVG path drawn instead of <paramref name="Glyph"/>, for the few
/// tools where no icon-font glyph reads right; the rail falls back to the glyph
/// when this is null. The authoring box does not matter, since the rail scales
/// the path to fit its own.
/// </param>
public sealed record ToolDefinition(
    ToolMode Mode,
    string Name,
    string Glyph,
    char Shortcut,
    ToolOptions Options,
    string? PathData = null)
{
    /// <summary>Tooltip text, with the shortcut appended so it is discoverable.</summary>
    public string Tooltip => $"{Name} ({Shortcut})";

    public bool Offers(ToolOptions option) => (Options & option) != 0;
}

/// <summary>The tools the rail shows, in order.</summary>
public static class ToolCatalog
{
    /// <summary>
    /// Order matters: it is the order they appear, and it groups by what they
    /// do. Navigation first, then selection, then the marks, then the things
    /// that place an object.
    /// </summary>
    public static IReadOnlyList<ToolDefinition> All { get; } =
    [
        new(ToolMode.Hand, "Hand", "", 'H', ToolOptions.None, HandIcon),
        new(ToolMode.Select, "Select", "", 'V', ToolOptions.None),
        new(ToolMode.Highlight, "Highlight", "", 'U', ToolOptions.Color),
        new(ToolMode.Draw, "Draw", "", 'D', ToolOptions.Color | ToolOptions.Width),
        new(ToolMode.Shape, "Shape", "", 'R', ToolOptions.Color | ToolOptions.Width | ToolOptions.Shape),
        new(ToolMode.Text, "Text", "", 'T', ToolOptions.Color | ToolOptions.FontSize),
        new(ToolMode.Note, "Note", "", 'N', ToolOptions.None),
        new(ToolMode.Stamp, "Stamp", "", 'S', ToolOptions.Stamp, StampIcon),
        // No options of its own: a link has no appearance to style. Measured in
        // every real file, a link carries no /AP and a zero-width border, so
        // colour and thickness would be settings that change nothing.
        new(ToolMode.Link, "Link", "", 'L', ToolOptions.None),
    ];

    /// <summary>
    /// The hand icon: an open palm, four fingers, thumb out to the left. Same
    /// hand the pan CURSOR shows, from hand-paper.svg in the repo root.
    ///
    /// FILLED, not stroked, and it still reads as an outline: the second
    /// subpath traces the inside of the hand and the default even-odd rule
    /// cuts it out. That is why there is no "F1" prefix here, unlike the other
    /// path icon - nonzero winding would fill the hole back in.
    ///
    /// Coordinates are the source file's 512 box; the rail scales to fit.
    /// </summary>
    private const string HandIcon =
        "m 260.07812,39.066406 c -12.97162,0.476609 -25.71337,6.198807 -34.5371,15.746094 -0.22297,0.263068 -0.78359,0.861471 -1.12406,1.268154 -3.35873,3.851573 -6.04814,8.24227 -8.25525,12.839314 -0.55553,1.356589 -1.98204,2.080946 -3.29062,2.56636 -1.71229,0.587459 -3.57265,0.378713 -5.24868,-0.217205 -12.14887,-3.068086 -25.44917,-1.269258 -36.31412,4.986065 -7.14821,4.052817 -13.30505,9.846718 -17.75556,16.756531 -0.2589,0.415847 -0.54494,0.84216 -0.82412,1.314017 -3.09189,5.102754 -5.1987,10.780904 -6.31259,16.637154 -0.0904,0.49719 -0.25078,1.40559 -0.32358,1.90692 -0.22249,1.49458 -0.37882,2.88605 -0.48224,4.43525 -0.22064,4.04615 -0.0588,8.10233 -0.10861,12.15275 -0.002,37.84479 -0.002,75.68959 -0.002,113.53438 -4.97858,-1.76139 -9.8896,-3.73427 -14.95712,-5.23046 -12.27365,-3.41772 -25.85671,-1.89183 -37.011985,4.28716 -11.532141,6.30996 -20.444737,17.2501 -24.196803,29.85871 -3.827792,12.50793 -2.706266,26.48877 3.283273,38.14005 2.404021,4.83762 5.816465,9.0578 8.881755,13.47672 33.69157,47.29615 67.36782,94.60438 101.08254,141.88406 4.02803,5.20169 10.73032,8.12152 17.27889,7.6795 64.82198,0.0108 129.64407,0.0229 194.46598,-0.0144 8.19514,0.16006 16.15808,-5.36812 18.84655,-13.11437 1.30511,-4.10633 1.99962,-8.37064 3.05121,-12.54334 8.29223,-36.04925 16.63179,-72.08785 24.85081,-108.1537 0.91615,-4.33664 1.67839,-8.86183 2.24717,-13.12152 0.74191,-5.62535 1.24211,-11.36831 1.37462,-16.9742 0.19609,-8.65628 0.0653,-17.31573 0.10641,-25.97345 -0.008,-32.34904 0.035,-64.69883 -0.0524,-97.04738 -0.37306,-11.17508 -4.5745,-22.18234 -11.79248,-30.72824 -7.92104,-9.53635 -19.48879,-15.95948 -31.78854,-17.56382 -5.33285,-0.73212 -10.75356,-0.40939 -16.05493,0.40428 -3.76567,0.21701 -7.09192,-3.52115 -6.5368,-7.23047 -0.049,-7.42032 0.47924,-14.96134 -1.29407,-22.2411 -2.71573,-11.847443 -9.71911,-22.672848 -19.49729,-29.910715 -10.96334,-8.266152 -25.49479,-11.743529 -38.97483,-8.939712 -3.18557,0.613634 -6.30804,1.501279 -9.45841,2.267312 -2.902,-6.056647 -6.14372,-12.053502 -10.71325,-17.034022 -2.01454,-2.258137 -4.44965,-4.454134 -6.74183,-6.186681 -8.11954,-6.151837 -18.17793,-9.693431 -28.36688,-9.921977 -1.15055,-0.03785 -2.30282,-0.02598 -3.45344,0.006 " +
        "z m 1.7129,26.251953 c 8.81487,-0.01196 17.57283,5.103844 21.46176,13.089834 2.41463,4.693135 2.96226,10.088264 2.74722,15.292979 7.5e-4,54.326718 -0.007,108.654378 0.0338,162.980508 0.2845,3.10741 3.29592,5.68517 6.42284,5.40602 2.20614,0.0106 4.42041,0.0622 6.62152,-0.0604 2.78623,-0.4346 5.09022,-2.97453 5.10289,-5.82242 0.079,-2.3303 -0.0314,-4.66414 0.0182,-6.99633 0.008,-43.5918 -0.022,-87.18418 0.0413,-130.77561 0.24615,-7.42403 3.84899,-14.88824 10.12629,-19.039498 8.47767,-5.801482 20.6516,-5.320645 28.67636,1.080828 5.54189,4.3132 8.92032,11.17044 9.16707,18.16978 0.22879,4.1481 0.006,8.30454 0.0906,12.4563 0.003,41.86154 -0.007,83.72431 0.0349,125.58509 0.28432,3.10834 3.29918,5.68731 6.42748,5.40027 2.20422,0.0121 4.41612,0.0624 6.61567,-0.0584 2.81656,-0.44225 5.13552,-3.03208 5.10409,-5.91264 0.0618,-2.5665 -0.0197,-5.1357 0.0165,-7.70376 0.009,-20.47892 -0.0464,-40.95897 0.064,-61.43714 0.35239,-7.47686 4.02801,-14.95176 10.35608,-19.12848 8.61476,-5.92253 21.09513,-5.3842 29.12224,1.33733 5.63105,4.60302 8.70442,11.94125 8.53521,19.16222 0.061,13.84881 0.036,27.69824 0.0602,41.54731 0.0149,25.84974 0.0652,51.69999 0.0137,77.54943 -0.24109,11.89967 -2.20568,23.70358 -5.07957,35.23426 -8.00176,34.74149 -16.00288,69.48312 -24.00503,104.22452 -62.66407,0 -125.32813,0 -187.99219,0 -34.7862,-48.8773 -69.57287,-97.75353 -104.327057,-146.65319 -4.413266,-6.57763 -5.500853,-15.40472 -2.29606,-22.71667 3.791465,-9.16384 13.319077,-15.80052 23.310617,-15.63991 7.52608,0.0358 14.6539,4.37121 18.75123,10.58447 8.10522,11.27223 16.03713,22.67053 24.18646,33.91005 2.16749,2.4072 6.2752,2.34057 8.55277,0.11761 1.33939,-1.19133 1.95382,-3.00775 1.83503,-4.77253 0.0277,-29.75011 0.005,-59.50121 0.0124,-89.2517 0.01,-31.11222 -0.006,-62.22512 0.0404,-93.33691 0.24244,-7.4737 3.92079,-14.97772 10.27568,-19.09875 8.6186,-5.817228 20.9731,-5.156078 28.96148,1.48449 5.63796,4.59078 8.81985,11.85382 8.79244,19.08344 0.0836,4.56146 -0.0153,9.12464 0.0297,13.68687 0.003,40.7854 -0.006,81.57191 0.0325,122.35661 0.26798,3.07696 3.21285,5.66861 6.3166,5.43087 2.2367,0.0206 4.48124,0.0587 6.71334,-0.0558 2.7688,-0.42517 5.06914,-2.92532 5.11407,-5.75331 0.0972,-2.25489 -0.0329,-4.51419 0.0246,-6.7713 0.008,-53.65276 -0.0221,-107.30603 0.0367,-160.958458 0.21414,-7.410412 3.76314,-14.889006 10.01498,-19.058838 4.02522,-2.798164 8.95407,-4.189534 13.83893,-4.169035 " +
        "z";

    /// <summary>
    /// The stamp icon, as an SVG path in a 32x32 box. A drawn rubber stamp,
    /// because the nearest Segoe MDL2 glyph reads as a faint imprint device
    /// rather than a stamp. Kept beside the tool it belongs to.
    /// </summary>
    private const string StampIcon =
        "M26,16h-6v-4c0-1.1,0.9-2,2-2s2-0.9,2-2V6c0-1.1-0.9-2-2-2H10C8.9,4,8,4.9,8,6v2c0,1.1,0.9,2,2,2" +
        "s2,0.9,2,2v4H6c-1.1,0-2,0.9-2,2v10h2h20h2V18C28,16.9,27.1,16,26,16z M10,8V6h12v2c-2.206,0-4,1.794-4,4v4h-4v-4" +
        "C14,9.794,12.206,8,10,8z M6,26v-4h20v4H6z M6,20v-2h20v2H6z";

    public static ToolDefinition For(ToolMode mode) =>
        All.FirstOrDefault(t => t.Mode == mode) ?? All[0];

    /// <summary>
    /// The tools a mode offers, in rail order.
    ///
    /// ⚠️ DECLARED HERE, next to the tools themselves, and read by BOTH the
    /// rail and the keyboard. A rail that hid a tool while its shortcut still
    /// armed it would be the worst of both: the reader sees a viewer and one
    /// keystroke puts them in a drawing tool with no way to tell.
    ///
    /// View keeps only the two that change nothing: the hand pans and Select
    /// reads. Everything else places a mark, and placing a mark is editing.
    /// </summary>
    public static IReadOnlyList<ToolDefinition> ForMode(AppMode mode) =>
        mode == AppMode.Edit
            ? All
            : All.Where(t => t.Mode is ToolMode.Hand or ToolMode.Select).ToList();

    /// <summary>Whether a mode offers a tool at all.</summary>
    public static bool Offers(AppMode mode, ToolMode tool) =>
        ForMode(mode).Any(t => t.Mode == tool);

    /// <summary>
    /// The tool a key selects, or null.
    ///
    /// Lives here rather than in a switch in the key handler so that a tool
    /// and its shortcut are declared in exactly one place and cannot drift
    /// apart.
    /// </summary>
    public static ToolDefinition? ForShortcut(char key)
    {
        char upper = char.ToUpperInvariant(key);
        return All.FirstOrDefault(t => t.Shortcut == upper);
    }

    /// <summary>
    /// The tool a key selects IN THIS MODE, or null.
    ///
    /// ⚠️ The mode-aware one, and the only one the key handler may call. Its
    /// modeless sibling above would happily arm a tool the rail is not showing.
    /// </summary>
    public static ToolDefinition? ForShortcut(char key, AppMode mode)
    {
        char upper = char.ToUpperInvariant(key);
        return ForMode(mode).FirstOrDefault(t => t.Shortcut == upper);
    }
}
