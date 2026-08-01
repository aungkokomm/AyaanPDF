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

/// <summary>Mirrors render_core::HighlightQuad (src/lib.rs).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct HighlightQuad
{
    public float Left;
    public float Top;
    public float Right;
    public float Bottom;
}

/// <summary>
/// Mirrors render_core::HighlightSpec (src/lib.rs). One highlight ANNOTATION,
/// indexing however many quads it spans in a shared flat array, because a
/// highlight over several lines has to stay a single object.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct HighlightSpec
{
    public int PageIndex;
    public uint QuadOffset;
    public uint QuadCount;
    public byte R;
    public byte G;
    public byte B;
    public byte A;
}

/// <summary>Mirrors render_core::AnnotationInfo (src/lib.rs).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AnnotationInfo
{
    /// <summary>Position on the page, and the handle for edits and deletes.</summary>
    public int Index;

    /// <summary>One of <see cref="AnnotSubtype"/>.</summary>
    public int Subtype;

    // Top-left origin, both axes divided by the page WIDTH, matching how the
    // app normalizes its own annotations.
    public float Left;
    public float Top;
    public float Right;
    public float Bottom;

    /// <summary>
    /// Always -1. Colour is deliberately never reported: FPDFAnnot_GetColor
    /// access-violates on an annotation that has an appearance stream, and
    /// RENDERING generates appearance streams, so in an app that draws its
    /// pages the query is never safe. The field stays for layout stability.
    /// </summary>
    public int Color;

    public float Opacity;
}

/// <summary>Mirrors render_core::AnnotationArray (src/lib.rs).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AnnotationArray
{
    public IntPtr Items;
    public nuint Len;
    public int Status;
}

/// <summary>Annotation subtypes, matching the ANNOT_* constants in render_core.</summary>
internal static class AnnotSubtype
{
    public const int Other = 0;
    public const int Text = 1;
    public const int Highlight = 2;
    public const int Ink = 3;
    public const int Stamp = 4;
    public const int Square = 5;
    public const int FreeText = 6;
    public const int Underline = 7;
    public const int Strikeout = 8;
    public const int Squiggly = 9;
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
/// Mirrors render_core::ShapeSpec (src/lib.rs).
///
/// Carries the drag's start and end, not a normalized rectangle, because a line
/// and an arrow have direction: an arrow drawn right to left points left, and a
/// box built from min/max would lose that.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeShapeSpec
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

/// <summary>
/// Mirrors render_core::BurnNote (src/lib.rs). Text is a pointer, so the
/// caller must keep it alive for the duration of the call.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct BurnNote
{
    public int PageIndex;
    public float X;
    public float Y;
    public IntPtr Text;
}

/// <summary>Mirrors render_core::{STATUS_*} (src/lib.rs).</summary>
internal static class RenderStatus
{
    public const int OkPdfium = 0;
    public const int InvalidInput = 1;
    public const int Panic = 2;
    public const int OkPlaceholder = 3;

    /// <summary>
    /// Well formed, but PDFium cannot do it. Distinct from
    /// <see cref="InvalidInput"/> so a caller can fall back rather than
    /// report an error: the only case today is scaling a stamp or ink stroke,
    /// which has to be done by deleting and re-adding at the new size.
    /// </summary>
    public const int Unsupported = 4;
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
    /// Every AcroForm field widget in the document, as a self-describing byte
    /// buffer (page index, kind, flags, group index, rect, name, value per
    /// field). Parsed by <see cref="Viewport.FormFieldReader"/>. Free the result
    /// with <see cref="free_byte_buffer"/>.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern ByteBuffer get_form_fields(ulong docHandle);

    /// <summary>
    /// Deletes the widget(s) for a named form field, so PDFium's form layer
    /// stops painting the field box on top of the text the app draws to fill it.
    /// A no-op (still Ok) when no field matches.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int delete_form_field_widget(
        ulong docHandle, [MarshalAs(UnmanagedType.LPUTF8Str)] string fieldName);

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

