using System;
using System.Collections.Generic;
using System.Linq;

namespace PdfEditorApp.Viewport;

/// <summary>
/// Two-stack undo/redo.
///
/// Entries record the state to restore TO. Pushing a new edit clears redo,
/// because once you branch off a history there is nothing coherent to redo.
/// Undo pops the top undo entry, captures the CURRENT state as its inverse
/// onto the redo stack, and hands back the entry to apply; redo does the
/// mirror image, pushing onto the undo stack directly rather than through
/// <see cref="Push"/>, which would wipe the redo stack it is walking.
///
/// Both directions return an entry that the caller applies through one shared
/// path, so an asymmetry between undo and redo is not expressible here.
///
/// The stacks are bounded by BOTH count and total bytes: a document-scope
/// entry holds an entire PDF, so 50 page deletions on a large file would
/// otherwise pin hundreds of megabytes. Trimming drops the OLDEST undo
/// entries, since the recent past is what users actually reach for.
/// </summary>
public sealed class DocumentHistory
{
    private readonly List<HistoryEntry> _undo = [];
    private readonly List<HistoryEntry> _redo = [];
    private readonly int _maxEntries;
    private readonly long _byteBudget;

    public DocumentHistory(int maxEntries = 50, long byteBudget = 256L * 1024 * 1024)
    {
        _maxEntries = maxEntries;
        _byteBudget = byteBudget;
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public int UndoDepth => _undo.Count;
    public int RedoDepth => _redo.Count;

    /// <summary>Label of the next undo step, for menu text. Null if empty.</summary>
    public string? NextUndoLabel => _undo.Count > 0 ? _undo[^1].Label : null;

    public string? NextRedoLabel => _redo.Count > 0 ? _redo[^1].Label : null;

    /// <summary>Total bytes currently pinned by both stacks.</summary>
    public long TotalCost => _undo.Sum(e => e.Cost) + _redo.Sum(e => e.Cost);

    /// <summary>
    /// Records the state as it was BEFORE an edit. Call this immediately
    /// before mutating, never after.
    /// </summary>
    public void Push(HistoryEntry stateBeforeEdit)
    {
        _undo.Add(stateBeforeEdit);
        _redo.Clear();
        Trim();
    }

    /// <summary>
    /// Pops the next undo step and returns the state to apply, or null if
    /// there is nothing to undo (in which case nothing is captured and the
    /// stacks are untouched).
    ///
    /// <paramref name="captureCurrent"/> is invoked with the TARGET entry's
    /// scope and label, and its result becomes the redo entry. The history
    /// supplies the scope rather than the caller because the inverse must be
    /// captured at the same granularity as the step being reversed: capturing
    /// only annotations while undoing a page delete would leave redo with no
    /// document to restore.
    /// </summary>
    public HistoryEntry? Undo(Func<HistoryScope, string, HistoryEntry> captureCurrent)
    {
        if (_undo.Count == 0)
        {
            return null;
        }

        HistoryEntry target = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(captureCurrent(target.Scope, target.Label));
        return target;
    }

    /// <summary>
    /// The mirror of <see cref="Undo"/>. Pushes onto the undo stack directly,
    /// bypassing <see cref="Push"/> so the redo stack survives.
    /// </summary>
    public HistoryEntry? Redo(Func<HistoryScope, string, HistoryEntry> captureCurrent)
    {
        if (_redo.Count == 0)
        {
            return null;
        }

        HistoryEntry target = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(captureCurrent(target.Scope, target.Label));
        return target;
    }

    /// <summary>Drops all history, e.g. when a different document is opened.</summary>
    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }

    private void Trim()
    {
        while (_undo.Count > _maxEntries)
        {
            _undo.RemoveAt(0);
        }

        // Never trim to empty: the most recent step must always be undoable,
        // even if it is a snapshot larger than the whole budget on its own.
        while (_undo.Count > 1 && TotalCost > _byteBudget)
        {
            _undo.RemoveAt(0);
        }
    }
}
