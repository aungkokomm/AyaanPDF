namespace PdfEditorApp.Viewport;

/// <summary>
/// The cursor the viewport wants, independent of how the platform draws it.
///
/// The two hands are named states rather than a single "hand", because the
/// whole point of a hand tool is that resting and dragging look different.
/// </summary>
public enum ViewportCursor
{
    Arrow,
    IBeam,
    Cross,
    /// <summary>Open palm: the hand tool is armed but no button is down.</summary>
    HandOpen,
    /// <summary>Closed fist: the page is being dragged right now.</summary>
    HandGrab,
}

/// <summary>
/// Which cursor the active tool and drag state call for.
///
/// Extracted from MainPage because the bug it exists to prevent is invisible
/// to a reading of the switch: the hand tool used to answer SizeAll whether or
/// not the button was down, so picking it up and dragging it looked identical
/// and the tool felt dead. A test can assert the two states differ; a human
/// scanning a switch statement evidently cannot.
/// </summary>
public static class CursorPolicy
{
    /// <param name="tool">The armed tool.</param>
    /// <param name="spaceHandHeld">Space is down, which borrows the hand under any tool.</param>
    /// <param name="dragging">A pan is in progress: button down and captured.</param>
    public static ViewportCursor Resolve(ToolMode tool, bool spaceHandHeld, bool dragging)
    {
        // Space wins over the armed tool, exactly as it does for input routing,
        // so the cursor never disagrees with what a drag would actually do.
        if (spaceHandHeld || tool == ToolMode.Hand)
        {
            return dragging ? ViewportCursor.HandGrab : ViewportCursor.HandOpen;
        }

        return tool switch
        {
            ToolMode.Select or ToolMode.Highlight or ToolMode.Text => ViewportCursor.IBeam,
            ToolMode.Draw or ToolMode.Shape or ToolMode.Note or ToolMode.Stamp
                or ToolMode.Link => ViewportCursor.Cross,
            _ => ViewportCursor.Arrow,
        };
    }
}
