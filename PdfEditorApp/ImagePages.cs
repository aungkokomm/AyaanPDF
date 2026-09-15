using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace PdfEditorApp;

/// <summary>What Merge files needs to know about a picture before merging it.</summary>
/// <param name="PageCount">One page per frame of a TIFF; one for anything else.</param>
/// <param name="IsUprightJpeg">
/// A JPEG that needs no turning, whose own bytes can go into the PDF as they
/// are. Anything else is decoded to pixels first.
/// </param>
internal sealed record ImageInfo(int PageCount, int PixelWidth, int PixelHeight, double DpiX, double DpiY, bool IsUprightJpeg);

/// <summary>One frame of a picture as straight-alpha BGRA pixels, the right way up.</summary>
internal sealed record DecodedImage(byte[] Pixels, int Width, int Height, double DpiX, double DpiY);

/// <summary>
/// Reads pictures for Merge files with Windows' own imaging codecs, so JPEG,
/// PNG, BMP, TIFF and GIF all come through one decoder.
/// </summary>
internal static class ImagePages
{
    public static readonly string[] Extensions = { ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff", ".gif" };

    public static bool IsImage(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>The picture's size and page count, without decoding its pixels. Null when it can't be read.</summary>
    public static async Task<ImageInfo?> ReadInfoAsync(string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);

            bool jpeg = decoder.DecoderInformation.CodecId == BitmapDecoder.JpegDecoderId;
            bool tiff = decoder.DecoderInformation.CodecId == BitmapDecoder.TiffDecoderId;

            // Every frame of a TIFF is a scanned page; the frames of a GIF are
            // an animation, and only the first is a picture.
            int pages = tiff ? (int)Math.Max(1, decoder.FrameCount) : 1;

            return new ImageInfo(
                pages,
                (int)decoder.OrientedPixelWidth,
                (int)decoder.OrientedPixelHeight,
                decoder.DpiX,
                decoder.DpiY,
                jpeg && await IsUprightAsync(decoder));
        }
        catch (Exception ex)
        {
            Diag.Log($"merge: couldn't read the picture \"{Path.GetFileName(path)}\": {ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>One frame decoded, turned upright by its EXIF orientation. Null when it can't be decoded.</summary>
    public static async Task<DecodedImage?> DecodeAsync(string path, int frame)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var picture = await decoder.GetFrameAsync((uint)frame);

            var data = await picture.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Straight,
                new BitmapTransform(),
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb);

            return new DecodedImage(
                data.DetachPixelData(),
                (int)picture.OrientedPixelWidth,
                (int)picture.OrientedPixelHeight,
                picture.DpiX,
                picture.DpiY);
        }
        catch (Exception ex)
        {
            Diag.Log($"merge: couldn't decode frame {frame} of \"{Path.GetFileName(path)}\": {ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>
    /// Whether a JPEG is stored the right way up. PDFium draws a JPEG's bytes
    /// as stored, so a photo its camera marked as turned would come out on its
    /// side if it went in without being decoded.
    /// </summary>
    private static async Task<bool> IsUprightAsync(BitmapDecoder decoder)
    {
        const string orientation = "System.Photo.Orientation";
        try
        {
            var found = await decoder.BitmapProperties.GetPropertiesAsync(new[] { orientation });
            return !found.TryGetValue(orientation, out var value) || value.Value is not ushort turn || turn == 1;
        }
        catch (Exception)
        {
            // No readable orientation. A quarter turn would still show in the
            // oriented size, so only a file that plainly is not turned goes in
            // as its own bytes.
            return decoder.OrientedPixelWidth == decoder.PixelWidth
                && decoder.OrientedPixelHeight == decoder.PixelHeight;
        }
    }
}
