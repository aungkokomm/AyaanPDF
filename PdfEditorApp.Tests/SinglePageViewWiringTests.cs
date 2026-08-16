using System;
using System.IO;
using System.Text.RegularExpressions;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Showing one page at a time.
///
/// The geometry is covered by ContinuousLayoutTests. What only the app can get
/// wrong is the assumption this feature breaks: that a card's position in the
/// list is its page number. That held for as long as every page had a card,
/// and it is baked into anything that reaches for PageSlots by page.
/// </summary>
public class SinglePageViewWiringTests
{
    private static string Read(params string[] relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string path = Path.Combine(relative);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, path)))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, path));
    }

    private static string ViewModel() => Read("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");
    private static string PageCode() => Read("PdfEditorApp", "MainPage.xaml.cs");
    private static string PageXaml() => Read("PdfEditorApp", "MainPage.xaml");

    private static string MethodBody(string source, string signature)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{signature}' is gone; this test needs rewriting to match");

        int next = source.IndexOf("\n    private ", at + signature.Length, StringComparison.Ordinal);
        int alt = source.IndexOf("\n    public ", at + signature.Length, StringComparison.Ordinal);
        if (alt >= 0 && (next < 0 || alt < next))
        {
            next = alt;
        }

        return next > at ? source[at..next] : source[at..];
    }

    [Fact]
    public void no_card_is_reached_for_by_its_position_in_the_list()
    {
        // THE test in this file.
        //
        // PageSlots used to hold one card per page in page order, so
        // PageSlots[pageIndex] was the card for that page. Single-page view
        // lays out ONE card, and it is page 40 rather than page 0, so every
        // one of those reads the wrong card or throws. The failure is silent
        // and looks like annotations landing on the wrong page.
        //
        // Indexing by a LOOP variable is still fine: those walk the cards that
        // exist. What is banned is indexing by something that means a page.
        string vm = ViewModel();

        // SlotFor itself is the one place allowed to index by page: it is the
        // fast path for continuous view, and it checks the card it lands on is
        // really that page before returning it.
        string finder = MethodBody(vm, "private PageSlot? SlotFor");
        string rest = vm.Replace(finder, string.Empty);

        var offenders = new System.Collections.Generic.List<string>();

        foreach (Match m in Regex.Matches(rest, @"PageSlots\[([^\]]+)\]"))
        {
            string key = m.Groups[1].Value.Trim();
            bool looksLikeALoopVariable = key is "i" or "j" or "index" or "0";

            if (!looksLikeALoopVariable)
            {
                offenders.Add(key);
            }
        }

        Assert.True(
            offenders.Count == 0,
            "these reach for a card by page number instead of through SlotFor: " +
            string.Join(", ", offenders));
    }

    [Fact]
    public void the_card_for_a_page_is_found_by_asking_each_card_which_page_it_is()
    {
        string body = MethodBody(ViewModel(), "private PageSlot? SlotFor");

        Assert.Contains("slot.PageIndex == pageIndex", body, StringComparison.Ordinal);
    }

    [Fact]
    public void which_page_is_under_a_point_comes_from_the_card_not_its_position()
    {
        // PageAt returned the loop index as a page number. In single-page view
        // that is 0 for whatever page is actually on screen, which would drop
        // every guide and every ruler action onto page 1.
        string body = MethodBody(ViewModel(), "public int PageAt");

        Assert.Contains("slot.PageIndex", body, StringComparison.Ordinal);
        Assert.DoesNotContain("return i;", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_layout_is_told_which_single_page_to_show()
    {
        string body = MethodBody(ViewModel(), "private void RebuildContinuousLayoutCore");

        Assert.Contains("PageViewMode.SinglePage ? CurrentPageIndex : -1", body, StringComparison.Ordinal);
    }

    [Fact]
    public void turning_a_page_rebuilds_the_stack_only_when_one_page_is_shown()
    {
        // In continuous view the page changes on every scroll, and rebuilding
        // the whole stack each time would be catastrophic.
        string body = MethodBody(ViewModel(), "private void RelayoutForPageTurn");

        Assert.Contains("PageViewMode != PageViewMode.SinglePage", body, StringComparison.Ordinal);
        Assert.Contains("return;", body, StringComparison.Ordinal);
        Assert.Contains("_rebuildingLayout", body, StringComparison.Ordinal);
    }

    [Fact]
    public void a_page_turn_does_not_go_back_to_the_document_for_page_sizes()
    {
        // Single-page view rebuilds the layout on every page turn. Re-reading
        // every page's size from PDFium to move forward one page is an FFI call
        // and a marshalled array of a few thousand structs.
        string vm = ViewModel();

        Assert.Contains("RebuildContinuousLayout(pagesUnchanged: true)",
                        MethodBody(vm, "private void RelayoutForPageTurn"), StringComparison.Ordinal);
        Assert.Contains("RebuildContinuousLayout(pagesUnchanged: true)",
                        MethodBody(vm, "public void SetPageViewMode"), StringComparison.Ordinal);
    }

    [Fact]
    public void the_size_cache_is_kept_only_when_the_caller_says_so()
    {
        // Safe by default. A stale cache lays the document out at the shapes it
        // used to have, and there are many ways to change the pages (insert,
        // delete, duplicate, reorder, rotate, undo any of them, reload after
        // save) against three ways to merely rearrange them.
        string body = MethodBody(ViewModel(), "private void RebuildContinuousLayout(bool pagesUnchanged");

        Assert.Contains("if (!pagesUnchanged)", body, StringComparison.Ordinal);
        Assert.Contains("_pageSizes = null;", body, StringComparison.Ordinal);
        Assert.Contains("bool pagesUnchanged = false", body, StringComparison.Ordinal);
    }

    [Fact]
    public void only_the_rearranging_rebuilds_keep_the_cache()
    {
        // Everything else must take the default. One that opts out wrongly is a
        // document laid out at the wrong page shapes.
        var keepers = Regex.Matches(ViewModel(), @"RebuildContinuousLayout\(pagesUnchanged: true\)");

        Assert.Equal(3, keepers.Count);
    }

    [Fact]
    public void the_mode_never_touches_the_document()
    {
        // Like view rotation: it changes what is on screen, so there is nothing
        // to save and nothing to undo.
        string body = MethodBody(ViewModel(), "public void SetPageViewMode");

        Assert.DoesNotContain("IsDirty", body, StringComparison.Ordinal);
        Assert.DoesNotContain("PushHistory", body, StringComparison.Ordinal);
        Assert.DoesNotContain("RenderCoreNative", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_reader_stays_on_the_page_they_were_on()
    {
        // Switching mode rebuilds every card, so the scroll offset means
        // something different afterwards.
        Assert.Contains("ViewRotated?.Invoke(wasOn)",
                        MethodBody(ViewModel(), "public void SetPageViewMode"),
                        StringComparison.Ordinal);
    }

    [Fact]
    public void the_menu_offers_both_and_says_what_each_is()
    {
        // A radio pair, not a toggle: "Single page" unchecked does not say what
        // you get instead.
        string xaml = PageXaml();

        Assert.Contains("<RadioMenuFlyoutItem x:Name=\"ContinuousViewItem\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<RadioMenuFlyoutItem x:Name=\"SinglePageViewItem\"", xaml, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(xaml, @"GroupName=""pageViewMode""").Count);
    }

    [Fact]
    public void the_wheel_can_still_get_from_one_page_to_the_next()
    {
        // The gap that shipped. This app has NO next-page or previous-page
        // button anywhere: in continuous view you move between pages by
        // scrolling, so the wheel was the entire navigation model. Laying out
        // one page left it with nowhere to go and the reader stuck.
        string body = MethodBody(PageCode(), "private void PageScroller_PointerWheelChanged");

        Assert.Contains("ViewModel.IsSinglePageView", body, StringComparison.Ordinal);
        Assert.Contains("SinglePageScroll.Resolve(", body, StringComparison.Ordinal);
        Assert.Contains("ViewModel.GoToPage(target", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_wheel_handler_is_registered_where_it_will_actually_run()
    {
        // The ScrollView marks the wheel handled before this would bubble, so
        // without handledEventsToo the handler never runs and everything above
        // it is dead code that tests happily.
        string code = PageCode();

        Assert.Contains("UIElement.PointerWheelChangedEvent", code, StringComparison.Ordinal);
        Assert.Contains("handledEventsToo: true", code, StringComparison.Ordinal);
    }

    [Fact]
    public void turning_by_wheel_does_not_steal_zoom_or_run_off_the_ends()
    {
        string body = MethodBody(PageCode(), "private void PageScroller_PointerWheelChanged");

        // Ctrl+wheel is zoom, which the scroller does itself.
        Assert.Contains("_isCtrlDown", body, StringComparison.Ordinal);

        // And the first and last pages must not turn into nothing.
        Assert.Contains("target < 0 || target >= ViewModel.PageCount", body, StringComparison.Ordinal);
    }

    [Fact]
    public void a_flick_of_the_wheel_turns_one_page_not_five()
    {
        string body = MethodBody(PageCode(), "private void PageScroller_PointerWheelChanged");

        Assert.Contains("SinglePageScroll.MayTurn(", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_zoom_commands_are_in_the_view_menu()
    {
        // They existed only as a flyout off the status bar's percentage and as
        // toolbar buttons: fine once you know, invisible until then.
        string xaml = PageXaml();

        Assert.Contains("Text=\"Fit page\" Click=\"ZoomFitPage_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Fit width\" Click=\"ZoomFitWidth_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Actual size\" Click=\"ZoomActualSize_Click\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void the_menu_and_the_chord_run_the_same_zoom()
    {
        // Actual size lived inline in the key handler, which is why it was the
        // one zoom a menu could not offer. One path now.
        string code = PageCode();

        Assert.Contains("case VirtualKey.Number1 when _isCtrlDown:\r\n                ZoomActualSize_Click(this, null!);",
                        code.Replace("\n", "\r\n").Replace("\r\r", "\r"), StringComparison.Ordinal);
    }

    [Fact]
    public void the_choice_is_remembered_and_restored()
    {
        Assert.Contains("SettingsStore.Update(s => s with { PageViewMode = mode })",
                        MethodBody(PageCode(), "private void ApplyPageViewMode"), StringComparison.Ordinal);

        string applied = MethodBody(PageCode(), "private void ApplySettings");
        Assert.Contains("ViewModel.SetPageViewMode(s.PageViewMode)", applied, StringComparison.Ordinal);
        Assert.Contains("SinglePageViewItem.IsChecked", applied, StringComparison.Ordinal);
        Assert.Contains("ContinuousViewItem.IsChecked", applied, StringComparison.Ordinal);
    }

    [Fact]
    public void an_unreadable_setting_falls_back_to_continuous()
    {
        // The settings file is the user's and may be hand-edited or come from a
        // future version.
        var odd = new AppSettings { PageViewMode = (PageViewMode)99 };

        Assert.Equal(PageViewMode.Continuous, odd.Sanitised().PageViewMode);
        Assert.Equal(PageViewMode.Continuous, new AppSettings().PageViewMode);
    }
}
