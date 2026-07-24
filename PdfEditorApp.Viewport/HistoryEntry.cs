using System.Collections.Generic;

namespace PdfEditorApp.Viewport;

/// <summary>
/// How much state an undo step restores.
///
/// Annotation edits are cheap and reversible by swapping the overlay
/// collections. Page operations (rotate, delete) and form fills restructure
/// the PDF itself in ways no per-object inverse can express, so those are
/// recorded as whole-document byte snapshots.
/// </summary>
public enum HistoryScope
{
    Annotations,
    Document,
}

/// <summary>
/// A note's restorable state. Notes are the only mutable annotation (their
/// text is edited after creation), so history stores their values rather than
/// their references; highlights and ink strokes are immutable records and can
/// be captured by reference.
/// </summary>
public sealed record NoteState(int PageIndex, double X, double Y, string Text);

/// <summary>
/// One point in the document's history: the state to restore TO, not the
/// change that was made. Undo and redo therefore run through the exact same
/// apply path, and the only difference between them is which stack the entry
/// came from.
/// </summary>
public sealed class HistoryEntry
{
    public HistoryScope Scope { get; init; }

    /// <summary>Shown in the UI, e.g. "Delete page".</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>
    /// Whether the document had unsaved changes at capture time. Undoing back
    /// to a saved state should clear the dirty flag rather than leave the user
    /// prompted to save a document that matches what is on disk.
    /// </summary>
    public bool WasDirty { get; init; }

    public IReadOnlyList<HighlightAnnotation> Highlights { get; init; } = [];
    public IReadOnlyList<InkStrokeAnnotation> InkStrokes { get; init; } = [];
    public IReadOnlyList<NoteState> Notes { get; init; } = [];

    /// <summary>Set only for <see cref="HistoryScope.Document"/> entries.</summary>
    public byte[]? DocumentBytes { get; init; }

    /// <summary>The page in view when this state was captured.</summary>
    public int PageIndex { get; init; }

    /// <summary>
    /// Approximate memory cost, used to bound the stacks. Annotation entries
    /// are charged a nominal per-item cost; document entries are charged their
    /// real byte size, which for a large PDF can be megabytes.
    /// </summary>
    public long Cost =>
        DocumentBytes?.LongLength
        ?? ((Highlights.Count + InkStrokes.Count + Notes.Count) * 128L + 256L);
}
