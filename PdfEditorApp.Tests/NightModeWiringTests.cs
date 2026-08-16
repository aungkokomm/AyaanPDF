using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Where the night treatment is applied, and just as importantly where it is
/// not.
///
/// The transform itself is covered by NightModeTests. This covers the wiring,
/// whose failure modes are all about reach: too little and the page is dark
/// while the thumbnails glare, too much and printed pages come out inverted.
/// </summary>
public class NightModeWiringTests
{
    private static string Read(params string[] relative)
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

    private static string ViewModel() => Read("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");
    private static string PageCode() => Read("PdfEditorApp", "MainPage.xaml.cs");
    private static string PageXaml() => Read("PdfEditorApp", "MainPage.xaml");

    private static string MethodBody(string source, string signature)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{signature}' is gone; this test needs rewriting to match");

        int next = source.IndexOf("\n    private ", at + signature.Length, StringComparison.Ordinal);
        int alt = source.IndexOf("\n    public ", at + signature.Length, StringComparison.Ordinal);
        if (alt >= 0 && (next < 0 || alt < next))
        {
            next = alt;
        }

        return next > at ? source[at..next] : source[at..];
    }

    [Fact]
    public void printing_is_never_given_the_night_treatment()
    {
        // THE test in this file. Print output must look like the document, not
        // like the screen. Every page render in the app funnels through one
        // ToBitmap, so it would have been easy to catch printing by accident.
        string body = MethodBody(ViewModel(), "public WriteableBitmap? RenderPageForPrint");

        Assert.DoesNotContain("ForReading(", body, StringComparison.Ordinal);
        Assert.DoesNotContain("NightMode", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every method in the view model, paired with its body, split on
    /// four-space-indented member declarations.
    /// </summary>
    private static IEnumerable<(string Name, string Body)> Methods(string source)
    {
        var decl = new Regex(
            @"^    (?:private|public|protected|internal)[^\r\n(]*?(\w+)\s*(?:<[^>\r\n]*>)?\(",
            RegexOptions.Multiline);

        var found = decl.Matches(source);
        for (int i = 0; i < found.Count; i++)
        {
            int start = found[i].Index;
            int end = i + 1 < found.Count ? found[i + 1].Index : source.Length;
            yield return (found[i].Groups[1].Value, source[start..end]);
        }
    }

    [Fact]
    public void every_page_render_is_accounted_for()
    {
        // This replaces a test that named the two call sites it knew about and
        // passed while the page on screen stayed white.
        //
        // There are five raw render paths, not two: the base tier, the tiles,
        // the debounced sharpen pass, the lazily realized thumbnail, and print.
        // The sharpen pass is the one you actually read, so leaving it out left
        // a white page under dark tiles. Enumerating them means a sixth path
        // has to declare itself rather than be remembered.
        var exempt = new[] { "RenderPageForPrint" };
        var missing = new List<string>();

        foreach (var (name, body) in Methods(ViewModel()))
        {
            if (!Regex.IsMatch(body, @"PageRenderer\.Render\w*Raw\("))
            {
                continue;
            }

            if (exempt.Contains(name))
            {
                Assert.DoesNotContain("ForReading(", body, StringComparison.Ordinal);
                continue;
            }

            if (!body.Contains("ForReading(", StringComparison.Ordinal))
            {
                missing.Add(name);
            }
        }

        Assert.True(
            missing.Count == 0,
            "these render raw pixels without the night treatment: " + string.Join(", ", missing));
    }

    [Fact]
    public void the_enumeration_actually_finds_the_render_paths()
    {
        // Guards the test above against its own regex quietly matching nothing,
        // which would make it pass no matter what the view model does.
        var seen = Methods(ViewModel())
            .Where(m => Regex.IsMatch(m.Body, @"PageRenderer\.Render\w*Raw\("))
            .Select(m => m.Name)
            .ToList();

        Assert.Contains("RenderBaseTier", seen);
        Assert.Contains("RenderTile", seen);
        Assert.Contains("SharpenSlot", seen);
        Assert.Contains("EnsureThumbnailRendered", seen);
        Assert.Contains("RenderPageForPrint", seen);
    }

    [Fact]
    public void thumbnails_follow_the_page()
    {
        // A strip of white panels beside a dark page is what the mode exists to
        // avoid looking at. One helper, so a fourth call site cannot forget.
        Assert.Contains(
            "ForReading(",
            MethodBody(ViewModel(), "private PageRenderResult RenderThumbnail"),
            StringComparison.Ordinal);

        Assert.DoesNotContain("PageRenderer.RenderLowRes(", ViewModel(), StringComparison.Ordinal);
    }

    [Fact]
    public void the_treatment_runs_off_the_ui_thread()
    {
        // A per-pixel pass over a 3-megapixel render belongs in the raw phase,
        // which is already on a background thread, not in the bitmap phase,
        // which is documented as UI-thread only.
        string vm = ViewModel();

        Assert.Contains("ForReading(await Task.Run(", vm, StringComparison.Ordinal);
    }

    [Fact]
    public void toggling_throws_away_every_cached_bitmap()
    {
        // The transform is not its own inverse, so a cached bitmap cannot be
        // converted back or forward. It has to be re-rendered from source.
        string body = MethodBody(ViewModel(), "partial void OnIsNightModeChanged");

        Assert.Contains("ClearTiles()", body, StringComparison.Ordinal);
        Assert.Contains("ReleaseBitmap()", body, StringComparison.Ordinal);
        Assert.Contains("RefreshThumbnailsForNightMode()", body, StringComparison.Ordinal);
    }

    [Fact]
    public void toggling_re_renders_the_window_not_every_page()
    {
        // The bug this exists to prevent, which shipped once and was reported
        // as two separate faults.
        //
        // Walking every slot and calling RenderBaseTier queued hundreds of
        // renders on a 3,352-page book. The page actually on screen sat behind
        // all of them showing white, and the sharpen pass was starved so what
        // did appear stayed at low resolution. Measured: 938 base renders and
        // only 12 sharpen passes across two toggles.
        string body = MethodBody(ViewModel(), "partial void OnIsNightModeChanged");

        Assert.Contains("RepaintVisibleWindow()", body, StringComparison.Ordinal);
        Assert.DoesNotContain("RenderBaseTier(", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_repaint_goes_through_the_render_budget()
    {
        // Not a bespoke loop. UpdateVisibleWindow is what knows which pages are
        // worth rendering and which to release, and it is already tested.
        Assert.Contains(
            "UpdateVisibleWindow(",
            MethodBody(ViewModel(), "private void RepaintVisibleWindow"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void the_choice_is_remembered()
    {
        string body = MethodBody(PageCode(), "private void NightModeToggle_Click");

        // Three controls can set this now, so the handler works out what was
        // meant ONCE and then uses that one value everywhere. Reading a
        // control's state again further down is how the two halves of a
        // setting come to disagree.
        Assert.Contains("ViewModel.IsNightMode = on;", body, StringComparison.Ordinal);
        Assert.Contains("ApplyPageSheet(on)", body, StringComparison.Ordinal);
        Assert.Contains("NightMode = on", body, StringComparison.Ordinal);
    }

    [Fact]
    public void no_card_is_left_on_a_hardcoded_white_sheet()
    {
        // The page bitmap does not cover its card at all times. A slot that has
        // released its bitmap draws the card bare, and a toggle puts every slot
        // in that state at once, which is how a white page appeared under a
        // dark theme. Both the page card and the thumbnail card have to follow.
        string xaml = PageXaml();

        Assert.Equal(2, Regex.Matches(xaml, @"Background=""\{StaticResource PageSheetBrush\}""").Count);
        Assert.DoesNotContain(@"Background=""White""", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void the_sheet_is_repainted_from_both_places_the_mode_can_change()
    {
        // Clicking the menu item and restoring the saved setting are separate
        // paths, and a sheet repainted from only one of them would come back
        // white on the next launch.
        Assert.Contains("ApplyPageSheet(", MethodBody(PageCode(), "private void NightModeToggle_Click"), StringComparison.Ordinal);
        Assert.Contains("ApplyPageSheet(", MethodBody(PageCode(), "private void ApplySettings"), StringComparison.Ordinal);

        // Mutating the resolved brush, not swapping the dictionary entry, which
        // a template that has already expanded would never see.
        Assert.Contains("sheet.Color =", MethodBody(PageCode(), "private void ApplyPageSheet"), StringComparison.Ordinal);
    }

    [Fact]
    public void restoring_the_mode_does_not_depend_on_the_rulers_setting()
    {
        // It shipped nested inside the rulers check, so a saved dark session
        // came back light unless the rulers setting happened to differ too.
        var rulers = Regex.Match(
            MethodBody(PageCode(), "private void ApplySettings"),
            @"if \(RulersToggle\.IsChecked != s\.ShowRulers\)\s*\{(.*?)\}",
            RegexOptions.Singleline);

        Assert.True(rulers.Success, "the rulers block moved; this test needs rewriting to match");
        Assert.DoesNotContain("NightMode", rulers.Groups[1].Value, StringComparison.Ordinal);
    }

    [Fact]
    public void the_saved_choice_is_restored_and_the_menu_agrees()
    {
        // Restoring the render without ticking the menu would leave the app
        // dark with an unchecked box, and the next click would appear to do
        // nothing.
        string body = MethodBody(PageCode(), "private void ApplySettings");

        Assert.Contains("NightModeToggle.IsChecked = s.NightMode", body, StringComparison.Ordinal);
        Assert.Contains("ViewModel.IsNightMode = s.NightMode", body, StringComparison.Ordinal);
    }
}
