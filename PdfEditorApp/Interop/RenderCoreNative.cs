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

    /// <summary>
    /// Which mark: 0 highlight, 1 underline, 2 strikeout. Mirrors the core's
    /// MARKUP_ constants.
    ///
    /// ⚠️ A FIELD ADDED HERE AND NOT IN RUST, OR THE OTHER WAY ROUND, IS SILENT
    /// CORRUPTION. Neither compiler can see across the boundary, and every
    /// colour and count after the divergence would be read from the wrong
    /// offset. Both languages assert the same size, twenty bytes, so the
    /// mistake fails a test instead of a document.
    /// </summary>
    public uint Kind;

    /// <summary>What this measures, asserted against the core's own constant
    /// by the interop tests.</summary>
    public const int Bytes = 20;
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
    public const int Link = 10;
    public const int Widget = 11;
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
    /// <summary>
    /// Clockwise rotation about the shape's centre, in degrees on screen.
    /// APPENDED after WidthPx so a zero-init struct (from callers that don't
    /// set this) still renders unrotated; the C ABI stays additive.
    /// </summary>
    public float RotationDeg;
    /// <summary>
    /// Fill colour for rectangles and ellipses as 0xAARRGGBB. Zero means
    /// stroke only, which was the original shape behaviour. Non-zero puts a
    /// solid fill (with alpha) BEHIND the stroke; lines and arrows ignore it.
    /// APPENDED after RotationDeg for the same additive-ABI reason.
    /// </summary>
    public uint FillRgba;
    /// <summary>
    /// Corner radius for <see cref="ShapeKind.RoundedRectangle"/>, in capture
    /// pixels (the same space as <see cref="WidthPx"/>). Ignored by every other
    /// kind. APPENDED after FillRgba for the same additive-ABI reason.
    /// </summary>
    public float CornerRadiusPx;
    /// <summary>
    /// The shape's EFFECTS, as text, in the tag's own tail format: one
    /// self-describing <c>kind(key=value,...)</c> field per effect, joined by
    /// colons. <see cref="IntPtr.Zero"/> or a length of zero is a shape with no
    /// effects, which is what a zero-init struct gets.
    ///
    /// ONE FIELD RATHER THAN A PAIR PER EFFECT. The core does not model effects
    /// any more: it carries this through to the tag, reserves the room it asks
    /// for, and draws the one effect a PDF can express as paths. Adding an
    /// effect costs nothing here or there.
    ///
    /// Lengths inside are in capture pixels like <see cref="WidthPx"/> and
    /// unlike the points the tag stores; the core converts on the way through.
    /// The bytes must stay alive for the duration of the call, which is what
    /// <see cref="NativeEffects"/> is for.
    ///
    /// APPENDED for the same additive-ABI reason as everything above.
    /// </summary>
    public IntPtr EffectsUtf8;
    public nuint EffectsLen;
}

/// <summary>
/// One effects string, as unmanaged UTF-8 bytes for the length of a call.
///
/// <see cref="NativeShapeSpec"/> is an array crossing the ABI, so its effects
/// cannot be a managed string: each element needs its own pointer, alive until
/// the call returns. Allocated here and freed by the caller in a finally.
/// </summary>
internal static class NativeEffects
{
    /// <summary>Copies the text to unmanaged memory, or zero for none.</summary>
    public static IntPtr Alloc(string? text, out nuint length)
    {
        if (string.IsNullOrEmpty(text))
        {
            length = 0;

            return IntPtr.Zero;
        }

        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
        IntPtr buffer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, buffer, bytes.Length);
        length = (nuint)bytes.Length;

