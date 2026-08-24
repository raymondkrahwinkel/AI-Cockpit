namespace Cockpit.Core.Abstractions.Agents;

// AC-1013: One agent session's standing claim on a piece of work (AC-393). OwnerPaneId is stamped from the
// transport-verified caller, never the claimer's own declaration.
public sealed record AgentResourceClaim(string Resource, string OwnerPaneId, DateTimeOffset ClaimedAtUtc);

// What became of one `IAgentResourceClaims.Claim` call.
public enum AgentClaimOutcome
{
    // Nobody on the caller's desk held it, and now the caller does.
    Claimed,

    // The caller already held it, so nothing changed and the original claim stands — re-claiming is not an error.
    AlreadyHeldByYou,

    // Another agent on the caller's desk holds it. The claim was not taken.
    HeldByAnother,

    // The caller already holds the most claims one pane may hold, so this one was not taken.
    TooManyClaims,
}

// AC-1013: The result of a claim attempt. Claim is the standing claim on the resource — including the
// neighbour's on HeldByAnother, so the second claimer can be told who holds it and since when — null only on TooManyClaims.
public sealed record AgentClaimResult(AgentClaimOutcome Outcome, AgentResourceClaim? Claim);

// What became of one `IAgentResourceClaims.Release` call.
public enum AgentReleaseOutcome
{
    // The caller held it and no longer does.
    Released,

    // Nothing on the caller's desk holds that resource, so there was nothing to give up.
    NotClaimed,

    // Another agent holds it. A claim is only the holder's to give up.
    HeldByAnother,
}

// AC-1013: The result of a release attempt. Claim is the one just given up on Released, the neighbour's on
// HeldByAnother (so the caller knows who to ask), null only on NotClaimed.
public sealed record AgentReleaseResult(AgentReleaseOutcome Outcome, AgentResourceClaim? Claim);

/// <summary>
/// Who is working on what, across agent sessions sharing a desk (AC-393): what <c>claim</c>/<c>release</c>/
/// <c>list_claims</c> use to stop the AC-119 collision of two agents on one worktree. Advisory only, no OS lock;
/// partitioned via caller-passed <c>workspacePaneIds</c>, not a workspace id, since a pane's derived workspace drifts but its identity doesn't — cross-workspace collisions are the accepted gap, surfaced to the operator (AC-439).
/// </summary>
public interface IAgentResourceClaims
{
    /// <summary>
    /// Claims <paramref name="resource"/> for <paramref name="paneId"/>, unless a pane sharing its desk already holds
    /// it (matched exactly). <paramref name="workspacePaneIds"/> is the host's own desk answer, always including
    /// <paramref name="paneId"/>, resolved before the lock is taken — one claim per resource per desk is upheld, not guaranteed: a pane joining mid-call can leave two visible claims, releasable since <see cref="Release"/> checks the caller's own claim first.
    /// </summary>
    AgentClaimResult Claim(string paneId, string resource, IReadOnlySet<string> workspacePaneIds);

    /// <summary>
    /// Gives up <paramref name="paneId"/>'s claim on <paramref name="resource"/>. Only the holder may — a release any
    /// neighbour could call guarantees nothing, since a mid-rebase agent's warning would quietly vanish. The caller's
    /// own claim is checked first, so it can always release what it holds even when a desk shows two claims on one name (see <see cref="Claim"/>).
    /// </summary>
    AgentReleaseResult Release(string paneId, string resource, IReadOnlySet<string> workspacePaneIds);

    /// <summary>
    /// Every claim held by a pane in <paramref name="workspacePaneIds"/>, oldest first — the whole of what one desk
    /// can see, which is what both <c>list_claims</c> and the per-pane claims on <c>list_agents</c> are built from.
    /// </summary>
    IReadOnlyList<AgentResourceClaim> List(IReadOnlySet<string> workspacePaneIds);

    /// <summary>
    /// Drops every claim <paramref name="paneId"/> holds — for an ended session, so a claim does not outlive its
    /// agent and go on warning neighbours off an unused worktree. Idempotent; a pane holding nothing is a no-op.
    /// Keeps a crashed agent from leaving a permanent claim behind, so phase 1 needs no heartbeat or expiry.
    /// </summary>
    void Forget(string paneId);
}

/// <summary>
/// Host-only read of the entire claim store, unpartitioned — the one view <see cref="IAgentResourceClaims"/>
/// deliberately never offers (AC-439), since an agent must never learn a claim exists on another desk. Exists for the
/// cross-workspace collision monitor, telling the <em>operator</em>, not any agent, of a collision; kept as a separate interface so the boundary is enforced by which one a class is given — <c>AgentsMcpTools</c> gets <see cref="IAgentResourceClaims"/> alone.
/// </summary>
public interface IAgentResourceClaimsAudit
{
    /// <summary>
    /// Every standing claim, from every desk, oldest first. Never to be reachable from an MCP tool result.
    /// </summary>
    IReadOnlyList<AgentResourceClaim> ListAll();
}
