using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PdfEditorApp.Interop;

/// <summary>One annotation already present in an opened document.</summary>
/// <param name="Index">Position on its page, and the handle for edits and deletes.</param>
/// <param name="Subtype">One of <see cref="AnnotSubtype"/>.</param>
internal readonly record struct ExistingAnnotation(
    int PageIndex,
    int Index,
    int Subtype,
    double Left,
    double Top,
    double Right,
    double Bottom,
    int Color,
    double Opacity);

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

            for (int i = 0; i < count; i++)
            {
                var native = Marshal.PtrToStructure<AnnotationInfo>(array.Items + i * structSize);
                result.Add(new ExistingAnnotation(
                    pageIndex,
                    native.Index,
                    native.Subtype,
                    native.Left,
                    native.Top,
                    native.Right,
                    native.Bottom,
                    native.Color,
                    native.Opacity));
            }

            return result;
        }
        finally
        {
            RenderCoreNative.free_annotation_array(array);
        }
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
