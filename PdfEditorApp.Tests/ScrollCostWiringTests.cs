using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// A scroll step costs what is near the viewport, not what is in the book.
///
/// ⚠️ WRITTEN AFTER MEASURED HITCHES. On a 39881-page book the UiStall log
/// showed UpdateVisibleWindow at 146 to 215 ms around jumps. Every scroll step
/// walked every slot to release pixels, every page change cleared hundreds of
/// thousands of empty collections, and the status label read the new page's
/// text on the UI thread. Read out of the source, because the view model lives
/// in the WinUI project.
/// </summary>
public class ScrollCostWiringTests
{
    private static string ViewModel()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string path = Path.Combine("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, path)))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, path)).Replace("\r\n", "\n");
    }

    private static string Body(string code, string signature)
    {
        int at = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at > 0, $"{signature} is gone");
        int end = code.IndexOf("\n    }\n", at, StringComparison.Ordinal);
        return code[at..end];
    }

    [Fact]
    public void a_scroll_step_releases_pixels_from_the_slots_holding_them_not_every_slot()
    {
        string code = ViewModel();
        string body = Body(code, "public void UpdateVisibleWindow(");

        Assert.DoesNotContain("for (int i = 0; i < PageSlots.Count; i++)", body, StringComparison.Ordinal);
        Assert.Contains("foreach (var held in _slotsWithPixels.ToArray())", body, StringComparison.Ordinal);
        Assert.Contains("for (int i = keepFrom; i <= keepTo && i < PageSlots.Count; i++)", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("private async void RenderBaseTier(PageSlot slot)")]
    [InlineData("private async void SharpenSlot(PageSlot slot, int targetWidth)")]
    [InlineData("private void RenderVisibleTiles(PageSlot slot)")]
    public void every_path_that_gives_a_slot_pixels_lists_it(string signature)
    {
        // A slot given pixels but not listed would never be released.
        Assert.Contains("_slotsWithPixels.Add(slot);", Body(ViewModel(), signature), StringComparison.Ordinal);
    }

    [Fact]
    public void a_rebuilt_stack_forgets_the_old_slots()
    {
        Assert.Contains("_slotsWithPixels.Clear();",
            Body(ViewModel(), "private void RebuildContinuousLayoutCore()"), StringComparison.Ordinal);
    }

    [Fact]
    public void a_page_change_clears_only_collections_that_hold_something()
    {
        string body = Body(ViewModel(), "private void DistributeAnnotationsToSlots()");

        Assert.Contains("if (slot.Highlights.Count > 0) { slot.Highlights.Clear(); }", body, StringComparison.Ordinal);
        Assert.Contains("if (slot.SelectionRects.Count > 0) { slot.SelectionRects.Clear(); }", body, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\n\s+slot\.\w+\.Clear\(\);", body);
    }

    [Fact]
    public void the_status_label_never_reads_a_page_on_the_ui_thread()
    {
        string body = Body(ViewModel(), "public string TextAvailabilityLabel");

        Assert.DoesNotContain("PageHasText(", body, StringComparison.Ordinal);
        Assert.DoesNotContain("TextLayerFor(", body, StringComparison.Ordinal);
        Assert.Contains("LoadTextLayerInBackgroundAsync(CurrentPageIndex)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void a_background_text_layer_cannot_outlive_the_document_it_describes()
    {
        string code = ViewModel();

        // One place drops the layers, and it moves the epoch on.
        Assert.Single(Regex.Matches(code, @"_textLayers\.Clear\(\);"));
        Assert.Contains("_textLayerEpoch++;", Body(code, "private void ForgetTextLayers()"), StringComparison.Ordinal);

        string load = Body(code, "private async Task LoadTextLayerInBackgroundAsync(int pageIndex)");
        Assert.Contains("epoch == _textLayerEpoch", load, StringComparison.Ordinal);
        Assert.Contains("Task.Run(() => TextLayerLoader.Load(handle, pageIndex, width))", load, StringComparison.Ordinal);
    }
}
