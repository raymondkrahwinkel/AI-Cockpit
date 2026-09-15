namespace Cockpit.Core.Assistant;

// Who the assistant is, in the two names the mount rule is built out of (AC-544, criterion 2). Lives in Core
// because App and Infrastructure cannot see each other, so a constant copied into both could drift silently
// — a renamed pane id would not fail to compile, it would just quietly stop matching.
public static class AssistantIdentity
{
    // The pane id the assistant is always known by, and the only identity the broad read tools answer to.
    // Not a secret: checked against `McpRequestContext.CurrentPaneId`, stamped host-side from the
    // request's own per-session bearer (AC-89) and unmovable by any argument.
    public const string PaneId = "cockpit-assistant";

    // The MCP server the broad read tools are hosted under. Registered as an internal endpoint, so it never fans
    // out to a session that did not name it — and the assistant's own launch is the only place in the codebase
    // that names it.
    public const string McpServerName = "cockpit-assistant";

    // The MCP server the assistant's *acting* tools are hosted under (AC-545): starting, stopping and
    // placing sessions. Internal like `McpServerName`, guarded by the same per-tool `PaneId` check, and
    // separate so the read server's promise that nothing on it changes anything stays true.
    public const string ActMcpServerName = "cockpit-assistant-agents";

    // AC-1322: the inbox key a node's `notify cockpit-assistant` lands in while a paired controller holds the
    // line — the controller's assistant collects it on its next poll (`read_node_inbox`). Not a pane: nothing on
    // this machine answers to it, and when the controller drops away what waits here folds into `PaneId`.
    public const string ControllerInboxPaneId = "cockpit-assistant@controller";
}
