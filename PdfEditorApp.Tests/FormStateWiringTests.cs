using System;
using System.IO;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The call sites that carry a form-field edit.
///
/// Read out of the source, because they live in the WinUI project and a net10.0
/// test assembly cannot load one. Green here is NOT proof the click works; it is
/// proof the rules that are easy to break by accident are still in place.
/// </summary>
public class FormStateWiringTests
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

    private static string ViewModel() =>
        Source("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");

    private static string Page() => Source("PdfEditorApp", "MainPage.xaml.cs");

    private static string Core() => Source("render_core", "src", "form_state.rs");

    private static string Body(string code, string signature)
    {
        int at = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at > 0, $"there is no {signature}");

        int next = code.IndexOf("\n    /// <summary>", at, StringComparison.Ordinal);
        return code[at..(next > at ? next : code.Length)];
    }

    /// <summary>
    /// One Rust item's body. Separate from the C# reader because the two mark
    /// the end of an item differently, and a body that runs on into the next
    /// function makes a test that cannot fail.
    /// </summary>
    private static string RustBody(string code, string signature)
    {
        int at = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at > 0, $"there is no {signature}");

        // The next item at column zero: a doc comment, an fn, or an attribute.
        int end = code.Length;
        foreach (string marker in new[] { "\n/// ", "\nfn ", "\npub fn ", "\n#[" })
        {
            int found = code.IndexOf(marker, at + signature.Length, StringComparison.Ordinal);
            if (found > at && found < end) { end = found; }
        }

        return code[at..end];
    }

    // ---------------- the core's rules ----------------

    [Fact]
    public void the_on_state_is_read_from_the_file_and_never_assumed()
    {
        // ⚠️ THE ASSUMPTION THAT BREAKS REAL FORMS, and the vendored crate makes
        // it: it hard-codes "/Yes". The on state is whichever key of the widget's
        // own /AP /N is not Off, and it differs per widget inside one radio group.
        string body = RustBody(Core(), "fn on_state(doc: &Document, widget: ObjectId)");

        Assert.Contains("\"AP\"", body.Replace("b\"AP\"", "\"AP\""), StringComparison.Ordinal);
        Assert.Contains("!= \"Off\"", body.Replace("k != \"Off\"", "!= \"Off\""), StringComparison.Ordinal);
        Assert.DoesNotContain("Yes", body, StringComparison.Ordinal);
    }

    [Fact]
    public void selecting_a_radio_writes_every_widget_in_the_group()
    {
        // A group shows its selection through each button's own /AS, so leaving
        // the others alone draws two selected buttons at once.
        string body = RustBody(Core(), "fn set_radio(");

        Assert.Contains("for (i, &widget) in widgets.iter().enumerate()", body, StringComparison.Ordinal);
        Assert.Contains("\"Off\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public void a_choice_field_stores_the_export_value_and_the_index()
    {
        // /Opt is legal in two forms, and the pair form is where the display
        // label and the stored value differ. Writing the label is the mistake
        // that makes a form submit the wrong thing with nothing on screen wrong.
        string body = RustBody(Core(), "fn export_value(entry: &Object)");
        Assert.Contains("Object::Array(pair)", body, StringComparison.Ordinal);
        Assert.Contains(".first()", body, StringComparison.Ordinal);

        string choice = RustBody(Core(), "fn set_choice(");
        Assert.Contains("dict.set(\"V\"", choice, StringComparison.Ordinal);
        Assert.Contains("dict.set(\"I\"", choice, StringComparison.Ordinal);
    }

    [Fact]
    public void only_choice_fields_ask_for_appearances_to_be_rebuilt()
    {
        // A checkbox and a radio already carry a stream per state, so /AS just
        // picks one. Setting the flag for them would make every reader rebuild
        // appearances it has no reason to doubt.
        string checkbox = RustBody(Core(), "fn set_checkbox(");
        string radio = RustBody(Core(), "fn set_radio(");
        string choice = RustBody(Core(), "fn set_choice(");

        Assert.DoesNotContain("need_appearances", checkbox, StringComparison.Ordinal);
        Assert.DoesNotContain("need_appearances", radio, StringComparison.Ordinal);
        Assert.Contains("need_appearances(doc)", choice, StringComparison.Ordinal);
    }

    [Fact]
    public void a_field_is_found_by_its_qualified_name_and_never_by_an_index()
    {
        // Every write here rewrites the whole document, so an object or page
        // index captured beforehand names something else afterwards.
        string body = RustBody(Core(), "fn find_field(doc: &Document, wanted: &str)");

        Assert.Contains("qualified_name(doc, id)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_parent_walk_is_bounded()
    {
        // A malformed file can point a /Parent chain back at itself, and walking
        // one forever inside a save is not a failure anyone can diagnose.
        string body = RustBody(Core(), "fn qualified_name(doc: &Document, id: ObjectId)");

        Assert.Contains("depth > 32", body, StringComparison.Ordinal);
    }

    // ---------------- the app's rules ----------------

    [Fact]
    public void a_form_edit_is_one_document_scope_history_entry()
    {
        // The whole document is rewritten, so that is the scope. The reader
        // clicked once, so Ctrl+Z has to put it back in one step.
        string body = Body(ViewModel(), "public bool SetFormFieldState(");

        Assert.Contains("Capture(HistoryScope.Document", body, StringComparison.Ordinal);
        Assert.Contains("_history.Push(before)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void a_refused_form_edit_leaves_no_history_entry_behind()
    {
        // The core leaves the document untouched when it refuses, so an entry
        // pushed anyway would be a Ctrl+Z that appears to do nothing.
        string body = Body(ViewModel(), "public bool SetFormFieldState(");

        int refusal = body.IndexOf("if (status != RenderStatus.OkPdfium)", StringComparison.Ordinal);
        int push = body.IndexOf("_history.Push(before)", StringComparison.Ordinal);

        Assert.True(refusal > 0 && push > refusal, "the entry is pushed before the write is known to have worked");
    }

    [Fact]
    public void a_form_edit_drops_every_per_page_cache()
    {
        // ⚠️ THE DOCUMENT IS A DIFFERENT ONE afterwards, behind the same handle.
        // Anything cached per page describes the document that was replaced.
        string body = Body(ViewModel(), "public bool SetFormFieldState(");

        Assert.Contains("ClearLoadedAnnotations()", body, StringComparison.Ordinal);
        Assert.Contains("LoadFormFields()", body, StringComparison.Ordinal);
    }

    [Fact]
    public void restoring_a_document_re_reads_the_form()
    {
        // Undoing a form edit restores the document's bytes. Without this the app
        // goes on holding the field states it has just undone.
        string body = Body(ViewModel(), "private void RestoreDocumentBytes(byte[] bytes)");

        Assert.Contains("LoadFormFields()", body, StringComparison.Ordinal);
    }

    [Fact]
    public void a_click_is_routed_by_the_kind_of_field_it_landed_on()
    {
        // Every fillable field used to open a TEXT editor, including a checkbox.
        string body = Body(Page(), "private void HandleFormFieldClick(");

        Assert.Contains("FormFieldKind.Text", body, StringComparison.Ordinal);
        Assert.Contains("BeginFormFieldEdit(", body, StringComparison.Ordinal);
        Assert.Contains("field.IsToggle", body, StringComparison.Ordinal);
        Assert.Contains("field.IsChoice", body, StringComparison.Ordinal);
        Assert.Contains("ShowFieldOptions(", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_picker_sends_back_an_index_and_never_a_label()
    {
        // The export value never crosses the FFI boundary, which is what makes
        // it impossible for the app to store a display label by mistake.
        string body = Body(Page(), "private void ShowFieldOptions(");

        Assert.Contains("int index = option.Index;", body, StringComparison.Ordinal);
        Assert.Contains("SetFormFieldState(field, index, true)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("option.Label)", body.Replace("Text = option.Label", ""), StringComparison.Ordinal);
    }

    [Fact]
    public void clicking_the_selected_radio_again_does_nothing()
    {
        // What a radio group means, and what every other reader does.
        string body = Body(ViewModel(), "public bool? ToggleStateFor(FormField field)");

        Assert.Contains("field.Checked ? null : true", body, StringComparison.Ordinal);
    }

    [Fact]
    public void a_signature_field_is_never_offered_for_editing()
    {
        string body = Body(
            Source("PdfEditorApp.Viewport", "FormField.cs"), "public bool IsFillable =>");

        Assert.DoesNotContain("Signature", body, StringComparison.Ordinal);
        Assert.DoesNotContain("PushButton", body, StringComparison.Ordinal);
    }
}
