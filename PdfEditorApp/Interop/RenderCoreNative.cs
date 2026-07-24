using System;
using System.Runtime.InteropServices;

namespace PdfEditorApp.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct RenderResult
{
    public int Width;
    public int Height;
    public IntPtr Buffer;
    public nuint Len;
    public int Status;
}

/// <summary>Mirrors render_core::CharInfo (src/lib.rs) — one character's box + codepoint.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeCharInfo
{
    public float Left;
    public float Top;
    public float Right;
    public float Bottom;
    public uint Codepoint;
}

/// <summary>Mirrors render_core::CharInfoArray (src/lib.rs).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CharInfoArray
{
    public IntPtr Chars;
    public nuint Len;
    public int Status;
}

/// <summary>Mirrors render_core::BurnRect (src/lib.rs) — a filled rect in render-pixel space.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct BurnRect
{
    public int PageIndex;
    public float Left;
    public float Top;
    public float Right;
    public float Bottom;
    public byte R;
    public byte G;
    public byte B;
    public byte A;
}

/// <summary>Mirrors render_core::BurnPoint (src/lib.rs).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct BurnPoint
{
    public float X;
    public float Y;
}

/// <summary>
/// Mirrors render_core::BurnStroke (src/lib.rs). Strokes index into a shared
/// flat point array rather than carrying their own, which keeps the FFI to
/// plain slices instead of a pointer-to-pointer.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct BurnStroke
{
    public int PageIndex;
    public uint PointOffset;
    public uint PointCount;
    public float WidthPx;
    public byte R;
    public byte G;
    public byte B;
    public byte A;
}

/// <summary>
/// Mirrors render_core::ByteBuffer (src/lib.rs). Must be released with
/// <see cref="RenderCoreNative.free_byte_buffer"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ByteBuffer
{
    public IntPtr Data;
    public nuint Len;
    public int Status;
}

/// <summary>Mirrors render_core::PageSize (src/lib.rs) — one page in PDF points.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativePageSize
{
    public float Width;
    public float Height;
}

/// <summary>Mirrors render_core::PageSizeArray (src/lib.rs).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PageSizeArray
{
    public IntPtr Sizes;
    public nuint Len;
    public int Status;
}

/// <summary>Mirrors render_core::{STATUS_*} (src/lib.rs).</summary>
internal static class RenderStatus
{
    public const int OkPdfium = 0;
    public const int InvalidInput = 1;
    public const int Panic = 2;
    public const int OkPlaceholder = 3;
}

/// <summary>Mirrors render_core::{POLL_*} (src/lib.rs).</summary>
internal static class PollStatus
{
    public const int Pending = 0;
    public const int Ready = 1;
    public const int Cancelled = 2;
    public const int UnknownRequest = 3;
}

/// <summary>
/// Raw P/Invoke surface for the render_core Rust cdylib. Field layout of
/// <see cref="RenderResult"/> must stay in sync with render_core::RenderResult
/// (src/lib.rs) — same field order, same primitive widths.
/// </summary>
internal static partial class RenderCoreNative
{
    private const string LibraryName = "render_core.dll";

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong open_document([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void close_document(ulong docHandle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int get_page_count(ulong docHandle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern CharInfoArray get_page_chars(ulong docHandle, int pageIndex, int targetWidth);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void free_char_info_array(CharInfoArray array);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern RenderResult render_low_res(ulong docHandle, int pageIndex, int targetWidth);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong request_high_res(ulong docHandle, int pageIndex, int targetWidth);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int poll_high_res(ulong requestId, out RenderResult outResult);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void discard_request(ulong requestId);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void free_render_result(RenderResult result);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rotate_page(ulong docHandle, int pageIndex, int degrees);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int delete_page(ulong docHandle, int pageIndex);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int save_document(ulong docHandle, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int get_form_field_count(ulong docHandle);

    /// <summary>
    /// Every page's size in one locked pass. The continuous viewport needs all
    /// sizes before it renders anything, so slot heights are known up front.
    /// </summary>
    /// <summary>
    /// Renders bypassing the tile cache entirely, in both directions. The
    /// sharpening tier's bitmaps are large and short-lived, so caching them
    /// would evict the whole modest base tier to hold pages about to scroll
    /// out of view.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern RenderResult render_uncached(ulong docHandle, int pageIndex, int targetWidth);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern PageSizeArray get_page_sizes(ulong docHandle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void free_page_size_array(PageSizeArray array);

    /// <summary>
    /// Serializes the whole document to memory: the document-level undo
    /// snapshot. Page deletes and rotations restructure the PDF in ways no
    /// per-object inverse can express.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern ByteBuffer snapshot_document(ulong docHandle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void free_byte_buffer(ByteBuffer buffer);

    /// <summary>
    /// Reopens a snapshot. The bytes are copied natively, so the same managed
    /// array can be restored repeatedly (undo, redo, undo again).
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong open_document_from_bytes([In] byte[] data, nuint len);

    /// <summary>
    /// Flattens annotations into page content. Must be followed by a save to
    /// a NEW path and then a reload of the document, or a second save
    /// re-burns the same marks on top of the already-burned ones.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int burn_annotations(
        ulong docHandle,
        int captureWidth,
        [In] BurnRect[]? rects,
        nuint rectCount,
        [In] BurnStroke[]? strokes,
        nuint strokeCount,
        [In] BurnPoint[]? points,
        nuint pointCount);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fill_text_field(
        ulong docHandle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string fieldName,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
}
