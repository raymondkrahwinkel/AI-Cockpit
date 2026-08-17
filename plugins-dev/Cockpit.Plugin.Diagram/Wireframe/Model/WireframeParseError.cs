namespace Cockpit.Plugin.Diagram.Wireframe.Model;

// Message is operator-facing (Dutch, like the rest of this plugin's UI text) and Line is 1-based, so the source
// box can point at the line it came from.
internal sealed record WireframeParseError(int Line, string Message);
