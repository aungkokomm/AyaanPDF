using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Printing;
using PdfEditorApp.ViewModels;
using Windows.Graphics.Printing;

namespace PdfEditorApp.Printing;

/// <summary>
/// Prints the open document through the Windows print dialog.
///
/// The app had no Print at all, which for a PDF application is the most
/// conspicuous thing it could be missing.
///
/// Unpackaged WinUI 3 has no window of its own that PrintManager can find, so
/// everything goes through PrintManagerInterop with the HWND. The rest is the
/// standard three-event contract: say how many pages there are, hand back a
/// page when the preview asks for one, then hand over all of them when the user
/// commits.
/// </summary>
internal sealed class DocumentPrinter
{
    /// <summary>
    /// Render width per page, in pixels, for both preview and output.
    ///
    /// About 200 dpi across a Letter page. Print resolution has nothing to do
    /// with what fits on screen, so this deliberately ignores the viewport's
    /// tier budget: rendering at screen density would print visibly soft, and
    /// rendering at 600 dpi would spend seconds per page for detail no printer
    /// keeps from a rasterised page.
    /// </summary>
    private const int PrintWidthPx = 1700;

    private readonly ViewportViewModel _viewModel;

    // Bitmaps are cached, the Image elements around them are not: an element
    // can only have one parent, and the preview and the final pass both want
    // the same page. Re-rendering per request would re-rasterise every page
    // each time the preview redrew.
    private readonly Dictionary<int, WriteableBitmap> _pages = new();

    private PrintManager? _manager;
    private PrintDocument? _document;
    private IPrintDocumentSource? _source;

    public DocumentPrinter(ViewportViewModel viewModel) => _viewModel = viewModel;

    /// <summary>Opens the system print dialog for the open document.</summary>
    public async Task<bool> ShowAsync(nint windowHandle)
    {
        if (_viewModel.PageCount <= 0)
        {
            return false;
        }

        _pages.Clear();
        BuildDocument();

        // Registered per invocation and unhooked first, so repeated prints do
        // not stack handlers and hand the dialog a stale document source.
        _manager ??= PrintManagerInterop.GetForWindow(windowHandle);
        _manager.PrintTaskRequested -= OnPrintTaskRequested;
        _manager.PrintTaskRequested += OnPrintTaskRequested;

        try
        {
            await PrintManagerInterop.ShowPrintUIForWindowAsync(windowHandle);
            return true;
        }
        catch (Exception ex)
        {
            // Most often "another print is already in progress", which is not
            // worth a dialog of its own.
            Diag.Log($"print: ShowPrintUIForWindowAsync failed: {ex.Message}");
            return false;
        }
    }

    private void BuildDocument()
    {
        _document = new PrintDocument();
        _source = _document.DocumentSource;

        _document.Paginate += (_, _) =>
            _document.SetPreviewPageCount(_viewModel.PageCount, PreviewPageCountType.Final);

        // Page numbers here are 1-based; ours are 0-based.
        _document.GetPreviewPage += (_, e) =>
            _document.SetPreviewPage(e.PageNumber, PageElement(e.PageNumber - 1));

        _document.AddPages += (_, _) =>
        {
            for (int i = 0; i < _viewModel.PageCount; i++)
            {
                _document.AddPage(PageElement(i));
            }
            _document.AddPagesComplete();
        };
    }

    private void OnPrintTaskRequested(PrintManager sender, PrintTaskRequestedEventArgs args)
    {
        // The job is named after the document, which is what shows in the print
        // queue. "Ayaan PDF" for every job would be useless with two queued.
        var task = args.Request.CreatePrintTask(
            _viewModel.DocumentTitle,
            request => request.SetSource(_source));

        task.Completed += (_, e) =>
            Diag.Log($"print: job completed with {e.Completion}");
    }

    /// <summary>
    /// The element for one page. A fresh Image every time, over a cached
    /// bitmap, because a UIElement cannot be in two visual trees at once.
    /// </summary>
    private UIElement PageElement(int pageIndex)
    {
        if (!_pages.TryGetValue(pageIndex, out var bitmap))
        {
            bitmap = _viewModel.RenderPageForPrint(pageIndex, PrintWidthPx)!;
            if (bitmap is not null)
            {
                _pages[pageIndex] = bitmap;
            }
        }

        // Uniform, so a page is never cropped or stretched to the paper: the
        // printer's margins and aspect ratio will not match the page's.
        return new Image
        {
            Source = bitmap,
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
        };
    }
}
