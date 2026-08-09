using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

// Draws Ayaan PDF's two hand cursors and writes them as multi-size .cur files.
//
// Windows has no open-palm / closed-fist cursor pair. IDC_HAND is a pointing
// finger and IDC_SIZEALL is four arrows, so an Acrobat-style hand tool - open
// at rest, closed while dragging - has to ship its own art. This tool is kept
// in the repo so the art stays editable: the .cur files are otherwise opaque
// binaries nobody could adjust.
//
// Run it from this folder:  dotnet run
// Then rebuild the resource DLL, see tools/CursorGen/README.md.

const float Design = 64f;      // everything below is in a 64x64 design box
const float Outline = 2f;      // black rim thickness, per side
const float FingerW = 9f;
const float ThumbW = 10f;
const float FingerBottom = 42f;

// One hand, drawn twice. Open and closed differ ONLY in how far the four
// fingers reach: same palm, same thumb, same everything else, so the pair reads
// as one hand opening and closing rather than as two different pictures.
float[] fingerX = [22.5f, 32.5f, 42.5f, 51.5f];

// Staggered, because at 32px the 1-unit gaps between fingers do not survive and
// the differing heights are the only thing left saying "four fingers".
float[] openTops = [17f, 12f, 16.5f, 23f];

// Curled: the fingertips clear the palm by a couple of units, no more.
float[] closedTops = [27.5f, 25.5f, 27f, 30f];

// The thumb points DOWN and out to the left. Angled upwards it lines up with
// the fingers and reads as a fifth one.
var thumbA = new PointF(21f, 46f);
var thumbB = new PointF(8f, 38f);

// The SAME hotspot in both cursors, so pressing the button swaps the picture
// without the hand appearing to jump sideways under the pointer.
const int HotX = 30;
const int HotY = 32;

int[] sizes = [32, 48, 64];

string outDir = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "PdfEditorApp", "Assets", "Cursors");
Directory.CreateDirectory(outDir);

WriteCursor(Path.Combine(outDir, "hand_open.cur"), DrawOpenHand);
WriteCursor(Path.Combine(outDir, "hand_closed.cur"), DrawClosedHand);
Console.WriteLine($"wrote hand_open.cur and hand_closed.cur to {Path.GetFullPath(outDir)}");

// Rebuild the resource DLL in the same breath. Nothing at runtime reads the
// .cur files - they are only ever seen through AyaanCursors.dll - so leaving
// this as a manual follow-up step means new art that silently does not ship.
// That is exactly what happened the first time.
BuildResourceDll(outDir);

// ---------------------------------------------------------------- the art --

// Fingers are drawn as round-capped strokes rather than outlined paths: a
// stroke IS a capsule, and the "black underneath, white on top" order below
// gives every shape a rim without any path union maths.
static void Capsule(Graphics g, Color color, float w, PointF a, PointF b)
{
    using var pen = new Pen(color, w) { StartCap = LineCap.Round, EndCap = LineCap.Round };
    g.DrawLine(pen, a, b);
}

static GraphicsPath RoundedRect(RectangleF r, float radius)
{
    float d = radius * 2;
    var path = new GraphicsPath();
    path.AddArc(r.Left, r.Top, d, d, 180, 90);
    path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
    path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
    path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
    path.CloseFigure();
    return path;
}

// Stroking with a pen of 2*Outline centred on the edge puts half the ink
// outside the shape and half inside; the white fill that follows reclaims the
// inside half, leaving a clean rim of exactly Outline.
static void Body(Graphics g, Color color, GraphicsPath path)
{
    using var pen = new Pen(color, Outline * 2) { LineJoin = LineJoin.Round };
    g.DrawPath(pen, path);
}

void DrawOpenHand(Graphics g) => DrawHand(g, openTops);

void DrawClosedHand(Graphics g) => DrawHand(g, closedTops);

void DrawHand(Graphics g, float[] fingerTops)
{
    var palm = RoundedRect(new RectangleF(17f, 33f, 39f, 23f), 10f);

    // Pass 1: every outline, so the fills that follow erase the seams where
    // shapes overlap and only the silhouette's rim survives.
    for (int i = 0; i < fingerX.Length; i++)
    {
        Capsule(g, Color.Black, FingerW + Outline * 2, new PointF(fingerX[i], fingerTops[i]), new PointF(fingerX[i], FingerBottom));
    }
    Capsule(g, Color.Black, ThumbW + Outline * 2, thumbA, thumbB);
    Body(g, Color.Black, palm);

    // Pass 2: fills. Fingers and thumb first so the palm covers where they
    // enter it and the hand becomes one shape below the knuckles.
    for (int i = 0; i < fingerX.Length; i++)
    {
        Capsule(g, Color.White, FingerW, new PointF(fingerX[i], fingerTops[i]), new PointF(fingerX[i], FingerBottom));
    }
    Capsule(g, Color.White, ThumbW, thumbA, thumbB);
    g.FillPath(Brushes.White, palm);
}

