namespace Cockpit.Core.Abstractions.Agents;

/// <summary>
/// Host-side answer to "which workspace is every live agent-session pane on", across the whole cockpit rather than
/// one caller's desk — contrast <see cref="IWorkspaceAgentGateway"/>, whose <c>GetWorkspaceSnapshotAsync</c> only
/// answers for panes sharing one caller's workspace, the boundary every agent-facing tool must respect. AC-439's
/// collision monitor is the one consumer: deciding whether two claims on the same physical resource belong to
/// different desks needs every claim owner's workspace at once, a question no agent-facing tool may answer.
/// </summary>
public interface IPaneWorkspaceDirectory
{
    /// <summary>
    /// Every live agent-session pane's workspace id, keyed by pane id. A pane not present here is not a live agent
    /// session the cockpit can place in a workspace (closed, or never one to begin with).
    /// </summary>
    IReadOnlyDictionary<string, string> WorkspaceIdsByPane();
}