    /// <summary>
    /// Renders only a rectangle of a page, in normalized top-left-origin
    /// coordinates. Cost tracks the region, so deep zoom stays sharp without
    /// the whole-page bitmap growing quadratically.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern RenderResult render_region(
        ulong docHandle, int pageIndex, float x, float y, float w, float h, int outWidth);

    /// <summary>
    /// One 256x256 tile of a page's level-of-detail pyramid, served from
    /// render_core's byte-budgeted cache when already rendered. This is what
    /// makes panning at deep zoom cheap: tiles still on screen are reused.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern RenderResult render_tile(
        ulong docHandle, int pageIndex, int level, int col, int row);

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
    /// Reads an annotation's /Contents string as UTF-8. Used to recover a text
    /// box's words so it can be re-edited. Free the result with
    /// <see cref="free_byte_buffer"/>.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern ByteBuffer get_annotation_contents(ulong docHandle, int pageIndex, int index);

    /// <summary>
    /// Rebuilds the document from a list of its own page indices: reorder,
    /// duplicate (repeat an index), delete (omit one) or extract (a subset).
    /// The handle is preserved. Pages keep their annotations.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rebuild_page_order(ulong docHandle, [In] int[] indices, nuint count);

    /// <summary>
    /// Inserts every page of another PDF (its bytes) at a position. Returns the
    /// NUMBER of pages inserted, or a NEGATIVE value on error.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int insert_pages_from_bytes(ulong docHandle, [In] byte[] data, nuint len, int atIndex);

    /// <summary>Inserts one blank page, sized in points, at a position.</summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int insert_blank_page(ulong docHandle, int atIndex, float widthPts, float heightPts);

    /// <summary>
    /// Saves the given pages, in order, to a new PDF at <paramref name="path"/>,
    /// leaving this document unchanged.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int extract_pages_to_file(
        ulong docHandle, [In] int[] indices, nuint count,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path);

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

    /// <summary>
    /// Adds notes as real PDF text annotations rather than burning them.
    /// A note's value is its text, and flattening it to a marker graphic
    /// would keep the mark and lose the words.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int add_note_annotations(
        ulong docHandle,
        int captureWidth,
        [In] BurnNote[]? notes,
        nuint noteCount);

    // ---------------- Annotations as editable objects ----------------
    //
    // The counterpart to burning. A burned mark is pixels: reopen the file and
    // it cannot be moved, recoloured or removed, and no other viewer sees it
    // as markup. These write real annotation objects instead.

    /// <summary>
    /// Every annotation on a page. Free the result with
    /// <see cref="free_annotation_array"/>.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern AnnotationArray get_annotations(ulong docHandle, int pageIndex);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void free_annotation_array(AnnotationArray array);

    /// <summary>
    /// Adds highlights as real /Highlight annotations. A highlight spanning
    /// several lines is ONE spec pointing at several quads, so it stays a
    /// single object to select and delete.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int add_highlight_annotations(
        ulong docHandle,
        int captureWidth,
        [In] HighlightSpec[]? specs,
        nuint specCount,
        [In] HighlightQuad[]? quads,
        nuint quadCount);

    /// <summary>
    /// Adds rectangles, ellipses, lines and arrows as real annotations.
    ///
    /// The kind numbers are <see cref="ShapeKind"/> and must match
    /// render_core's SHAPE_* constants.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int add_shape_annotations(
        ulong docHandle,
        int captureWidth,
        [In] NativeShapeSpec[]? specs,
        nuint specCount);

    /// <summary>
    /// Places text as a real, editable text box: vector text inside a stamp
    /// annotation. <paramref name="fontSizePx"/> is in the same capture space
    /// as the rectangle. Lines are split on '\n'.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int add_text_box_annotation(
        ulong docHandle,
        int pageIndex,
        int captureWidth,
        float left,
        float top,
        float right,
        float bottom,
        [In] byte[] textUtf8,
        nuint textLen,
        float fontSizePx,
        byte r,
        byte g,
        byte b,
        byte a);

    /// <summary>
    /// The styled text box: <paramref name="align"/> is one of ALIGN_* (0..3),
    /// <paramref name="fillRgba"/> and <paramref name="outlineRgba"/> are packed
    /// 0xRRGGBBAA with alpha 0 meaning none, and <paramref name="outlineWidthPx"/>
    /// is in capture space.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int add_text_box_annotation_styled(
        ulong docHandle,
        int pageIndex,
        int captureWidth,
        float left,
        float top,
        float right,
        float bottom,
        [In] byte[] textUtf8,
        nuint textLen,
        float fontSizePx,
        byte r,
        byte g,
        byte b,
        byte a,
        int align,
        uint fillRgba,
        uint outlineRgba,
        float outlineWidthPx,
        [In] byte[]? fontPathUtf8,
        nuint fontPathLen,
        int underline,
        int strikethrough);

    /// <summary>Adds freehand strokes as real /Ink annotations, one per stroke.</summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int add_ink_annotations(
        ulong docHandle,
        int captureWidth,
        [In] BurnStroke[]? strokes,
        nuint strokeCount,
        [In] BurnPoint[]? points,
        nuint pointCount);

    /// <summary>
    /// Places an image as a real /Stamp annotation. Pixels must be tightly
    /// packed BGRA: decoding happens here rather than in the core, which keeps
    /// the image crate out of the native binary.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int add_stamp_annotation(
        ulong docHandle,
        int pageIndex,
        int captureWidth,
        float left,
        float top,
        float right,
        float bottom,
        [In] byte[] bgra,
        nuint byteLen,
        int pixelWidth,
        int pixelHeight);

    /// <summary>Removes one annotation by the index <see cref="get_annotations"/> reported.</summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int delete_annotation(ulong docHandle, int pageIndex, int index);

    /// <summary>
    /// Resizes a SHAPE by redrawing it inside a new rectangle, which is only
    /// possible because a shape records what kind it is, in what colour and at
    /// what width. Returns <see cref="RenderStatus.Unsupported"/> for anything
    /// else, so the caller can fall back.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int resize_shape_annotation(
        ulong docHandle,
        int pageIndex,
        int index,
        int captureWidth,
        float left,
        float top,
        float right,
        float bottom,
        out int newIndex);

    /// <summary>
    /// Resizes an annotation, rebuilding it when PDFium will not scale it in
    /// place. Rebuilding reads the image back out of the annotation, so it
    /// works for a stamp the app never placed, including one from a saved file
    /// or made in another editor.
    ///
    /// A rebuilt annotation moves to the END of its page's list, so
    /// <paramref name="newIndex"/> reports where it ended up. For anything
    /// scaled in place it comes back unchanged.
    ///
    /// Returns <see cref="RenderStatus.Unsupported"/> for ink, whose shape is
    /// a path rather than an image and cannot be rebuilt this way.
    /// </summary>
    /// <summary>
    /// Resizes one of our TEXT BOXES by re-laying-out its text at the new bounds:
    /// the words re-wrap to the new width and the box grows to fit, the way a Word
    /// text box behaves, instead of stretching the rendered glyphs. Like a shape,
    /// the rebuilt box moves to the END of the page's list, so
    /// <paramref name="newIndex"/> reports where it landed.
    ///
    /// Returns <see cref="RenderStatus.Unsupported"/> for anything that is not one
    /// of our text boxes, so the caller can fall back to the generic resize.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int resize_text_box_annotation(
        ulong docHandle,
        int pageIndex,
        int index,
        int captureWidth,
        float left,
        float top,
        float right,
        float bottom,
        out int newIndex);

    /// <summary>
    /// Turns one of our text boxes to a new absolute angle (clockwise degrees on
    /// screen) about its centre, re-laying-it-out at its own UPRIGHT bounds (the
    /// rect stored in its tag, not the enlarged bounds a rotated box reports). The
    /// rebuilt box moves to the END of the page's list, so <paramref name="newIndex"/>
    /// reports where it landed. Unsupported for anything not one of our text boxes.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rotate_text_box_annotation(
        ulong docHandle,
        int pageIndex,
        int index,
        int captureWidth,
        float left,
        float top,
        float right,
        float bottom,
        float degrees,
        out int newIndex);

    /// <summary>
    /// Applies a new style to one of our text boxes without moving or turning
    /// it: text colour, alignment, fill, outline, thickness, underline and
    /// strikethrough. Any field the caller does not want to change is left
    /// alone: pass 0 for <paramref name="textRgba"/> to keep the text colour,
    /// -1 for <paramref name="align"/> to keep the alignment, a negative width
    /// to keep the thickness, and -1 for the decoration flags to keep those.
    /// The tag's bounds, rotation, font, size, and words are all preserved.
    /// </summary>
    /// <summary>
    /// Applies a new colour and/or stroke width to one of our shapes, keeping
    /// its bounds and kind. An alpha of 0 in <paramref name="colorRgba"/> keeps
    /// the current colour; a negative <paramref name="widthPx"/> keeps the
    /// current width.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int restyle_shape_annotation(
        ulong docHandle,
        int pageIndex,
        int index,
        int captureWidth,
        uint colorRgba,
        float widthPx,
        out int newIndex);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int restyle_text_box_annotation(
        ulong docHandle,
        int pageIndex,
        int index,
        int captureWidth,
        uint textRgba,
        int align,
        uint fillRgba,
        uint outlineRgba,
        float outlineWidthPx,
        int underline,
        int strikethrough,
        out int newIndex);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int resize_annotation(
        ulong docHandle,
        int pageIndex,
        int index,
        int captureWidth,
        float left,
        float top,
        float right,
        float bottom,
        out int newIndex);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int set_annotation_bounds(
        ulong docHandle,
        int pageIndex,
        int index,
        int captureWidth,
        float left,
        float top,
        float right,
        float bottom);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fill_text_field(
        ulong docHandle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string fieldName,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
}
