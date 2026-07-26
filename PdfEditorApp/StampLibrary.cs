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
    /// <summary>A Stamps folder beside the exe, which turns on portable mode.</summary>
    private static string PortableFolder => Path.Combine(AppContext.BaseDirectory, "Stamps");

    /// <summary>
    /// Where the stamps live.
    ///
    /// Per-user by default, NOT beside the executable. A stamp is universal:
    /// the same signature is wanted in every document and every version of the
    /// app, so it has to outlive the install folder. Keeping it next to the
    /// exe tied it to one installation, and reinstalling or installing to a
    /// different folder left it behind, which is exactly what happened.
    ///
    /// A Stamps folder beside the exe still wins if one exists, so a copy on a
    /// USB stick stays self-contained. Nothing creates that folder
    /// automatically; it is opt-in by putting it there.
    ///
    /// THIS IS USER DATA either way. Created if missing, otherwise left
    /// entirely alone, and never part of an installer payload.
    /// </summary>
    public static string FolderPath =>
        Directory.Exists(PortableFolder)
            ? PortableFolder
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppInfo.Name,
                "Stamps");

    public static string EnsureFolder()
    {
        string path = FolderPath;
        Directory.CreateDirectory(path);   // no-op when it already exists
        MigrateFromBesideExe(path);
        return path;
    }

    /// <summary>
    /// Brings stamps across from an older install that kept them beside the
    /// exe, once.
    ///
    /// Earlier versions stored them there, so anyone upgrading has stamps in a
    /// folder the app no longer reads. Leaving them behind would look exactly
    /// like the app had thrown them away, which is the complaint this whole
    /// change exists to answer.
    ///
    /// COPIES rather than moves, and never overwrites: the old folder is left
    /// untouched, so a failure here costs nothing and the originals are always
    /// still where they were.
    /// </summary>
    private static void MigrateFromBesideExe(string destination)
    {
        try
        {
            // In portable mode the two are the same folder, so there is
            // nothing to move.
            if (string.Equals(destination, PortableFolder, StringComparison.OrdinalIgnoreCase)
                || !Directory.Exists(PortableFolder))
            {
                return;
            }

            foreach (string source in Directory.EnumerateFiles(PortableFolder, "*.png"))
            {
                string target = Path.Combine(destination, Path.GetFileName(source));
                if (!File.Exists(target))
                {
                    File.Copy(source, target);
                    Diag.Log($"stamp migrated from the install folder: {Path.GetFileName(source)}");
                }
            }
        }
        catch (Exception ex)
        {
            Diag.Log($"stamp migration skipped: {ex.Message}");
        }
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
    /// Remembers which stamp was last used, so it is still chosen next time.
    ///
    /// People keep one or two stamps, a signature and maybe a seal, and reach
    /// for the same one constantly. Making them re-pick it on every launch is
    /// a small tax charged on the most common action there is.
    ///
    /// Stored as a plain text file next to the stamps, for the same reason
    /// they are: it is inspectable, portable, and losing it costs one click.
    /// </summary>
    private static string LastUsedPath => Path.Combine(FolderPath, ".last-used");

    public static void RememberLastUsed(StampEntry entry)
    {
        try
        {
            // The NAME, not the full path, so the library still works after
            // the folder is moved or the app is installed somewhere else.
            File.WriteAllText(LastUsedPath, Path.GetFileName(entry.Path));
        }
        catch (Exception ex)
        {
            // Never worth interrupting anyone over.
            Diag.Log($"could not remember the last stamp: {ex.Message}");
        }
    }

    /// <summary>
    /// The stamp to preselect: the last one used if it is still there,
    /// otherwise the first, otherwise none.
    /// </summary>
    public static StampEntry? LastUsed(IReadOnlyList<StampEntry> stamps)
    {
        if (stamps.Count == 0)
        {
            return null;
        }

        try
        {
            if (File.Exists(LastUsedPath))
            {
                string name = File.ReadAllText(LastUsedPath).Trim();
                foreach (var s in stamps)
                {
                    if (string.Equals(Path.GetFileName(s.Path), name,
                                      StringComparison.OrdinalIgnoreCase))
                    {
                        return s;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Diag.Log($"could not read the last stamp: {ex.Message}");
        }

        // The remembered one was deleted or renamed, so fall back rather than
        // leaving nothing selected.
        return stamps[0];
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
