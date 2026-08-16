using System.Collections.Generic;
using System.Runtime.InteropServices;
using PdfEditorApp.Viewport;

namespace PdfEditorApp.Interop;

/// <summary>
/// Marshals render_core::get_page_text_runs into plain
/// <see cref="StyledRun"/>s, always freeing the native buffer.
/// </summary>
internal static class StyledRunLoader
{
    /// <summary>
    /// The longest run that can be a heading.
    ///
    /// Applied here rather than in the core, which stays a straight report of
    /// what the page contains. This is the FEATURE's rule: a bookmark title is
    /// short, and a run of three hundred characters is a paragraph. Dropping
    /// them keeps the style list readable, since body text otherwise dominates
    /// it, and keeps a whole book's worth of runs to a size worth holding.
    /// </summary>
    public const int LongestHeading = 300;

    public static StyledRunPage Load(ulong docHandle, int pageIndex)
    {
        var buffer = RenderCoreNative.get_page_text_runs(docHandle, pageIndex);
        try
        {
            if (buffer.Status != RenderStatus.OkPdfium || buffer.Data == System.IntPtr.Zero || buffer.Len == 0)
            {
                return StyledRunPage.Empty(pageIndex);
            }

            byte[] bytes = new byte[(int)buffer.Len];
            Marshal.Copy(buffer.Data, bytes, 0, bytes.Length);
            var page = StyledRunReader.Parse(bytes, pageIndex);

            // Lines first, THEN the length cap and the repair. A document that
            // sets every word as its own text object gives one run per word,
            // and a bookmark made from one of those is three characters long;
            // capping before joining would also measure the wrong thing, since
            // it is the finished line that is or is not too long for a title.
            var runs = new List<StyledRun>();
            foreach (var run in StyledRunMerge.JoinLines(page.Runs))
            {
                if (run.Text.Length > LongestHeading)
                {
                    continue;
                }

                // Some documents record their text in the order the glyphs are
                // PAINTED. In Devanagari that puts the short-i sign before the
                // consonant it belongs to, so a heading arrives as nonsense and
                // makes a bookmark nobody can read. Costs nothing on text that
                // does not need it, which is nearly all of it.
                runs.Add(run with { Text = DevanagariText.Repair(run.Text) });
            }

            // The page's own character count is kept whole. It is what says
            // whether this page HAD text, which is the difference between a
            // scan and a document nothing can read, and dropping paragraphs
            // must not change that answer.
            return page with { Runs = runs };
        }
        finally
        {
            RenderCoreNative.free_byte_buffer(buffer);
        }
    }
}
