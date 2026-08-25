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
/// A page's paint order changed: Bring to Front, Send to Back, and the one-step
/// moves.
///
/// The whole order is stored, before and after, rather than a description of
/// the change. It is a list of Guids, so a page of fifty marks costs about a
/// kilobyte, against the WHOLE PDF that a document snapshot copied for every
/// click of one of these buttons.
///
/// Reversing is the same operation in the other direction: re-add the objects
/// after the first disagreement, in the wanted order, because appending is the
/// only ordering primitive PDFium has. So undo and redo run identical code and
/// cannot drift apart.
/// </summary>
public sealed record OrderRecord(
    int Page,
    IReadOnlyList<Guid> Before,
    IReadOnlyList<Guid> After)
    : EditRecord(Guid.Empty, Page);

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

/// <summary>
/// One WORD of the document's own text was rewritten.
///
/// ⚠️ KEYED BY OBJECT INDEX, not by Guid like every other record here, and that
/// difference is the whole hazard. A page's own text is not an annotation and
/// carries no identity we put there, so the only handle on it is where it sits
/// in the page's content.
///
/// Content indices are not eternal. Nothing in this first version removes an
/// object, so they do not shift underneath a normal edit, but the searchable
/// layer written for our own text boxes does add page content, and a document
/// can be reloaded. So <see cref="Before"/> is not only what to restore: it is
/// what the objects must still SAY for the record to be applied at all. If the
/// page no longer reads that way, the record refuses rather than overwriting
/// text it was never about.
/// </summary>
public sealed record WordTextRecord(
    int Page,
    IReadOnlyList<int> Objects,
    string Before,
    string After)
    : EditRecord(Guid.Empty, Page);

/// <summary>
/// A URI link was created, retargeted or removed.
///
/// <paramref name="Before"/> and <paramref name="After"/> are the URL on each
/// side, and null means the link is not there: null to a URL is a creation, a
/// URL to null is a deletion, and one URL to another is a retarget. So all three
/// commands share one record and one reversal.
///
/// ⚠️ KEYED BY RECTANGLE AND URL, not by Guid, for the same reason
/// <see cref="WordTextRecord"/> is keyed by object index: a link is often not
/// ours. A document's own links carry no identity we put there, and stamping one
/// would mean writing to a link the user only wanted to follow.
///
/// An annotation index would not do either. Every write in this app renumbers
/// them, so an index recorded at edit time names something else by the time undo
/// runs. The rectangle survives all of that, and the URL is checked as well as
/// restored: if the link at that rectangle no longer says what the record
/// expects, the step refuses rather than retargeting a link it was never about.
/// </summary>
public sealed record LinkRecord(
    int Page,
    EditRect Rect,
    string? Before,
    string? After)
    : EditRecord(Guid.Empty, Page);