        return buffer;
    }

    /// <summary>Releases what <see cref="Alloc"/> returned. Zero is a no-op.</summary>
    public static void Free(IntPtr buffer)
    {
        if (buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}

/// <summary>
/// Adding shapes whose effects live in managed strings.
///
/// The core BORROWS the effects bytes for the length of the call, so they have
/// to be unmanaged and they have to be freed afterwards. Wrapped here rather
/// than at each call site: four places add shapes, and a missed free in any one
/// of them is a leak nobody would notice.
/// </summary>
internal static class NativeShapes
{
    /// <summary>Adds several shapes, each with its own effects text.</summary>
    public static int Add(
        ulong docHandle, int captureWidth, NativeShapeSpec[] specs, string?[] effects)
    {
        var buffers = new IntPtr[specs.Length];

        try
        {
            for (int at = 0; at < specs.Length; at++)
            {
                buffers[at] = NativeEffects.Alloc(
                    at < effects.Length ? effects[at] : null, out nuint length);
                specs[at].EffectsUtf8 = buffers[at];
                specs[at].EffectsLen = length;
            }

            return RenderCoreNative.add_shape_annotations(
                docHandle, captureWidth, specs, (nuint)specs.Length);
        }
        finally
        {
            foreach (IntPtr buffer in buffers)
            {
                NativeEffects.Free(buffer);
            }
        }
    }

    /// <summary>Adds one shape with its effects text.</summary>
    public static int Add(
        ulong docHandle, int captureWidth, NativeShapeSpec spec, string? effects) =>
        Add(docHandle, captureWidth, new[] { spec }, new[] { effects });
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
/// <summary>Mirrors render_core::OpenResult (src/lib.rs).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeOpenResult
{
    /// <summary>Non-zero on success, zero on every failure.</summary>
    public ulong Handle;

    /// <summary>One of <see cref="RenderStatus"/>.</summary>
    public int Status;
}

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

    /// <summary>
    /// A real PDF, but encrypted, and the password given (if any) did not open
    /// it. The one failure the user can do something about, which is why it is
    /// distinct: reported for both "no password yet" and "wrong password",
    /// because PDFium does not tell them apart and the prompt does the same
    /// thing either way.
    /// </summary>
    public const int NeedsPassword = 5;

    /// <summary>
    /// Well formed, but it would not fit on the page. Distinct from
    /// <see cref="Unsupported"/> because it is the one refusal the user can act
    /// on: the same edit with fewer words goes through. Measured before the
    /// check existed, a three-hundred-character replacement was accepted and
    /// left the text four and a half page widths off the paper.
    /// </summary>
    public const int TooWide = 6;
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

    /// <summary>
    /// Opens a document, optionally with a password, and says why not.
    ///
    /// Pass null for the password to try without one. That is also what opens a
    /// document carrying an empty user password, which is the common case where
    /// a PDF restricts printing or editing but not reading.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern NativeOpenResult open_document_protected(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? password);

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
    /// Sets a form field's state: checkbox, radio, combo box or list box.
    ///
    /// ⚠️ THE ONLY WAY THIS CAN BE DONE. A button's /V and a widget's /AS are
    /// PDF NAMES, and PDFium has no call that writes a name into an annotation
    /// dictionary; writing them as strings was measured to destroy the widget's
    /// appearance rather than merely fail. So the core serializes the document,
    /// rewrites it with lopdf and reopens it under the SAME handle. Every
    /// annotation index the app holds is invalid afterwards, exactly as it is
    /// after any other document-scope edit.
    ///
    /// <paramref name="fieldName"/> is the fully qualified name
    /// <see cref="get_form_fields"/> reported. <paramref name="index"/> is the
    /// widget's position within its group for a checkbox or radio, and the
    /// option's position for a choice field. Neither is a page or annotation
    /// index, so neither drifts when the document is rewritten.
    ///
    /// <paramref name="on"/> applies only to a checkbox.
    ///
    /// The field remains a real, interactive form field: nothing is deleted and
    /// nothing is drawn over it.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int set_form_field_state(
        ulong docHandle,
        byte[] fieldNameUtf8,
        nuint fieldNameLen,
        int kind,
        int index,
        int on);

    /// <summary>
    /// The document's own outline, flattened into reading order as a
    /// self-describing byte buffer (depth, page index, title per entry). Parsed
    /// by <see cref="Viewport.BookmarkReader"/>. A document without an outline
    /// is a successful EMPTY result, not an error. Free the result with
    /// <see cref="free_byte_buffer"/>.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern ByteBuffer get_bookmarks(ulong docHandle);

    /// <summary>
    /// Every stretch of text on a page that shares a font, a size and a colour,
    /// as a self-describing byte buffer. Parsed by
    /// <see cref="Viewport.StyledRunReader"/>. Free with
    /// <see cref="free_byte_buffer"/>.
    ///
    /// This is what bookmarking by style needs and
    /// <see cref="get_page_chars"/> cannot answer: that reports where each
    /// character IS, never what it is set in. A page with no text layer is a
    /// successful EMPTY result, not an error.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern ByteBuffer get_page_text_runs(ulong docHandle, int pageIndex);

    /// <summary>
    /// Every TEXT OBJECT in a page's CONTENT stream, as a self-describing byte
    /// buffer. Parsed by <see cref="PageTextObjectLoader"/>. Free with
    /// <see cref="free_byte_buffer"/>.
    ///
    /// The difference from the two calls above is the whole point of it. Both
    /// of those read the TEXT PAGE, which is an extraction of the page's
    /// characters: it can be searched and it can be highlighted, but it cannot
    /// be pointed at or changed. This reads the page's OBJECT GRAPH, where each
    /// entry has a real index PDFium will hand back and can modify in place. A
    /// run is something to find; an object is something to select.
    ///
    /// Text this app authored is excluded: our text boxes are annotations, not
    /// page content, and the invisible searchable runs written for them are
    /// skipped by their mark.
    ///
    /// A page with no text is a successful EMPTY result, not an error.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern ByteBuffer get_page_text_objects(ulong docHandle, int pageIndex);

    /// <summary>
    /// Every WORD on a page, with the objects that draw it.
    ///
    /// The unit the reader works in, and the reason it is not
    /// <see cref="get_page_text_objects"/>: a producer decides for itself where
    /// one text object ends. Measured on real files, Chromium emits ONE OBJECT
    /// PER GLYPH, 565 of them for four short paragraphs, while other producers
    /// emit one per run. Offering objects to a reader would mean offering to
    /// edit one letter at a time on some documents and a whole paragraph at a
    /// time on others.
    ///
    /// Each word carries the reason it cannot be rewritten, if it cannot, so a
    /// word that refuses can still be selected and read and the app can say why.
    ///
    /// A page with no text is a successful EMPTY result, not an error.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern ByteBuffer get_page_word_clusters(ulong docHandle, int pageIndex);

    /// <summary>
    /// Every LINK on a page, as a self-describing byte buffer (annotation index,
    /// kind, four bounds, target page, URI per link). Parsed by
    /// <see cref="Viewport.LinkReader"/>. Free with
    /// <see cref="free_byte_buffer"/>.
    ///
    /// ⚠️ The core finds these by WALKING THE ANNOTATIONS, not through PDFium's
    /// own link collection. That collection indexes the /Annots array rather
    /// than a dense list of links, and on a page holding a stamp and two links
    /// it was measured to report three and to hand back the first one twice.
    ///
    /// Internal and unrecognised links are reported too, so the app can show
    /// them and refuse to retarget them.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern ByteBuffer get_page_links(ulong docHandle, int pageIndex);

    /// <summary>
    /// Adds a URI link over a rectangle in capture coordinates, writing the
    /// annotation index it landed at to <paramref name="outIndex"/>.
    ///
    /// The index cannot be predicted by the caller: PDFium appends, but a page
    /// can already hold anything, so every following call (retarget, move,
    /// delete) needs the one the core actually used.
    ///
    /// The link is marked printable, which is what every real producer sets. It
    /// has no appearance of its own and draws NOTHING; the rectangle is a hit
    /// area and making it visible is the app's job.
    ///
    /// Returns OkPdfium only when the link is in the document afterwards.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int add_uri_link(
        ulong docHandle,
        int pageIndex,
        int captureWidth,
        float left,
        float top,
        float right,
        float bottom,
        byte[] uriUtf8,
        nuint uriLen,
        out int outIndex);

    /// <summary>
    /// Re-points an existing URI link at a different URI.
    ///
    /// Refuses anything that is not a URI link. PDFium has no setter for an
    /// internal destination, so writing a URI action over one would leave the
    /// document holding both and disagreeing with itself.
    ///
    /// Returns OkPdfium when the link now says exactly what was asked,
    /// Unsupported when it is not a URI link, and InvalidInput when the index
    /// does not name a link at all.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int set_uri_link(
        ulong docHandle,
        int pageIndex,
        int index,
        byte[] uriUtf8,
        nuint uriLen);

    /// <summary>
    /// Rewrites one word, or changes nothing at all.
    ///
    /// <paramref name="objects"/> and <paramref name="prefixChars"/> must be
    /// exactly what the word was read with; the core re-derives the page's words
    /// and refuses if they no longer describe one, so a stale selection cannot
    /// edit whatever happens to sit there now.
    ///
    /// ⚠️ BOTH are needed to name a word. A producer that emits one object per
    /// LINE gives every word on it the same object list, so the offset is what
    /// tells them apart.
    ///
    /// <paramref name="fallbackFontPath"/> is used only when the word's own font
    /// cannot spell the replacement, which for a subset font is the common case.
    /// Pass null to refuse rather than substitute.
    ///
    /// Returns OkPdfium when the page now says exactly what was asked,
    /// Unsupported when it refused and left the page as it found it, and
    /// InvalidInput for a request that does not describe a word.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int set_word_cluster_text(
        ulong docHandle,
        int pageIndex,
        uint[] objects,
        nuint objectCount,
        uint prefixChars,
        byte[] newTextUtf8,
        nuint newTextLen,
        byte[]? fallbackFontPathUtf8,
        nuint fallbackFontPathLen);

    /// <summary>
    /// Every visual LINE on a page, as a self-describing byte buffer. Parsed by
    /// <see cref="Viewport.LineReader"/>. Free with
    /// <see cref="free_byte_buffer"/>.
    ///
    /// ⚠️ A line is PDFium's own line, not a group of words that share a
    /// baseline. Grouping by baseline was measured giving 25 groups on a page
    /// with 20 lines: a rotated line shattered into six fragments in reverse
    /// reading order and two columns interleaved.
    ///
    /// Lines that cannot be retyped are reported too, carrying the reason, so
    /// the app can show them and say why.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern ByteBuffer get_page_lines(ulong docHandle, int pageIndex);

    /// <summary>
    /// Replaces one visual line with one string, or changes nothing at all.
    ///
    /// The three identifying arguments must be exactly what the line was read
    /// with; the core re-derives the page's lines and refuses if they no longer
    /// describe one, so a stale selection cannot overwrite whatever sits there
    /// now.
    ///
    /// ⚠️ The whole object RANGE is rewritten, including the whitespace-only
    /// objects producers scatter between words. That is what makes the word
    /// count free: the line becomes one string in one object.
    ///
    /// <paramref name="fallbackFontPathUtf8"/> is used only when the line's own
    /// font cannot spell the replacement, which for a subset font is the common
    /// case. Pass null to refuse rather than substitute.
    ///
    /// Returns OkPdfium when the page now says exactly what was asked, TooWide
    /// when the replacement would leave the page and the page was put back,
    /// Unsupported when it refused and left the page as it found it, and
    /// InvalidInput for a request that does not describe a line.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int set_line_text(
        ulong docHandle,
        int pageIndex,
        uint firstObject,
        uint lastObject,
        uint prefixChars,
        byte[] newTextUtf8,
        nuint newTextLen,
        byte[]? fallbackFontPathUtf8,
        nuint fallbackFontPathLen);

    /// <summary>
    /// Rewrites the searchable text layer on the given pages.
    ///
    /// Our text boxes draw their glyphs inside a stamp annotation, and a page's
    /// text layer is its CONTENT stream, so the words cannot be found by any
    /// reader, ours included. This writes them a second time into the page
    /// content in an invisible render mode: nothing is drawn, the page renders
    /// byte for byte identically, and the words become searchable.
    ///
    /// Takes explicit page indices because asking a page for its annotations
    /// LOADS it, and sweeping all 3352 pages of a book to find the two with
    /// text boxes is the mistake that cost 34 seconds on the form-field path.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sync_text_layer(ulong docHandle, int[] pages, nuint pageCount);

    /// <summary>
    /// Replaces the document's outline, reading <paramref name="srcPath"/> and
    /// writing to <paramref name="dstPath"/>.
    ///
    /// Does NOT go through PDFium and does NOT take a document handle: PDFium
    /// can read bookmarks but has no API to create them, so this works on the
    /// file with a PDF object-graph library instead. The source is left
    /// untouched; the caller swaps the files.
    ///
    /// An empty buffer removes the outline. Returns Ok, InvalidInput,
    /// Unsupported (unparseable or encrypted), or Panic.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int write_outline(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string srcPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string dstPath,
        byte[] data,
        nuint len);

    /// <summary>
    /// Writes every gradient-filled shape's paint into the file as a real PDF
    /// shading, which is the second thing PDFium cannot create.
    ///
    /// File to file, like the outline writer and for the same reason: the
    /// source is left untouched and the caller swaps them.
    ///
    /// <paramref name="written"/> comes back as the number of gradients
    /// written, and when it is ZERO the destination is not created at all, so
    /// an ordinary document pays for a read and nothing else.
    ///
    /// Returns Ok, InvalidInput, Unsupported (unparseable or encrypted), or
    /// Panic.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int write_gradients(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string srcPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string dstPath,
        out int written);

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
    /// Returns the 32-char hex Guid embedded in the annotation's /Contents
    /// ID prefix, or an empty buffer if no prefix is present. The C# side
    /// stamps identity via <see cref="set_annotation_id"/> so that stable
    /// references (groups, selection extras) can survive the delete+re-add
    /// churn of every edit. Free the result with <see cref="free_byte_buffer"/>.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern ByteBuffer get_annotation_id(ulong docHandle, int pageIndex, int index);

    /// <summary>
    /// Turns an image stamp to an ABSOLUTE angle about its own centre, keeping
    /// its picture and its upright size. Delete + re-add underneath, so the
    /// annotation's index changes; the new one is returned.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rotate_stamp_annotation(
        ulong docHandle, int pageIndex, int index, int captureWidth, float degrees, out int newIndex);

    /// <summary>
    /// Stamps a 32-char hex Guid onto the annotation's /Contents. Any existing
    /// ID prefix is replaced; the tag body (AyaanShape:.., AyaanText:..) is
    /// preserved. Call this immediately after every add_*_annotation and every
    /// resize_*_annotation so the identity persists across the churn.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int set_annotation_id(ulong docHandle, int pageIndex, int index, [In] byte[] idHex, nuint idLen);

    /// <summary>
    /// Replaces the tag BODY, preserving any ID prefix. The mirror of
    /// <see cref="set_annotation_id"/>.
    ///
    /// Ink uses it: a stroke's geometry belongs in its tag the way a shape's
    /// does, but the points are only known here, after smoothing, so the add
    /// call cannot write them itself.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int set_annotation_body(ulong docHandle, int pageIndex, int index, [In] byte[] bodyUtf8, nuint bodyLen);

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
    /// A shape's own extent, recovered from the /Rect it is reported at.
    ///
    /// An annotation's rectangle is not its geometry. The writer inflates it by
    /// the stroke pad, and for a ROTATED shape it is the axis-aligned box that
    /// CONTAINS the turned content, larger than the shape in both axes. Every
    /// path that rebuilds a shape from its tag has to undo both first, or the
    /// copy comes back bigger; at 90 degrees it comes back lying the wrong way
    /// round, because the axis-aligned box of a shape on its side is the
    /// upright box with its sides swapped.
    ///
    /// Only the resize path used to know this. Pure geometry, so it takes the
    /// page width rather than a document handle. Bounds go in and come out in
    /// capture-space pixels with a top-left origin.
    ///
    /// Returns <see cref="RenderStatus.Unsupported"/> for a tag that is not a
    /// shape, so the caller can fall back to the rectangle it already had.
    /// </summary>
    /// <summary>
    /// Puts a rasterised shadow into a shape's own annotation, underneath the
    /// shape, or takes it away when <paramref name="bgra"/> is null.
    ///
    /// PDF has no blur, so a soft shadow is drawn by Skia and carried here as
    /// pixels. The box is in capture space, like every other rectangle across
    /// this boundary, and the caller supplies it because the caller is the one
    /// that blurred the thing and knows how far the ink reached.
    ///
    /// REBUILDS the annotation, so the index changes: read the new one out of
    /// <paramref name="outNewIndex"/> rather than reusing the old.
    /// </summary>
    /// <summary>
    /// Whether a shape's annotation already carries its rasterised shadow: 1
    /// yes, 0 no, negative for a mark that cannot be read.
    ///
    /// Asked before drawing one, because attaching rebuilds the annotation and
    /// a rebuild changes its index.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int shape_has_shadow_image(ulong docHandle, int pageIndex, int index);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int set_shape_shadow_image(
        ulong docHandle,
        int pageIndex,
        int index,
        int captureWidth,
        float left,
        float top,
        float right,
        float bottom,
        [In] byte[]? bgra,
        nuint byteLen,
        int pixelWidth,
        int pixelHeight,
        out int outNewIndex);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int shape_upright_bounds(
        int captureWidth,
        float pageWidthPts,
        [In] byte[] tagUtf8,
        nuint tagLen,
        float left,
        float top,
        float right,
        float bottom,
        out float uprightLeft,
        out float uprightTop,
        out float uprightRight,
        out float uprightBottom);

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
    /// Turns one of our shapes to a new absolute angle (clockwise degrees on
    /// screen) about its centre, keeping its kind, colour, width and bounds.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rotate_shape_annotation(
        ulong docHandle,
        int pageIndex,
        int index,
        int captureWidth,
        float degrees,
        out int newIndex);

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

    /// <summary>
    /// Sets or clears the fill colour on one of our rectangle/ellipse shapes.
    /// Zero clears the fill (shape becomes stroke-only); non-zero fills it with
    /// the given 0xAARRGGBB. Lines and arrows ignore fill visually but round-
    /// trip the value on their tag.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int restyle_shape_fill_annotation(
        ulong docHandle,
        int pageIndex,
        int index,
        int captureWidth,
        uint fillRgba,
        out int newIndex);

    /// <summary>
    /// Sets, changes or clears the DROP SHADOW on one of our shapes, leaving
    /// Sets, changes or clears ALL of a shape's EFFECTS, leaving its position,
    /// size, rotation, colour, width, fill and corners alone.
    ///
    /// THE WHOLE LIST IS REPLACED, not merged: a caller changing one effect
    /// sends all of them, and an EMPTY string clears the lot. One entry point
    /// for every effect, which is why there is no second one to add when an
    /// effect is.
    ///
    /// Lengths inside the text are in capture pixels, like the corner radius
    /// and unlike the points the tag stores.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int restyle_shape_effects_annotation(
        ulong docHandle,
        int pageIndex,
        int index,
        int captureWidth,
        [In] byte[]? effectsUtf8,
        nuint effectsLen,
        out int newIndex);

    /// <summary>
    /// Sets a rounded rectangle's corner radius, in capture pixels, leaving its
    /// position, size, colour, width and fill alone. A negative or non-finite
    /// radius is refused rather than coerced to zero, since zero is itself a
    /// meaningful value here.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int restyle_shape_radius_annotation(
        ulong docHandle,
        int pageIndex,
        int index,
        int captureWidth,
        float radiusPx,
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

    /// <summary>
    /// Rebuilds a STAMP at the bounds given so that it lands at the end of the
    /// page's annotation list, which is the top of the paint order.
    ///
    /// Not interchangeable with <see cref="resize_annotation"/>: that one writes
    /// the bounds in place when it can, which for a same-size call succeeds and
    /// leaves the stamp exactly where it was in the list. Reordering needs the
    /// rebuild, so this entry point skips the in-place attempt. Anything that is
    /// not a stamp comes back Unsupported.
    /// </summary>
    /// <summary>
    /// Moves or resizes a shape whose new bounds are in /Rect space, that is,
    /// INCLUDING the stroke pad the writer adds.
    ///
    /// This is what the app has: it reads an annotation's reported rectangle,
    /// shifts it by the drag delta and hands it back. Use this rather than
    /// <see cref="resize_shape_annotation"/>, which treats its bounds as the
    /// UNPADDED extent and so inflates the shape a little on every move.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int move_shape_annotation(
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
    public static extern int raise_stamp_annotation(
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
    /// Records which group an annotation belongs to, as 32 lowercase hex chars.
    /// A null/zero-length value clears it.
    ///
    /// A key of its own rather than a tag field: a group can hold shapes, text
    /// boxes and stamps together, and those have entirely different tag
    /// formats, so no single tag field could carry it.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int set_annotation_group_id(
        ulong docHandle,
        int pageIndex,
        int index,
        [In] byte[]? groupUtf8,
        nuint groupLen);

    /// <summary>The annotation's group id, or an empty buffer when it is in no
    /// group. Release with <see cref="free_byte_buffer"/>.</summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern ByteBuffer get_annotation_group_id(ulong docHandle, int pageIndex, int index);

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
