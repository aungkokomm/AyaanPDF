using System;
using System.IO;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The licences of what ships travel with it: PDFium and the libraries built
/// into it, the Rust libraries in render_core.dll, the .NET libraries and the
/// .NET runtime, in THIRD-PARTY-NOTICES.txt beside the exe, opened from About.
/// </summary>
public class ThirdPartyNoticesTests
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
        return File.ReadAllText(Path.Combine(dir!.FullName, path)).Replace("\r\n", "\n");
    }

    [Theory]
    // PDFium, its own licence and the libraries compiled into it.
    [InlineData("PDFium\n")]
    [InlineData("freetype (in PDFium)")]
    [InlineData("libjpeg_turbo (in PDFium)")]
    [InlineData("zlib (in PDFium)")]
    [InlineData("icu (in PDFium)")]
    [InlineData("pdfium-binaries build scripts, by Benoit Blanchon")]
    // What render_core.dll is built from.
    [InlineData("pdfium-render 0.8.37")]
    [InlineData("rustybuzz 0.20.1")]
    [InlineData("lopdf 0.44.0")]
    // The .NET side.
    [InlineData("SkiaSharp 3.119.1")]
    [InlineData("Microsoft.WindowsAppSDK.WinUI 2.3.0")]
    [InlineData("CommunityToolkit.Mvvm 8.4.2")]
    [InlineData("Tesseract 5.2.0")]
    [InlineData("PdfPig 0.1.16")]
    [InlineData(".NET runtime 10.0.")]
    public void the_notices_cover_each_part_that_ships(string part)
    {
        Assert.Contains(part, Read("PdfEditorApp", "THIRD-PARTY-NOTICES.txt"), StringComparison.Ordinal);
    }

    [Fact]
    public void the_notices_ship_beside_the_exe_and_about_opens_them()
    {
        Assert.Contains("<Content Include=\"THIRD-PARTY-NOTICES.txt\">", Read("PdfEditorApp", "PdfEditorApp.csproj"), StringComparison.Ordinal);

        string page = Read("PdfEditorApp", "MainPage.xaml.cs");
        Assert.Contains("Content = \"Third-party notices\"", page, StringComparison.Ordinal);
        Assert.Contains("Path.Combine(AppContext.BaseDirectory, \"THIRD-PARTY-NOTICES.txt\")", page, StringComparison.Ordinal);
        // The classic Notepad by full path; the Store one fails beside this app.
        Assert.Contains("Environment.GetFolderPath(Environment.SpecialFolder.System), \"notepad.exe\")", page, StringComparison.Ordinal);

        // And the other notices it points to are real.
        string notices = Read("PdfEditorApp", "THIRD-PARTY-NOTICES.txt");
        Assert.Contains("THIRD-PARTY-NOTICES-OCR.txt", notices, StringComparison.Ordinal);
        Read("PdfEditorApp", "Assets", "Ocr", "THIRD-PARTY-NOTICES-OCR.txt");
        Read("PdfEditorApp", "Assets", "Fonts", "THIRD-PARTY-NOTICES.txt");
    }

    [Fact]
    public void pdfiums_licences_are_kept_with_the_vendored_dll()
    {
        // From the pdfium-binaries chromium/7961 release, whose pdfium.dll is
        // byte for byte the vendored one.
        Assert.Contains("Copyright 2014 The PDFium Authors", Read("render_core", "vendor", "pdfium", "licenses", "pdfium.txt"), StringComparison.Ordinal);
        Read("render_core", "vendor", "pdfium", "licenses", "freetype.txt");
    }
}
