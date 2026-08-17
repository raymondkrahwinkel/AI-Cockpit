namespace Cockpit.Plugin.Diagram.Wireframe.Model;

// Same keyword-is-the-name rule as WireframeNodeKind. W and H are flex weights, never pixels (AC-871).
internal enum WireframeModifierName
{
    Primary,
    Selected,
    Checked,
    Disabled,
    W,
    H,
    Align,
    Value,
}
