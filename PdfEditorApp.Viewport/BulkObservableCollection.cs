using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace PdfEditorApp.Viewport;

/// <summary>
/// An <see cref="ObservableCollection{T}"/> that can be refilled with one
/// change notification instead of one per item.
/// </summary>
/// <remarks>
/// ⚠️ ONE ADD IS ONE TRIP INTO THE UI. A bound ListView or ItemsRepeater hears
/// every Add as a change of its own, across the WinRT boundary. Refilling the
/// page thumbnails one Add per page cost half a second on a 39,881-page book,
/// and the page stack as much again, on the UI thread, before the first page
/// could draw. <see cref="ReplaceAll"/> swaps the contents and says so once,
/// as a Reset, which every items control answers by reading the list afresh.
/// </remarks>
public class BulkObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>Replaces everything in the collection with <paramref name="items"/>, raising one Reset.</summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
