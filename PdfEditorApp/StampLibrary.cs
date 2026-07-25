using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace PdfEditorApp;

/// <summary>One PNG in the user's stamp library.</summary>
/// <param name="Name">File name without extension, shown in the picker.</param>
public sealed record StampEntry(string Name, string Path);

/// <summary>Decoded pixels ready for render_core.</summary>
public sealed record StampPixels(byte[] Bgra, int Width, int Height);

/// <summary>
/// The user's own stamp images, kept as ordinary PNG files in a folder.
///
/// A folder of files rather than a database or an app-data blob, because this
/// app is portable and the stamps are the user's: they can add one by dropping
/// a file in, back them up by copying the folder, and take them to another
/// machine. Nothing here is hidden in a format only this app can read.
/// </summary>
internal static class StampLibrary
{
    /// <summary>
    /// Where the stamps live: a Stamps folder beside the executable.
    ///
    /// THIS IS USER DATA. It is created if missing and otherwise left entirely
    /// alone: never cleared, never overwritten, and it must never be swept up
    /// by an installer or a deploy step. Losing it means losing files the user
    /// put there by hand.
    /// </summary>
    public static string FolderPath => Path.Combine(AppContext.BaseDirectory, "Stamps");

    public static string EnsureFolder()
    {
        string path = FolderPath;
        Directory.CreateDirectory(path);   // no-op when it already exists
        return path;
    }

    /// <summary>
    /// Every stamp in the library, by name.
    ///
    /// Failures are swallowed to an empty list on purpose: a missing or
    /// unreadable folder means "no stamps yet", which is the normal state on
    /// first run, not an error worth interrupting the user for.
    /// </summary>
    public static IReadOnlyList<StampEntry> List()
    {
        try
        {
            return Directory
                .EnumerateFiles(EnsureFolder(), "*.png", SearchOption.TopDirectoryOnly)
                .OrderBy(p => Path.GetFileName(p), StringComparer.CurrentCultureIgnoreCase)
                .Select(p => new StampEntry(Path.GetFileNameWithoutExtension(p), p))
                .ToList();
        }
        catch (Exception ex)
        {
            Diag.Log($"stamp library unreadable: {ex.Message}");
            return Array.Empty<StampEntry>();
        }
    }

    /// <summary>
    /// Copies a PNG into the library, returning the stored entry.
    ///
    /// Names are made unique rather than overwriting: two files called
    /// "signature.png" from different folders are two different stamps, and
    /// silently replacing one with the other would destroy something the user
    /// put there.
    /// </summary>
    public static async Task<StampEntry?> ImportAsync(StorageFile file)
    {
        try
        {
            string folder = EnsureFolder();
            string baseName = Path.GetFileNameWithoutExtension(file.Name);
            string target = Path.Combine(folder, baseName + ".png");

            for (int n = 2; File.Exists(target); n++)
            {
                target = Path.Combine(folder, $"{baseName} ({n}).png");
            }

            using (var source = await file.OpenStreamForReadAsync())
            using (var dest = File.Create(target))
            {
                await source.CopyToAsync(dest);
            }

            Diag.Log($"stamp imported: {Path.GetFileName(target)}");
            return new StampEntry(Path.GetFileNameWithoutExtension(target), target);
        }
        catch (Exception ex)
        {
            Diag.Log($"stamp import failed: {ex}");
            return null;
        }
    }

    /// <summary>
    /// Decodes a stamp to the tightly packed BGRA render_core expects.
    ///
    /// Decoding happens here rather than in the native core so that the image
    /// crate and its whole encoder stack stay out of that binary, and because
    /// the app needs the same pixels to show a preview anyway.
    ///
    /// Straight alpha, not premultiplied: PDFium builds the image's soft mask
    /// from these bytes, and premultiplied colour would darken every
    /// semi-transparent edge against it.
    /// </summary>
    public static async Task<StampPixels?> DecodeAsync(string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read);

            var decoder = await BitmapDecoder.CreateAsync(stream);
            var pixels = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Straight,
                new BitmapTransform(),
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.DoNotColorManage);

            byte[] bytes = pixels.DetachPixelData();
            int width = (int)decoder.PixelWidth;
            int height = (int)decoder.PixelHeight;

            // render_core rejects a length that disagrees with the dimensions,
            // since it hands the buffer straight to PDFium. Catch it here with
            // a name attached rather than as a bare status code.
            if (bytes.Length != width * height * 4)
            {
                Diag.Log($"stamp {Path.GetFileName(path)}: {width}x{height} implies " +
                         $"{width * height * 4} bytes, decoder produced {bytes.Length}");
                return null;
            }

            return new StampPixels(bytes, width, height);
        }
        catch (Exception ex)
        {
            Diag.Log($"stamp decode failed for {path}: {ex.Message}");
            return null;
        }
    }
}
