using System.Collections.Generic;

namespace PdfEditorApp.Viewport;

/// <summary>
/// Picks the annotation under a point.
///
/// Annotations are drawn in creation order, so the LAST one added is on top
/// and must be the one a click selects. Searching back to front is what makes
/// picking agree with what the user can actually see; front to back would
/// select whatever happens to be underneath.
/// </summary>
public static class AnnotationHitTester
{
    /// <summary>
    /// Default pick tolerance in normalized units. Roughly a few pixels at a
    /// typical page width, so thin ink and rect edges stay clickable without
    /// the pointer having to be exact.
    /// </summary>
    public const double DefaultTolerance = 0.004;

    /// <summary>
    /// The topmost annotation on <paramref name="page"/> under the given
    /// normalized page-local point, or null.
    /// </summary>
    public static IAnnotation? HitTest(
        IReadOnlyList<IAnnotation> annotations,
        int page,
        double x,
        double y,
        double tolerance = DefaultTolerance)
    {
        for (int i = annotations.Count - 1; i >= 0; i--)
        {
            var a = annotations[i];
            if (a.PageIndex == page && a.HitTest(x, y, tolerance))
            {
                return a;
            }
        }

        return null;
    }
}
