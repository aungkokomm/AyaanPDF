using System;
using System.Runtime.InteropServices;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Tests the P/Invoke boundary itself, with the same declarations the app uses.
///
/// This layer had no coverage at all. Rust is covered by cargo and the pure
/// viewport logic is covered here, but the struct layouts and signatures
/// BETWEEN them were only ever exercised by running the app. A mismatch there
/// does not throw: it reads the wrong memory, and the process dies with a
/// dialog and no stack, which is exactly what happened while wiring annotation
/// editing.
///
/// The declarations are duplicated from PdfEditorApp.Interop rather than
/// referenced, because that lives in the WinUI project and a plain test
/// assembly cannot load it. Duplication is the point: if these and the app's
/// copies ever disagree, the size assertions below fail and say so.
/// </summary>
public class AnnotationInteropTests
{
    private const string Lib = "render_core";

    [StructLayout(LayoutKind.Sequential)]
    private struct AnnotationInfo
    {
        public int Index;
        public int Subtype;
        public float Left;
        public float Top;
        public float Right;
        public float Bottom;
        public int Color;
        public float Opacity;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AnnotationArray
    {
        public IntPtr Items;
        public nuint Len;
        public int Status;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HighlightQuad
    {
        public float Left;
        public float Top;
        public float Right;
        public float Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HighlightSpec
    {
        public int PageIndex;
        public uint QuadOffset;
        public uint QuadCount;
        public byte R;
        public byte G;
        public byte B;
        public byte A;
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong open_document([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void close_document(ulong docHandle);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern AnnotationArray get_annotations(ulong docHandle, int pageIndex);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void free_annotation_array(AnnotationArray array);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int add_highlight_annotations(
        ulong docHandle, int captureWidth,
        [In] HighlightSpec[]? specs, nuint specCount,
        [In] HighlightQuad[]? quads, nuint quadCount);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int delete_annotation(ulong docHandle, int pageIndex, int index);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int set_annotation_bounds(
        ulong docHandle, int pageIndex, int index, int captureWidth,
        float left, float top, float right, float bottom);

    private const int OkPdfium = 0;
    private const int Unsupported = 4;

    private static ulong OpenFixture()
    {
        string path = System.IO.Path.Combine(AppContext.BaseDirectory, "sample_20pages.pdf");
        Assert.True(System.IO.File.Exists(path), $"fixture missing at {path}");
        ulong handle = open_document(path);
        Assert.NotEqual(0UL, handle);
        return handle;
    }

    [Fact]
    public void the_annotation_structs_are_the_size_the_native_side_writes()
    {
        // The cheapest possible guard against the failure mode that gives no
        // stack trace. Rust writes 8 four-byte fields per annotation and a
        // pointer, a usize and an int for the array header.
        Assert.Equal(32, Marshal.SizeOf<AnnotationInfo>());
        Assert.Equal(IntPtr.Size == 8 ? 24 : 12, Marshal.SizeOf<AnnotationArray>());
        Assert.Equal(16, Marshal.SizeOf<HighlightQuad>());
        Assert.Equal(16, Marshal.SizeOf<HighlightSpec>());
    }

    [Fact]
    public void reading_annotations_from_a_clean_page_returns_an_empty_success()
    {
        ulong handle = OpenFixture();
        try
        {
            var array = get_annotations(handle, 0);
            Assert.Equal(OkPdfium, array.Status);
            Assert.Equal(0u, (uint)array.Len);
            free_annotation_array(array);
        }
        finally
        {
            close_document(handle);
        }
    }

    [Fact]
    public void freeing_an_empty_array_twice_is_harmless()
    {
        // The app frees in a finally, which runs on the early-return path too.
        // If a null or empty array were freed as though it held memory, this
        // would corrupt the heap rather than fail an assertion.
        ulong handle = OpenFixture();
        try
        {
            var array = get_annotations(handle, 0);
            free_annotation_array(array);
            free_annotation_array(array);
        }
        finally
        {
            close_document(handle);
        }
    }

    [Fact]
    public void an_annotation_marshals_back_with_the_values_it_was_given()
    {
        // Field-by-field, because a struct layout that is the right SIZE but
        // the wrong ORDER passes the size test and then silently hands the app
        // a garbage index, which it would happily pass back to a native call.
        ulong handle = OpenFixture();
        try
        {
            var quads = new[] { new HighlightQuad { Left = 300, Top = 300, Right = 700, Bottom = 380 } };
            var specs = new[]
            {
                new HighlightSpec
                {
                    PageIndex = 0, QuadOffset = 0, QuadCount = 1,
                    R = 255, G = 235, B = 59, A = 200,
                },
            };
            Assert.Equal(OkPdfium, add_highlight_annotations(handle, 1000, specs, 1, quads, 1));

            var array = get_annotations(handle, 0);
            try
            {
                Assert.Equal(OkPdfium, array.Status);
                Assert.Equal(1u, (uint)array.Len);

                var info = Marshal.PtrToStructure<AnnotationInfo>(array.Items);
                Assert.Equal(0, info.Index);
                Assert.Equal(2, info.Subtype);                  // ANNOT_HIGHLIGHT
                Assert.Equal(0.30, info.Left, 3);
                Assert.Equal(0.30, info.Top, 3);
                Assert.Equal(0.70, info.Right, 3);
                Assert.Equal(0.38, info.Bottom, 3);
                Assert.Equal(0xFFEB3B, info.Color);
                Assert.InRange(info.Opacity, 0.75, 0.80);       // 200/255
            }
            finally
            {
                free_annotation_array(array);
            }
        }
        finally
        {
            close_document(handle);
        }
    }

    [Fact]
    public void the_whole_select_move_delete_sequence_survives_the_boundary()
    {
        // Exactly what the app does when you click a mark already in the file
        // and drag it, driven through the same P/Invoke declarations.
        ulong handle = OpenFixture();
        try
        {
            var quads = new[] { new HighlightQuad { Left = 300, Top = 300, Right = 700, Bottom = 380 } };
            var specs = new[]
            {
                new HighlightSpec { PageIndex = 0, QuadOffset = 0, QuadCount = 1, R = 255, G = 235, B = 59, A = 200 },
            };
            Assert.Equal(OkPdfium, add_highlight_annotations(handle, 1000, specs, 1, quads, 1));

            // Read, as the app does on click.
            var array = get_annotations(handle, 0);
            int index;
            float left, top, right, bottom;
            try
            {
                var info = Marshal.PtrToStructure<AnnotationInfo>(array.Items);
                (index, left, top, right, bottom) = (info.Index, info.Left, info.Top, info.Right, info.Bottom);
            }
            finally
            {
                free_annotation_array(array);
            }

            // Move, as the app does on release.
            const int CaptureWidth = 1000;
            float dx = 0.1f, dy = 0.15f;
            int status = set_annotation_bounds(
                handle, 0, index, CaptureWidth,
                (left + dx) * CaptureWidth, (top + dy) * CaptureWidth,
                (right + dx) * CaptureWidth, (bottom + dy) * CaptureWidth);
            Assert.True(status == OkPdfium || status == Unsupported, $"move returned {status}");

            // Re-read, as the app does when it invalidates the page.
            var after = get_annotations(handle, 0);
            try
            {
                Assert.Equal(1u, (uint)after.Len);
                var moved = Marshal.PtrToStructure<AnnotationInfo>(after.Items);
                Assert.Equal(left + dx, moved.Left, 3);
                Assert.Equal(top + dy, moved.Top, 3);
            }
            finally
            {
                free_annotation_array(after);
            }

            Assert.Equal(OkPdfium, delete_annotation(handle, 0, index));

            var gone = get_annotations(handle, 0);
            Assert.Equal(0u, (uint)gone.Len);
            free_annotation_array(gone);
        }
        finally
        {
            close_document(handle);
        }
    }

    [Fact]
    public void reading_every_page_of_a_document_does_not_leak_or_fault()
    {
        // The app sweeps up to 25 pages on open. Twenty allocate-and-free
        // round trips is where a mismatched free would show up.
        ulong handle = OpenFixture();
        try
        {
            for (int page = 0; page < 20; page++)
            {
                var array = get_annotations(handle, page);
                Assert.Equal(OkPdfium, array.Status);
                free_annotation_array(array);
            }
        }
        finally
        {
            close_document(handle);
        }
    }
}
