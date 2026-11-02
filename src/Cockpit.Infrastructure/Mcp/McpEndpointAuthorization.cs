using Cockpit.Core.Mcp;
using Cockpit.Core.Sessions;

namespace Cockpit.Infrastructure.Mcp;

// AC-1148: the one authorization decision every cockpit MCP endpoint makes on the identity McpAuthMiddleware
// already verified, before tool dispatch. Authentication says who is calling; this says whether that caller may
// be here at all. Fail-closed: an identity nothing granted is refused, never let through for want of a rule.
internal static class McpEndpointAuthorization
{
    // `paneId` is the verified identity: a pane, the node caller's reserved id, or null for the app key.
    // `isEnabled` is the live master switch (AC-34), read now so a flip lands on the next call; `nodeScopeGranted`
    // whether the pairing may use anything (AC-794); `nodeOnly` whether a controller may be here at all (AC-856).
    public static bool Allows(string? paneId, string serverName, bool isEnabled, bool nodeScopeGranted, bool nodeOnly, SessionMcpMounts mounts) =>
        paneId switch
        {
            // The app's own key names no session (AC-40/AC-89): it is the cockpit calling itself in-process, and
            // there is no mount decision to hold it to.
            null => true,

            // A controller reaches what this node's operator switched on, ticked a scope for, and built to face a
            // controller (AC-856) — an empty scope is refused at the door, not per tool. Stated here as well as at
            // the bind: a missing socket enforces the rule only for as long as nothing else opens one.
            NodeCallerIdentity.PaneId => nodeOnly && isEnabled && nodeScopeGranted,

            // And a session reaches exactly the endpoints its launch mounted for it.
            _ => isEnabled && mounts.IsMounted(paneId, serverName),
        };
}
