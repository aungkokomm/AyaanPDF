using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using PdfEditorApp.Viewport;
using Xunit;
using Xunit.Abstractions;

namespace PdfEditorApp.Tests;

/// <summary>
/// What discovering gradient shapes actually COSTS, measured rather than
/// asserted.
///
/// The overlay is prepared per page, and preparing a page means building its
/// model, which means asking PDFium for the page's annotations and reading each
/// of their tags. That is page work, and page work in a scroll handler is a
/// mistake this codebase has made three times: getting a page PARSES it.
///
/// So the bounded rule is that a page is prepared once, when it enters the
/// visible range, and every frame after that is a dictionary lookup. These
/// measure both halves of that claim against the real core:
///
/// <list type="number">
/// <item>THE FIRST TIME: one page load, which is the cost that could hitch.</item>
/// <item>EVERY TIME AFTER: the per-frame cost, which is what runs at scroll
/// rate and must be nothing.</item>
/// </list>
///
/// Timings are printed rather than asserted tightly. A build machine is not a
/// user's machine and a millisecond bound would either be meaningless or
/// flaky; the bounds here are loose enough to catch an ORDER of magnitude,
/// which is what "does it hitch" actually means.
/// </summary>
public class GradientOverlayCostTests
{
    private const string Lib = "render_core";
    private const int OkPdfium = 0;
    private const int Cap = 1000;

    /// <summary>Enough shapes that a per-shape cost would be visible.</summary>
    private const int ShapesPerPage = 40;

    private readonly ITestOutputHelper _out;

    public GradientOverlayCostTests(ITestOutputHelper output) => _out = output;

