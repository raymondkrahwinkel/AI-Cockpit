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
}
