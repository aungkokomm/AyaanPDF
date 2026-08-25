using System;
using System.IO;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// What a plain Select-tool click on a form field does.
///
/// ⚠️ WRITTEN AFTER A REAL FAILURE. A user opened an ordinary government-style
/// form, clicked its fields, and every click went into the object path instead:
/// their session log shows six presses that each MOVED or RESIZED a field
/// rather than operating it. Form editing worked only inside a Fill mode behind
/// an unlabelled toolbar toggle they never found, and nothing said so.
///
/// Two rules came out of it, and this holds both:
/// a form widget is not something the object pick hands back, and a click on
/// one operates the form without any mode being entered first.
/// </summary>
public class FormInteractionTests
{
    private static AnnotationSnapshot Widget(int index, double top) =>
        new(index, PdfAnnotationSubtype.Widget, 0.10, top, 0.50, top + 0.04,
            1.0, Guid.NewGuid(), null);

    private static AnnotationSnapshot Stamp(int index, double top) =>
        new(index, PdfAnnotationSubtype.Stamp, 0.10, top, 0.60, top + 0.20,
            1.0, Guid.NewGuid(), "AyaanStamp:0");

    // ---------------- the model ----------------

    [Fact]
    public void a_form_widget_is_a_form_field_and_not_something_unrecognised()
    {
        var page = DocumentModelBuilder.BuildPage(0, [Widget(0, 0.20)]);

        Assert.Equal(DocumentObjectKind.FormField, Assert.Single(page.Objects).Kind);
    }

    [Fact]
    public void the_object_pick_never_hands_back_a_form_field()
    {
        // THE END-TO-END RULE, at the layer that decides it. Whatever comes back
        // from here is turned into a selection that can be dragged, resized and
        // deleted. A form field must not be one.
        var page = DocumentModelBuilder.BuildPage(0, [Widget(0, 0.20)]);

        Assert.Null(ObjectHitTest.PickTopmost(page, 0.30, 0.22));
    }

    [Fact]
    public void a_mark_of_ours_under_a_form_field_is_still_pickable()
    {
        // Skipping widgets must not blind the pick to what is beneath them: a
        // form field laid over one of our stamps would otherwise make the stamp
        // unselectable.
        var page = DocumentModelBuilder.BuildPage(0, [Stamp(0, 0.50), Widget(1, 0.55)]);

        var hit = ObjectHitTest.PickTopmost(page, 0.30, 0.57);

        Assert.NotNull(hit);
        Assert.Equal(DocumentObjectKind.Stamp, hit!.Kind);
    }

    [Fact]
    public void a_form_field_still_occupies_its_place_in_the_paint_order()
    {
        // Skipped by the PICK, not dropped from the model. A page whose form
        // widgets were missing would report the wrong stacking for everything
        // above them.
        var page = DocumentModelBuilder.BuildPage(
            0, [Stamp(0, 0.10), Widget(1, 0.40), Stamp(2, 0.70)]);

        Assert.Equal(3, page.Objects.Count);
        Assert.Equal(DocumentObjectKind.FormField, page.Objects[1].Kind);
        Assert.Equal(1, page.Objects[1].ZOrder);
    }

    [Fact]
    public void the_widget_subtype_matches_the_number_the_core_sends()
    {
        // Two copies of this table exist, one per language, because the viewport
        // library cannot reference the app.
        Assert.Equal(11, PdfAnnotationSubtype.Widget);
    }

    // ---------------- the click ----------------

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

    private static string SelectCase()
    {
        string page = Source("PdfEditorApp", "MainPage.xaml.cs");
        int at = page.IndexOf("case ToolMode.Select:", StringComparison.Ordinal);
        Assert.True(at > 0, "the Select tool no longer has a case");

        int next = page.IndexOf("\n            case ToolMode.", at + 10, StringComparison.Ordinal);
        return page[at..(next > at ? next : page.Length)];
    }

    [Fact]
    public void a_select_click_operates_a_form_field_before_it_picks_up_anything()
    {
        // ⚠️ ORDER IS THE BEHAVIOUR. SelectAnnotationAt is what dragged the real
        // form's fields around, so the field test has to come first.
        string body = SelectCase();

        int form = body.IndexOf("ViewModel.FillableFieldAt(content.Page, nx, ny)", StringComparison.Ordinal);
        int annotation = body.IndexOf("ViewModel.SelectAnnotationAt(", StringComparison.Ordinal);
        int text = body.IndexOf("ViewModel.BeginTextSelection(", StringComparison.Ordinal);

        Assert.True(form > 0, "a Select click does not look for a form field at all");
        Assert.True(form < annotation, "a form field is tested after object selection");
        Assert.True(form < text, "a form field is tested after text selection");
    }

    [Fact]
    public void a_select_click_on_a_form_field_ends_the_gesture()
    {
        // Marking it handled and breaking is what stops the same press also
        // starting a drag, a marquee or a text selection.
        string body = SelectCase();

        int form = body.IndexOf("ViewModel.FillableFieldAt(content.Page, nx, ny)", StringComparison.Ordinal);
        int handled = body.IndexOf("e.Handled = true;", form, StringComparison.Ordinal);
        int brk = body.IndexOf("break;", handled, StringComparison.Ordinal);
        int annotation = body.IndexOf("ViewModel.SelectAnnotationAt(", StringComparison.Ordinal);

        Assert.True(handled > form && handled < annotation);
        Assert.True(brk > handled && brk < annotation);
    }

