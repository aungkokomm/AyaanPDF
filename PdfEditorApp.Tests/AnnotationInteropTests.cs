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
    private static extern int save_document(
        ulong docHandle, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int resize_annotation(
        ulong docHandle, int pageIndex, int index, int captureWidth,
        float left, float top, float right, float bottom, out int newIndex);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int add_stamp_annotation(
        ulong docHandle, int pageIndex, int captureWidth,
        float left, float top, float right, float bottom,
        [In] byte[] bgra, nuint byteLen, int pixelWidth, int pixelHeight);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int set_annotation_bounds(
        ulong docHandle, int pageIndex, int index, int captureWidth,
        float left, float top, float right, float bottom);

    // Field ORDER matters and must match Rust: (width, height, buffer, len,
    // status). An earlier version of this file had it wrong; it happened to be
    // harmless because the struct only ever passes through unchanged, but a
    // test that reads Width would have read half a pointer.
    [StructLayout(LayoutKind.Sequential)]
    private struct RenderResult
    {
        public int Width;
        public int Height;
        public IntPtr Buffer;
        public nuint Len;
        public int Status;
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern RenderResult render_low_res(ulong docHandle, int pageIndex, int targetWidth);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern RenderResult render_tile(
        ulong docHandle, int pageIndex, int level, int col, int row);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void free_render_result(RenderResult result);

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
                // Colour is deliberately NOT reported. FPDFAnnot_GetColor
                // access-violates on an annotation that has an appearance
                // stream, and RENDERING generates appearance streams, so in
                // an app that draws its pages the query is never safe.
                Assert.Equal(-1, info.Color);
                Assert.Equal(1.0f, info.Opacity, 2);
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
    public void the_users_reported_sequence_save_reopen_select_drag()
    {
        // Highlight, Save As, reopen THE SAVED FILE, click the highlight and
        // drag it. Reported as an access violation, and the earlier tests all
        // missed it because none of them saved to a real file and reopened
        // from that path: they either edited the still-open document or went
        // through an in-memory byte round trip. The app does neither.
        string saved = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"ayaan-a6b-{Guid.NewGuid():N}.pdf");

        ulong first = OpenFixture();
        try
        {
            var quads = new[] { new HighlightQuad { Left = 300, Top = 300, Right = 700, Bottom = 380 } };
            var specs = new[]
            {
                new HighlightSpec { PageIndex = 0, QuadOffset = 0, QuadCount = 1, R = 255, G = 235, B = 59, A = 200 },
            };
            Assert.Equal(OkPdfium, add_highlight_annotations(first, 1000, specs, 1, quads, 1));
            Assert.Equal(OkPdfium, save_document(first, saved));
        }
        finally
        {
            close_document(first);
        }

        Assert.True(System.IO.File.Exists(saved), "the save produced no file");

        // Reopen from the PATH, which is what the app does after a Save As.
        ulong reopened = open_document(saved);
        Assert.NotEqual(0UL, reopened);
        try
        {
            // Click: read the page's annotations and pick one.
            var array = get_annotations(reopened, 0);
            int index;
            float left, top, right, bottom;
            try
            {
                Assert.Equal(OkPdfium, array.Status);
                Assert.Equal(1u, (uint)array.Len);
                var info = Marshal.PtrToStructure<AnnotationInfo>(array.Items);
                (index, left, top, right, bottom) = (info.Index, info.Left, info.Top, info.Right, info.Bottom);
            }
            finally
            {
                free_annotation_array(array);
            }

            // Drag: commit the move on release.
            const int CaptureWidth = 1000;
            const float dx = 0.05f, dy = 0.05f;
            int status = set_annotation_bounds(
                reopened, 0, index, CaptureWidth,
                (left + dx) * CaptureWidth, (top + dy) * CaptureWidth,
                (right + dx) * CaptureWidth, (bottom + dy) * CaptureWidth);
            Assert.True(status == OkPdfium || status == Unsupported, $"move returned {status}");

            // And the app re-reads the page straight afterwards.
            var after = get_annotations(reopened, 0);
            Assert.Equal(OkPdfium, after.Status);
            free_annotation_array(after);
        }
        finally
        {
            close_document(reopened);
            try { System.IO.File.Delete(saved); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void adding_and_deleting_repeatedly_does_not_fault()
    {
        // The same loop as the concurrency test with the render threads taken
        // away, to separate two explanations for the same crash: a genuine
        // race, or a bug in add-delete-re-read that no earlier test hit
        // because they all ran the cycle exactly once.
        ulong handle = OpenFixture();
        try
        {
            var quads = new[] { new HighlightQuad { Left = 300, Top = 300, Right = 700, Bottom = 380 } };
            var specs = new[]
            {
                new HighlightSpec { PageIndex = 0, QuadOffset = 0, QuadCount = 1, R = 255, G = 235, B = 59, A = 200 },
            };

            for (int round = 0; round < 25; round++)
            {
                Assert.Equal(OkPdfium, add_highlight_annotations(handle, 1000, specs, 1, quads, 1));

                var array = get_annotations(handle, 0);
                Assert.Equal(OkPdfium, array.Status);
                Assert.True(array.Items != IntPtr.Zero, $"round {round}: no annotations after adding one");
                int index = Marshal.PtrToStructure<AnnotationInfo>(array.Items).Index;
                free_annotation_array(array);

                set_annotation_bounds(handle, 0, index, 1000, 350, 350, 750, 430);
                Assert.Equal(OkPdfium, delete_annotation(handle, 0, index));
            }
        }
        finally
        {
            close_document(handle);
        }
    }

    [Fact] // Was skipped while it reproduced a real crash: reading a page's
           // annotations after the page had been rendered access-violated in
           // FPDFAnnot_GetColor, because rendering generates appearance
           // streams and the colour query dies on annotations that have one.
           // get_annotations no longer queries colour at all, so this now
           // doubles as the end-to-end regression for that fix.
    public void editing_an_annotation_while_the_page_renders_does_not_fault()
    {
        // The last interaction the other tests do not cover, and the only one
        // left that matches the reported crash.
        //
        // The app never edits a document in isolation: base renders, the
        // sharpen pass and tile renders are all running on background threads
        // against the SAME document while the click, drag and commit happen on
        // the UI thread. PDFium is not thread-safe, so everything that touches
        // it has to serialize, and this is what proves it does. Reasoning
        // about the locks is not proof; a fault here would be.
        ulong handle = OpenFixture();
        using var stop = new System.Threading.CancellationTokenSource();

        var renderers = new List<System.Threading.Tasks.Task>();
        for (int t = 0; t < 3; t++)
        {
            renderers.Add(System.Threading.Tasks.Task.Run(() =>
            {
                var rng = new Random(Environment.CurrentManagedThreadId);
                while (!stop.IsCancellationRequested)
                {
                    free_render_result(render_low_res(handle, rng.Next(0, 20), 900));
                    free_render_result(render_tile(handle, 0, 4, rng.Next(0, 16), rng.Next(0, 16)));
                }
            }));
        }

        try
        {
            var quads = new[] { new HighlightQuad { Left = 300, Top = 300, Right = 700, Bottom = 380 } };
            var specs = new[]
            {
                new HighlightSpec { PageIndex = 0, QuadOffset = 0, QuadCount = 1, R = 255, G = 235, B = 59, A = 200 },
            };

            // Add, read, move and delete repeatedly while the renders hammer
            // the same document.
            for (int round = 0; round < 25; round++)
            {
                Assert.Equal(OkPdfium, add_highlight_annotations(handle, 1000, specs, 1, quads, 1));

                var array = get_annotations(handle, 0);
                Assert.Equal(OkPdfium, array.Status);
                int index = Marshal.PtrToStructure<AnnotationInfo>(array.Items).Index;
                free_annotation_array(array);

                set_annotation_bounds(handle, 0, index, 1000, 350, 350, 750, 430);
                Assert.Equal(OkPdfium, delete_annotation(handle, 0, index));
            }
        }
        finally
        {
            stop.Cancel();
            System.Threading.Tasks.Task.WaitAll([.. renderers]);
            close_document(handle);
        }
    }

    [Fact]
    public void resizing_a_stamp_across_the_boundary_reports_its_new_index()
    {
        // resize_annotation has an OUT parameter, which is a new shape at this
        // boundary. A rebuilt annotation moves to the end of its page's list,
        // so an index that came back wrong would leave the app editing a
        // different annotation than the one the user selected.
        ulong handle = OpenFixture();
        try
        {
            // 8x8 opaque BGRA.
            var px = new byte[8 * 8 * 4];
            for (int i = 0; i < px.Length; i += 4)
            {
                px[i] = 32; px[i + 1] = 64; px[i + 2] = 200; px[i + 3] = 255;
            }

            Assert.Equal(OkPdfium, add_stamp_annotation(
                handle, 0, 1000, 100, 100, 200, 200, px, (nuint)px.Length, 8, 8));

            Assert.Equal(OkPdfium, resize_annotation(
                handle, 0, 0, 1000, 100, 100, 400, 400, out int newIndex));

            var array = get_annotations(handle, 0);
            try
            {
                Assert.Equal(1u, (uint)array.Len);
                var info = Marshal.PtrToStructure<AnnotationInfo>(array.Items);
                Assert.Equal(info.Index, newIndex);
                Assert.Equal(4, info.Subtype);           // still a stamp
                Assert.Equal(0.40, info.Right, 3);       // and actually resized
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
    public void undoing_a_move_by_restoring_bounds_puts_it_back_exactly()
    {
        // The cheap undo step is "put the rectangle back", so the test that
        // matters is whether replaying the old rectangle lands the annotation
        // exactly where it started. If it drifts, undo quietly moves things.
        ulong handle = OpenFixture();
        try
        {
            var quads = new[] { new HighlightQuad { Left = 300, Top = 300, Right = 700, Bottom = 380 } };
            var specs = new[]
            {
                new HighlightSpec { PageIndex = 0, QuadOffset = 0, QuadCount = 1, R = 255, G = 235, B = 59, A = 200 },
            };
            Assert.Equal(OkPdfium, add_highlight_annotations(handle, 1000, specs, 1, quads, 1));

            var before = get_annotations(handle, 0);
            var original = Marshal.PtrToStructure<AnnotationInfo>(before.Items);
            free_annotation_array(before);

            // Move it somewhere else.
            Assert.Equal(OkPdfium, resize_annotation(
                handle, 0, original.Index, 1000, 500, 500, 900, 580, out int movedIndex));

            // Undo: replay the ORIGINAL rectangle, which is all the history
            // entry holds.
            Assert.Equal(OkPdfium, resize_annotation(
                handle, 0, movedIndex, 1000,
                original.Left * 1000, original.Top * 1000,
                original.Right * 1000, original.Bottom * 1000, out int restoredIndex));

            var after = get_annotations(handle, 0);
            try
            {
                Assert.Equal(1u, (uint)after.Len);
                var back = Marshal.PtrToStructure<AnnotationInfo>(after.Items);
                Assert.Equal(restoredIndex, back.Index);
                Assert.Equal(original.Left, back.Left, 3);
                Assert.Equal(original.Top, back.Top, 3);
                Assert.Equal(original.Right, back.Right, 3);
                Assert.Equal(original.Bottom, back.Bottom, 3);
            }
            finally
            {
                free_annotation_array(after);
            }
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
