namespace Cockpit.Plugin.Diagram.Collab;

// AC-910: what one surface fills in for a shared "Ask the agent…" — one primitive, three descriptors. `ObjectRef`
// is what the agent could address the object with over MCP, null where there is none (whiteboard). `ObjectLabel` is
// the human-facing description alongside it, already worded by the call site — see the three body call sites.
internal sealed record AskContext(string SurfaceKind, string SurfaceId, string SurfaceName, string? ObjectRef, string? ObjectLabel);
