using System;

namespace PdfEditorApp.Viewport;

/// <summary>
/// Telling a click apart from a drag, and how far the drag has come.
/// </summary>
/// <remarks>
/// ⚠️ A PRESS IS NOT YET A DRAG, AND THAT IS THE WHOLE JOB. A press inside the
/// selected text box already means "put a caret here", so a move cannot simply
/// begin on press without taking that gesture away. It begins only once the
/// pointer has travelled far enough that no one could have meant to click, and
/// until then the press is still a click.
///
/// ⚠️ MEASURED ACROSS THE PAGE, NOT ACROSS THE SCREEN. Everything here is in
/// normalized page units, both axes over the page WIDTH, which is what the
/// pointer handlers already work in. A threshold in those units means the same
/// fraction of the reader's document at any zoom, where one in screen pixels
/// would let a shaky hand move a whole line at low zoom and nothing at all at
/// high.
/// </remarks>
public sealed class DragGesture
{
    /// <summary>
    /// How far the pointer must travel before a press becomes a drag, as a
    /// fraction of the page width. About four points on A4.
    /// </summary>
    public const double Threshold = 0.007;

    private double _fromX;
    private double _fromY;
    private bool _pressed;

    /// <summary>Which page the press landed on, or -1 when nothing is armed.</summary>
    public int Page { get; private set; } = -1;

    /// <summary>Whether the pointer has travelled far enough to mean it.</summary>
    public bool IsDragging { get; private set; }

    /// <summary>How far it has come since the press. Zero until it is dragging.</summary>
    public double Dx { get; private set; }

    public double Dy { get; private set; }

    /// <summary>Arms a press that MIGHT become a drag.</summary>
    public void Press(int page, double x, double y)
    {
        Page = page;
        _fromX = x;
        _fromY = y;
        _pressed = true;
        IsDragging = false;
        Dx = 0;
        Dy = 0;
    }

    /// <summary>
    /// Takes the pointer's new position. Returns true on the move that turns
    /// the press into a drag, and only that one, so a caller can act on the
    /// change without having to remember the last answer.
    /// </summary>
    public bool Move(double x, double y)
    {
        if (!_pressed) { return false; }

        double dx = x - _fromX;
        double dy = y - _fromY;

        if (!IsDragging)
        {
            // ⚠️ THE DISTANCE, NOT EITHER AXIS ON ITS OWN. Testing them apart
            // would start a drag on a pointer that had wandered a little in
            // both directions and travelled nowhere.
            if (Math.Sqrt((dx * dx) + (dy * dy)) < Threshold)
            {
                return false;
            }
            IsDragging = true;
            Dx = dx;
            Dy = dy;
            return true;
        }

        Dx = dx;
        Dy = dy;
        return false;
    }

    /// <summary>
    /// Ends the gesture and says whether it was a drag. A press that never
    /// travelled returns false, and the caller treats it as the click it was.
    /// </summary>
    public bool Release()
    {
        bool dragged = IsDragging;
        Clear();
        return dragged;
    }

    /// <summary>Forgets the gesture, leaving nothing armed.</summary>
    public void Clear()
    {
        _pressed = false;
        IsDragging = false;
        Page = -1;
        Dx = 0;
        Dy = 0;
    }
}
