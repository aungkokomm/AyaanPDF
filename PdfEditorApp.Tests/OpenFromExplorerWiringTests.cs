using System;
using System.IO;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Opening a PDF from Explorer, as wired: the launch reads its files, a second
/// launch hands them to the window already open, and the installer offers the
/// app for PDFs. The pieces themselves are proved in SingleInstanceTests.
/// </summary>
public class OpenFromExplorerWiringTests
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
        return File.ReadAllText(Path.Combine(dir!.FullName, path)).Replace("\r\n", "\n");
    }

    private static int IndexIn(string text, string part)
    {
        int at = text.IndexOf(part, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{part}' was not found");
        return at;
    }

    [Fact]
    public void a_launch_opens_its_files_or_hands_them_to_the_open_window_before_making_one()
    {
        string app = Read("PdfEditorApp", "App.xaml.cs");

        int files = IndexIn(app, "LaunchFiles.From(");
        int claim = IndexIn(app, "SingleInstance.TryClaim(name, FilesFromAnotherLaunch)");
        int send = IndexIn(app, "SingleInstance.TrySend(name, files, System.TimeSpan.FromSeconds(5))");
        int window = IndexIn(app, "Window = new MainWindow(files);");
        Assert.True(files < claim && claim < send && send < window);

        // Handed over: this launch quits without a window of its own.
        Assert.True(IndexIn(app, "System.Environment.Exit(0);") < window);

        // Each install folder is its own window, so a test build never hands
        // its files to the installed app.
        Assert.Contains("SingleInstance.NameFor(AppInfo.Name, System.AppContext.BaseDirectory)", app, StringComparison.Ordinal);

        // Files from another launch are opened on the UI thread.
        Assert.Contains("DispatcherQueue.TryEnqueue(", app, StringComparison.Ordinal);
        Assert.Contains("OpenLaunchedFiles(files)", app, StringComparison.Ordinal);
    }

    [Fact]
    public void the_window_opens_each_file_in_a_tab_and_comes_forward()
    {
        string window = Read("PdfEditorApp", "MainWindow.xaml.cs");
        Assert.Contains("public MainWindow(System.Collections.Generic.IReadOnlyList<string> files)", window, StringComparison.Ordinal);

        int open = IndexIn(window, "public void OpenLaunchedFiles(");
        string body = window[open..];
        // A file already open shows its tab rather than opening twice.
        Assert.Contains("string.Equals(p.ViewModel.DocumentPath, file, StringComparison.OrdinalIgnoreCase)", body, StringComparison.Ordinal);
        // An unused welcome tab gives way.
        Assert.Contains("MainPage { IsIdleWelcome: true }", body, StringComparison.Ordinal);
        Assert.Contains("BringToFront();", body, StringComparison.Ordinal);

        // A launched file is not lost when recovered work takes the first tab.
        string page = Read("PdfEditorApp", "MainPage.xaml.cs");
        Assert.Contains("if (recovered && InitialDocumentPath is { } launched", page, StringComparison.Ordinal);
    }

    [Fact]
    public void the_installer_offers_the_app_for_pdfs_per_user_and_removes_it_on_uninstall()
    {
        string iss = Read("installer", "PdfEditor.iss");

        Assert.Contains("Name: \"pdffiles\";", iss, StringComparison.Ordinal);
        Assert.Contains("ChangesAssociations=WizardIsTaskSelected('pdffiles')", iss, StringComparison.Ordinal);

        // Opens the file it was given.
        Assert.Contains("Subkey: \"Software\\Classes\\AyaanPDF.Document\\shell\\open\\command\"; ValueType: string; ValueName: \"\"; ValueData: \"\"\"{app}\\{#ExeName}\"\" \"\"%1\"\"\"", iss, StringComparison.Ordinal);
        // Listed in Open with, and in Settings > Default apps.
        Assert.Contains("Subkey: \"Software\\Classes\\.pdf\\OpenWithProgids\"; ValueType: string; ValueName: \"AyaanPDF.Document\"; ValueData: \"\"; Flags: uninsdeletevalue", iss, StringComparison.Ordinal);
        Assert.Contains("Subkey: \"Software\\RegisteredApplications\"; ValueType: string; ValueName: \"{#AppName}\"; ValueData: \"Software\\{#AppName}\\Capabilities\"; Flags: uninsdeletevalue", iss, StringComparison.Ordinal);

        // Per user, like the install, and never taking over .pdf's default.
        foreach (string line in iss.Split('\n'))
        {
            if (line.StartsWith("Root:", StringComparison.Ordinal))
            {
                Assert.StartsWith("Root: HKA;", line);
                Assert.EndsWith("Tasks: pdffiles", line.TrimEnd());
                Assert.DoesNotContain("Subkey: \"Software\\Classes\\.pdf\"; ValueType", line, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void undoing_a_turned_shapes_resize_puts_back_its_size_from_the_tag()
    {
        string vm = Read("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");

        Assert.Contains("resizing ? TurnedShapeTagAt(start.PageIndex, start.Index) : null", vm, StringComparison.Ordinal);
        Assert.Contains("ApplyBoundsRecord(b, backwards ? b.Before : b.After, backwards ? b.BeforeTag : b.AfterTag);", vm, StringComparison.Ordinal);

        int apply = IndexIn(vm, "private void ApplyBoundsRecord(BoundsRecord record, EditRect target, string? tag)");
        string body = vm[apply..IndexIn(vm, "private int ResizeTurnedShape(")];
        Assert.Contains("? ResizeTurnedShape(page, index, tag, sel)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void a_turned_text_box_among_extras_is_written_at_its_own_box()
    {
        string vm = Read("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");

        // The drag's extras, align/distribute/nudge's extras, and both sides of
        // the drag's undo record.
        Assert.Contains("target.PageIndex, target.Index, target.Left, target.Top, target.Right, target.Bottom);", vm, StringComparison.Ordinal);
        Assert.Contains("old.PageIndex, old.Index, newExtras[i].Left, newExtras[i].Top, newExtras[i].Right, newExtras[i].Bottom);", vm, StringComparison.Ordinal);
        Assert.Contains("TextBoxUprightAt(e.PageIndex, e.Index, e.Left, e.Top, e.Right, e.Bottom);", vm, StringComparison.Ordinal);
        Assert.Contains("TextBoxUprightAt(page, index, a.Left, a.Top, a.Right, a.Bottom);", vm, StringComparison.Ordinal);
    }
}
