using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace PdfEditorApp.ViewModels;

public enum MergeItemKind
{
    Pdf,
    Picture,
}

public enum MergeItemState
{
    Reading,
    Ready,
    NeedsPassword,
    Unreadable,
}

/// <summary>
/// One file in Merge files: what it is, whether it can be merged yet, and
/// which of its pages go in.
/// </summary>
/// <remarks>
/// A file that needs a password or can't be read stays in the list, marked on
/// its own row, rather than being dropped with a dialog: with thirty files, a
/// file that silently vanished from the merge is worse than one that has to be
/// dealt with.
/// </remarks>
public partial class MergeItem : ObservableObject, IDisposable
{
    public MergeItem(string path, MergeItemKind kind)
    {
        Path = path;
        Kind = kind;
    }

    public string Path { get; }

    public MergeItemKind Kind { get; }

    public string Name => System.IO.Path.GetFileName(Path);

    /// <summary>What the file's bookmark in the merged file is called.</summary>
    public string Title => System.IO.Path.GetFileNameWithoutExtension(Path);

    /// <summary>The open PDF its pages are copied from, once it has opened.</summary>
    internal SourceDocument? Source { get; set; }

    /// <summary>The picture's size and page count, once it has been read.</summary>
    internal ImageInfo? Picture { get; set; }

    [ObservableProperty]
    public partial MergeItemState State { get; set; }

    [ObservableProperty]
    public partial int PageCount { get; set; }

    /// <summary>Which pages go in, as "1-4, 7, 9" or "All".</summary>
    [ObservableProperty]
    public partial string PagesText { get; set; } = "All";

    [ObservableProperty]
    public partial ImageSource? Thumbnail { get; set; }

    [ObservableProperty]
    public partial string Detail { get; set; } = "Reading…";

    public string Problem => State switch
    {
        MergeItemState.NeedsPassword => "Password needed",
        MergeItemState.Unreadable => "Can't be read. The file may be damaged or not really this kind of file.",
        _ => string.Empty,
    };

    public bool HasProblem => State is MergeItemState.NeedsPassword or MergeItemState.Unreadable;

    public Visibility ProblemVisibility => HasProblem ? Visibility.Visible : Visibility.Collapsed;

    public Visibility DetailVisibility => HasProblem ? Visibility.Collapsed : Visibility.Visible;

    public Visibility UnlockVisibility => State == MergeItemState.NeedsPassword ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ChoosePagesVisibility =>
        Kind == MergeItemKind.Pdf && State == MergeItemState.Ready ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Only a PDF that has opened has pages to choose; a picture goes in whole.</summary>
    public bool CanChoosePages => Kind == MergeItemKind.Pdf && State == MergeItemState.Ready;

    partial void OnStateChanged(MergeItemState value)
    {
        OnPropertyChanged(nameof(Problem));
        OnPropertyChanged(nameof(HasProblem));
        OnPropertyChanged(nameof(ProblemVisibility));
        OnPropertyChanged(nameof(DetailVisibility));
        OnPropertyChanged(nameof(UnlockVisibility));
        OnPropertyChanged(nameof(ChoosePagesVisibility));
        OnPropertyChanged(nameof(CanChoosePages));
    }

    /// <summary>"1 page" or "1,240 pages".</summary>
    public static string Pages(int count) =>
        count == 1 ? "1 page" : $"{count.ToString("N0", CultureInfo.CurrentCulture)} pages";

    /// <summary>A file size as people read one: "840 KB", "2.1 MB".</summary>
    public static string Size(long bytes) => bytes switch
    {
        < 1024 * 1024 => $"{Math.Max(1, bytes / 1024)} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB",
    };

    public void Dispose()
    {
        Source?.Dispose();
        Source = null;
    }
}
