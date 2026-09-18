using System;
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

    /// <summary>
    /// One user action expressed as a list of reversible <see cref="EditRecord"/>s.
    ///
    /// The preferred scope for anything that edits annotations. Each record
    /// carries the state before AND after, so undo and redo are the same walk
    /// in opposite directions and neither has to reconstruct the other's
    /// target. It also costs bytes rather than megabytes: a document snapshot
    /// for a nudge copies the whole PDF.
    /// </summary>
    Records,

    /// <summary>
    /// One or more annotation objects in the file were moved or resized.
    ///
    /// Its own scope because the inverse is exactly four numbers per object:
    /// put the rectangles back. Recording a whole-document snapshot for a
    /// nudge would cost megabytes per drag, and dragging something across a
    /// page is the most repeated edit there is.
    ///
    /// Only for edits with a cheap exact inverse. DELETING an annotation does
    /// not qualify, because undoing it means recreating content this entry
    /// does not hold, so that stays a <see cref="Document"/> snapshot.
    /// </summary>
    AnnotationBounds,

    /// <summary>
    /// A change made in Document properties: what a save will write into the
    /// file's properties, language and opening settings. Carries the pending
    /// state before and after, so like <see cref="Records"/> it describes its
    /// own reversal and costs a few strings rather than a document.
    /// </summary>
    Properties,
}

/// <summary>
/// The rectangle to restore one annotation to, in normalized page coordinates.
/// </summary>
/// <param name="Id">
/// The annotation's STABLE identity. Undo used to address its target by
/// (page, index), which is volatile: every edit deletes and re-adds, so by
/// the time undo ran the index could point at a different mark. The index is
/// still carried as a hint for the common case where nothing moved, but the
/// Id is what actually resolves the target.
/// </param>
public sealed record AnnotationBoundsState(
    int PageIndex, int Index, double Left, double Top, double Right, double Bottom, Guid Id, string? Tag = null);

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
    public IReadOnlyList<ShapeAnnotation> Shapes { get; init; } = [];
    public IReadOnlyList<NoteState> Notes { get; init; } = [];

    /// <summary>Set only for <see cref="HistoryScope.Document"/> entries.</summary>
    public byte[]? DocumentBytes { get; init; }

    /// <summary>
    /// Set for <see cref="HistoryScope.Records"/> entries: the individual
    /// changes this one user action made, in the order they were applied.
    /// Undo walks them BACKWARDS applying each Before; redo walks them
    /// forwards applying each After.
    ///
    /// A list because one user action routinely touches many objects: aligning
    /// a multi-selection, dragging a group, pasting several marks. Those must
    /// undo as ONE step, not as one step per object.
    /// </summary>
    public IReadOnlyList<EditRecord> Records { get; init; } = [];

    /// <summary>Set for <see cref="HistoryScope.Properties"/>: the pending properties before the change.</summary>
    public DocumentPropertiesState? PropertiesBefore { get; init; }

    /// <summary>Set for <see cref="HistoryScope.Properties"/>: the pending properties after the change.</summary>
    public DocumentPropertiesState? PropertiesAfter { get; init; }

    /// <summary>
    /// A document snapshot paired with records, for actions that include a
    /// change no record can reverse (deleting an image stamp, whose pixels are
    /// not recoverable from a tag). Undo restores the bytes and then still
    /// applies the records, so the cheap parts stay cheap.
    /// </summary>
    public byte[]? FallbackBytes { get; init; }

    /// <summary>
    /// Set only for <see cref="HistoryScope.AnnotationBounds"/> entries: the
    /// rectangles to put annotations back to.
    ///
    /// A LIST, because a move can carry a whole multi-selection. It used to be
    /// a single state recording the drag's anchor only, so undoing a group
    /// move returned one mark and left the rest where they had been dragged.
    /// </summary>
    public IReadOnlyList<AnnotationBoundsState> Bounds { get; init; } = [];

    /// <summary>The page in view when this state was captured.</summary>
    public int PageIndex { get; init; }

    /// <summary>
    /// Approximate memory cost, used to bound the stacks. Annotation entries
    /// are charged a nominal per-item cost; document entries are charged their
    /// real byte size, which for a large PDF can be megabytes.
    /// </summary>
    public long Cost =>
        DocumentBytes?.LongLength
        ?? FallbackBytes?.LongLength
        ?? (Records.Count > 0
                ? Records.Count * 256L
                : Bounds.Count > 0
                ? Bounds.Count * 64L
                : (Highlights.Count + InkStrokes.Count + Shapes.Count + Notes.Count) * 128L + 256L);
}
