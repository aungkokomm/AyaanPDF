using System;
using System.IO;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Deleting a selected unit of the document's own text.
///
/// ⚠️ THE RECORD IS POSITIONAL, and it has to be. Every other text record finds
/// its target again by what the page says, which cannot work here: a deleted
/// word leaves no cluster and a deleted line leaves no line, because both are
/// built from characters. The core's positional write is the address that
/// survives an empty range, and this is the wiring that uses it.
/// </summary>
public class TextDeleteTests
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

    private static string ViewModel() =>
        Read("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");

    /// <summary>One method's CODE, comment lines dropped, so that commenting a
    /// call out counts as removing it.</summary>
    private static string Body(string src, string signature)
    {
        int at = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at > 0, $"there is no {signature}");

        int next = src.IndexOf("    /// <summary>", at, StringComparison.Ordinal);
        string body = src[at..(next > at ? next : src.Length)];

        return string.Join(
            "|",
            Array.FindAll(
                body.Split('\n'),
                l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
    }

    // ---------------- one operation, one entry ----------------

    [Fact]
    public void a_deletion_opens_one_entry_and_closes_it_around_the_write()
    {
        // The same shape a retype uses, so however many objects the core
        // empties the reader undoes it in one step.
        string body = Body(ViewModel(), "public bool DeleteSelectedTextUnit()");

        int begin = body.IndexOf("BeginEdit(", StringComparison.Ordinal);
        int record = body.IndexOf("RecordEdit(new TextDeleteRecord(", StringComparison.Ordinal);
        int write = body.IndexOf("WriteAtAnchor(", StringComparison.Ordinal);
        int commit = body.IndexOf("CommitEdit()", StringComparison.Ordinal);

        Assert.True(begin > 0 && record > begin, "the entry is not opened before the record");
        Assert.True(write > record, "the record is written after the page changed");
        Assert.True(commit > write, "the entry is closed before the write happened");
    }

    [Fact]
    public void a_refused_deletion_leaves_no_step_to_undo()
    {
        // ⚠️ THE CORE LEAVES THE PAGE AS IT FOUND IT when it refuses, so a
        // history entry left behind would let the reader undo something that
        // never happened.
        string body = Body(ViewModel(), "public bool DeleteSelectedTextUnit()");

        int refused = body.IndexOf("!= RenderStatus.OkPdfium", StringComparison.Ordinal);
        int abandon = body.IndexOf("AbandonEdit()", StringComparison.Ordinal);
        int commit = body.IndexOf("CommitEdit()", StringComparison.Ordinal);

        Assert.True(refused > 0 && abandon > refused, "a refusal does not abandon the entry");
        Assert.True(abandon < commit, "the entry is committed before the refusal is handled");
    }

    // ---------------- what may be deleted ----------------

    [Fact]
    public void only_an_editable_unit_can_be_deleted()
    {
        // Every existing refusal rule stands: justified, rotated, complex
        // script, mixed style and the rest all arrive here as CanEdit false,
        // and none of them becomes deletable by being selected.
        string body = Body(ViewModel(), "private (int First, int Last, int Prefix, string Text)? SelectedUnitAnchor()");

        Assert.Contains("CanEdit: true", body, StringComparison.Ordinal);
        Assert.Contains("return null", body, StringComparison.Ordinal);
    }

    [Fact]
    public void both_units_the_selection_model_offers_can_be_deleted()
    {
        // A line knows its own range; a word carries the objects that draw it
        // and the offset it starts at. Both are the same triple the writes are
        // already identified by.
        string body = Body(ViewModel(), "private (int First, int Last, int Prefix, string Text)? SelectedUnitAnchor()");

        Assert.Contains("unit.Line is { } line", body, StringComparison.Ordinal);
        Assert.Contains("line.FirstObject, line.LastObject, line.PrefixChars", body, StringComparison.Ordinal);
        Assert.Contains("unit.Word is { } word", body, StringComparison.Ordinal);
        Assert.Contains("word.ObjectIndices[0], word.ObjectIndices[^1], word.PrefixChars", body, StringComparison.Ordinal);
    }

    // ---------------- undo and redo ----------------

    [Fact]
    public void undo_and_redo_are_the_same_write_with_the_strings_swapped()
    {
        // ⚠️ NOT TWO MECHANISMS. Redo taking a different path from undo is how
        // the two come to disagree, and here neither can: one call, one pair of
        // strings, chosen by direction.
        string body = Body(ViewModel(), "private void ApplyTextDelete(TextDeleteRecord record, bool backwards)");

        Assert.Contains("backwards ? string.Empty : record.Text", body, StringComparison.Ordinal);
        Assert.Contains("backwards ? record.Text : string.Empty", body, StringComparison.Ordinal);
        Assert.Contains("WriteAtAnchor(", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_history_knows_how_to_apply_a_deletion()
    {
        // A record type with no case in the dispatch is a step that silently
        // does nothing when undone.
        string vm = ViewModel();

        Assert.Contains("case TextDeleteRecord d:", vm, StringComparison.Ordinal);
        Assert.Contains("ApplyTextDelete(d, backwards);", vm, StringComparison.Ordinal);
    }

    [Fact]
    public void the_record_carries_a_position_and_never_looks_text_up()
    {
        // The whole reason this record exists rather than reusing the line one.
        string record = Read("PdfEditorApp.Viewport", "EditRecord.cs");
        int at = record.IndexOf("public sealed record TextDeleteRecord(", StringComparison.Ordinal);
        Assert.True(at > 0, "there is no TextDeleteRecord");

        string body = record.Substring(at, 220);
        Assert.Contains("FirstObject", body, StringComparison.Ordinal);
        Assert.Contains("LastObject", body, StringComparison.Ordinal);
        Assert.Contains("PrefixChars", body, StringComparison.Ordinal);
    }

    // ---------------- the key ----------------

    [Fact]
    public void delete_reaches_a_selected_text_unit_before_a_guide_or_an_annotation()
    {
        // Each step is what the reader most recently clicked, and no two of
        // them can be selected at once.
        string page = Read("PdfEditorApp", "MainPage.xaml.cs");
        int at = page.IndexOf("case VirtualKey.Delete:", StringComparison.Ordinal);
        Assert.True(at > 0, "Delete no longer has a case");

        string body = page.Substring(at, 900);
        int unit = body.IndexOf("DeleteSelectedTextUnit()", StringComparison.Ordinal);
        int guide = body.IndexOf("DeleteSelectedGuide()", StringComparison.Ordinal);
        int annotation = body.IndexOf("DeleteSelectedAnnotation()", StringComparison.Ordinal);

        Assert.True(unit > 0, "Delete does not reach a selected text unit");
        Assert.True(unit < guide && guide < annotation, "the order changed");
    }

    [Fact]
    public void delete_inside_the_text_editor_still_means_what_it_means_there()
    {
        // ⚠️ UNCHANGED, and it is the early return that keeps it so: the whole
        // key handler gives up before any of its cases when a text box has
        // focus. Backspace in the editor must delete a character, not the
        // paragraph the editor is sitting on.
        string page = Read("PdfEditorApp", "MainPage.xaml.cs");

        int guard = page.IndexOf("if (IsTextInputFocused)", StringComparison.Ordinal);
        int delete = page.IndexOf("case VirtualKey.Delete:", StringComparison.Ordinal);

        Assert.True(guard > 0, "the text-focus guard is gone");
        Assert.True(guard < delete, "the Delete case is now reachable while typing");
    }
}
