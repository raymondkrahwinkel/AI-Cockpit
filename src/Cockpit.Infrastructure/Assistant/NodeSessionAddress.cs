using Cockpit.Core.Mcp;

namespace Cockpit.Infrastructure.Assistant;

// AC-1320: how list_sessions names a session on a paired node — "<node> · <paneId>", the registry's own shape,
// so the field the assistant addresses by can never equal a local pane id (a bare GUID). AC-1323: every
// pane-taking tool splits it and goes to the node, except the watch pair (no event stream between machines).
internal static class NodeSessionAddress
{
    public static string For(string nodeName, string paneId) => NodeServerName.For(nodeName, paneId);

    // The node and that machine's own pane id, or null for a pane id that is this machine's.
    public static (string NodeName, string PaneId)? Split(string paneId) =>
        NodeServerName.Split(paneId) is { } on ? (on.NodeName, on.ServerName) : null;

    // The refusal watch_session/unwatch_session give a node session's address, or null for a local pane id.
    public static string? WatchRefusal(string paneId) =>
        Split(paneId) is { } on
            ? $"That session runs on node '{on.NodeName}', and a session on a node cannot be watched: there is no event stream between the two machines. What does work on this same address: read_transcript to see where it is, send_prompt and send_message to steer or tell it, rename_session and stop_agent — or ask it in a prompt to notify cockpit-assistant when it is done, which reaches you here. Nothing was done."
            : null;

    // What an address that no node client can serve is told — the wiring gap, not a refusal of the node.
    public const string NoClient = "Reaching a session on a node is not wired into this cockpit. Nothing was done.";
}
