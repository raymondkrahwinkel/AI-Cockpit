using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Mcp;

namespace Cockpit.App.Services;

// AC-1324: the controller's end of a node session's Allow/Deny — each open question on a paired node becomes a row
// in the assistant's own conversation, the same row a local session shows, with the machine on it; the click goes
// back over the line. Runs on the 20s poll, so a question gone from the node is closed here. No state but the rows.
public sealed class NodePermissionRelay(INodeSessionsClient nodes, Func<SessionViewModel?> assistant)
{
    // UI thread: it adds to and closes rows in the assistant's transcript. With no assistant conversation to draw on
    // — not started yet, or off — there is nothing to do; the question stays on the node, and the next poll tries again.
    public void Reconcile(NodeSessionsSnapshot snapshot)
    {
        if (assistant() is not { } chat)
        {
            return;
        }

        var open = snapshot.Sessions
            .SelectMany(session => (session.PendingPermissions ?? []).Select(permission => (Session: session, Permission: permission)))
            .ToList();
        var drawn = chat.Transcript
            .Where(row => row.NodePermission is { } origin && string.Equals(origin.Node, snapshot.NodeName, StringComparison.Ordinal))
            .ToList();

        foreach (var row in drawn.Where(row => row.IsPendingPermission && !open.Any(question => _Same(row, question.Session, question.Permission))))
        {
            row.PermissionDecision = $"Answered on {snapshot.NodeName}";
            row.IsPendingPermission = false;
        }

        foreach (var (session, permission) in open.Where(question => !drawn.Any(row => _Same(row, question.Session, question.Permission))))
        {
            chat.Transcript.Add(Row(snapshot.NodeName, session, permission,
                allow => nodes.AnswerPermissionAsync(snapshot.NodeName, session.PaneId, permission.ToolUseId, allow)));
        }
    }

    // The row itself: a tool-use row like the one the session's own pane shows, pending, with the machine on it.
    // One construction site, so a scene draws exactly what a poll draws.
    internal static TranscriptEntryViewModel Row(string nodeName, NodeSessionRow session, NodePendingPermission permission, Func<bool, Task<NodePermissionAnswer>> answer) =>
        new(TranscriptEntryKind.ToolUse, $"Session '{session.Name}' on {nodeName} asks: {permission.ToolName}({permission.InputJson})")
        {
            ToolUseId = permission.ToolUseId,
            ToolName = permission.ToolName,
            InputJson = permission.InputJson,
            Machine = nodeName,
            NodePermission = new NodePermissionOrigin(nodeName, session.PaneId, answer),
            IsPendingPermission = true,
        };

    private static bool _Same(TranscriptEntryViewModel row, NodeSessionRow session, NodePendingPermission permission) =>
        string.Equals(row.NodePermission?.PaneId, session.PaneId, StringComparison.Ordinal)
        && string.Equals(row.ToolUseId, permission.ToolUseId, StringComparison.Ordinal);
}

// AC-1324: what a node row knows about where its question lives and how its click gets there.
public sealed record NodePermissionOrigin(string Node, string PaneId, Func<bool, Task<NodePermissionAnswer>> Answer);
