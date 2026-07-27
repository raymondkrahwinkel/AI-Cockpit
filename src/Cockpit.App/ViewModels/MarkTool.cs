namespace Cockpit.App.ViewModels;

/// <summary>
/// A tool that puts something on the capture (AC-359), as opposed to one that chooses what to take. They share a
/// drag, a list and an undo — which is the point of the mark layer, and why this is one enum rather than a flag
/// per tool that could be on at the same time as another.
/// </summary>
public enum MarkTool
{
    /// <summary>Drag a box over what should not leave the machine (AC-331).</summary>
    Redaction,

    /// <summary>Drag a frame around what the model should look at.</summary>
    Outline,

    /// <summary>Drag from where the arrow starts to the one thing it points at.</summary>
    Arrow,

    /// <summary>Drag a band over something to wash it in colour without hiding what it says.</summary>
    Highlight,

    /// <summary>Draw freehand — round a thing, through a thing, along a path the other tools cannot describe.</summary>
    Stroke,

    /// <summary>Click a spot and type a note onto the capture.</summary>
    Text,
}
