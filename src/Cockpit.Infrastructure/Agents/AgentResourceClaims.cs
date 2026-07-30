using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Agents;

namespace Cockpit.Infrastructure.Agents;

/// <summary>
/// The concrete claim store behind <see cref="IAgentResourceClaims"/> (AC-393): one list of standing claims, oldest
/// first, with the caller's desk applied on every call rather than baked into a key.
/// <para>
/// Everything is behind one lock, like the inbox and unlike the roster: claiming is a check-then-act — does anyone on
/// this desk already hold it, and is the caller already at its limit — that has to be atomic, or two agents claiming
/// the same worktree on two MCP request threads both see it free and both get it, which is precisely the collision
/// this exists to prevent.
/// </para>
/// <para>
/// A flat list rather than a dictionary keyed on resource, because every operation here needs the owner as well as
/// the resource: a claim is only visible to a caller whose desk holds its owner, and both <see cref="Forget"/> and the
/// per-pane cap look claims up by owner rather than by resource. <see cref="MaxClaimsPerPane"/> keeps the list small
/// enough that a scan is the cheaper answer as well as the simpler one.
/// </para>
/// </summary>
internal sealed class AgentResourceClaims : IAgentResourceClaims, IAgentResourceClaimsAudit, ISingletonService
{
    /// <summary>
    /// Cap on the claims one pane may hold at once. An agent claims the handful of things it is working on — a
    /// worktree, a branch, a file or two — so the number is generous for the use and still a bound: without one, a
    /// looping agent grows host memory a distinct resource string at a time, and every neighbour that calls
    /// <c>list_agents</c> pays for the pile in its own context window.
    /// </summary>
    internal const int MaxClaimsPerPane = 50;

    private readonly object _lock = new();

    // Append-ordered, so it is already oldest-first for List and stays that way across removals — no timestamp sort
    // anywhere, and the age a caller reads is the age of the claim it is looking at.
    private readonly List<AgentResourceClaim> _claims = [];

    public AgentClaimResult Claim(string paneId, string resource, IReadOnlySet<string> workspacePaneIds)
    {
        lock (_lock)
        {
            if (_Standing(resource, workspacePaneIds) is { } standing)
            {
                return new AgentClaimResult(
                    string.Equals(standing.OwnerPaneId, paneId, StringComparison.Ordinal)
                        ? AgentClaimOutcome.AlreadyHeldByYou
                        : AgentClaimOutcome.HeldByAnother,
                    standing);
            }

            // Counted over every claim this pane holds, not only the ones visible from this desk: the cap is a bound
            // on host memory, and memory does not care which desk the pane was on when it took them.
            if (_claims.Count(claim => string.Equals(claim.OwnerPaneId, paneId, StringComparison.Ordinal)) >= MaxClaimsPerPane)
            {
                return new AgentClaimResult(AgentClaimOutcome.TooManyClaims, null);
            }

            var claimed = new AgentResourceClaim(resource, paneId, DateTimeOffset.UtcNow);
            _claims.Add(claimed);
            return new AgentClaimResult(AgentClaimOutcome.Claimed, claimed);
        }
    }

    public AgentReleaseResult Release(string paneId, string resource, IReadOnlySet<string> workspacePaneIds)
    {
        lock (_lock)
        {
            // The caller's own claim is looked for before anyone else's. With one claim per resource per desk the two
            // lookups find the same entry; they only come apart in the window Claim's docs describe, where a desk can
            // show two claims on one name — and there an agent must still be able to give up what it holds rather than
            // be told, about its own claim, that somebody else has it.
            if (_Standing(resource, workspacePaneIds, ownedBy: paneId) is { } mine)
            {
                _claims.Remove(mine);
                return new AgentReleaseResult(AgentReleaseOutcome.Released, mine);
            }

            return _Standing(resource, workspacePaneIds) is { } theirs
                ? new AgentReleaseResult(AgentReleaseOutcome.HeldByAnother, theirs)
                : new AgentReleaseResult(AgentReleaseOutcome.NotClaimed, null);
        }
    }

    public IReadOnlyList<AgentResourceClaim> List(IReadOnlySet<string> workspacePaneIds)
    {
        lock (_lock)
        {
            // Materialised inside the lock, so the caller never enumerates a list another request thread is adding to.
            return [.. _claims.Where(claim => workspacePaneIds.Contains(claim.OwnerPaneId))];
        }
    }

    public void Forget(string paneId)
    {
        lock (_lock)
        {
            _claims.RemoveAll(claim => string.Equals(claim.OwnerPaneId, paneId, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// <see cref="IAgentResourceClaimsAudit.ListAll"/> — every claim, from every desk, with no
    /// <c>workspacePaneIds</c> filter applied. Reachable only through that separate interface (AC-439), never
    /// through <see cref="IAgentResourceClaims"/>, so nothing in <c>AgentsMcpTools</c> can call it.
    /// </summary>
    public IReadOnlyList<AgentResourceClaim> ListAll()
    {
        lock (_lock)
        {
            return [.. _claims];
        }
    }

    /// <summary>
    /// The claim on <paramref name="resource"/> that the caller's desk can see — narrowed to one holder when
    /// <paramref name="ownedBy"/> is given — or null when that desk holds none. Resources and pane ids are both
    /// compared ordinally: the agents choose the resource string, and a host that case-folded or normalised paths
    /// here would be guessing which of "a branch", "a worktree path" and "a file" it had been handed, while a pane id
    /// is a host-minted identifier where two spellings are two panes.
    /// </summary>
    private AgentResourceClaim? _Standing(string resource, IReadOnlySet<string> workspacePaneIds, string? ownedBy = null) =>
        _claims.FirstOrDefault(claim =>
            string.Equals(claim.Resource, resource, StringComparison.Ordinal)
            && workspacePaneIds.Contains(claim.OwnerPaneId)
            && (ownedBy is null || string.Equals(claim.OwnerPaneId, ownedBy, StringComparison.Ordinal)));
}
