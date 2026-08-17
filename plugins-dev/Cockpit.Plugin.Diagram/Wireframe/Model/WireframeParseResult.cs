namespace Cockpit.Plugin.Diagram.Wireframe.Model;

// Errors are data, not exceptions: the source box shows them next to the text it could not read, and a wireframe
// with one bad line still renders the rest. Root is null for an empty source or a source with no usable screen.
internal sealed record WireframeParseResult(WireframeNode? Root, IReadOnlyList<WireframeParseError> Errors);
