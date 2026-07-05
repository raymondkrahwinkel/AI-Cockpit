using Cockpit.Core.Claude.Permissions;

namespace Cockpit.Core.Abstractions.Claude;

/// <summary>
/// Bridges the shared in-process MCP permission-prompt server and the individual sessions.
/// The CLI's <c>--permission-prompt-tool</c> call carries no <c>session_id</c>, so requests are
/// correlated on <c>tool_use_id</c>: a session sees the <c>tool_use</c> (and its id) in its own
/// stream before the permission call arrives, so the MCP tool can await the operator's decision
/// keyed on that id and the session resolves it when the UI's allow/deny lands.
/// </summary>
public interface IPermissionCoordinator
{
    /// <summary>
    /// Called by the MCP permission tool. Registers a pending decision for
    /// <paramref name="toolUseId"/> and returns a task that completes once a session resolves it
    /// (via <see cref="Resolve"/>) or the request is cancelled. There is intentionally no short
    /// timeout — the operator may take arbitrarily long — but <paramref name="cancellationToken"/>
    /// (the MCP request's own token) still aborts the wait if the CLI drops the call.
    /// </summary>
    Task<PermissionDecision> RequestDecisionAsync(
        string toolUseId,
        string toolName,
        string proposedInputJson,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves an outstanding request for <paramref name="toolUseId"/> with the operator's
    /// decision. Returns false when no request is pending for that id (e.g. the tool was
    /// auto-allowed by the CLI and never prompted, or it was already resolved).
    /// </summary>
    bool Resolve(string toolUseId, PermissionDecision decision);

    /// <summary>
    /// Denies every still-pending request whose id is in <paramref name="toolUseIds"/>, so a
    /// closing/disposing session never leaves the CLI blocked on an answer that will never come.
    /// </summary>
    void DenyPending(IEnumerable<string> toolUseIds, string reason);
}
