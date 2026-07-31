using System.IO;
using System.Linq;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The font catalog has two halves: pure variant-resolution logic (fully
/// deterministic) and a binary sfnt parser (checked against real Windows fonts
/// when present). A wrong offset in the parser shows up as a garbled family
/// name or a missed bold flag, which these catch.
/// </summary>
public class FontCatalogTests
{
    [Fact]
    public void resolve_prefers_the_exact_variant_then_falls_back_sensibly()
    {
        var f = new FontFamily("Test")
        {
            RegularPath = "reg",
            BoldPath = "bold",
            ItalicPath = "ital",
            BoldItalicPath = "bi",
        };

        Assert.Equal("reg", f.ResolvePath(false, false));
        Assert.Equal("bold", f.ResolvePath(true, false));
        Assert.Equal("ital", f.ResolvePath(false, true));
        Assert.Equal("bi", f.ResolvePath(true, true));
    }

    [Fact]
    public void resolve_falls_back_when_a_variant_is_missing()
    {
        var onlyRegular = new FontFamily("Reg") { RegularPath = "reg" };
        Assert.Equal("reg", onlyRegular.ResolvePath(true, true)); // no bold-italic -> regular

        var noRegular = new FontFamily("NoReg") { BoldPath = "bold" };
        Assert.Equal("bold", noRegular.ResolvePath(false, false)); // regular missing -> any

        var empty = new FontFamily("Empty");
        Assert.Null(empty.ResolvePath(false, false));
    }

    [Theory]
    [InlineData("arial.ttf", "Arial", false, false)]
    [InlineData("arialbd.ttf", "Arial", true, false)]
    [InlineData("ariali.ttf", "Arial", false, true)]
    [InlineData("times.ttf", "Times New Roman", false, false)]
    public void parses_family_and_style_from_a_real_windows_font(
        string file, string expectedFamily, bool expectedBold, bool expectedItalic)
    {
        string path = Path.Combine(WindowsFontsDir(), file);
        if (!File.Exists(path))
        {
            return; // this machine does not ship that font: nothing to check
        }

        using var stream = File.OpenRead(path);
        Assert.True(FontCatalog.TryParseFace(stream, out var face), $"failed to parse {file}");
        Assert.Equal(expectedFamily, face.Family);
        Assert.Equal(expectedBold, face.Bold);
        Assert.Equal(expectedItalic, face.Italic);
    }

    [Fact]
    public void garbage_data_is_rejected_rather_than_throwing()
    {
        using var stream = new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        Assert.False(FontCatalog.TryParseFace(stream, out _));
    }

    [Fact]
    public void enumerate_groups_arial_variants_into_one_family()
    {
        string dir = WindowsFontsDir();
        var files = new[] { "arial.ttf", "arialbd.ttf", "ariali.ttf" }
            .Select(f => Path.Combine(dir, f))
            .Where(File.Exists)
            .ToList();
        if (files.Count == 0)
        {
            return; // no Arial on this machine
        }

        var families = FontCatalog.Enumerate(files);
        var arial = families.SingleOrDefault(f => f.Name == "Arial");
        Assert.NotNull(arial);
        Assert.NotNull(arial!.RegularPath);
        if (files.Any(f => f.EndsWith("arialbd.ttf")))
        {
            Assert.NotNull(arial.BoldPath);
        }
    }

    private static string WindowsFontsDir()
    {
        string? windir = System.Environment.GetEnvironmentVariable("WINDIR");
        return Path.Combine(windir ?? "C:\\Windows", "Fonts");
    }
}
