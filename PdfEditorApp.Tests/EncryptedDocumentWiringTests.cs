using System;
using System.IO;
using System.Text.RegularExpressions;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Opening a document that is protected rather than broken.
///
/// The Rust side proves PDFium's behaviour against a real encrypted fixture.
/// This covers what only the app can get wrong: that every way of opening a
/// file reaches the prompt, that the password does not outlive the attempt,
/// and that the two sides still agree on what the status number means.
/// </summary>
public class EncryptedDocumentWiringTests
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

    private static string PageCode() => Read("PdfEditorApp", "MainPage.xaml.cs");
    private static string PageXaml() => Read("PdfEditorApp", "MainPage.xaml");
    private static string ViewModel() => Read("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");
    private static string Interop() => Read("PdfEditorApp", "Interop", "RenderCoreNative.cs");
    private static string Rust() => Read("render_core", "src", "lib.rs");

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
    public void the_two_sides_agree_on_what_the_status_number_means()
    {
        // A constant mirrored by hand across a language boundary, where one
        // side is a raw integer over FFI. If these drift, an encrypted document
        // silently becomes "failed to open" again, which is the exact bug this
        // work exists to fix, and nothing else would notice.
        var rust = Regex.Match(Rust(), @"pub const STATUS_NEEDS_PASSWORD: i32 = (\d+);");
        var csharp = Regex.Match(Interop(), @"public const int NeedsPassword = (\d+);");

        Assert.True(rust.Success, "STATUS_NEEDS_PASSWORD is gone from render_core");
        Assert.True(csharp.Success, "RenderStatus.NeedsPassword is gone");
        Assert.Equal(rust.Groups[1].Value, csharp.Groups[1].Value);
    }

    [Fact]
    public void every_way_of_opening_a_file_can_ask_for_a_password()
    {
        // The prompt is only useful if it is on the path a file actually takes.
        // There are four: a tab opened for a path, the headless auto-open, the
        // picker, and everything that routes through OpenPickedFile (the
        // welcome list, the recent menu and a dropped file).
        string code = PageCode();

        // No direct call anywhere but inside the one helper.
        foreach (Match m in Regex.Matches(code, @"ViewModel\.OpenDocument\("))
        {
            string before = code[..m.Index];
            int helper = before.LastIndexOf("LoadDocumentAsync", StringComparison.Ordinal);
            int otherMethod = before.LastIndexOf("\n    private ", StringComparison.Ordinal);

            Assert.True(
                helper > otherMethod,
                "ViewModel.OpenDocument is called outside LoadDocumentAsync, so that path " +
                "reports an encrypted document as a broken one");
        }
    }

    [Fact]
    public void the_password_is_never_written_to_the_trace()
    {
        // A trace the user might send on is the last place a password belongs.
        // The view model logs the refusal without the password OR the path.
        string open = MethodBody(ViewModel(), "public DocumentOpenOutcome OpenDocument");

        foreach (Match m in Regex.Matches(open, @"Diag\.Log\([^;]*;"))
        {
            Assert.DoesNotContain("password", m.Value.Replace("is encrypted", ""), StringComparison.OrdinalIgnoreCase);
        }

        // The prompt may log that it could not be shown. What it must never do
        // is put the typed value in the line, so the rule is about the VALUE
        // rather than about logging.
        string prompt = MethodBody(PageCode(), "private async System.Threading.Tasks.Task<DocumentOpenOutcome> LoadDocumentAsync");

        foreach (Match m in Regex.Matches(prompt, @"Diag\.Log\([^;]*;"))
        {
            Assert.DoesNotContain("PasswordEntry", m.Value, StringComparison.Ordinal);
            Assert.DoesNotContain(".Password", m.Value, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void the_password_is_not_kept_after_the_attempt()
    {
        // Not stored, not carried to the next document, not left sitting in the
        // box for whoever opens the next protected file.
        string body = MethodBody(PageCode(), "private async System.Threading.Tasks.Task<DocumentOpenOutcome> LoadDocumentAsync");

        Assert.Equal(2, Regex.Matches(body, @"PasswordEntry\.Password = string\.Empty;").Count);
        Assert.DoesNotContain("SettingsStore", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_prompt_masks_what_is_typed()
    {
        string xaml = PageXaml();

        Assert.Contains("<PasswordBox x:Name=\"PasswordEntry\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<TextBox x:Name=\"PasswordEntry\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void a_wrong_password_asks_again_rather_than_giving_up()
    {
        // PDFium reports "no password" and "wrong password" identically, so the
        // retry loop is the only thing that makes a typo recoverable.
        string body = MethodBody(PageCode(), "private async System.Threading.Tasks.Task<DocumentOpenOutcome> LoadDocumentAsync");

        Assert.Contains("while (true)", body, StringComparison.Ordinal);
        Assert.Contains("PasswordError.Visibility = Visibility.Visible", body, StringComparison.Ordinal);
    }

    [Fact]
    public void cancelling_leaves_an_empty_tab_not_a_ghost_document()
    {
        // OpenDocument records the path before it knows the file will open, so
        // a cancelled prompt would otherwise leave a tab named after a document
        // it does not have, offering to save it.
        string body = MethodBody(PageCode(), "private async System.Threading.Tasks.Task<DocumentOpenOutcome> LoadDocumentAsync");

        Assert.Contains("ViewModel.AbandonOpen()", body, StringComparison.Ordinal);

        string abandon = MethodBody(ViewModel(), "public void AbandonOpen");
        Assert.Contains("_currentDocumentPath = null", abandon, StringComparison.Ordinal);
        Assert.Contains("IsDirty = false", abandon, StringComparison.Ordinal);
    }

    [Fact]
    public void a_document_that_never_opened_is_not_added_to_the_recent_list()
    {
        // The recent list is a list of things you can reopen. A file whose
        // password was refused is not one of them.
        string body = MethodBody(ViewModel(), "public DocumentOpenOutcome OpenDocument");

        int add = body.IndexOf("RecentFilesStore.Add", StringComparison.Ordinal);
        Assert.True(add >= 0, "the recent list is no longer written here; this test needs rewriting");

        // The guard is on the handle, so a refused password never reaches it.
        int guard = body.LastIndexOf("_documentHandle != 0", add, StringComparison.Ordinal);
        Assert.True(
            guard >= 0 && body.IndexOf('}', guard) > add,
            "the recent list must only record a document that actually opened");
    }

    [Fact]
    public void an_encrypted_document_is_reported_as_its_own_outcome()
    {
        // Three answers, not a bool: the middle one is not a failure.
        Assert.Equal(3, Enum.GetValues<DocumentOpenOutcome>().Length);
        Assert.Equal(DocumentOpenOutcome.Opened, default);

        string body = MethodBody(ViewModel(), "public DocumentOpenOutcome OpenDocument");
        Assert.Contains("RenderStatus.NeedsPassword", body, StringComparison.Ordinal);
        Assert.Contains("return DocumentOpenOutcome.NeedsPassword", body, StringComparison.Ordinal);
        Assert.Contains("return DocumentOpenOutcome.Failed", body, StringComparison.Ordinal);
        Assert.Contains("return DocumentOpenOutcome.Opened", body, StringComparison.Ordinal);
    }

    [Fact]
    public void an_empty_password_means_none_at_all()
    {
        // PDFium treats an empty string as an attempt and fails a document that
        // would have opened unprotected, so render_core folds "" to None. Every
        // ordinary document now goes through this path, so it matters.
        Assert.Contains("Ok(\"\") => None,", Rust(), StringComparison.Ordinal);
    }
}
