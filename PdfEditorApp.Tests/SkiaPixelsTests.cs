using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using SkiaSharp;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The tests read a bitmap's pixels through <see cref="SkiaPixels"/>, and only
/// through it.
/// </summary>
public class SkiaPixelsTests
{
    [Fact]
    public void the_pixels_outlive_the_bitmap()
    {
        byte[] pixels = SkiaPixels.Of(Painted());

        // Whatever Skia held for that bitmap is finalized and freed here.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.Equal(16 * 16 * 4, pixels.Length);
        Assert.All(pixels.Chunk(4), p => Assert.Equal(new byte[] { 0x30, 0x20, 0x10, 0xFF }, p));
    }

    [Fact]
    public void no_test_reads_a_bitmap_s_pixels_in_place()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PdfEditorApp.Tests", "SkiaPixels.cs")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);

        var offenders = Directory
            .EnumerateFiles(Path.Combine(dir!.FullName, "PdfEditorApp.Tests"), "*.cs")
            .Where(f => Path.GetFileName(f) is not ("SkiaPixels.cs" or "SkiaPixelsTests.cs"))
            .Where(f => File.ReadAllText(f).Contains(".GetPixelSpan()", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0,
            "read the pixels with SkiaPixels.Of(bitmap), which keeps the bitmap alive: " +
            string.Join(", ", offenders));
    }

    /// <summary>A bitmap nothing refers to once this returns.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static SKBitmap Painted()
    {
        var bitmap = new SKBitmap(new SKImageInfo(16, 16, SKColorType.Bgra8888, SKAlphaType.Premul));
        bitmap.Erase(new SKColor(0x10, 0x20, 0x30));
        return bitmap;
    }
}
