using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using PdfEditorApp.Viewport;
using Tesseract;

namespace PdfEditorApp.Ocr;

/// <summary>A page to read, with its size in points.</summary>
internal readonly record struct OcrPageJob(int Page, double Width, double Height);

/// <summary>
/// What reading one page came to. <see cref="Words"/> is null when the page was
/// skipped or could not be rendered.
/// </summary>
internal readonly record struct OcrPageResult(int Page, IReadOnlyList<OcrWord>? Words, bool Skipped);

/// <summary>
/// Reads pages off the UI thread with the recogniser the plan names, and hands
/// each one back as soon as it is read. Writing the words into the document is
/// left to the caller.
/// </summary>
/// <remarks>
/// Pages come back in the order they finish, not in page order. Tesseract pages
/// run side by side with one engine per worker: on this project's benchmark
/// (4 cores, 8 threads) six workers read twice as fast as one. The Myanmar model
/// gained nothing from parallel pages, so it reads one page at a time and uses
/// several threads inside each.
/// </remarks>
internal static class OcrRunner
{
    public static int TesseractWorkers { get; } = Math.Clamp(Environment.ProcessorCount * 3 / 4, 1, 8);

    public static async IAsyncEnumerable<OcrPageResult> ReadAsync(
        OcrPlan plan,
        ulong docHandle,
        IReadOnlyList<OcrPageJob> pages,
        bool skipPagesWithText,
        [EnumeratorCancellation] CancellationToken token)
    {
        var results = Channel.CreateUnbounded<OcrPageResult>(new UnboundedChannelOptions { SingleReader = true });
        Task reading = Task.Run(async () =>
        {
            try
            {
                await ReadAllAsync(plan, docHandle, pages, skipPagesWithText, results.Writer, token);
                results.Writer.TryComplete();
            }
            catch (AggregateException ex) when (ex.InnerExceptions.Count > 0)
            {
                results.Writer.TryComplete(ex.InnerExceptions[0]);
            }
            catch (Exception ex)
            {
                results.Writer.TryComplete(ex);
            }
        }, CancellationToken.None);

        await foreach (OcrPageResult result in results.Reader.ReadAllAsync(token))
        {
            yield return result;
        }

        await reading;
    }

    private static async Task ReadAllAsync(
        OcrPlan plan,
        ulong doc,
        IReadOnlyList<OcrPageJob> pages,
        bool skipPagesWithText,
        ChannelWriter<OcrPageResult> results,
        CancellationToken token)
    {
        switch (plan.Engine)
        {
            case OcrEngineKind.Windows:
            {
                var windows = WindowsRecognizer.TryCreate("en-US") ?? WindowsRecognizer.TryCreate("en")
                    ?? throw new InvalidOperationException("Windows has no English text recognition installed. Choose Accurate instead.");
                foreach (OcrPageJob job in pages)
                {
                    token.ThrowIfCancellationRequested();
                    if (Prepare(doc, job, skipPagesWithText, results) is { } image)
                    {
                        results.TryWrite(new OcrPageResult(job.Page, OcrWordFilter.WithoutSpecks(await windows.ReadAsync(image)), false));
                    }
                }
                break;
            }

            case OcrEngineKind.Myanmar:
            {
                MyanmarRecognizer? myanmar = null;
                try
                {
                    foreach (OcrPageJob job in pages)
                    {
                        token.ThrowIfCancellationRequested();
                        if (Prepare(doc, job, skipPagesWithText, results) is { } image)
                        {
                            myanmar ??= new MyanmarRecognizer(OcrPageImage.Dpi, Math.Max(1, Environment.ProcessorCount / 2));
                            results.TryWrite(new OcrPageResult(job.Page, myanmar.Read(image), false));
                        }
                    }
                }
                finally
                {
                    myanmar?.Dispose();
                }
                break;
            }

            default:
                Parallel.ForEach(
                    pages,
                    new ParallelOptions { MaxDegreeOfParallelism = TesseractWorkers, CancellationToken = token },
                    // Made on a worker's first page that needs reading, so a run
                    // that skips every page loads no model at all.
                    () => (TesseractRecognizer?)null,
                    (job, _, engine) =>
                    {
                        token.ThrowIfCancellationRequested();
                        if (Prepare(doc, job, skipPagesWithText, results) is { } image)
                        {
                            engine ??= new TesseractRecognizer(plan.TesseractLanguages, OcrPageImage.Dpi);
                            results.TryWrite(new OcrPageResult(job.Page, OcrWordFilter.WithoutSpecks(engine.Read(image, PageSegMode.Auto)), false));
                        }
                        return engine;
                    },
                    engine => engine?.Dispose());
                break;
        }
    }

    /// <summary>
    /// The page's picture, or null when there is nothing to read: the page was
    /// skipped for already having text, or would not render. Either way its
    /// result has already been handed back.
    /// </summary>
    private static GrayImage? Prepare(ulong doc, OcrPageJob job, bool skipPagesWithText, ChannelWriter<OcrPageResult> results)
    {
        if (skipPagesWithText && OcrPageText.HasText(doc, job.Page))
        {
            results.TryWrite(new OcrPageResult(job.Page, null, Skipped: true));
            return null;
        }

        GrayImage? image = OcrPageImage.Render(doc, job.Page, job.Width, job.Height);
        if (image is null)
        {
            results.TryWrite(new OcrPageResult(job.Page, null, Skipped: false));
        }
        return image;
    }
}
