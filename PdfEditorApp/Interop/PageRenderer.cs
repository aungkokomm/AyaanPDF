using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;

namespace PdfEditorApp.Interop;

internal enum PageRenderOutcome
{
    /// <summary>The real page, rendered by PDFium (fresh or served from render_core's tile cache).</summary>
    RealPage,
    /// <summary>PDFium unavailable or the page/document failed to load — this is the checkerboard fallback.</summary>
    Placeholder,
    /// <summary>A high-res request is still in flight; keep polling.</summary>
    Pending,
    /// <summary>A newer request for the same page superseded this one — nothing will ever arrive for it.</summary>
    Cancelled,
    /// <summary>No bitmap was produced and it isn't a normal pending/cancelled state (invalid input, panic, or an unknown request id).</summary>
    Failed,
}

/// <summary>
/// Pixel data with no WinUI types attached, so it can be produced on a
/// background thread. <see cref="WriteableBitmap"/> has UI-thread affinity
/// and cannot be constructed off-thread, which is why rendering is split into
/// this raw phase plus a cheap <see cref="PageRenderer.ToBitmap"/> step.
/// </summary>
internal readonly record struct RawPageRender(int Width, int Height, byte[]? Bgra, PageRenderOutcome Outcome);

internal readonly record struct PageRenderResult(WriteableBitmap? Bitmap, PageRenderOutcome Outcome);

/// <summary>
/// Thin wrapper around the render_core native calls: copies returned BGRA8
/// buffers into WriteableBitmaps and always frees native memory, even if the
/// managed copy throws.
/// </summary>
internal static class PageRenderer
{
    // ---------------- Raw phase: safe to call from any thread ----------------

    public static RawPageRender RenderLowResRaw(ulong docHandle, int pageIndex, int targetWidth) =>
        ToRaw(RenderCoreNative.render_low_res(docHandle, pageIndex, targetWidth));

    public static RawPageRender PollHighResRaw(ulong requestId)
    {
        int status = RenderCoreNative.poll_high_res(requestId, out RenderResult result);
        return status switch
        {
            PollStatus.Ready => ToRaw(result),
            PollStatus.Pending => new RawPageRender(0, 0, null, PageRenderOutcome.Pending),
            PollStatus.Cancelled => new RawPageRender(0, 0, null, PageRenderOutcome.Cancelled),
            _ => new RawPageRender(0, 0, null, PageRenderOutcome.Failed),
        };
    }

    private static RawPageRender ToRaw(RenderResult result)
    {
        try
        {
            if (result.Buffer == IntPtr.Zero || result.Width <= 0 || result.Height <= 0)
            {
                return new RawPageRender(0, 0, null, PageRenderOutcome.Failed);
            }

            var managed = new byte[(int)result.Len];
            Marshal.Copy(result.Buffer, managed, 0, managed.Length);

            var outcome = result.Status == RenderStatus.OkPdfium ? PageRenderOutcome.RealPage : PageRenderOutcome.Placeholder;
            return new RawPageRender(result.Width, result.Height, managed, outcome);
        }
        finally
        {
            RenderCoreNative.free_render_result(result);
        }
    }

    // ---------------- Bitmap phase: UI thread only ----------------

    /// <summary>Wraps raw pixels in a WriteableBitmap. Must run on the UI thread.</summary>
    public static PageRenderResult ToBitmap(RawPageRender raw)
    {
        if (raw.Bgra is null || raw.Width <= 0 || raw.Height <= 0)
        {
            return new PageRenderResult(null, raw.Outcome);
        }

        var bitmap = new WriteableBitmap(raw.Width, raw.Height);
        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(raw.Bgra, 0, raw.Bgra.Length);
        }

        bitmap.Invalidate();
        return new PageRenderResult(bitmap, raw.Outcome);
    }

    // ---------------- Convenience: raw + bitmap in one call (UI thread) ----------------

    public static PageRenderResult RenderLowRes(ulong docHandle, int pageIndex, int targetWidth) =>
        ToBitmap(RenderLowResRaw(docHandle, pageIndex, targetWidth));

    public static PageRenderResult PollHighRes(ulong requestId) => ToBitmap(PollHighResRaw(requestId));
}
