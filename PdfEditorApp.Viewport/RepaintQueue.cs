using System;
using System.Collections.Generic;
using System.Linq;

namespace PdfEditorApp.Viewport;

/// <summary>
/// Which pages still need their pixels redrawn, and when to do it.
///
/// Repainting a page is the expensive half of invalidating one: the cheap half
/// drops a dictionary entry, this half throws away the bitmap and every tile
/// and renders the page again. The two were bundled together, so an operation
/// that had to drop the annotation cache N times to re-resolve N indices also
/// repainted the page N times. A five-shape group move paid for five.
///
/// The rule is one repaint per user command, per page. Marks made inside a
/// command are collected and flushed at its end; marks made outside one repaint
/// immediately, which is the behaviour every existing call site already has.
/// Batching is therefore opt-in: a caller that has not been taught to open a
/// command keeps working exactly as before, and cannot silently defer a repaint
/// that never arrives.
/// </summary>
public sealed class RepaintQueue
{
    private readonly HashSet<int> _pages = new();
    private int _depth;

    /// <summary>True while at least one command is open.</summary>
    public bool InCommand => _depth > 0;

    /// <summary>How many pages are waiting. For tests and diagnostics.</summary>
    public int PendingCount => _pages.Count;

    /// <summary>
    /// Opens a user command. Nestable, because commands call each other: a
    /// group move commits each member, and undo replays whole operations.
    /// </summary>
    public void Begin() => _depth++;

    /// <summary>
    /// Records that a page's pixels are stale.
    /// </summary>
    /// <returns>
    /// True if the caller should repaint right now, which is the case outside
    /// any command. False when the repaint has been deferred to <see cref="End"/>.
    /// </returns>
    public bool Mark(int page)
    {
        if (_depth == 0)
        {
            return true;
        }
        _pages.Add(page);
        return false;
    }

    /// <summary>
    /// Closes a command and returns the pages to repaint, once each, in page
    /// order. A nested close returns nothing: only the outermost command
    /// flushes, so an inner operation cannot repaint halfway through an outer
    /// one and be repainted again at the end.
    /// </summary>
    public IReadOnlyList<int> End()
    {
        if (_depth > 0)
        {
            _depth--;
        }

        if (_depth > 0)
        {
            return Array.Empty<int>();
        }

        // Ordered so a multi-page operation repaints top-down rather than in
        // whatever order the hash set happens to hold.
        var pages = _pages.OrderBy(p => p).ToList();
        _pages.Clear();
        return pages;
    }
}
