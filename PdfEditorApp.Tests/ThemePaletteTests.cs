using System;
using System.Linq;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The rules the palette has to obey. Both come from bugs the user reported:
/// switching to Light left Sepia's warm chrome on the tool rail, and Dark blue
/// put a canvas brighter than its own panels behind the pages.
/// </summary>
public class ThemePaletteTests
{
    private static AppTheme[] AllThemes => Enum.GetValues<AppTheme>();

    [Fact]
    public void every_theme_states_all_three_surfaces()
    {
        // The point is that there is no hole to fall through. A theme that
        // returned "nothing" for a surface is how the previous theme's paint
        // survived a theme change: nothing was ever assigned to take it off.
        foreach (var theme in AllThemes)
        {
            foreach (var systemIsDark in new[] { false, true })
            {
                var s = ThemePalette.For(theme, systemIsDark);
                Assert.NotEqual(default, s.Canvas);
                Assert.NotEqual(default, s.Chrome);
                Assert.NotEqual(default, s.Window);
            }
        }
    }

    [Fact]
    public void the_canvas_is_never_lighter_than_its_own_chrome()
    {
        // A canvas brighter than the panels around it reads as a hole cut in
        // the app rather than a surface behind it. The page is meant to be the
        // brightest thing on screen.
        //
        // Checked at every position of the intensity slider, not just at 0: a
        // setting the user can drag is a setting that can break an invariant,
        // and the whole reason the shift is applied equally to all three
        // surfaces is so that it cannot.
        foreach (var theme in AllThemes)
        {
            foreach (var systemIsDark in new[] { false, true })
            {
                for (int i = AppSettings.MinIntensity; i <= AppSettings.MaxIntensity; i++)
                {
                    var s = ThemePalette.For(theme, systemIsDark, i);
                    Assert.True(
                        s.Canvas.Luminance <= s.Chrome.Luminance,
                        $"{theme} (systemIsDark={systemIsDark}, intensity={i}): canvas "
                        + $"{s.Canvas} is lighter than chrome {s.Chrome}, which reads as a "
                        + "hole in the app");
                }
            }
        }
    }

    [Fact]
    public void intensity_zero_is_the_theme_untouched()
    {
        // The default has to be a true no-op, not "close enough": anything else
        // means the theme on screen is never the theme as designed.
        foreach (var theme in AllThemes)
        {
            Assert.Equal(
                ThemePalette.For(theme, systemIsDark: false),
                ThemePalette.For(theme, systemIsDark: false, intensity: 0));
        }
    }

    [Fact]
    public void positive_intensity_lightens_and_negative_darkens()
    {
        foreach (var theme in AllThemes)
        {
            var mid = ThemePalette.For(theme, false, 0);
            var up = ThemePalette.For(theme, false, AppSettings.MaxIntensity);
            var down = ThemePalette.For(theme, false, AppSettings.MinIntensity);

            foreach (var (a, b, c) in new[]
            {
                (down.Canvas, mid.Canvas, up.Canvas),
                (down.Chrome, mid.Chrome, up.Chrome),
                (down.Window, mid.Window, up.Window),
            })
            {
                Assert.True(
                    a.Luminance < b.Luminance && b.Luminance < c.Luminance,
                    $"{theme}: {a} / {b} / {c} are not in increasing order of lightness");
            }
        }
    }

    [Fact]
    public void the_slider_cannot_be_dragged_out_of_range()
    {
        Assert.Equal(
            AppSettings.MaxIntensity,
            new AppSettings { ColorIntensity = 9999 }.Sanitised().ColorIntensity);
        Assert.Equal(
            AppSettings.MinIntensity,
            new AppSettings { ColorIntensity = -9999 }.Sanitised().ColorIntensity);
        Assert.Equal(0, new AppSettings().Sanitised().ColorIntensity);
    }

