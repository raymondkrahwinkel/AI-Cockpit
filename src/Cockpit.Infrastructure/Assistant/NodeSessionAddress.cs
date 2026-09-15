using Cockpit.Core.Mcp;

namespace Cockpit.Infrastructure.Assistant;

// AC-1320: how list_sessions names a session on a paired node — "<node> · <paneId>", the "belongs to node X"
// shape the MCP registry already gives that node's endpoints, so the field the assistant addresses by can never
// equal a local pane id (a bare GUID). Until AC-1318 part d, every pane-taking tool refuses one before acting.
internal static class NodeSessionAddress
{
    public static string For(string nodeName, string paneId) => NodeServerName.For(nodeName, paneId);

    // The refusal for a node session's address, or null for a pane id that is this machine's.
    public static string? Refusal(string paneId) =>
        NodeServerName.Split(paneId) is { } on
            ? $"That session runs on node '{on.NodeName}', not on this machine; reaching a session on a node is not available yet. Nothing was done."
            : null;
}
