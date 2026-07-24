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

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fill_text_field(
        ulong docHandle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string fieldName,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
}
