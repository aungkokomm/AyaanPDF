using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// What opening a very large document costs the UI thread.
/// </summary>
/// <remarks>
/// ⚠️ WRITTEN FROM A REAL LOG. A 39,881-page book held the UI thread for about
/// four seconds on every open. Measured, the parts were: every page's size read
/// twice (the rulers read them, then the layout threw that away and read them
/// again), the thumbnails and the page stack each refilled with one change
/// notification per page, the stack's geometry built by asking the repeater
/// for each of 39,881 items, and a second-long form-field check held under the
/// core's lock. Each of those is pinned here.
/// </remarks>
public class OpenFreezeWiringTests
{
    private static string Source(params string[] relative)
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

    private static string ViewModel() => Source("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");

    private static string Body(string source, string signature)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{signature}' is gone; this test needs rewriting to match");
        int open = source.IndexOf('{', at);
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') { depth++; }
            else if (source[i] == '}' && --depth == 0) { return source[at..(i + 1)]; }
        }
        return source[at..];
    }

    [Fact]
    public void the_rulers_never_read_every_page_s_size_themselves()
    {
        string body = Body(ViewModel(), "private (double W, double H) CurrentPageSizePoints()");

        Assert.True(body.IndexOf("if (_pageSizes is null)", StringComparison.Ordinal)
                    < body.IndexOf("PagePointsFor(", StringComparison.Ordinal));
    }

    [Fact]
    public void a_closed_document_takes_its_page_sizes_with_it()
    {
        Assert.Contains("_pageSizes = null;", Body(ViewModel(), "private void CloseCurrentDocument()"), StringComparison.Ordinal);
    }

    [Fact]
    public void the_thumbnails_and_the_page_stack_are_refilled_in_one_notification()
    {
        string vm = ViewModel();

        Assert.Contains("public BulkObservableCollection<PageThumbnail> Thumbnails", vm, StringComparison.Ordinal);
        Assert.Contains("public BulkObservableCollection<PageSlot> PageSlots", vm, StringComparison.Ordinal);
        Assert.DoesNotContain("Thumbnails.Add(", vm, StringComparison.Ordinal);
        Assert.DoesNotContain("PageSlots.Add(", vm, StringComparison.Ordinal);
        Assert.Contains("Thumbnails.ReplaceAll(ThumbnailPlaceholders())", Body(vm, "public DocumentOpenOutcome OpenDocument("), StringComparison.Ordinal);
        Assert.Contains("PageSlots.ReplaceAll(", Body(vm, "private void RebuildContinuousLayoutCore()"), StringComparison.Ordinal);
    }

    [Fact]
    public void opening_reads_the_form_fields_off_the_ui_thread_and_drops_a_stale_answer()
    {
        string vm = ViewModel();
        string open = Body(vm, "public DocumentOpenOutcome OpenDocument(");

        Assert.Contains("LoadFormFieldsInBackgroundAsync()", open, StringComparison.Ordinal);
        Assert.DoesNotContain("LoadFormFields();", open, StringComparison.Ordinal);

        string background = Body(vm, "private async Task LoadFormFieldsInBackgroundAsync()");
        Assert.Contains("await Task.Run(() => ReadFormFields(handle))", background, StringComparison.Ordinal);
        Assert.Contains("_documentHandle != handle || generation != _formFieldsGeneration", background, StringComparison.Ordinal);
    }

    [Fact]
    public void the_page_stack_s_geometry_reads_card_sizes_from_the_list()
    {
        string layout = Source("PdfEditorApp", "Controls", "PageStackLayout.cs");
        Assert.Contains("Slots is { } slots && slots.Count == context.ItemCount", layout, StringComparison.Ordinal);

        Assert.Contains("PageCardLayout.Slots = ViewModel.PageSlots;", Source("PdfEditorApp", "MainPage.xaml.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void the_catalog_form_check_runs_with_no_core_lock_held()
    {
        string rust = Source("render_core", "src", "lib.rs");
        int at = rust.IndexOf("fn get_form_fields_inner(", StringComparison.Ordinal);
        string body = rust[at..rust.IndexOf("let mut out: Vec<u8>", at, StringComparison.Ordinal)];

        // One guard for the quick FPDF_GetFormType check, released before the
        // lopdf parse, and one for the walk after it.
        var guards = Regex.Matches(body, @"call_guard\(\)");
        int lopdf = body.IndexOf("declares_no_form_fields(", StringComparison.Ordinal);

        Assert.Equal(2, guards.Count);
        Assert.True(guards[0].Index < lopdf && lopdf < guards[1].Index);
    }
}
