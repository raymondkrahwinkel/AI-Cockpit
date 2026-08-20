namespace Cockpit.Core.Wireframe.Model;

// Message is operator-facing (English, like the rest of the wireframe surface's UI text since AC-900) and Line is
// 1-based, so the source box can point at the line it came from.
public sealed record WireframeParseError(int Line, string Message);
