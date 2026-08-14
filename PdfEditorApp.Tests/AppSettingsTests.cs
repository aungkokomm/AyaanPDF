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

        // Find behaves the way it always has until someone says otherwise.
        Assert.False(s.SearchMatchCase);
        Assert.False(s.SearchWholeWord);
    }

    // ---------------- Remembering how someone searches ----------------

    /// <summary>Serialised and read back exactly as SettingsStore does it, so
    /// this exercises the real round trip rather than a stand-in.</summary>
    private static AppSettings RoundTrip(AppSettings settings) =>
        System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
            System.Text.Json.JsonSerializer.Serialize(settings))!;

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void the_search_options_survive_being_written_and_read_back(bool matchCase, bool wholeWord)
    {
        // The whole point of storing them: they have to still be set the next
        // time the app starts.
        var read = RoundTrip(new AppSettings
        {
            SearchMatchCase = matchCase,
            SearchWholeWord = wholeWord,
        });

        Assert.Equal(matchCase, read.SearchMatchCase);
        Assert.Equal(wholeWord, read.SearchWholeWord);
    }

    [Fact]
    public void sanitising_leaves_the_search_options_alone()
    {
        // Sanitised() runs on every load. A property it forgets about comes
        // back as its default, which would look exactly like the setting never
        // being saved at all.
        var s = new AppSettings { SearchMatchCase = true, SearchWholeWord = true }.Sanitised();

        Assert.True(s.SearchMatchCase);
        Assert.True(s.SearchWholeWord);
    }

    [Fact]
    public void a_settings_file_written_before_these_options_existed_still_loads()
    {
        // Everyone already running the app has one of these on disk. Enums are
        // written as NUMBERS: System.Text.Json's default converter, which is
        // what SettingsStore uses, so 2 is Dark. Writing "Dark" here would
        // throw, which is the shape of a real settings.json, not a detail of
        // this test.
        var read = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
            """{"Theme":2,"ShowRulers":false,"RulerUnit":"Points"}""")!.Sanitised();

        Assert.Equal(AppTheme.Dark, read.Theme);
        Assert.False(read.ShowRulers);
        Assert.Equal("Points", read.RulerUnit);

        // The point of the test: the keys that were not there yet read as off.
        Assert.False(read.SearchMatchCase);
        Assert.False(read.SearchWholeWord);
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
    [InlineData(AppTheme.MidnightBlue, true)]
    [InlineData(AppTheme.Light, false)]
    [InlineData(AppTheme.Sepia, false)]
    public void the_tinted_themes_know_which_base_they_sit_on(AppTheme theme, bool dark)
    {
        // Sepia is a LIGHT theme with warm paper and Dark blue is a DARK theme
        // with a blue cast. Getting this backwards paints light text on light.
        Assert.Equal(dark, AppSettings.IsDark(theme));
    }
}
