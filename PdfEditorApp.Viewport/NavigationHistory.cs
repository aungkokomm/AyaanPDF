using System;
using System.Collections.Generic;

namespace PdfEditorApp.Viewport;

/// <summary>Somewhere the reader has been, as a page and how far down it.</summary>
/// <remarks>
/// No zoom. Browser back does not resize the window, and a Back that silently
/// changed the zoom would be a second surprise on top of the movement.
/// </remarks>
public readonly record struct NavigationPoint(int PageIndex, double PageFraction);

/// <summary>
/// Where the reader has been, so they can get back.
///
/// The gap this fills: jump to a bookmark from page 1500 of a 3352-page book
/// and there is no way back except remembering the number. Every reader and
/// every browser has this; the app had the position machinery for it already
/// and nothing joining it up.
///
/// The browser model exactly, because it is the one people already know: a
/// list with a cursor on the CURRENT position. Going somewhere new throws away
/// whatever was ahead, which is what makes forward mean "undo my back" rather
/// than "somewhere I went once".
/// </summary>
public sealed class NavigationHistory
{
    /// <summary>
    /// How many places are kept. Fifty is far past what anyone retraces by
    /// hand, and the whole list is smaller than a single page's thumbnail.
    /// </summary>
    public const int Max = 50;

    /// <summary>
    /// How far a move has to go before it counts as a jump.
    ///
    /// Reading forwards must NOT fill the history: if every page turn were
    /// recorded, Back would step back one page, which is what PageUp already
    /// does, and the one thing Back is for, undoing a jump across the
    /// document, would be buried under a hundred entries.
    /// </summary>
    public const int MinJumpPages = 3;

    private readonly List<NavigationPoint> _points = [];
    private int _cursor = -1;

    /// <summary>Whether a move between these pages is a jump rather than reading on.</summary>
    public static bool IsWorthRecording(int fromPage, int toPage) =>
        Math.Abs(toPage - fromPage) >= MinJumpPages;

    public bool CanGoBack => _cursor > 0;

    public bool CanGoForward => _cursor >= 0 && _cursor < _points.Count - 1;

    /// <summary>How many places are held, for tests and diagnostics.</summary>
    public int Count => _points.Count;

    /// <summary>
    /// Starts again from one place. Called when a document opens: history
    /// belongs to a document, and offering to go "back" into the previous one
    /// would be nonsense.
    /// </summary>
    public void Reset(NavigationPoint start)
    {
        _points.Clear();
        _points.Add(start);
        _cursor = 0;
    }

    /// <summary>
    /// Records a jump from one place to another.
    ///
    /// <paramref name="from"/> updates the entry the cursor is on rather than
    /// being appended, because the reader has almost certainly scrolled since
    /// arriving there and it is where they are NOW that Back has to return to.
    /// Without it, Back would land wherever they happened to enter that page.
    /// </summary>
    public void Record(NavigationPoint from, NavigationPoint to)
    {
        if (_cursor < 0)
        {
            Reset(from);
        }
        else
        {
            _points[_cursor] = from;
        }

        // Anything ahead of the cursor is a future the reader has just chosen
        // not to have.
        if (_cursor < _points.Count - 1)
        {
            _points.RemoveRange(_cursor + 1, _points.Count - _cursor - 1);
        }

        _points.Add(to);
        _cursor = _points.Count - 1;

        // Trim from the OLDEST end, and move the cursor with it so it keeps
        // pointing at the same place.
        while (_points.Count > Max)
        {
            _points.RemoveAt(0);
            _cursor--;
        }
    }

    /// <summary>The previous place, or null if there is none.</summary>
    public NavigationPoint? Back()
    {
        if (!CanGoBack)
        {
            return null;
        }

        _cursor--;
        return _points[_cursor];
    }

    /// <summary>The place Back came from, or null.</summary>
    public NavigationPoint? Forward()
    {
        if (!CanGoForward)
        {
            return null;
        }

        _cursor++;
        return _points[_cursor];
    }

    /// <summary>
    /// Updates where the cursor points, without recording a move.
    ///
    /// So that scrolling after arriving somewhere is not lost: the next jump
    /// records where the reader actually is, not where they landed.
    /// </summary>
    public void NoteCurrent(NavigationPoint where)
    {
        if (_cursor >= 0)
        {
            _points[_cursor] = where;
        }
    }
}