    [Fact]
    public void operating_a_form_field_needs_no_mode_to_be_entered_first()
    {
        // The whole point of the fix. Nobody opens a PDF, finds a tick box, and
        // goes looking for a mode.
        string body = SelectCase();

        int form = body.IndexOf("ViewModel.FillableFieldAt(", StringComparison.Ordinal);
        string upTo = body[..form];

        Assert.DoesNotContain("FormFillMode", upTo, StringComparison.Ordinal);
    }

    [Fact]
    public void every_kind_of_field_is_routed_to_its_own_interaction()
    {
        string page = Source("PdfEditorApp", "MainPage.xaml.cs");
        int at = page.IndexOf("private void HandleFormFieldClick(", StringComparison.Ordinal);
        Assert.True(at > 0);

        int next = page.IndexOf("\n    /// <summary>", at, StringComparison.Ordinal);
        string body = page[at..(next > at ? next : page.Length)];

        Assert.Contains("FormFieldKind.Text", body, StringComparison.Ordinal);
        Assert.Contains("BeginFormFieldEdit(", body, StringComparison.Ordinal);
        Assert.Contains("field.IsToggle", body, StringComparison.Ordinal);
        Assert.Contains("SetFormFieldState(field, field.GroupIndex, on)", body, StringComparison.Ordinal);
        Assert.Contains("field.IsChoice", body, StringComparison.Ordinal);
        Assert.Contains("ShowFieldOptions(", body, StringComparison.Ordinal);
    }

    [Fact]
    public void showing_the_fields_is_a_view_option_and_no_longer_a_mode()
    {
        // ⚠️ IT USED TO INTERCEPT THE POINTER, whatever tool was armed, which
        // made filling a form something a reader had to discover and switch
        // into. A field is operated by an ordinary click now, so all that is
        // left is the answer to "which of these are fields": a thing to draw,
        // not a way to behave.
        string vm = Source("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");
        string page = Source("PdfEditorApp", "MainPage.xaml.cs");

        Assert.Contains("public partial bool ShowFormFields", vm, StringComparison.Ordinal);
        Assert.Contains("private void DistributeFormOutlines()", vm, StringComparison.Ordinal);

        Assert.DoesNotContain("FormFillMode", vm, StringComparison.Ordinal);
        Assert.DoesNotContain("FormFillMode", page, StringComparison.Ordinal);
    }

    // ---------------- the repaint ----------------

    /// <summary>
    /// One method's CODE. Comment lines are dropped, so that commenting a call
    /// out counts as removing it: without that these read a commented-out line
    /// as the call still being made, which was measured.
    /// </summary>
    private static string ViewModelBody(string signature)
    {
        string vm = Source("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");
        int at = vm.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at > 0, $"there is no {signature}");

        int next = vm.IndexOf("\n    /// <summary>", at, StringComparison.Ordinal);
        string body = vm[at..(next > at ? next : vm.Length)];

        return string.Join('\n', Array.FindAll(
            body.Split('\n'), line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
    }

    [Fact]
    public void a_form_edit_throws_away_the_pixels_as_well_as_the_caches()
    {
        // ⚠️ THE SECOND REAL-FORM FAILURE. The write was correct all along: on
        // the user's own file a checkbox went from 16 dark pixels to 113 and a
        // combo from 0 to 349 showing its new label. What never happened was the
        // repaint, so the file changed and the screen did not. Their log carried
        // no repaint line for the entire session.
        //
        // RenderCurrentPage refreshes the annotation OVERLAY. Form fields are
        // painted by PDFium into the page BITMAP, which only this releases.
        string body = ViewModelBody("public bool SetFormFieldState(");

        Assert.Contains("InvalidateAllPageRasters()", body, StringComparison.Ordinal);
    }

    [Fact]
    public void undoing_a_form_edit_throws_away_the_pixels_too()
    {
        // A document-scope entry carries no per-page records, so the repaint the
        // record loop performs never fires for one. Undo was as invisible as the
        // edit had been.
        string body = ViewModelBody("private void RestoreDocumentBytes(byte[] bytes)");

        Assert.Contains("InvalidateAllPageRasters()", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_raster_invalidation_releases_every_page_and_redraws_the_visible_one()
    {
        // Releasing every page is what makes it correct after a whole-document
        // rewrite; redrawing only the visible one is what keeps it affordable on
        // a document with three thousand pages. The scroll pass renders the
        // others again when they come back into view.
        string body = ViewModelBody("private void InvalidateAllPageRasters()");

        Assert.Contains("foreach (var slot in PageSlots)", body, StringComparison.Ordinal);
        Assert.Contains("slot.ReleaseBitmap()", body, StringComparison.Ordinal);
        Assert.Contains("slot.ClearTiles()", body, StringComparison.Ordinal);
        Assert.Contains("RedrawPage(CurrentPageIndex)", body, StringComparison.Ordinal);
    }

    // ---------------- the refusals ----------------

    [Fact]
    public void a_multi_select_list_is_not_offered()
    {
        var multi = new FormField(
            0, FormFieldKind.ListBox, ReadOnly: false, Checked: false, GroupIndex: 0,
            MultiSelect: true, 0, 0, 0.3, 0.2, "Sizes", "M",
            Array.Empty<FormFieldOption>());

        Assert.False(multi.IsFillable);
        Assert.True((multi with { MultiSelect = false }).IsFillable);
    }

    [Fact]
    public void a_read_only_field_is_not_offered()
    {
        var locked = new FormField(
            0, FormFieldKind.Checkbox, ReadOnly: true, Checked: false, GroupIndex: 0,
            MultiSelect: false, 0, 0, 0.02, 0.02, "Agree", "Off",
            Array.Empty<FormFieldOption>());

        Assert.False(locked.IsFillable);
    }
}
