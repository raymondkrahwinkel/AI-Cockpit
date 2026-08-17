namespace Cockpit.Core.Wireframe.Model;

// Errors are data, not exceptions: the source box shows them next to the text it could not read, and a wireframe
// with one bad line still renders the rest. Root is null for an empty source or a source with no usable screen.
public sealed record WireframeParseResult(WireframeNode? Root, IReadOnlyList<WireframeParseError> Errors);
