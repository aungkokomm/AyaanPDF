using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The settings file is the user's and lives on disk between versions, so what
/// matters is that reading it can never leave the app in a state no menu can
/// represent.
/// </summary>
public class AppSettingsTests
{
    [Fact]
    public void the_defaults_are_the_safe_answers()
    {
        var s = new AppSettings();

        Assert.Equal(AppTheme.System, s.Theme);
        // Fit page shows the shape of a document before anything is read, and
        // is safe whatever size the pages turn out to be.
        Assert.Equal(DefaultView.FitPage, s.DefaultView);
        Assert.True(s.ShowRulers);
        Assert.Equal("Inches", s.RulerUnit);
    }

    [Fact]
    public void a_theme_from_a_newer_version_falls_back_rather_than_breaking()
    {
        // A file written by a later build can name a theme this one has never
        // heard of. Casting an unknown number into the enum is legal in C# and
        // would leave the app painted by nothing.
        var s = new AppSettings { Theme = (AppTheme)99 }.Sanitised();
        Assert.Equal(AppTheme.System, s.Theme);
    }

    [Fact]
    public void an_unknown_default_view_falls_back()
    {
        var s = new AppSettings { DefaultView = (DefaultView)42 }.Sanitised();
        Assert.Equal(DefaultView.FitPage, s.DefaultView);
    }

    [Theory]
    [InlineData("Furlongs")]
    [InlineData("")]
    [InlineData("inches")]   // the menu tags are case-sensitive
    public void a_ruler_unit_the_menu_cannot_show_falls_back(string unit)
    {
        Assert.Equal("Inches", new AppSettings { RulerUnit = unit }.Sanitised().RulerUnit);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(5000, 50)]
    [InlineData(10, 10)]
    public void the_recent_limit_is_clamped_to_something_usable(int given, int expected)
    {
        Assert.Equal(expected, new AppSettings { RecentLimit = given }.Sanitised().RecentLimit);
    }

    [Fact]
    public void sanitising_leaves_a_good_file_untouched()
    {
        var s = new AppSettings
        {
            Theme = AppTheme.Sepia,
            DefaultView = DefaultView.FitWidth,
            ShowRulers = false,
            RulerUnit = "Millimeters",
            RecentLimit = 20,
        };

        Assert.Equal(s, s.Sanitised());
    }

    [Theory]
    [InlineData(AppTheme.Dark, true)]
    [InlineData(AppTheme.DarkBlue, true)]
    [InlineData(AppTheme.Light, false)]
    [InlineData(AppTheme.Sepia, false)]
    public void the_tinted_themes_know_which_base_they_sit_on(AppTheme theme, bool dark)
    {
        // Sepia is a LIGHT theme with warm paper and Dark blue is a DARK theme
        // with a blue cast. Getting this backwards paints light text on light.
        Assert.Equal(dark, AppSettings.IsDark(theme));
    }
}
