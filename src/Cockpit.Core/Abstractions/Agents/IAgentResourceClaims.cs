namespace Cockpit.Core.Abstractions.Agents;

// One agent session's standing claim on a piece of work (AC-393).
//
// `Resource`: What was claimed, in the claiming agent's own words — a worktree path, a branch, a file. The host never interprets it.
// `OwnerPaneId`: The pane holding it. Stamped from the transport-verified caller, never from anything the claimer declared.
// `ClaimedAtUtc`: When the claim was taken. Reported back so an old claim — the shape a crashed agent leaves behind — is recognisable as old.
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

// The result of a claim attempt: what happened, and the claim that now stands on the resource.
//
// `Outcome`: Taken, already the caller's, held by a neighbour, or refused because the caller holds too many.
// `Claim`:
// The claim standing on the resource — the new one on `AgentClaimOutcome.Claimed`, the caller's original on
// `AgentClaimOutcome.AlreadyHeldByYou`, and the neighbour's on `AgentClaimOutcome.HeldByAnother`,
// which is what lets the second claimer be told *who* holds it and since when. Null only when nothing stands and
// nothing was taken (`AgentClaimOutcome.TooManyClaims`).
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

// The result of a release attempt: what happened, and which claim it happened to.
//
// `Outcome`: Released, nothing there to release, or held by a neighbour.
// `Claim`:
// The claim the outcome is about — the one just given up on `AgentReleaseOutcome.Released` (so the caller
// can be told how long it had held it), and the neighbour's on `AgentReleaseOutcome.HeldByAnother` (so it
// can be told who to ask). Null only on `AgentReleaseOutcome.NotClaimed`, where there is no claim to name.
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
    /// <summary>Every standing claim, from every desk, oldest first. Never to be reachable from an MCP tool result.</summary>
    IReadOnlyList<AgentResourceClaim> ListAll();
}
