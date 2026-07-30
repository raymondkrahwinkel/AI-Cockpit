namespace Cockpit.Core.Abstractions.Agents;

/// <summary>
/// Host-side answer to "which workspace is every live agent-session pane on", across the whole cockpit rather than
/// one caller's own desk. Contrast <see cref="IWorkspaceAgentGateway"/>, whose <c>GetWorkspaceSnapshotAsync</c>
/// only ever answers for the panes sharing one caller's workspace — the boundary every agent-facing tool has to
/// respect. AC-439's collision monitor is the one consumer of this interface: to decide whether two claims on the
/// same physical resource belong to two different desks, it needs the workspace of every claim's owner pane at
/// once, which is a question no agent-facing tool asks and none may answer.
/// </summary>
public interface IPaneWorkspaceDirectory
{
    /// <summary>
    /// Every live agent-session pane's workspace id, keyed by pane id. A pane not present here is not a live agent
    /// session the cockpit can place in a workspace (closed, or never one to begin with).
    /// </summary>
    IReadOnlyDictionary<string, string> WorkspaceIdsByPane();
}
