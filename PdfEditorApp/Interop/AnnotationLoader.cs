using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace PdfEditorApp.Interop;

/// <summary>One annotation already present in an opened document.</summary>
/// <param name="Index">Position on its page, and the handle for edits and deletes.</param>
/// <param name="Subtype">One of <see cref="AnnotSubtype"/>.</param>
/// <param name="Id">
/// Stable identity that survives the delete+re-add churn of every edit.
/// Populated from the annotation's /Contents ID prefix; a fresh Guid is
/// generated for legacy annotations that don't have one yet. Groups and any
/// other cross-edit references use this instead of (page, index).
/// </param>
internal readonly record struct ExistingAnnotation(
    int PageIndex,
    int Index,
    int Subtype,
    double Left,
    double Top,
    double Right,
    double Bottom,
    int Color,
    double Opacity,
    Guid Id);

/// <summary>
/// Marshals render_core::get_annotations, always freeing the native array.
///
/// This is how the app finally sees markup it did not make itself. Until
/// annotations became objects there was nothing to read: highlights and ink
/// were flattened into page content on save, so a file annotated here and
/// reopened showed pixels, and a file annotated in Acrobat looked untouched.
/// </summary>
internal static class AnnotationLoader
{
    public static List<ExistingAnnotation> Load(ulong docHandle, int pageIndex)
    {
        var result = new List<ExistingAnnotation>();

        AnnotationArray array = RenderCoreNative.get_annotations(docHandle, pageIndex);
        try
        {
            // A page with no annotations is a successful, empty result rather
            // than a failure, since most pages of most documents are exactly
            // that.
            if (array.Status != RenderStatus.OkPdfium || array.Items == System.IntPtr.Zero)
            {
                return result;
            }

            int count = (int)array.Len;
            int structSize = Marshal.SizeOf<AnnotationInfo>();

            // Counted, not logged per annotation. This used to write one line
            // per annotation per load, and a page cache is invalidated after
            // every edit, so a heavily annotated page produced a burst of file
            // opens on each one. The only thing worth knowing here is whether
            // any annotation still lacks a persisted id.
            int unstamped = 0;

            for (int i = 0; i < count; i++)
            {
                var native = Marshal.PtrToStructure<AnnotationInfo>(array.Items + i * structSize);
                var readId = ReadId(docHandle, pageIndex, native.Index);
                var id = readId ?? Guid.NewGuid();
                if (readId is null)
                {
                    unstamped++;
                }
                result.Add(new ExistingAnnotation(
                    pageIndex,
                    native.Index,
                    native.Subtype,
                    native.Left,
                    native.Top,
                    native.Right,
                    native.Bottom,
                    native.Color,
                    native.Opacity,
                    id));
            }

            PdfEditorApp.Diag.Log($"Load p{pageIndex}: {count} annotations, {unstamped} with no persisted id");
            return result;
        }
        finally
        {
            RenderCoreNative.free_annotation_array(array);
        }
    }

    /// <summary>
    /// Returns the Guid embedded in the annotation's /Contents ID prefix, or
    /// null if the annotation has no such prefix (legacy annotation, or one
    /// made by Acrobat or another editor).
    /// </summary>
    public static Guid? ReadId(ulong docHandle, int pageIndex, int index)
    {
        var buffer = RenderCoreNative.get_annotation_id(docHandle, pageIndex, index);
        try
        {
            if (buffer.Status != RenderStatus.OkPdfium || buffer.Data == System.IntPtr.Zero || buffer.Len == 0)
            {
                return null;
            }
            byte[] bytes = new byte[(int)buffer.Len];
            Marshal.Copy(buffer.Data, bytes, 0, bytes.Length);
            string hex = Encoding.UTF8.GetString(bytes);
            // Accept 32-char lowercase hex; anything else is corrupt and we
            // treat it as no ID rather than throw, since a hand-edited PDF is
            // an entirely legal state.
            if (hex.Length != 32 || !IsHex(hex))
            {
                return null;
            }
            return Guid.ParseExact(hex, "N");
        }
        finally
        {
            RenderCoreNative.free_byte_buffer(buffer);
        }
    }

    /// <summary>
    /// Stamps the annotation's /Contents with this Guid as an ID prefix. Call
    /// after every add_*_annotation and every resize_*_annotation so the
    /// identity survives the next delete+re-add churn.
    /// </summary>
    public static void WriteId(ulong docHandle, int pageIndex, int index, Guid id)
    {
        // 32 ASCII hex chars, no dashes; matches ID_HEX_LEN on the Rust side.
        byte[] hex = Encoding.ASCII.GetBytes(id.ToString("N"));
        int status = RenderCoreNative.set_annotation_id(docHandle, pageIndex, index, hex, (nuint)hex.Length);
        // Failures only. This runs on every click and on every write of a move,
        // so logging the successes buries everything else in the file.
        if (status != RenderStatus.OkPdfium)
        {
            PdfEditorApp.Diag.Log($"WriteId p{pageIndex}#{index} id={id:N} FAILED status={status}");
        }
    }

    private static bool IsHex(string s)
    {
        foreach (char c in s)
        {
            bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>
    /// Counts the annotations across a document, for reporting what an opened
    /// file already carries.
    /// </summary>
    public static int CountAll(ulong docHandle, int pageCount)
    {
        int total = 0;
        for (int page = 0; page < pageCount; page++)
        {
            total += Load(docHandle, page).Count;
        }
        return total;
    }
}
