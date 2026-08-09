using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The hand tool's whole job is to look like a hand doing something. These
/// pin the states apart.
/// </summary>
public class CursorPolicyTests
{
    [Fact]
    public void the_armed_hand_tool_rests_open()
    {
        Assert.Equal(ViewportCursor.HandOpen, CursorPolicy.Resolve(ToolMode.Hand, spaceHandHeld: false, dragging: false));
    }

    [Fact]
    public void dragging_with_the_hand_tool_closes_it()
    {
        Assert.Equal(ViewportCursor.HandGrab, CursorPolicy.Resolve(ToolMode.Hand, spaceHandHeld: false, dragging: true));
    }

    [Fact]
    public void resting_and_dragging_never_look_the_same()
    {
        // The actual bug: both states answered SizeAll, so pressing the button
        // changed nothing on screen and the tool felt unresponsive. Asserting
        // the two are different catches any future collapse of the pair,
        // whatever the two cursors happen to be.
        Assert.NotEqual(
            CursorPolicy.Resolve(ToolMode.Hand, spaceHandHeld: false, dragging: false),
            CursorPolicy.Resolve(ToolMode.Hand, spaceHandHeld: false, dragging: true));
    }

    [Fact]
    public void space_borrows_the_hand_under_any_tool()
    {
        Assert.Equal(ViewportCursor.HandOpen, CursorPolicy.Resolve(ToolMode.Draw, spaceHandHeld: true, dragging: false));
        Assert.Equal(ViewportCursor.HandGrab, CursorPolicy.Resolve(ToolMode.Draw, spaceHandHeld: true, dragging: true));
    }

    [Fact]
    public void a_drag_that_is_not_a_pan_leaves_other_tools_alone()
    {
        // dragging is the PAN flag, not "a button is down". A shape being
        // dragged out must not turn the crosshair into a fist.
        Assert.Equal(ViewportCursor.Cross, CursorPolicy.Resolve(ToolMode.Shape, spaceHandHeld: false, dragging: true));
    }

    [Theory]
    [InlineData(ToolMode.Select, ViewportCursor.IBeam)]
    [InlineData(ToolMode.Highlight, ViewportCursor.IBeam)]
    [InlineData(ToolMode.Text, ViewportCursor.IBeam)]
    [InlineData(ToolMode.Draw, ViewportCursor.Cross)]
    [InlineData(ToolMode.Shape, ViewportCursor.Cross)]
    [InlineData(ToolMode.Note, ViewportCursor.Cross)]
    [InlineData(ToolMode.Stamp, ViewportCursor.Cross)]
    public void every_other_tool_keeps_the_cursor_it_had(ToolMode tool, ViewportCursor expected)
    {
        Assert.Equal(expected, CursorPolicy.Resolve(tool, spaceHandHeld: false, dragging: false));
    }
}