    [Fact]
    public void the_title_strip_is_always_deeper_than_the_chrome_on_it()
    {
        // What makes the tab in front readable. The tab is painted with the
        // CHROME colour and sits on the strip, so if the strip were ever the
        // lighter of the two the front tab would read as a dent rather than as
        // a raised surface.
        //
        // This holds for the light themes as well, which is why the tab must
        // NOT follow the canvas: the canvas is deliberately dark even in Light,
        // so a tab painted with it came out near-black in a pale title bar.
        foreach (var theme in AllThemes)
        {
            foreach (var systemIsDark in new[] { false, true })
            {
                for (int i = AppSettings.MinIntensity; i <= AppSettings.MaxIntensity; i++)
                {
                    var s = ThemePalette.For(theme, systemIsDark, i);
                    Assert.True(
                        s.Window.Luminance < s.Chrome.Luminance,
                        $"{theme} (systemIsDark={systemIsDark}, intensity={i}): title strip "
                        + $"{s.Window} is not deeper than the chrome {s.Chrome} sitting on it");
                }
            }
        }
    }

    [Fact]
    public void the_canvas_is_dark_in_every_theme_including_the_light_ones()
    {
        // The invariant the welcome screen's text colours depend on, and it was
        // not written down anywhere. A white page only reads as a sheet against
        // something darker than itself, so even Light and Sepia keep a dark
        // surround. Anything drawn straight onto the canvas can therefore use
        // fixed LIGHT text in all five themes.
        //
        // If this ever stops being true, the OnCanvas brushes in MainPage.xaml
        // become unreadable and this test is the warning.
        foreach (var theme in AllThemes)
        {
            foreach (var systemIsDark in new[] { false, true })
            {
                var canvas = ThemePalette.For(theme, systemIsDark).Canvas;

                Assert.True(
                    canvas.Luminance < 96,
                    $"{theme} (systemIsDark={systemIsDark}): canvas {canvas} is too light "
                    + "for the fixed light text drawn on it");
            }
        }
    }

    [Fact]
    public void midnight_blue_is_a_midnight_rather_than_an_electric_blue()
    {
        // #0F1A3C. Was #060866, an electric indigo: nearly no red or green
        // against a lot of blue, so a full window of it glowed instead of
        // receding behind the page. Asserted so that tuning the derived
        // surfaces later cannot quietly move the base.
        var s = ThemePalette.For(AppTheme.MidnightBlue, systemIsDark: false);
        Assert.Equal(new ThemeColor(0x0F, 0x1A, 0x3C), s.Canvas);
    }

    [Fact]
    public void midnight_blue_is_dark_enough_to_sit_a_white_page_on()
    {
        // The canvas exists to give a white page an edge. A base that drifted
        // light would take that away, and the page would stop reading as a
        // sheet.
        var canvas = ThemePalette.For(AppTheme.MidnightBlue, systemIsDark: false).Canvas;

        Assert.True(canvas.R + canvas.G + canvas.B < 0x50 * 3, "the canvas has drifted too light for a white page");
    }

    [Fact]
    public void dark_blue_stays_blue_all_the_way_through()
    {
        // Deriving from one base is what keeps the three surfaces a family. If
        // any of them stopped being dominated by blue, the theme would have
        // drifted into grey, which is the complaint that started this.
        var s = ThemePalette.For(AppTheme.MidnightBlue, systemIsDark: false);
        foreach (var c in new[] { s.Canvas, s.Chrome, s.Window })
        {
            Assert.True(c.B > c.R && c.B > c.G, $"{c} is not a blue");
        }
    }

    [Fact]
    public void only_system_depends_on_what_windows_says()
    {
        // Every explicit choice must give the same answer whatever Windows is
        // set to. A theme that drifted with the OS setting would make "Light"
        // mean two different things.
        foreach (var theme in AllThemes.Where(t => t != AppTheme.System))
        {
            Assert.Equal(
                ThemePalette.For(theme, systemIsDark: false),
                ThemePalette.For(theme, systemIsDark: true));
        }

        Assert.NotEqual(
            ThemePalette.For(AppTheme.System, systemIsDark: false),
            ThemePalette.For(AppTheme.System, systemIsDark: true));
    }

    [Fact]
    public void system_resolves_to_the_same_surfaces_as_the_explicit_theme()
    {
        Assert.Equal(
            ThemePalette.For(AppTheme.Light, false),
            ThemePalette.For(AppTheme.System, systemIsDark: false));
        Assert.Equal(
            ThemePalette.For(AppTheme.Dark, false),
            ThemePalette.For(AppTheme.System, systemIsDark: true));
    }
}
