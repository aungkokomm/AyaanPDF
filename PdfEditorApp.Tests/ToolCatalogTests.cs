using System;
using System.Linq;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

public class ToolCatalogTests
{
    [Fact]
    public void every_tool_mode_appears_in_the_rail()
    {
        // A tool that exists in the enum but not the catalog is unreachable:
        // the rail is built from the catalog, so it would have no button.
        foreach (ToolMode mode in Enum.GetValues<ToolMode>())
        {
            Assert.Contains(ToolCatalog.All, t => t.Mode == mode);
        }
    }

    [Fact]
    public void no_tool_appears_twice()
    {
        Assert.Equal(ToolCatalog.All.Count, ToolCatalog.All.Select(t => t.Mode).Distinct().Count());
    }

    [Fact]
    public void every_shortcut_is_unique()
    {
        // Two tools on one key means one of them can never be reached by
        // keyboard, and which one wins depends on list order.
        var keys = ToolCatalog.All.Select(t => t.Shortcut).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void shortcuts_are_uppercase_so_lookup_is_case_insensitive()
    {
        Assert.All(ToolCatalog.All, t => Assert.Equal(char.ToUpperInvariant(t.Shortcut), t.Shortcut));

        // And a lowercase press still finds the tool.
        Assert.Equal(ToolMode.Draw, ToolCatalog.ForShortcut('d')!.Mode);
        Assert.Equal(ToolMode.Draw, ToolCatalog.ForShortcut('D')!.Mode);
    }

    [Fact]
    public void an_unbound_key_selects_nothing()
    {
        Assert.Null(ToolCatalog.ForShortcut('Z'));
        Assert.Null(ToolCatalog.ForShortcut('1'));
    }

    [Fact]
    public void every_tool_has_a_name_and_a_glyph()
    {
        Assert.All(ToolCatalog.All, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Name), $"{t.Mode} has no name");
            Assert.False(string.IsNullOrWhiteSpace(t.Glyph), $"{t.Mode} has no glyph");
        });
    }

    [Fact]
    public void the_tooltip_shows_the_shortcut()
    {
        // The shortcuts are only useful if they can be discovered, and the
        // tooltip is the only place they appear.
        var draw = ToolCatalog.For(ToolMode.Draw);
        Assert.Contains("D", draw.Tooltip);
        Assert.Contains("Draw", draw.Tooltip);
    }

    [Fact]
    public void tools_declare_the_options_they_need()
    {
        // The property bar is built from these, so a wrong flag shows the
        // wrong controls, or none.
        Assert.True(ToolCatalog.For(ToolMode.Draw).Offers(ToolOptions.Color));
        Assert.True(ToolCatalog.For(ToolMode.Draw).Offers(ToolOptions.Width));

        // A highlighter's thickness comes from the text it covers, so it has
        // no width of its own to offer.
        Assert.True(ToolCatalog.For(ToolMode.Highlight).Offers(ToolOptions.Color));
        Assert.False(ToolCatalog.For(ToolMode.Highlight).Offers(ToolOptions.Width));

        Assert.True(ToolCatalog.For(ToolMode.Stamp).Offers(ToolOptions.Stamp));
        Assert.False(ToolCatalog.For(ToolMode.Stamp).Offers(ToolOptions.Color));

        // Navigation and selection put nothing on the page.
        Assert.Equal(ToolOptions.None, ToolCatalog.For(ToolMode.Hand).Options);
        Assert.Equal(ToolOptions.None, ToolCatalog.For(ToolMode.Select).Options);
    }

    [Fact]
    public void an_unknown_mode_falls_back_rather_than_throwing()
    {
        // For is called while binding, where an exception would take the whole
        // rail down rather than show one wrong icon.
        Assert.NotNull(ToolCatalog.For((ToolMode)999));
    }
}
