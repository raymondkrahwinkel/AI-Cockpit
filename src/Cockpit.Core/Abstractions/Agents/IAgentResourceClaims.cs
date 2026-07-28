namespace Cockpit.Core.Abstractions.Agents;

/// <summary>One agent session's standing claim on a piece of work (AC-393).</summary>
/// <param name="Resource">What was claimed, in the claiming agent's own words — a worktree path, a branch, a file. The host never interprets it.</param>
/// <param name="OwnerPaneId">The pane holding it. Stamped from the transport-verified caller, never from anything the claimer declared.</param>
/// <param name="ClaimedAtUtc">When the claim was taken. Reported back so an old claim — the shape a crashed agent leaves behind — is recognisable as old.</param>
public sealed record AgentResourceClaim(string Resource, string OwnerPaneId, DateTimeOffset ClaimedAtUtc);

/// <summary>What became of one <see cref="IAgentResourceClaims.Claim"/> call.</summary>
public enum AgentClaimOutcome
{
    /// <summary>Nobody on the caller's desk held it, and now the caller does.</summary>
    Claimed,

    /// <summary>The caller already held it, so nothing changed and the original claim stands — re-claiming is not an error.</summary>
    AlreadyHeldByYou,

    /// <summary>Another agent on the caller's desk holds it. The claim was not taken.</summary>
    HeldByAnother,

    /// <summary>The caller already holds the most claims one pane may hold, so this one was not taken.</summary>
    TooManyClaims,
}

/// <summary>The result of a claim attempt: what happened, and the claim that now stands on the resource.</summary>
/// <param name="Outcome">Taken, already the caller's, held by a neighbour, or refused because the caller holds too many.</param>
/// <param name="Claim">
/// The claim standing on the resource — the new one on <see cref="AgentClaimOutcome.Claimed"/>, the caller's original on
/// <see cref="AgentClaimOutcome.AlreadyHeldByYou"/>, and the neighbour's on <see cref="AgentClaimOutcome.HeldByAnother"/>,
/// which is what lets the second claimer be told <em>who</em> holds it and since when. Null only when nothing stands and
/// nothing was taken (<see cref="AgentClaimOutcome.TooManyClaims"/>).
/// </param>
public sealed record AgentClaimResult(AgentClaimOutcome Outcome, AgentResourceClaim? Claim);

/// <summary>What became of one <see cref="IAgentResourceClaims.Release"/> call.</summary>
public enum AgentReleaseOutcome
{
    /// <summary>The caller held it and no longer does.</summary>
    Released,

    /// <summary>Nothing on the caller's desk holds that resource, so there was nothing to give up.</summary>
    NotClaimed,

    /// <summary>Another agent holds it. A claim is only the holder's to give up.</summary>
    HeldByAnother,
}

/// <summary>The result of a release attempt: what happened, and — when it was somebody else's — whose.</summary>
/// <param name="Outcome">Released, nothing there to release, or held by a neighbour.</param>
/// <param name="Claim">The claim that blocked the release on <see cref="AgentReleaseOutcome.HeldByAnother"/>, so the caller can be told who to ask; null otherwise.</param>
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
    /// <param name="workspacePaneIds">The panes that share the caller's desk, from the host's own answer to that question. Claims held by anyone outside this set are not visible to this call.</param>
    AgentClaimResult Claim(string paneId, string resource, IReadOnlySet<string> workspacePaneIds);

    /// <summary>
    /// Gives up <paramref name="paneId"/>'s claim on <paramref name="resource"/>. Only the holder may: a release that
    /// any neighbour could call is a claim that guarantees nothing, since the agent that is mid-rebase would find its
    /// warning to the others quietly gone.
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
