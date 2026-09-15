using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Refilling a bound collection in one notification. The page stack and the
/// thumbnails are refilled with every page of a document on open, and each
/// notification is a trip into the UI.
/// </summary>
public class BulkObservableCollectionTests
{
    [Fact]
    public void a_refill_of_any_size_is_announced_once_as_a_reset()
    {
        var collection = new BulkObservableCollection<int> { 1, 2, 3 };
        var heard = new List<NotifyCollectionChangedAction>();
        collection.CollectionChanged += (_, e) => heard.Add(e.Action);

        collection.ReplaceAll(Enumerable.Range(0, 39881));

        Assert.Equal([NotifyCollectionChangedAction.Reset], heard);
        Assert.Equal(39881, collection.Count);
        Assert.Equal(0, collection[0]);
        Assert.Equal(39880, collection[^1]);
    }

    [Fact]
    public void the_count_is_announced_so_bindings_to_it_follow()
    {
        var collection = new BulkObservableCollection<string>();
        var properties = new List<string?>();
        ((System.ComponentModel.INotifyPropertyChanged)collection).PropertyChanged += (_, e) => properties.Add(e.PropertyName);

        collection.ReplaceAll(["a", "b"]);

        Assert.Contains("Count", properties);
        Assert.Contains("Item[]", properties);
    }

    [Fact]
    public void refilling_with_nothing_empties_it()
    {
        var collection = new BulkObservableCollection<int> { 1, 2 };

        collection.ReplaceAll([]);

        Assert.Empty(collection);
    }
}
