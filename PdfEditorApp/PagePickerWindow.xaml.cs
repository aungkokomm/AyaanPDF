using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using PdfEditorApp.Interop;
using PdfEditorApp.ViewModels;
using PdfEditorApp.Viewport;

namespace PdfEditorApp;

/// <summary>
/// Shows every page of one PDF as a thumbnail, with a large preview, so the
/// pages wanted can be chosen by looking at them rather than by number.
/// </summary>
/// <remarks>
/// ⚠️ NOTHING HERE READS EVERY PAGE. The grid holds one small object per page
/// and draws only the thumbnails a container is showing; the file's page sizes
/// are never asked for, because that one whole-document read is what froze
/// scrolling for 40 seconds on a large book.
/// </remarks>
public sealed partial class PagePickerWindow : Window
{
    private const int AfterCurrentPage = 0;
    private const int AtStart = 1;
    private const int AtEnd = 2;
    private const int AfterPageNumber = 3;

    private readonly SourceDocument _source;
    private readonly bool _ownsSource;
    private readonly ViewportViewModel? _insertInto;
    private readonly Action<IReadOnlyList<int>>? _onChosen;
    private readonly int _currentPageAtOpen;
    private readonly IReadOnlyList<int> _initial;
    private readonly PickerPage[] _pages;

    /// <summary>
    /// Set while the grid is being selected from code, so each of the many
    /// SelectRange calls that raise SelectionChanged does not re-read and
    /// re-write the whole selection.
    /// </summary>
    private bool _syncing;
    private bool _closed;
    private int _previewRequest;

