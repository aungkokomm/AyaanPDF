using System;
using System.IO;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The UI-thread stall watchdog is running and knows where to look.
///
/// ⚠️ WRITTEN AFTER A FREEZE THE LOG COULD NOT EXPLAIN. Scrolling one page in
/// a 39881-page book took about 40 seconds, and diag.log showed only that
/// nothing happened for that long. These keep the watchdog started and the
/// suspect handlers timed, so the next freeze names its code.
/// </summary>
public class UiStallWiringTests
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
        return File.ReadAllText(Path.Combine(dir!.FullName, path)).Replace("\r\n", "\n");
    }

    [Fact]
    public void the_window_starts_the_watchdog()
    {
        Assert.Contains("UiStall.Start(DispatcherQueue);", Source("PdfEditorApp", "MainWindow.xaml.cs"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("MainPage.xaml.cs", "ViewChanged")]
    [InlineData("MainPage.xaml.cs", "WheelChanged")]
    [InlineData("MainPage.xaml.cs", "HoverCursor")]
    [InlineData("MainPage.xaml.cs", "PointerPressed")]
    [InlineData("MainPage.xaml.cs", "PointerMoved")]
    [InlineData("MainPage.xaml.cs", "PointerReleased")]
    [InlineData("ViewModels/ViewportViewModel.cs", "UpdateVisibleWindow")]
    [InlineData("ViewModels/ViewportViewModel.cs", "SharpenVisiblePages")]
    [InlineData("ViewModels/ViewportViewModel.cs", "PageChangedByScroll")]
    [InlineData("ViewModels/ViewportViewModel.cs", "DistributeAnnotations")]
    [InlineData("ViewModels/ViewportViewModel.cs", "RefreshSelectionRects")]
    [InlineData("ViewModels/ViewportViewModel.cs", "ContextFor")]
    [InlineData("ViewModels/ViewportViewModel.cs", "SetBaseRender")]
    [InlineData("ViewModels/ViewportViewModel.cs", "SetSharpRender")]
    [InlineData("Controls/PageStackLayout.cs", "PageStack.Measure")]
    public void the_suspect_code_is_timed(string file, string section)
    {
        string code = Source("PdfEditorApp", Path.Combine(file.Split('/')));
        Assert.Contains($"UiStall.Section(\"{section}\")", code, StringComparison.Ordinal);
    }

    [Fact]
    public void a_stall_names_the_section_and_a_slow_section_reports_itself()
    {
        string code = Source("PdfEditorApp", "UiStall.cs");

        Assert.Contains("UI STALL {gap} ms so far, in: {_where}", code, StringComparison.Ordinal);
        Assert.Contains("slow: {_name} took {ms:F0} ms", code, StringComparison.Ordinal);

        // Background, or the watchdog would keep the process alive after the
        // window closes.
        Assert.Contains("IsBackground = true", code, StringComparison.Ordinal);
    }
}
