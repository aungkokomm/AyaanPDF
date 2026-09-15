using System;

namespace PdfEditorApp.Viewport;

/// <summary>What size of page a picture gets in a merged file.</summary>
public enum ImagePageSize
{
    /// <summary>The picture's own size, from its pixels and resolution.</summary>
    OwnSize,
    A4,
    Letter,
}

/// <summary>The page, in points, that a picture of a given size goes on.</summary>
public static class ImagePageSizing
{
    /// <summary>The largest page a PDF can hold: 200 inches.</summary>
    public const float LargestPage = 14400f;

    private const double ScreenDpi = 96;

    /// <summary>
    /// A paper size is turned to match the picture: a wide picture gets a
    /// landscape page, so it is not shrunk to a strip across a portrait one.
    /// Its own size uses the resolution the file records, or 96 dots an inch
    /// when it records none, and is scaled down to fit the largest page.
    /// </summary>
    public static (float Width, float Height) For(int pixelWidth, int pixelHeight, double dpiX, double dpiY, ImagePageSize size)
    {
        switch (size)
        {
            case ImagePageSize.A4:
                return Turned(595.28f, 841.89f, pixelWidth, pixelHeight);
            case ImagePageSize.Letter:
                return Turned(612f, 792f, pixelWidth, pixelHeight);
            default:
                float width = (float)(pixelWidth * 72 / (dpiX > 1 ? dpiX : ScreenDpi));
                float height = (float)(pixelHeight * 72 / (dpiY > 1 ? dpiY : ScreenDpi));
                float shrink = Math.Min(1f, LargestPage / Math.Max(width, height));
                return (Math.Max(1f, width * shrink), Math.Max(1f, height * shrink));
        }
    }

    private static (float Width, float Height) Turned(float shortSide, float longSide, int pixelWidth, int pixelHeight) =>
        pixelWidth > pixelHeight ? (longSide, shortSide) : (shortSide, longSide);
}