    private PagePickerWindow(
        SourceDocument source, bool ownsSource, ViewportViewModel? insertInto, IReadOnlyList<int> initial,
        Action<IReadOnlyList<int>>? onChosen)
    {
        InitializeComponent();

        _source = source;
        _ownsSource = ownsSource;
        _insertInto = insertInto;
        _initial = initial;
        _onChosen = onChosen;

        AppWindow.SetIcon("Assets/AppIcon.ico");

        // Sized to the screen it opens on. A fixed 1100 by 760 is taller than a
        // small laptop's work area once the taskbar is counted, which puts the
        // bottom of the window, and its buttons, off the screen.
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        int width = Math.Min(1400, (int)(work.Width * 0.9));
        int height = (int)(work.Height * 0.9);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, width, height));

        Theming.Apply(this, SettingsStore.Current.Theme);

        Title = insertInto is null ? $"Choose pages from {source.Name}" : $"Insert pages from {source.Name}";
        ConfirmButton.Content = insertInto is null ? "Use these pages" : "Insert pages";

        _pages = Enumerable.Range(0, source.PageCount).Select(i => new PickerPage(i)).ToArray();
        PageGrid.ItemsSource = _pages;

        if (insertInto is null)
        {
            PositionPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            // The current page as it was when the window opened, so the label
            // and where the pages go can never disagree.
            _currentPageAtOpen = insertInto.CurrentPageIndex;
            PositionChoice.Items.Add($"After page {_currentPageAtOpen + 1}, the page you're on");
            PositionChoice.Items.Add("At the start, before page 1");
            PositionChoice.Items.Add($"At the end, after page {insertInto.PageCount}");
            PositionChoice.Items.Add("After a page number");
            PositionChoice.SelectedIndex = AfterCurrentPage;
            AfterPageBox.Maximum = insertInto.PageCount;
            AfterPageBox.Value = _currentPageAtOpen + 1;
        }

        PageGrid.Loaded += (_, _) => SelectPages(_initial, scrollToFirst: false);

        Closed += (_, _) =>
        {
            _closed = true;
            if (_ownsSource)
            {
                _source.Dispose();
            }
        };
    }

    /// <summary>
    /// Page > Insert > From file. Every page starts chosen, which is what
    /// inserting a file used to do; the window owns the file and closes it.
    /// </summary>
    internal static PagePickerWindow ForInsert(SourceDocument source, ViewportViewModel into) =>
        new(source, ownsSource: true, into, Enumerable.Range(0, source.PageCount).ToArray(), onChosen: null);

    /// <summary>
    /// Merge files' "Choose pages": the same window, answering with the pages
    /// chosen instead of inserting them. The file belongs to the merge, which
    /// closes it.
    /// </summary>
    internal static PagePickerWindow ForChoosing(SourceDocument source, IReadOnlyList<int> chosen, Action<IReadOnlyList<int>> onChosen) =>
        new(source, ownsSource: false, insertInto: null, chosen, onChosen);

    // ---------------- Thumbnails ----------------

    private void PageGrid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not PickerPage page)
        {
            return;
        }

        if (args.InRecycleQueue)
        {
            page.IsRealized = false;
            page.Bitmap = null;
            return;
        }

        page.IsRealized = true;
        if (page.Bitmap is null && !page.IsRendering)
        {
            RenderThumbnail(page);
        }
    }

    private async void RenderThumbnail(PickerPage page)
    {
        page.IsRendering = true;
        ulong handle = _source.Handle;
        int width = ThumbnailPixelWidth();

        var raw = await Task.Run(() => page.IsRealized
            ? PageRenderer.RenderLowResRaw(handle, page.Index, width)
            : default);

        page.IsRendering = false;
        if (_closed || !page.IsRealized || raw.Bgra is null)
        {
            return;
        }

        page.Bitmap = PageRenderer.ToBitmap(raw).Bitmap;
    }

    private int ThumbnailPixelWidth() =>
        (int)Math.Clamp(ThumbnailSize.Value * (Root.XamlRoot?.RasterizationScale ?? 1.0), 80, 600);

    private void ThumbnailSize_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        // Raised once while the tree is still being built.
        if (PageGrid?.ItemsPanelRoot is ItemsWrapGrid wrap)
        {
            wrap.ItemWidth = e.NewValue + 16;
            wrap.ItemHeight = e.NewValue * 1.3 + 34;
        }
    }

    // ---------------- Choosing ----------------

    private void SelectAll_Click(object sender, RoutedEventArgs e) =>
        SelectPages(Enumerable.Range(0, _source.PageCount).ToArray(), scrollToFirst: true);

    private void SelectNone_Click(object sender, RoutedEventArgs e) =>
        SelectPages(Array.Empty<int>(), scrollToFirst: false);

    private void SelectOdd_Click(object sender, RoutedEventArgs e) =>
        SelectPages(PageSelection.OddPages(_source.PageCount), scrollToFirst: true);

    private void SelectEven_Click(object sender, RoutedEventArgs e) =>
        SelectPages(PageSelection.EvenPages(_source.PageCount), scrollToFirst: true);

    /// <summary>
    /// A click in the grid: the typed list is rewritten to match, and a single
    /// page clicked is shown large.
    /// </summary>
    private void PageGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        if (e.AddedItems.Count == 1 && e.AddedItems[0] is PickerPage added)
        {
            ShowPreview(added.Index);
        }

        var chosen = ChosenInGrid();
        PagesBox.Text = PageSelection.Format(chosen, _source.PageCount);
        PagesError.Visibility = Visibility.Collapsed;
        UpdateCount(chosen.Count);
    }

    /// <summary>Selects exactly these pages, a run of neighbours at a time.</summary>
    private void SelectPages(IReadOnlyList<int> indices, bool scrollToFirst)
    {
        _syncing = true;
        try
        {
            if (_pages.Length > 0)
            {
                PageGrid.DeselectRange(new ItemIndexRange(0, (uint)_pages.Length));
            }

            foreach (var (first, count) in PageSelection.Runs(indices))
            {
                PageGrid.SelectRange(new ItemIndexRange(first, (uint)count));
            }
        }
        finally
        {
            _syncing = false;
        }

        PagesBox.Text = PageSelection.Format(indices, _source.PageCount);
        PagesError.Visibility = Visibility.Collapsed;
        UpdateCount(indices.Count);

        if (indices.Count > 0)
        {
            ShowPreview(indices[0]);
            if (scrollToFirst)
            {
                PageGrid.ScrollIntoView(_pages[indices[0]]);
            }
        }
    }

    private List<int> ChosenInGrid()
    {
        var chosen = new List<int>();
        foreach (var range in PageGrid.SelectedRanges)
        {
            for (int i = range.FirstIndex; i <= range.LastIndex; i++)
            {
                chosen.Add(i);
            }
        }

        chosen.Sort();
        return chosen;
    }

    private void PagesBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            ApplyTypedPages();
        }
    }

    private void PagesBox_LostFocus(object sender, RoutedEventArgs e) => ApplyTypedPages();

    /// <summary>Selects what was typed, or says what is wrong with it.</summary>
    private void ApplyTypedPages()
    {
        if (!PageSelection.TryParse(PagesBox.Text, _source.PageCount, out var indices, out var error))
        {
            ShowError(error ?? "Those pages couldn't be read.");
            return;
        }

        if (indices.SequenceEqual(ChosenInGrid()))
        {
            PagesError.Visibility = Visibility.Collapsed;
            return;
        }

        SelectPages(indices, scrollToFirst: true);
    }

    private void UpdateCount(int count) =>
        SelectedCount.Text = count switch
        {
            0 => "No pages selected",
            1 => "1 page selected",
            _ => $"{count:N0} pages selected",
        };

    private void ShowError(string message)
    {
        PagesError.Text = message;
        PagesError.Visibility = Visibility.Visible;
    }

    // ---------------- Preview ----------------

    private async void ShowPreview(int index)
    {
        int request = ++_previewRequest;
        PreviewCaption.Text = $"Page {index + 1} of {_source.PageCount}";

        ulong handle = _source.Handle;
        double scale = Root.XamlRoot?.RasterizationScale ?? 1.0;
        int width = (int)Math.Clamp(PreviewHost.ActualWidth * scale, 300, 1600);

        var raw = await Task.Run(() => PageRenderer.RenderLowResRaw(handle, index, width));
        if (_closed || request != _previewRequest || raw.Bgra is null)
        {
            return;
        }

        PreviewImage.Source = PageRenderer.ToBitmap(raw).Bitmap;
    }

    // ---------------- The answer ----------------

    private void PositionChoice_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        AfterPageBox.Visibility = PositionChoice.SelectedIndex == AfterPageNumber ? Visibility.Visible : Visibility.Collapsed;

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        // The typed list is the answer: every click in the grid rewrites it.
        if (!PageSelection.TryParse(PagesBox.Text, _source.PageCount, out var indices, out var error))
        {
            ShowError(error ?? "Those pages couldn't be read.");
            return;
        }

        if (indices.Count == 0)
        {
            ShowError("Choose at least one page.");
            return;
        }

        if (_insertInto is { } into)
        {
            int at = PositionChoice.SelectedIndex switch
            {
                AtStart => 0,
                AtEnd => into.PageCount,
                AfterPageNumber when !double.IsNaN(AfterPageBox.Value) => (int)Math.Clamp(AfterPageBox.Value, 1, into.PageCount),
                _ => _currentPageAtOpen + 1,
            };

            Diag.Log($"insert pages: {indices.Count} of {_source.PageCount} from \"{_source.Name}\", choice {PositionChoice.SelectedIndex}, at index {at}");
            if (!into.InsertPagesFromDocument(_source.Handle, indices, at))
            {
                ShowError("Couldn't insert those pages.");
                return;
            }
        }
        else
        {
            _onChosen?.Invoke(indices);
        }

        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            Close();
        }
    }
}
