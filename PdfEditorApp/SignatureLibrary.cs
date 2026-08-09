using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PdfEditorApp.Viewport;

namespace PdfEditorApp;

/// <summary>
/// The user's saved signatures, one JSON file each.
///
/// Stored per-user rather than beside the exe, for the reason StampLibrary
/// already records the hard way: a signature is universal, wanted in every
/// document and every version of the app, so it has to outlive the install
/// folder. Keeping it next to the executable ties it to one installation and
/// a reinstall looks exactly like the app threw it away.
///
/// A folder of readable files, not a database: they are the user's, and can be
/// copied, backed up or moved to another machine without this app's help.
///
/// THIS IS USER DATA. Created if missing, otherwise left alone, and never part
/// of an installer payload.
/// </summary>
internal static class SignatureLibrary
{
    private const string Extension = ".ayaansig";

    /// <summary>A Signatures folder beside the exe wins if one exists, so a copy on a stick stays self-contained.</summary>
    private static string PortableFolder => Path.Combine(AppContext.BaseDirectory, "Signatures");

    public static string FolderPath =>
        Directory.Exists(PortableFolder)
            ? PortableFolder
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppInfo.Name,
                "Signatures");

    /// <summary>Every saved signature, newest first, skipping anything unreadable.</summary>
    public static List<SignatureShape> Load()
    {
        var found = new List<SignatureShape>();
        try
        {
            if (!Directory.Exists(FolderPath))
            {
                return found;
            }

            foreach (var file in new DirectoryInfo(FolderPath)
                         .GetFiles("*" + Extension)
                         .OrderByDescending(f => f.LastWriteTimeUtc))
            {
                // One unreadable file must not hide the rest: a hand-edited or
                // half-written signature is a legal state on disk.
                try
                {
                    var dto = JsonSerializer.Deserialize<SignatureDto>(File.ReadAllText(file.FullName));
                    if (dto?.Strokes is { Count: > 0 })
                    {
                        found.Add(dto.ToShape(Path.GetFileNameWithoutExtension(file.Name)));
                    }
                }
                catch (Exception ex)
                {
                    Diag.Log($"signature: skipping {file.Name}, {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Diag.Log($"signature: could not list the folder, {ex.Message}");
        }
        return found;
    }

    public static bool Save(SignatureShape signature)
    {
        try
        {
            Directory.CreateDirectory(FolderPath);
            string path = Path.Combine(FolderPath, SafeName(signature.Name) + Extension);
            File.WriteAllText(path, JsonSerializer.Serialize(SignatureDto.From(signature)));
            return true;
        }
        catch (Exception ex)
        {
            Diag.Log($"signature: could not save, {ex.Message}");
            return false;
        }
    }

    public static void Delete(string name)
    {
        try
        {
            string path = Path.Combine(FolderPath, SafeName(name) + Extension);
            if (File.Exists(path)) { File.Delete(path); }
        }
        catch (Exception ex)
        {
            Diag.Log($"signature: could not delete {name}, {ex.Message}");
        }
    }

    /// <summary>The name IS the file name, so it has to survive being one.</summary>
    private static string SafeName(string name)
    {
        string cleaned = new string(name
            .Where(c => !Path.GetInvalidFileNameChars().Contains(c))
            .ToArray()).Trim();
        return cleaned.Length == 0 ? "signature" : cleaned;
    }

    // Serialised shape kept separate from the domain type: the file format is
    // a contract with files already on disk, and should not move whenever the
    // in-memory record gains a member.
    private sealed class SignatureDto
    {
        public double AspectRatio { get; set; } = 1;
        public List<StrokeDto> Strokes { get; set; } = new();

        public static SignatureDto From(SignatureShape s) => new()
        {
            AspectRatio = s.AspectRatio,
            Strokes = s.Strokes.Select(k => new StrokeDto
            {
                Color = k.ColorHex,
                Width = k.WidthNorm,
                // Flat [x,y,x,y,...] rather than objects: a signature is a few
                // hundred points and this halves the file for no loss.
                Points = k.Points.SelectMany(p => new[] { p.X, p.Y }).ToList(),
            }).ToList(),
        };

        public SignatureShape ToShape(string name) => new(
            name,
            Strokes.Select(k => new SignatureStroke(
                k.Color,
                k.Width,
                Pairs(k.Points))).ToList())
        { AspectRatio = AspectRatio };

        private static List<(double X, double Y)> Pairs(List<double> flat)
        {
            var pts = new List<(double X, double Y)>(flat.Count / 2);
            for (int i = 0; i + 1 < flat.Count; i += 2)
            {
                pts.Add((flat[i], flat[i + 1]));
            }
            return pts;
        }

        public sealed class StrokeDto
        {
            public string Color { get; set; } = "FF000000";
            public double Width { get; set; }
            public List<double> Points { get; set; } = new();
        }
    }
}