    [StructLayout(LayoutKind.Sequential)]
    private struct ShapeSpec
    {
        public int PageIndex;
        public int Kind;
        public float X1;
        public float Y1;
        public float X2;
        public float Y2;
        public byte R;
        public byte G;
        public byte B;
        public byte A;
        public float WidthPx;
        public float RotationDeg;
        public uint FillRgba;
        public float CornerRadiusPx;
        public IntPtr EffectsUtf8;
        public nuint EffectsLen;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AnnotationArray
    {
        public IntPtr Items;
        public nuint Len;
        public int Status;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByteBuffer
    {
        public IntPtr Data;
        public nuint Len;
        public int Status;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PageSize
    {
        public float Width;
        public float Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PageSizeArray
    {
        public IntPtr Sizes;
        public nuint Len;
        public int Status;
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong open_document([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void close_document(ulong docHandle);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int add_shape_annotations(
        ulong docHandle, int captureWidth, [In] ShapeSpec[]? specs, nuint specCount);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern AnnotationArray get_annotations(ulong docHandle, int pageIndex);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void free_annotation_array(AnnotationArray array);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern ByteBuffer get_annotation_contents(
        ulong docHandle, int pageIndex, int index);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void free_byte_buffer(ByteBuffer buffer);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern PageSizeArray get_page_sizes(ulong docHandle);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void free_page_size_array(PageSizeArray array);

    // ---------------- the harness ----------------

    private static ulong FixtureWithGradients(int count)
    {
        string path = System.IO.Path.Combine(AppContext.BaseDirectory, "sample_20pages.pdf");
        ulong handle = open_document(path);
        Assert.NotEqual(0UL, handle);

        byte[] tail = Encoding.UTF8.GetBytes(
            "f(c=FFFF0000,c2=FF0000FF,x0=0.0000,y0=0.5000,x1=1.0000,y1=0.5000)");
        IntPtr pinned = Marshal.AllocHGlobal(tail.Length);
        Marshal.Copy(tail, 0, pinned, tail.Length);

        try
        {
            var specs = new ShapeSpec[count];
            for (int at = 0; at < count; at++)
            {
                float y = 40 + (at * 20);
                specs[at] = new ShapeSpec
                {
                    PageIndex = 0,
                    Kind = at % 3,
                    X1 = 100, Y1 = y, X2 = 400, Y2 = y + 15,
                    R = 0, G = 0, B = 0, A = 0xFF,
                    WidthPx = 4,
                    FillRgba = 0,
                    EffectsUtf8 = pinned,
                    EffectsLen = (nuint)tail.Length,
                };
            }

            Assert.Equal(OkPdfium, add_shape_annotations(handle, Cap, specs, (nuint)count));

            return handle;
        }
        finally
        {
            Marshal.FreeHGlobal(pinned);
        }
    }

    private static double PageWidthPts(ulong handle)
    {
        var sizes = get_page_sizes(handle);
        try
        {
            return Marshal.PtrToStructure<PageSize>(sizes.Sizes).Width;
        }
        finally
        {
            free_page_size_array(sizes);
        }
    }

    private static string? Contents(ulong handle, int index)
    {
        var buffer = get_annotation_contents(handle, 0, index);
        try
        {
            if (buffer.Status != OkPdfium || buffer.Data == IntPtr.Zero || buffer.Len == 0)
            {
                return null;
            }

            byte[] bytes = new byte[(int)buffer.Len];
            Marshal.Copy(buffer.Data, bytes, 0, bytes.Length);

            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            free_byte_buffer(buffer);
        }
    }

    /// <summary>
    /// Exactly what PrepareGradientOverlay does for one page: load the
    /// annotations, read every tag, build the model, walk it for gradients.
    /// </summary>
    private static IReadOnlyList<ShapeRenderItem> PrepareOnePage(ulong handle, double widthPts)
    {
        var snapshots = new List<AnnotationSnapshot>();

        var array = get_annotations(handle, 0);
        int count;
        try
        {
            count = (int)array.Len;
        }
        finally
        {
            free_annotation_array(array);
        }

        for (int at = 0; at < count; at++)
        {
            snapshots.Add(new AnnotationSnapshot(
                at, 2, 0.1, 0.1, 0.4, 0.3, 1.0, Guid.NewGuid(), Contents(handle, at)));
        }

        return GradientOverlay.ItemsFor(
            DocumentModelBuilder.BuildPage(0, snapshots, widthPts));
    }

    // ---------------- the measurement ----------------

    [Fact]
    public void preparing_a_page_of_gradients_costs_one_page_load_and_no_more()
    {
        ulong handle = FixtureWithGradients(ShapesPerPage);
        try
        {
            double widthPts = PageWidthPts(handle);

            // Warm: the first call anywhere pays for PDFium loading the page at
            // all, which the renderer has already paid by the time the overlay
            // is prepared for a page on screen.
            PrepareOnePage(handle, widthPts);

            var first = Stopwatch.StartNew();
            var items = PrepareOnePage(handle, widthPts);
            first.Stop();

            Assert.Equal(ShapesPerPage, items.Count);

            // AND THE PER-FRAME COST, which is what a scroll actually pays once
            // the page is prepared: reading the cache and concatenating. Run
            // enough times that a per-frame cost of any size would show.
            var cached = new Dictionary<int, IReadOnlyList<ShapeRenderItem>> { [0] = items };

            var frames = Stopwatch.StartNew();
            int drawn = 0;
            for (int frame = 0; frame < 1000; frame++)
            {
                var built = new List<ShapeRenderItem>();
                foreach (var page in cached.Values)
                {
                    built.AddRange(page);
                }

                drawn += built.Count;
            }

            frames.Stop();

            double perFrameUs = frames.Elapsed.TotalMilliseconds * 1000 / 1000;

            _out.WriteLine($"shapes on the page: {ShapesPerPage}");
            _out.WriteLine($"preparing the page once: {first.Elapsed.TotalMilliseconds:F2} ms");
            _out.WriteLine($"per frame once prepared: {perFrameUs:F1} us");
            _out.WriteLine($"(sanity, items drawn across 1000 frames: {drawn})");

            // ORDERS OF MAGNITUDE, not milliseconds. A build machine is not a
            // user's machine, and the question is whether a scroll hitches, not
            // whether a number is small on this box.
            //
            // A frame at 60Hz has 16ms. Preparing a page of forty gradient
            // shapes has to be a small fraction of one frame, and it happens
            // once per page rather than once per frame.
            Assert.True(
                first.Elapsed.TotalMilliseconds < 16,
                $"preparing one page took {first.Elapsed.TotalMilliseconds:F2} ms, which is a frame");

            // And the steady state has to be free.
            Assert.True(
                perFrameUs < 500,
                $"a prepared page still costs {perFrameUs:F1} us a frame");
        }
        finally
        {
            close_document(handle);
        }
    }

    [Fact]
    public void a_page_with_no_gradients_costs_the_same_and_yields_nothing()
    {
        // The case that matters most, because it is every document anybody has
        // today. The model was going to be built for selection anyway; the
        // overlay adds a walk of it and nothing else.
        string path = System.IO.Path.Combine(AppContext.BaseDirectory, "sample_20pages.pdf");
        ulong handle = open_document(path);

        try
        {
            double widthPts = PageWidthPts(handle);
            PrepareOnePage(handle, widthPts);

            var timer = Stopwatch.StartNew();
            var items = PrepareOnePage(handle, widthPts);
            timer.Stop();

            _out.WriteLine($"a page with no gradients: {timer.Elapsed.TotalMilliseconds:F2} ms");

            Assert.Empty(items);
            Assert.True(
                timer.Elapsed.TotalMilliseconds < 16,
                $"an ordinary page took {timer.Elapsed.TotalMilliseconds:F2} ms");
        }
        finally
        {
            close_document(handle);
        }
    }
}
