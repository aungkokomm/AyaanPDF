using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using PdfEditorApp.Viewport;
using Windows.Foundation;
using IOPath = System.IO.Path;
using Windows.UI;

namespace PdfEditorApp.Rendering;

/// <summary>
/// Renders one identical rectangle through BOTH renderers and writes each to a
/// PNG, so the difference between them can be measured instead of argued about.
///
/// A diagnostic, not a feature. It runs only when AYAAN_SKIA_CAPTURE names an
/// output directory, so it cannot fire for a user, and it is the only way to
/// get the XAML renderer's actual pixels: a test assembly cannot load WinUI, so
/// until now every claim about what the overlay draws has been an argument from
/// its source rather than a measurement of its output.
///
/// It is also the runtime gate for the whole stage. SkiaSharp.Views.WinUI has a
/// documented history of failing in UNPACKAGED apps, and this app is
/// unpackaged: a build that succeeds proves nothing, because the failure is a
/// COM cast at paint time. If Skia cannot paint here, this is where that is
/// found.
///
/// WHAT IT MEASURED, 17 Aug 2026, at a device scale of 1.5, both captured
/// 600x600 through the same frame:
///
///   position     first ink at 78.00 DIPs for BOTH. Exact.
///   stroke width XAML 4.800 device px (3.200 DIPs) -- exactly what was asked
///                Skia 5.004 device px (3.336 DIPs) -- rounded up to 5.0
///
/// So Direct2D does not quantise stroke width and Skia does, to the nearest
/// half device pixel. Skia is 4.2% thicker here, which is the figure the
/// off-screen tests predicted for 1.5x. The difference is REAL and it is
/// Skia's, not the overlay's.
///
/// Left uncompensated on purpose. Whether a bounded quarter-device-pixel
/// difference is worth correcting is a stage 4 decision, and correcting it now
/// would mean tuning a renderer against a single measurement at a single DPI.
/// </summary>
internal static class SkiaParityCapture
{
    public const string DirectoryVariable = "AYAAN_SKIA_CAPTURE";

    /// <summary>The one shape both renderers are asked to draw.</summary>
    private static readonly ShapeAnnotation Subject = new(
        PageIndex: 0,
        Draft: new ShapeDraft(ShapeKind.Rectangle, 0.1, 0.1, 0.4, 0.3),
        ColorHex: "#FF000000",
        StrokeWidth: 0.004);

    private const double Scale = 800;
    private const int Width = 400;
    private const int Height = 400;

    public static string? RequestedDirectory =>
        Environment.GetEnvironmentVariable(DirectoryVariable) is { Length: > 0 } dir ? dir : null;

    /// <summary>
    /// Draws the subject twice into <paramref name="host"/>, captures each, and
    /// writes skia.png and xaml.png. Returns a one-line report.
    /// </summary>
    public static async Task<string> RunAsync(Panel host, string directory)
    {
        Directory.CreateDirectory(directory);

        string skia;
        try
        {
            skia = await CaptureSkiaAsync(host, directory);
        }
        catch (Exception ex)
        {
            // The failure this whole diagnostic exists to catch. Reported rather
            // than thrown: the XAML side is still worth capturing, and a crash
            // here would look like a fault in the app rather than in the
            // experiment.
            return $"SKIA FAILED: {ex.GetType().Name}: {ex.Message}";
        }

        string xaml = await CaptureXamlAsync(host, directory);
        return $"{skia} | {xaml}";
    }

    /// <summary>
    /// An opaque, fixed-size frame around whatever is being captured.
    ///
    /// Both renderers must be captured through one of these or the comparison
    /// is meaningless. RenderTargetBitmap renders only the area an element
    /// actually PAINTS, so a bare Canvas came back cropped to its polyline's
    /// inked bounds (365x245) while the Skia layer, which paints a full-size
    /// surface, came back at the requested 600x600. Same size, same origin,
    /// same ground, or the two images cannot be laid over each other.
    /// </summary>
    private static Border Frame(UIElement content) => new()
    {
        Width = Width,
        Height = Height,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Color.FromArgb(255, 255, 255, 255)),
        Child = content,
    };

    private static async Task<string> CaptureSkiaAsync(Panel host, string directory)
    {
        var layer = new SkiaShapeLayer { Width = Width, Height = Height };
        var frame = Frame(layer);

        host.Children.Add(frame);
        try
        {
            layer.Show(ShapeRenderList.From([], [Subject]), Scale, _ => 0);
            await SettleAsync(frame);
            await WriteAsync(frame, IOPath.Combine(directory, "skia.png"));
            return $"skia.png written ({Width}x{Height})";
        }
        finally
        {
            host.Children.Remove(frame);
        }
    }

    /// <summary>
    /// The XAML side, built the way the overlay builds it: a Polyline whose
    /// points are the same projected points, with the same thickness. Not a
    /// call into MainPage, which stays untouched, but the same construction.
    /// </summary>
    private static async Task<string> CaptureXamlAsync(Panel host, string directory)
    {
        var canvas = new Canvas { Width = Width, Height = Height, IsHitTestVisible = false };

        var item = ShapeRenderList.From([], [Subject])[0];
        var polyline = new Polyline
        {
            Stroke = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Color.FromArgb(item.Color.A, item.Color.R, item.Color.G, item.Color.B)),
            StrokeThickness = OverlayProjection.ToSlotThickness(item.StrokeWidth, Scale),
        };

        foreach (var p in item.Points)
        {
            var (x, y) = OverlayProjection.ToSlot(p, Scale, 0);
            polyline.Points.Add(new Point(x, y));
        }

        canvas.Children.Add(polyline);
        var frame = Frame(canvas);

        host.Children.Add(frame);
        try
        {
            await SettleAsync(frame);
            await WriteAsync(frame, IOPath.Combine(directory, "xaml.png"));
            return $"xaml.png written (thickness {polyline.StrokeThickness})";
        }
        finally
        {
            host.Children.Remove(frame);
        }
    }

    /// <summary>Lets layout and a paint pass happen before the capture.</summary>
    private static async Task SettleAsync(FrameworkElement element)
    {
        element.UpdateLayout();
        for (int i = 0; i < 8; i++)
        {
            await Task.Delay(60);
        }
    }

    private static async Task WriteAsync(UIElement element, string path)
    {
        var target = new RenderTargetBitmap();
        await target.RenderAsync(element);

        var pixels = await target.GetPixelsAsync();

        // Read through a DataReader rather than an IBuffer extension: the
        // extension resolves against ImmutableArray here and does not compile.
        byte[] bytes = new byte[pixels.Length];
        using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(pixels))
        {
            reader.ReadBytes(bytes);
        }

        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
            Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId,
            await OpenAsync(path));

        encoder.SetPixelData(
            Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
            Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
            (uint)target.PixelWidth,
            (uint)target.PixelHeight,
            96, 96,
            bytes);

        await encoder.FlushAsync();
    }

    private static async Task<Windows.Storage.Streams.IRandomAccessStream> OpenAsync(string path)
    {
        var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(
            IOPath.GetDirectoryName(path)!);
        var file = await folder.CreateFileAsync(
            IOPath.GetFileName(path), Windows.Storage.CreationCollisionOption.ReplaceExisting);
        return await file.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite);
    }
}
