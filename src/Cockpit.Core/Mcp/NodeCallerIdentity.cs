namespace Cockpit.Core.Mcp;

// AC-791/AC-790: the authorization model for a caller that reached this cockpit over the network node
// listener. One reserved pane id per node (not per controller, per AC-742/AC-792), stamped from the shared
// secret, with a constant lifetime tied to that secret rather than a session — see AC-1013 for the fuller model.
public static class NodeCallerIdentity
{
    // The pane id every request authenticated by the node's shared secret is stamped with. Shaped like
    // `AssistantIdentity.PaneId`: checked against `McpRequestContext.CurrentPaneId`, stamped host-side from the
    // transport, so it's not a secret and no tool argument can move it. A generated pane id can never collide.
    public const string PaneId = "cockpit-node-controller";
}
