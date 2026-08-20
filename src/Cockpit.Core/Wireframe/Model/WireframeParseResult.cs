namespace Cockpit.Core.Wireframe.Model;

// Errors are data, not exceptions: the source box shows them next to the text it could not read, and a wireframe
// with one bad line still renders the rest. Screens is empty for an empty source or one with no usable screen.
public sealed record WireframeParseResult(
    IReadOnlyList<WireframeNode> Screens,
    IReadOnlyList<WireframeParseError> Errors,
    // AC-915: the document's own viewport line, above the first screen — null when it declares none, which reads as
    // desktop everywhere the size is needed.
    WireframeViewport? Viewport = null)
{
    // AC-901: a document is a list of screens, so "is there anything to show" is a count rather than a null check.
    public bool HasScreens => Screens.Count > 0;
}
