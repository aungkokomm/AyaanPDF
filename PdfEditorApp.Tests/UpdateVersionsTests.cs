using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The update bubble is only as good as its answer to "is that release newer
/// than this one". A wrong yes nags the reader about their own version; a wrong
/// no hides a real update.
/// </summary>
public class UpdateVersionsTests
{
    [Theory]
    [InlineData("v3.51.5", "3.51.5")]
    [InlineData("V3.52.0", "3.52.0")]
    [InlineData("3.51.5", "3.51.5")]
    public void a_release_tag_reads_as_its_version(string tag, string version)
    {
        Assert.Equal(version, UpdateVersions.Normalize(tag));
    }

    [Theory]
    [InlineData("3.51.5", "3.51.4", true)]
    [InlineData("3.52.0", "3.51.9", true)]
    [InlineData("4.0", "3.99.99", true)]
    [InlineData("3.51.10", "3.51.9", true)]    // compared as numbers, not as text
    [InlineData("3.51.4", "3.51.4", false)]
    [InlineData("3.51", "3.51.0", false)]      // the same release written shorter
    [InlineData("3.51.3", "3.51.4", false)]    // never offer an older build
    public void only_a_later_release_is_newer(string latest, string current, bool newer)
    {
        Assert.Equal(newer, UpdateVersions.IsNewer(latest, current));
    }

    [Theory]
    [InlineData("nightly", "3.51.4")]
    [InlineData("3.52.0", "unknown")]
    [InlineData("", "3.51.4")]
    public void a_version_that_cannot_be_read_is_never_newer(string latest, string current)
    {
        Assert.False(UpdateVersions.IsNewer(latest, current));
    }

    [Fact]
    public void no_version_is_skipped_until_the_reader_skips_one()
    {
        Assert.Equal("", new AppSettings().SkippedUpdateVersion);

        var read = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
            System.Text.Json.JsonSerializer.Serialize(new AppSettings { SkippedUpdateVersion = "3.52.0" }))!;
        Assert.Equal("3.52.0", read.SkippedUpdateVersion);
    }
}
