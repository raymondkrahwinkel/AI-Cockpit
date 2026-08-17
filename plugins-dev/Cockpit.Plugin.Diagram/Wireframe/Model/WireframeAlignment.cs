namespace Cockpit.Plugin.Diagram.Wireframe.Model;

// The values `align:` accepts. Kept out of Avalonia's HorizontalAlignment on purpose: the parser validates the
// word without depending on a UI framework, and the renderer maps it.
internal enum WireframeAlignment
{
    Left,
    Center,
    Right,
}
