using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Which addresses out of a PDF may reach the operating system.
///
/// ⚠️ THE SECURITY BOUNDARY OF THIS FEATURE. A link's address is written by
/// whoever made the file, not by the user, and it can say anything. These are
/// the cases that must never be launchable, and they are checked here rather
/// than by clicking links in strange documents.
/// </summary>
public class LinkTargetTests
{
    [Theory]
    [InlineData("https://example.com/path?q=1")]
    [InlineData("http://example.com")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("HTTPS://EXAMPLE.COM")]
    public void the_addresses_a_reader_actually_needs_can_be_opened(string uri)
    {
        Assert.True(LinkTarget.CanOpen(uri));
        Assert.Equal(string.Empty, LinkTarget.RefusalReason(uri));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("ms-settings:windowsupdate")]
    [InlineData("ftp://example.com/thing")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("not a url at all")]
    [InlineData("")]
    [InlineData(null)]
    public void everything_else_is_refused(string? uri)
    {
        // A PDF that says "click here" and points at a local executable is the
        // whole reason this list is short. Anything not plainly web or mail is
        // shown to the reader and left unopened.
        Assert.False(LinkTarget.CanOpen(uri));
        Assert.NotEqual(string.Empty, LinkTarget.RefusalReason(uri));
    }

    [Fact]
    public void a_bare_host_becomes_https_the_way_an_address_box_does()
    {
        Assert.Equal("https://example.com", LinkTarget.Normalize("example.com"));
        Assert.Equal("https://example.com/a/b", LinkTarget.Normalize("  example.com/a/b  "));
        Assert.Equal("https://example.com:8080/x", LinkTarget.Normalize("example.com:8080/x"));
    }

    [Fact]
    public void an_address_that_already_has_a_scheme_keeps_it()
    {
        Assert.Equal("http://example.com", LinkTarget.Normalize("http://example.com"));
        Assert.Equal("mailto:a@b.com", LinkTarget.Normalize("mailto:a@b.com"));
    }

    [Fact]
    public void the_app_cannot_write_a_link_it_would_afterwards_refuse_to_open()
    {
        // Normalize is held to the SAME rule as CanOpen, so there is no way to
        // create a link that the follow path then declines. A one-way door like
        // that would look like the app having lost the link.
        Assert.Null(LinkTarget.Normalize("javascript:alert(1)"));
        Assert.Null(LinkTarget.Normalize("file:///C:/Windows"));
        Assert.Null(LinkTarget.Normalize("   "));
        Assert.Null(LinkTarget.Normalize(null));
    }
}