// ------------------------------------------------------- the resource DLL --

// Compiles cursors.rc into AyaanCursors.dll with the Windows SDK's rc.exe and
// the MSVC linker. /NOENTRY makes it resource-only: no code, no CRT, no
// imports, just the two CURSOR resources.
static void BuildResourceDll(string dir)
{
    string? rc = FindTool(@"C:\Program Files (x86)\Windows Kits\10\bin", "rc.exe", @"\x64\");
    string? link = FindTool(@"C:\Program Files\Microsoft Visual Studio", "link.exe", @"\Hostx64\x64\");
    if (rc is null || link is null)
    {
        Console.WriteLine("rc.exe or link.exe not found; AyaanCursors.dll NOT rebuilt. See README.md.");
        return;
    }

    if (Run(rc, "/nologo /fo cursors.res cursors.rc", dir) &&
        Run(link, "/NOLOGO /DLL /NOENTRY /MACHINE:X64 /OUT:AyaanCursors.dll cursors.res", dir))
    {
        Console.WriteLine($"rebuilt {Path.Combine(dir, "AyaanCursors.dll")}");
    }

    static string? FindTool(string root, string name, string pathContains)
    {
        if (!Directory.Exists(root)) { return null; }
        return Directory.EnumerateFiles(root, name, SearchOption.AllDirectories)
            .Where(p => p.Contains(pathContains, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p)
            .LastOrDefault();
    }

    static bool Run(string exe, string args, string workingDir)
    {
        var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, args)
        {
            WorkingDirectory = workingDir,
            UseShellExecute = false,
        })!;
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            Console.WriteLine($"{Path.GetFileName(exe)} failed with exit {p.ExitCode}");
        }
        return p.ExitCode == 0;
    }
}

// ------------------------------------------------------------- .cur output --

void WriteCursor(string path, Action<Graphics> draw)
{
    var images = new List<(int Size, byte[] Data)>();
    foreach (int size in sizes)
    {
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.ScaleTransform(size / Design, size / Design);
            draw(g);
        }
        images.Add((size, DibFor(bmp)));
    }

    using var fs = File.Create(path);
    using var w = new BinaryWriter(fs);

    // ICONDIR
    w.Write((ushort)0);              // reserved
    w.Write((ushort)2);              // 2 = cursor
    w.Write((ushort)images.Count);

    int offset = 6 + 16 * images.Count;
    foreach (var (size, data) in images)
    {
        w.Write((byte)size);                          // width
        w.Write((byte)size);                          // height
        w.Write((byte)0);                             // palette entries
        w.Write((byte)0);                             // reserved
        w.Write((ushort)(HotX * size / (int)Design)); // hotspot x, in this size
        w.Write((ushort)(HotY * size / (int)Design)); // hotspot y
        w.Write(data.Length);
        w.Write(offset);
        offset += data.Length;
    }

    foreach (var (_, data) in images)
    {
        w.Write(data);
    }
}

// A cursor image is a BITMAPINFOHEADER whose height is doubled, followed by the
// colour rows and then a 1bpp AND mask. The mask is left all-zero: with 32bpp
// data Windows honours the alpha channel, and a mask that disagreed with it
// would punch holes in the anti-aliased edge.
static byte[] DibFor(Bitmap bmp)
{
    int size = bmp.Width;
    int maskStride = (size + 31) / 32 * 4;
    var data = bmp.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    byte[] xor = new byte[size * size * 4];
    try
    {
        // Bottom-up, which is what the DIB format wants and what GDI+ is not.
        for (int y = 0; y < size; y++)
        {
            nint src = data.Scan0 + (size - 1 - y) * data.Stride;
            System.Runtime.InteropServices.Marshal.Copy(src, xor, y * size * 4, size * 4);
        }
    }
    finally
    {
        bmp.UnlockBits(data);
    }

    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms);
    w.Write(40);                    // biSize
    w.Write(size);                  // biWidth
    w.Write(size * 2);              // biHeight, colour rows + mask rows
    w.Write((ushort)1);             // biPlanes
    w.Write((ushort)32);            // biBitCount
    w.Write(0);                     // biCompression, BI_RGB
    w.Write(xor.Length + maskStride * size);
    w.Write(0);                     // biXPelsPerMeter
    w.Write(0);                     // biYPelsPerMeter
    w.Write(0);                     // biClrUsed
    w.Write(0);                     // biClrImportant
    w.Write(xor);
    w.Write(new byte[maskStride * size]);
    w.Flush();
    return ms.ToArray();
}
