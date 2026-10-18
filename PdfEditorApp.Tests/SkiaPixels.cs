using System;
using SkiaSharp;

namespace PdfEditorApp.Tests;

/// <summary>
/// A bitmap's pixels for a test to read.
/// </summary>
/// <remarks>
/// ⚠️ A COPY, NEVER <c>bitmap.GetPixelSpan()</c> ITSELF. That span points into
/// memory Skia owns, and nothing in it keeps the bitmap alive: once the test's
/// last mention of the bitmap is the call that took the span, the garbage
/// collector may finalize it, Skia frees the pixels, and the next read is an
/// access violation that kills the whole test host. It struck only when a
/// collection landed inside the loop, so the suite aborted now and then,
/// mid-run, with "Test host process crashed" and a Windows error box,
/// measured in <c>ShadowCasterTests.one_tone_throughout_and_not_two</c>.
/// </remarks>
internal static class SkiaPixels
{
    public static byte[] Of(SKBitmap bitmap)
    {
        byte[] copy = bitmap.GetPixelSpan().ToArray();
        GC.KeepAlive(bitmap);
        return copy;
    }
}
