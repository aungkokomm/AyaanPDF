using System;
using System.Collections.Generic;

namespace PdfEditorApp.Viewport;

/// <summary>A rectangle in normalized page coordinates (both axes over page width).</summary>
public readonly record struct EditRect(double Left, double Top, double Right, double Bottom);

/// <summary>
/// One reversible change to one annotation, holding BOTH the state before the
/// edit and the state after it.
///
/// Symmetric on purpose. The previous design stored only "the state to restore
/// to" and asked the view model to capture the current state at undo time to
/// build the redo step. That works only if capturing is exact and cheap for
/// every scope, and it made undo and redo travel different code, so they could
/// (and did) disagree. With before and after both recorded, undo applies
/// Before, redo applies After, and there is nothing to capture in between.
///
/// Records address their target by stable <see cref="Id"/>, never by index:
/// every write in this app deletes and re-adds, so an index recorded at edit
/// time names a different annotation by the time undo runs.
/// </summary>
public abstract record EditRecord(Guid Id, int PageIndex);

/// <summary>An annotation moved or resized. Covers drag, nudge, align, distribute.</summary>
public sealed record BoundsRecord(Guid Id, int PageIndex, EditRect Before, EditRect After)
    : EditRecord(Id, PageIndex);

/// <summary>
/// An annotation's tag changed: colour, fill, stroke width, opacity, rotation,
/// or a text box's words and style. The tag is the app's complete description
/// of one of its own marks, so swapping it back is a full reversal.
/// </summary>
public sealed record TagRecord(Guid Id, int PageIndex, string BeforeTag, string AfterTag, EditRect Rect)
    : EditRecord(Id, PageIndex);

/// <summary>
/// An annotation was created or destroyed.
///
/// <paramref name="ExistsAfter"/> says which way round: true for a creation
/// (undo deletes it, redo puts it back), false for a deletion (undo puts it
/// back, redo removes it). Recreation is driven from <paramref name="Tag"/>,
/// which fully describes shapes and text boxes. Kinds whose content is not
/// recoverable from a tag - an image stamp, whose pixels live in the file -
/// set <paramref name="Recoverable"/> false, and the caller pairs the entry
/// with a document snapshot instead.
/// </summary>
public sealed record ExistenceRecord(
    Guid Id, int PageIndex, string Tag, EditRect Rect, bool ExistsAfter, bool Recoverable)
    : EditRecord(Id, PageIndex);

/// <summary>
/// Session grouping changed. Groups are held in the view model rather than in
/// the PDF, so the whole before/after membership is small enough to store
/// outright. Covers group, ungroup, and the implicit regrouping that happens
/// when a mark already in a group is put into another one.
/// </summary>
public sealed record GroupsRecord(
    IReadOnlyList<IReadOnlyList<Guid>> Before,
    IReadOnlyList<IReadOnlyList<Guid>> After)
    : EditRecord(Guid.Empty, -1);
