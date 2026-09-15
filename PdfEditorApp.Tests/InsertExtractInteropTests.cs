using System;
using System.IO;
using System.Runtime.InteropServices;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Insert and extract across the real FFI boundary: byte[], int[] and a UTF-8
/// path all have to marshal correctly, and a wrong layout here shows up as
/// nothing more than a wrong page count or an access violation.
/// </summary>
public class InsertExtractInteropTests
{
    private const string Lib = "render_core";
    private const int OkPdfium = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct ByteBuffer
    {
        public IntPtr Data;
        public nuint Len;
        public int Status;
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong open_document([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void close_document(ulong docHandle);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int get_page_count(ulong docHandle);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern ByteBuffer snapshot_document(ulong docHandle);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void free_byte_buffer(ByteBuffer buffer);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int insert_pages_from_bytes(ulong docHandle, [In] byte[] data, nuint len, int atIndex);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int insert_pages_from_document(
        ulong docHandle, ulong sourceHandle, [In] int[] indices, nuint count, int atIndex);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int insert_blank_page(ulong docHandle, int atIndex, float widthPts, float heightPts);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int extract_pages_to_file(
        ulong docHandle, [In] int[] indices, nuint count,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    private static ulong OpenFixture()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "sample_20pages.pdf");
        Assert.True(File.Exists(path), $"fixture missing at {path}");
        ulong handle = open_document(path);
        Assert.NotEqual(0UL, handle);
        return handle;
    }

    private static byte[] DocBytes(ulong handle)
    {
        var buf = snapshot_document(handle);
        try
        {
            Assert.Equal(OkPdfium, buf.Status);
            byte[] bytes = new byte[(int)buf.Len];
            Marshal.Copy(buf.Data, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            free_byte_buffer(buf);
        }
    }

    [Fact]
    public void inserting_a_documents_bytes_grows_the_page_count_by_that_many()
    {
        ulong source = OpenFixture();
        byte[] sourceBytes = DocBytes(source);
        close_document(source);

        ulong handle = OpenFixture();
        try
        {
            // Insert the whole 20-page source at index 5: 20 + 20 = 40.
            int inserted = insert_pages_from_bytes(handle, sourceBytes, (nuint)sourceBytes.Length, 5);
            Assert.Equal(20, inserted);
            Assert.Equal(40, get_page_count(handle));
        }
        finally
        {
            close_document(handle);
        }
    }

    [Fact]
    public void chosen_pages_of_an_open_document_grow_the_count_by_that_many()
    {
        ulong source = OpenFixture();
        ulong handle = OpenFixture();
        try
        {
            var chosen = new[] { 0, 4, 5, 19 };
            Assert.Equal(4, insert_pages_from_document(handle, source, chosen, (nuint)chosen.Length, 3));
            Assert.Equal(24, get_page_count(handle));

            // The file the pages came from is only read.
            Assert.Equal(20, get_page_count(source));
        }
        finally
        {
            close_document(handle);
            close_document(source);
        }
    }

    [Fact]
    public void a_blank_page_grows_the_count_by_one()
    {
        ulong handle = OpenFixture();
        try
        {
            Assert.Equal(OkPdfium, insert_blank_page(handle, 0, 612f, 792f));
            Assert.Equal(21, get_page_count(handle));
        }
        finally
        {
            close_document(handle);
        }
    }

    [Fact]
    public void extract_writes_a_file_with_the_chosen_pages_and_leaves_the_original()
    {
        ulong handle = OpenFixture();
        string outPath = Path.Combine(Path.GetTempPath(),
            $"ayaan_extract_{System.Diagnostics.Process.GetCurrentProcess().Id}_{Guid.NewGuid():N}.pdf");

        try
        {
            var indices = new[] { 1, 4, 9 };
            Assert.Equal(OkPdfium, extract_pages_to_file(handle, indices, (nuint)indices.Length, outPath));

            // Original untouched.
            Assert.Equal(20, get_page_count(handle));

            // The extracted file exists and has exactly three pages.
            Assert.True(File.Exists(outPath));
            ulong extracted = open_document(outPath);
            Assert.NotEqual(0UL, extracted);
            Assert.Equal(3, get_page_count(extracted));
            close_document(extracted);
        }
        finally
        {
            close_document(handle);
            try { File.Delete(outPath); } catch { /* best effort */ }
        }
    }
}
