using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PdfEditorApp.Interop;
using PdfEditorApp.ViewModels;
using PdfEditorApp.Viewport;
using Windows.ApplicationModel.DataTransfer;

namespace PdfEditorApp;

/// <summary>
/// Merges PDFs and pictures, in the order listed, into one new PDF that opens
/// in a tab of its own.
/// </summary>
/// <remarks>
/// The files are opened as they are added, so a file that needs a password or
/// can't be read is marked on its row straight away, and the merge refuses to
/// start until each one is unlocked or removed. The new file is built in
/// memory from the chosen pages, written to a temporary name, given its
/// bookmarks, and only then moved to where it was asked to go.
/// </remarks>
public sealed partial class MergeFilesWindow : Window
{
    private readonly ObservableCollection<MergeItem> _items = new();
    private CancellationTokenSource? _merging;
    private bool _closed;

    public MergeFilesWindow()
    {
        InitializeComponent();

        AppWindow.SetIcon("Assets/AppIcon.ico");

        // Sized to the screen it opens on, as the page picker is, so the
        // buttons along the bottom are never below the taskbar.
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        int width = Math.Min(1100, (int)(work.Width * 0.9));
        int height = (int)(work.Height * 0.9);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, width, height));

        Theming.Apply(this, SettingsStore.Current.Theme);

        FileList.ItemsSource = _items;
        _items.CollectionChanged += (_, _) => UpdateSummary();
        UpdateSummary();

        // A merge holds the files open while it writes the new one, so closing
        // the window asks it to stop rather than pulling them out from under it.
        AppWindow.Closing += (_, args) =>
        {
            if (_merging is not null)
            {
                args.Cancel = true;
                _merging.Cancel();
            }
        };

        Closed += (_, _) =>
        {
            _closed = true;
            foreach (var item in _items)
            {
                item.Dispose();
            }
        };
    }

    // ---------------- Adding files ----------------

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add(".pdf");
        foreach (string extension in ImagePages.Extensions)
        {
            picker.FileTypeFilter.Add(extension);
        }

        var files = await picker.PickMultipleFilesAsync();
        AddPaths(files.Select(f => f.Path));
    }

    private void Files_DragOver(object sender, DragEventArgs e)
    {
        if (_merging is null && e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Add to the merge";
            e.DragUIOverride.IsCaptionVisible = true;
            e.Handled = true;
        }
    }

    private async void Files_Drop(object sender, DragEventArgs e)
    {
        // A drag of the list's own rows carries no files, and is the list
        // reordering itself.
        if (_merging is not null || !e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        e.Handled = true;
        var dropped = await e.DataView.GetStorageItemsAsync();
        AddPaths(dropped.OfType<Windows.Storage.StorageFile>().Select(f => f.Path));
    }

    /// <summary>
    /// Adds files to the end of the list and starts reading each one. Anything
    /// that is neither a PDF nor a picture is left out, and said so.
    /// </summary>
    private void AddPaths(IEnumerable<string> paths)
    {
        int skipped = 0;
        foreach (string path in paths)
        {
            bool pdf = Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
            if (!pdf && !ImagePages.IsImage(path))
            {
                skipped++;
                continue;
            }

            var item = new MergeItem(path, pdf ? MergeItemKind.Pdf : MergeItemKind.Picture);
            _items.Add(item);
            _ = ReadAsync(item);
        }

        ShowMessage(skipped == 0
            ? null
            : $"{(skipped == 1 ? "1 file was" : $"{skipped} files were")} left out: only PDFs and pictures (JPG, PNG, BMP, TIFF, GIF) can be merged.");
    }

    /// <summary>Opens a PDF, or reads a picture's size, off the UI thread.</summary>
    private async Task ReadAsync(MergeItem item)
    {
        if (item.Kind == MergeItemKind.Pdf)
        {
            var (document, status) = await Task.Run(() =>
            {
                var opened = SourceDocument.TryOpen(item.Path, null, out int s);
                return (opened, s);
            });

            if (_closed)
            {
                document?.Dispose();
                return;
            }

            if (document is null)
            {
                item.State = status == RenderStatus.NeedsPassword ? MergeItemState.NeedsPassword : MergeItemState.Unreadable;
                UpdateSummary();
                return;
            }

            Attach(item, document);
            return;
        }

        var picture = await ImagePages.ReadInfoAsync(item.Path);
        if (_closed)
        {
            return;
        }

        if (picture is null)
        {
            item.State = MergeItemState.Unreadable;
            UpdateSummary();
            return;
        }

        item.Picture = picture;
        item.PageCount = picture.PageCount;
        item.Detail = $"Picture · {MergeItem.Pages(picture.PageCount)} · {SizeOf(item.Path)}";
        item.State = MergeItemState.Ready;
        UpdateSummary();
        item.Thumbnail = await PictureThumbnailAsync(item.Path);
    }

    /// <summary>A PDF has opened: its row becomes ready, with a thumbnail of its first page.</summary>
    private async void Attach(MergeItem item, SourceDocument document)
    {
        item.Source = document;
        item.PageCount = document.PageCount;
        item.Detail = $"{MergeItem.Pages(document.PageCount)} · {SizeOf(item.Path)}";
        item.State = MergeItemState.Ready;
        UpdateSummary();

        ulong handle = document.Handle;
        var raw = await Task.Run(() => PageRenderer.RenderLowResRaw(handle, 0, 96));
        if (!_closed && raw.Bgra is not null)
        {
            item.Thumbnail = PageRenderer.ToBitmap(raw).Bitmap;
        }
    }

    private static async Task<ImageSource?> PictureThumbnailAsync(string path)
    {
        try
        {
            var bitmap = new BitmapImage { DecodePixelWidth = 72 };
            using var stream = File.OpenRead(path);
            await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string SizeOf(string path)
    {
        try
        {
            return MergeItem.Size(new FileInfo(path).Length);
        }
        catch (Exception)
        {
            return "size unknown";
        }
    }

    // ---------------- Each row ----------------

    private static MergeItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as MergeItem;

    private void ChoosePages_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { Source: { } source } item)
        {
            return;
        }

        IReadOnlyList<int> chosen =
            PageSelection.TryParse(item.PagesText, item.PageCount, out var pages, out _) && pages.Count > 0
                ? pages
                : Enumerable.Range(0, item.PageCount).ToArray();

        PagePickerWindow.ForChoosing(source, chosen, picked =>
        {
            item.PagesText = PageSelection.Format(picked, item.PageCount);
            UpdateSummary();
        }).Activate();
    }

    private async void Unlock_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item)
        {
            return;
        }

        var document = await SourceDocument.UnlockAsync(item.Path, Root.XamlRoot);
        if (document is null)
        {
            return;
        }

        if (_closed)
        {
            document.Dispose();
            return;
        }

        Attach(item, document);
    }

    private void PagesBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Written through here as well as by the binding, so the total below is
        // counted from what is in the box now rather than a keystroke ago.
        if (sender is TextBox { DataContext: MergeItem item } box)
        {
            item.PagesText = box.Text;
        }

        UpdateSummary();
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => Move(sender, -1);

    private void MoveDown_Click(object sender, RoutedEventArgs e) => Move(sender, 1);

    private void Move(object sender, int delta)
    {
        if (ItemOf(sender) is not { } item)
        {
            return;
        }

        int from = _items.IndexOf(item);
        int to = from + delta;
        if (from >= 0 && to >= 0 && to < _items.Count)
        {
            _items.Move(from, to);
        }
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
        {
            _items.Remove(item);
            item.Dispose();
        }
    }

    /// <summary>Puts the files in name order, numbered the way people number them: "Chapter 2" before "Chapter 10".</summary>
    private void SortByName_Click(object sender, RoutedEventArgs e)
    {
        var sorted = _items.OrderBy(i => i.Name, NaturalOrder.Instance).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            int from = _items.IndexOf(sorted[i]);
            if (from != i)
            {
                _items.Move(from, i);
            }
        }
    }

    // ---------------- The totals ----------------

    private void UpdateSummary()
    {
        int pages = 0;
        int attention = 0;
        foreach (var item in _items)
        {
            if (item.HasProblem)
            {
                attention++;
            }
            else if (item.State == MergeItemState.Ready)
            {
                pages += ChosenPageCount(item);
            }
        }

        EmptyHint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SummaryText.Text = _items.Count == 0
            ? "No files yet"
            : $"{(_items.Count == 1 ? "1 file" : $"{_items.Count} files")}, {MergeItem.Pages(pages)}";
        AttentionText.Text = attention switch
        {
            0 => string.Empty,
            1 => " · 1 needs attention",
            _ => $" · {attention} need attention",
        };
    }

    private static int ChosenPageCount(MergeItem item) =>
        item.Kind == MergeItemKind.Picture
            ? item.PageCount
            : PageSelection.TryParse(item.PagesText, item.PageCount, out var pages, out _) ? pages.Count : 0;

    private void ShowMessage(string? message)
    {
        MessageText.Text = message ?? string.Empty;
        MessageText.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------------- Merging ----------------

    private async void Merge_Click(object sender, RoutedEventArgs e)
    {
        ShowMessage(null);

        if (_items.Count == 0)
        {
            ShowMessage("Add some files first.");
            return;
        }

        if (_items.FirstOrDefault(i => i.HasProblem) is { } problem)
        {
            ShowMessage($"“{problem.Name}” needs attention. Unlock it or remove it, then merge.");
            return;
        }

        if (_items.Any(i => i.State == MergeItemState.Reading))
        {
            ShowMessage("Some files are still being read. Try again in a moment.");
            return;
        }

        var plan = new List<(MergeItem Item, IReadOnlyList<int> Pages)>();
        foreach (var item in _items)
        {
            if (item.Kind == MergeItemKind.Picture)
            {
                plan.Add((item, Enumerable.Range(0, item.PageCount).ToArray()));
                continue;
            }

            if (!PageSelection.TryParse(item.PagesText, item.PageCount, out var pages, out var error))
            {
                ShowMessage($"“{item.Name}”: {error}");
                return;
            }

            if (pages.Count == 0)
            {
                ShowMessage($"“{item.Name}” has no pages chosen. Choose some, or remove it.");
                return;
            }

            plan.Add((item, pages));
        }

        var picker = new Windows.Storage.Pickers.FileSavePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.SuggestedFileName = "Merged";
        picker.FileTypeChoices.Add("PDF", new List<string> { ".pdf" });

        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        bool bookmarkEachFile = BookmarkEachFile.IsChecked == true;
        bool keepFileBookmarks = KeepBookmarks.IsChecked == true;
        var sizing = (ImagePageSize)Math.Max(0, PicturePageSize.SelectedIndex);

        _merging = new CancellationTokenSource();
        SetMerging(true, plan.Count);
        var progress = new Progress<(int Done, string Name)>(step =>
        {
            MergeProgress.Value = step.Done;
            ProgressText.Text = step.Done < plan.Count
                ? $"Adding {step.Name} ({step.Done + 1} of {plan.Count})"
                : "Writing the new file";
        });

        string? failed;
        try
        {
            failed = await MergeAsync(plan, file.Path, bookmarkEachFile, keepFileBookmarks, sizing, progress, _merging.Token);
        }
        catch (OperationCanceledException)
        {
            failed = "Merge stopped. Nothing was saved.";
        }
        finally
        {
            _merging = null;
            SetMerging(false, 0);
        }

        if (failed is not null)
        {
            ShowMessage(failed);
            return;
        }

        if (App.Window is MainWindow window)
        {
            window.AddDocumentTab(file.Path);
        }

        Close();
    }

    /// <summary>
    /// Builds the merged file: a new document, every chosen page and picture
    /// in order, written to a temporary name, given its bookmarks, then moved
    /// into place. Null when it worked, or what went wrong.
    /// </summary>
    private static async Task<string?> MergeAsync(
        IReadOnlyList<(MergeItem Item, IReadOnlyList<int> Pages)> plan,
        string path,
        bool bookmarkEachFile,
        bool keepFileBookmarks,
        ImagePageSize sizing,
        IProgress<(int Done, string Name)> progress,
        CancellationToken token)
    {
        string written = path + ".ayaan-merging";
        string outlined = path + ".ayaan-outline";

        ulong merged = RenderCoreNative.create_document();
        if (merged == 0)
        {
            return "Couldn't start a new PDF.";
        }

        try
        {
            var parts = new List<MergePart>(plan.Count);
            int at = 0;

            for (int i = 0; i < plan.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var (item, pages) = plan[i];
                progress.Report((i, item.Name));

                if (item.Kind == MergeItemKind.Pdf && item.Source is { } source)
                {
                    int[] chosen = pages.ToArray();
                    int start = at;
                    ulong handle = source.Handle;
                    int inserted = await Task.Run(
                        () => RenderCoreNative.insert_pages_from_document(merged, handle, chosen, (nuint)chosen.Length, start),
                        token);
                    if (inserted != chosen.Length)
                    {
                        return $"Couldn't add the pages of “{item.Name}”.";
                    }

                    parts.Add(new MergePart(
                        item.Title,
                        pages,
                        keepFileBookmarks ? source.ReadBookmarks() : Array.Empty<Bookmark>()));
                    at += inserted;
                    continue;
                }

                if (item.Picture is not { } picture)
                {
                    return $"Couldn't read “{item.Name}”.";
                }

                for (int frame = 0; frame < picture.PageCount; frame++)
                {
                    token.ThrowIfCancellationRequested();
                    if (!await AddPicturePageAsync(merged, at, item.Path, frame, picture, sizing))
                    {
                        return $"Couldn't add the picture “{item.Name}”.";
                    }

                    at++;
                }

                parts.Add(new MergePart(item.Title, pages, Array.Empty<Bookmark>()));
            }

            token.ThrowIfCancellationRequested();
            progress.Report((plan.Count, string.Empty));

            if (await Task.Run(() => RenderCoreNative.save_document(merged, written)) != RenderStatus.OkPdfium)
            {
                return "Couldn't write the new PDF.";
            }

            // Before the bookmarks, whose writer keeps what it finds.
            await Task.Run(() => ViewportViewModel.StampWrittenFile(written));

            RenderCoreNative.close_document(merged);
            merged = 0;

            // Bookmarks are written into the file on disk, because PDFium can't
            // create one. A file whose bookmarks fail to go in is still the
            // merged file, so it is kept, and the log says why.
            string finished = written;
            var outline = MergePlan.Outline(parts, bookmarkEachFile, keepFileBookmarks);
            if (outline.Count > 0)
            {
                byte[] buffer = BookmarkWriter.Serialise(outline);
                int status = await Task.Run(() => RenderCoreNative.write_outline(written, outlined, buffer, (nuint)buffer.Length));
                if (status == RenderStatus.OkPdfium)
                {
                    finished = outlined;
                }
                else
                {
                    Diag.Log($"merge: bookmarks not written ({status})");
                }
            }

            File.Move(finished, path, overwrite: true);
            Diag.Log($"merge: {plan.Count} file(s), {at} page(s), {outline.Count} bookmark(s) into \"{Path.GetFileName(path)}\"");
            return null;
        }
        catch (IOException ex)
        {
            Diag.Log($"merge: couldn't move the new file into place: {ex.Message}");
            return "The new PDF couldn't be saved there. Is that file open somewhere else?";
        }
        finally
        {
            if (merged != 0)
            {
                RenderCoreNative.close_document(merged);
            }

            TryDelete(written);
            TryDelete(outlined);
        }
    }

    /// <summary>
    /// One page for one frame of a picture. A JPEG that needs no turning goes
    /// in as its own bytes; everything else, and a JPEG PDFium won't take that
    /// way, goes in as decoded pixels.
    /// </summary>
    private static async Task<bool> AddPicturePageAsync(ulong merged, int at, string path, int frame, ImageInfo picture, ImagePageSize sizing)
    {
        if (picture.IsUprightJpeg && frame == 0)
        {
            var (pageWidth, pageHeight) = ImagePageSizing.For(picture.PixelWidth, picture.PixelHeight, picture.DpiX, picture.DpiY, sizing);
            int status = await Task.Run(() => RenderCoreNative.insert_jpeg_page(
                merged, at, path, picture.PixelWidth, picture.PixelHeight, pageWidth, pageHeight));
            if (status == RenderStatus.OkPdfium)
            {
                return true;
            }
        }

        var decoded = await ImagePages.DecodeAsync(path, frame);
        if (decoded is null)
        {
            return false;
        }

        var (width, height) = ImagePageSizing.For(decoded.Width, decoded.Height, decoded.DpiX, decoded.DpiY, sizing);
        return await Task.Run(() => RenderCoreNative.insert_image_page(
            merged, at, decoded.Pixels, decoded.Width, decoded.Height, width, height)) == RenderStatus.OkPdfium;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // Left behind is harmless; failing the merge over it would not be.
        }
    }

    private void SetMerging(bool merging, int steps)
    {
        ProgressPanel.Visibility = merging ? Visibility.Visible : Visibility.Collapsed;
        MergeButton.Visibility = merging ? Visibility.Collapsed : Visibility.Visible;
        CloseButton.Visibility = merging ? Visibility.Collapsed : Visibility.Visible;

        AddFilesButton.IsEnabled = !merging;
        SortButton.IsEnabled = !merging;
        FileList.IsEnabled = !merging;
        BookmarkEachFile.IsEnabled = !merging;
        KeepBookmarks.IsEnabled = !merging;
        PicturePageSize.IsEnabled = !merging;

        MergeProgress.Maximum = Math.Max(1, steps);
        MergeProgress.Value = 0;
        ProgressText.Text = string.Empty;
    }

    private void StopMerge_Click(object sender, RoutedEventArgs e) => _merging?.Cancel();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape && e.OriginalSource is not TextBox)
        {
            e.Handled = true;
            Close();
        }
    }
}
