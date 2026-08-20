using Cockpit.Core.Wireframe.Model;

namespace Cockpit.Core.Wireframe;

// AC-902: `goto:"Title"` names a screen by its title, not its id, because that is what the operator wrote — this is
// the one place that title gets resolved against the document's actual screens, shared by the parser's post-pass
// (which turns a bad one into an error), the MCP payload (which turns a good one into the target's id) and the
// overlay (which turns a good one into an arrow or a marker).
public static class WireframeGotoResolver
{
    public static WireframeGotoResolution Resolve(IReadOnlyList<WireframeNode> screens, string title)
    {
        var matches = screens.Where(screen => screen.Text == title).ToList();
        return matches.Count switch
        {
            0 => new WireframeGotoResolution(null, $"'{title}' is not a screen in this wireframe."),
            1 => new WireframeGotoResolution(matches[0], null),
            _ => new WireframeGotoResolution(null, $"'{title}' names {matches.Count} screens in this wireframe — give it a title that is unique."),
        };
    }
}

// The screen a `goto:` title resolved to, or the reason it did not — never both.
public sealed record WireframeGotoResolution(WireframeNode? Screen, string? Error);
