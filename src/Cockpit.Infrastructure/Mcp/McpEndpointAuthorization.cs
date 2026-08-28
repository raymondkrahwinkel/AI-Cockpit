using Cockpit.Core.Mcp;
using Cockpit.Core.Sessions;

namespace Cockpit.Infrastructure.Mcp;

// AC-1148: the one authorization decision every cockpit MCP endpoint makes on the identity McpAuthMiddleware
// already verified, before tool dispatch. Authentication says who is calling; this says whether that caller may
// be here at all. Fail-closed: an identity nothing granted is refused, never let through for want of a rule.
internal static class McpEndpointAuthorization
{
    // `paneId` is the verified identity: a session's pane, the node caller's reserved id, or null for the app key.
    // `isEnabled` is the endpoint's live master switch (AC-34), read now so flipping it off lands on the next call;
    // `nodeScopeGranted` says whether the pairing may use anything here at all (AC-794).
    public static bool Allows(string? paneId, string serverName, bool isEnabled, bool nodeScopeGranted, SessionMcpMounts mounts) =>
        paneId switch
        {
            // The app's own key names no session (AC-40/AC-89): it is the cockpit calling itself in-process, and
            // there is no mount decision to hold it to.
            null => true,

            // A controller reaches what this node's operator switched on and ticked a scope for — a fresh or
            // just-revoked pairing grants nothing, so an empty scope is refused at the door instead of per tool.
            NodeCallerIdentity.PaneId => isEnabled && nodeScopeGranted,

            // And a session reaches exactly the endpoints its launch mounted for it.
            _ => isEnabled && mounts.IsMounted(paneId, serverName),
        };
}
