namespace Cockpit.Core.Wireframe.Model;

// Same keyword-is-the-name rule as WireframeNodeKind. W and H are flex weights, never pixels (AC-871).
public enum WireframeModifierName
{
    Primary,
    Selected,
    Checked,
    Disabled,
    W,
    H,
    Align,
    Value,
    Goto,
    // AC-907: a requirement the format cannot draw — never rendered, never a layout input.
    Note,
    // AC-914: on a `state` only — the id of the container in its own screen whose content it stands in for.
    Replaces,
}
