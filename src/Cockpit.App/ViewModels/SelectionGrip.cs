namespace Cockpit.App.ViewModels;

/// <summary>
/// One of the eight handles on a marked-out selection (AC-565): the four corners and the four side midpoints.
/// Dragging one moves only the edge or edges it sits on; the rest of the rectangle stays where it was.
/// </summary>
public enum SelectionGrip
{
    TopLeft,
    Top,
    TopRight,
    Right,
    BottomRight,
    Bottom,
    BottomLeft,
    Left,
}
