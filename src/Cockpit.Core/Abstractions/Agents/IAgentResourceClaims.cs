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
/// Who is working on what, across the agent sessions sharing a desk (AC-393): what <c>claim</c> writes,
/// <c>release</c> gives up and <c>list_claims</c> reads. It exists to stop the collision the whole of AC-119 was
/// opened for — two agents on the same worktree, the second finding out only when an edit failed to compile.
/// <para>
/// <strong>Advisory, and only advisory.</strong> Nothing here takes an OS lock or stops anyone from touching a
/// resource; a refused claim refuses the <em>claim</em>, not the work. That is the deliberate scope of phase 1: the
/// collision was one of ignorance, not of intent, and telling an agent "pane-7 has held this since 09:12" is enough
/// to prevent it without the failure modes real locking brings (a held lock outliving its holder is worse than no
/// lock at all).
/// </para>
/// <para>
/// <strong>Why state and not an event.</strong> The question a claim answers is about the present — "is this worktree
/// taken?" — and an append-only log only answers it by replay, which breaks the moment an entry is missed, the history
/// is trimmed, or an agent dies without writing its release. One place holding the standing claims has exactly one
/// answer.
/// </para>
/// <para>
/// <strong>Partitioning: per workspace, but not keyed on one.</strong> Raymond settled the design question on the
/// ticket — claims are partitioned per workspace, so a claim taken on one desk is invisible on another and two agents
/// on different desks may hold the same resource name without ever learning of each other. That boundary is what these
/// methods take <c>workspacePaneIds</c> for: the caller passes the panes the host says share its desk, and the lookup
/// only ever considers claims whose owner is one of them.
/// </para>
/// <para>
/// The partition is therefore expressed as "whose owner is on your desk" rather than by filing each claim under a
/// workspace id, and that is not a weakening of it but the only way to hold it. A pane's workspace is derived per
/// call by <see cref="IWorkspaceAgentGateway"/> and can change over the pane's life without the pane moving — an
/// unassigned session falls back to "the first Sessions workspace", and which desk that is changes as soon as the
/// operator closes one. A claim filed under the workspace its owner resolved to at claim time would be looked for
/// under a different key afterwards: invisible to the neighbours it exists to warn, and impossible for its owner to
/// release. Pane identity is what the transport verifies and what does not drift, so the claims hang off that and the
/// desk is applied fresh on every call.
/// </para>
/// <para>
/// <strong>What this does not cover, by decision.</strong> Two agents in <em>different</em> workspaces that reach for
/// the same physical worktree do not see each other and still collide. That is the accepted gap of this partitioning,
/// tracked as AC-439, where it is answered by telling the operator — who sees every desk anyway — rather than by
/// letting one agent learn what another desk is working on.
/// </para>
/// </summary>
public interface IAgentResourceClaims
{
    /// <summary>
    /// Claims <paramref name="resource"/> for <paramref name="paneId"/>, unless a pane sharing its desk already holds
    /// it. Resources are matched exactly, as the claiming agents wrote them: two agents that mean the same worktree but
    /// spell it differently do not meet, which is the honest limit of a key the callers choose rather than a guess at
    /// what a path, a branch and a file name have in common.
    /// </summary>
    /// <param name="paneId">The claiming pane — always the transport-verified caller.</param>
    /// <param name="resource">What is being claimed, already normalised and bounded by the caller.</param>
    /// <param name="workspacePaneIds">
    /// The panes that share the caller's desk, from the host's own answer to that question — and therefore always
    /// including <paramref name="paneId"/> itself. Claims held by anyone outside this set are not visible to this call.
    /// Because the set is resolved before the lock is taken, one claim per resource per desk is what this upholds and
    /// not an invariant it can guarantee: a pane that joined the desk after the set was taken, and claimed the same
    /// name in that window, leaves two claims on it that are both visible afterwards. The caller with the newer of the
    /// two can still release it (<see cref="Release"/> looks for the caller's own first), and the window is the same
    /// one the caller closes by re-checking after the write.
    /// </param>
    AgentClaimResult Claim(string paneId, string resource, IReadOnlySet<string> workspacePaneIds);

    /// <summary>
    /// Gives up <paramref name="paneId"/>'s claim on <paramref name="resource"/>. Only the holder may: a release that
    /// any neighbour could call is a claim that guarantees nothing, since the agent that is mid-rebase would find its
    /// warning to the others quietly gone. The caller's own claim is looked for first, so an agent can always give up
    /// what it holds even in the one case where a desk could show two claims on one name (see <see cref="Claim"/>).
    /// </summary>
    /// <param name="paneId">The releasing pane — always the transport-verified caller.</param>
    /// <param name="resource">The resource to give up, matched exactly against what was claimed.</param>
    /// <param name="workspacePaneIds">The panes that share the caller's desk. A claim held outside this set is invisible here, exactly as it is to <see cref="Claim"/>.</param>
    AgentReleaseResult Release(string paneId, string resource, IReadOnlySet<string> workspacePaneIds);

    /// <summary>
    /// Every claim held by a pane in <paramref name="workspacePaneIds"/>, oldest first — the whole of what one desk
    /// can see, which is what both <c>list_claims</c> and the per-pane claims on <c>list_agents</c> are built from.
    /// </summary>
    IReadOnlyList<AgentResourceClaim> List(IReadOnlySet<string> workspacePaneIds);

    /// <summary>
    /// Drops every claim <paramref name="paneId"/> holds — for a pane whose session has ended, so a claim does not
    /// outlive the agent that took it and go on warning neighbours off a worktree nobody is using. Idempotent; a pane
    /// holding nothing is a no-op. This is what keeps an agent that crashed without releasing from leaving a
    /// permanent one behind, and the reason phase 1 needs no heartbeat or expiry to go with it.
    /// </summary>
    void Forget(string paneId);
}

/// <summary>
/// Host-only read of the entire claim store, unpartitioned — the one view <see cref="IAgentResourceClaims"/>
/// deliberately never offers (AC-439). Every method on that interface takes a <c>workspacePaneIds</c> desk and
/// answers only for it, because that is the boundary AC-393 exists to hold: an agent must never learn that a claim
/// exists on another desk. This interface exists for the one consumer that is allowed to see past that boundary —
/// the cross-workspace collision monitor that tells the <em>operator</em>, not any agent, when two desks have
/// reached for the same physical resource.
/// <para>
/// Kept as a separate interface rather than a method added to <see cref="IAgentResourceClaims"/> so the boundary is
/// enforced by which interface a class is given, not by a comment telling every future caller of the wide interface
/// not to use the one unpartitioned method on it. <c>AgentsMcpTools</c> — the only place an agent's request reaches
/// the claim store — is constructed with <see cref="IAgentResourceClaims"/> alone and never with this one.
/// </para>
/// </summary>
public interface IAgentResourceClaimsAudit
{
    /// <summary>Every standing claim, from every desk, oldest first. Never to be reachable from an MCP tool result.</summary>
    IReadOnlyList<AgentResourceClaim> ListAll();
}
